using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public sealed class NavmeshManager : IDisposable
{
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;

    public string CurrentKey { get; private set; } = ""; // unique string representing currently loaded navmesh
    public Navmesh? Navmesh { get; private set; }
    public NavmeshQuery? Query { get; private set; }
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;

    private volatile float _loadTaskProgress = -1;
    public float LoadTaskProgress => _loadTaskProgress; // negative if load task is not running, otherwise in [0, 1] range

    private CancellationTokenSource? _currentCTS; // this is signalled when mesh is unloaded, all pathfinding tasks that use it are then cancelled

    // 兩個 CTS 分工，不可合併成一個：
    //   _currentCTS  ＝「網格生命週期」。ClearState/Reload 用它，**載入工作本身也綁在它上面**。
    //   _pathfindCTS ＝「尋路批次」。CancelAllPathfinds 只取消這一個。
    // 🔴 為什麼一定要分開：載入工作與尋路工作原本共用同一個 token，所以「取消全部尋路」若
    //    直接取消 _currentCTS，會**連進行中的網格載入一起殺掉**，而且沒有任何東西會把它
    //    重新啟動 ⇒ 網格永遠載不起來。分成兩個之後，取消尋路對載入零影響。
    // 尋路工作同時連結兩者，所以「網格被卸掉時尋路也要一起取消」的原有語意完全保留。
    private CancellationTokenSource _pathfindCTS = new();
    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    private int _numActivePathfinds;
    public bool PathfindInProgress => _numActivePathfinds > 0;
    public int NumQueuedPathfindRequests => _numActivePathfinds > 0 ? _numActivePathfinds - 1 : 0;

    private DirectoryInfo _cacheDir;

    // 🔴 IPC 全量重建的節流狀態。詳見 RebuildFromIPC 的說明。
    public static readonly TimeSpan IPCRebuildMinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IPCRebuildHardCap = TimeSpan.FromMinutes(5);
    private DateTime _lastIPCRebuild = DateTime.MinValue;
    private DateTime _lastIPCRebuildSkipLog = DateTime.MinValue;
    private int _ipcRebuildSkipCount;

    // 只節流「說明訊息」，不節流取消動作本身。詳見 CancelAllPathfinds。
    private static readonly TimeSpan CancelAllLogMinInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastCancelAllLog = DateTime.MinValue;

    public unsafe NavmeshManager(DirectoryInfo cacheDir)
    {
        _cacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists

        // prepare a task with correct task scheduler that other tasks can be chained off
        _lastLoadQueryTask = Service.Framework.Run(() => Log("Tasks kicked off"));
    }

    public void Dispose()
    {
        Log("Disposing");
        ClearState();
    }

    public void Update()
    {
        CosmicProgress.Update(); // 主執行緒；見 CosmicProgress 的執行緒約定

        var curKey = GetCurrentKey();
        if (curKey != CurrentKey)
        {
            // navmesh needs to be reloaded
            if (!Service.Config.AutoLoadNavmesh)
            {
                if (CurrentKey.Length == 0)
                    return; // nothing is loaded, and auto-load is forbidden
                curKey = ""; // just unload existing mesh
            }
            Log($"Starting transition from '{CurrentKey}' to '{curKey}'");
            CurrentKey = curKey;
            Reload(true);
            // mesh load is now in progress
        }
    }

    public bool Reload(bool allowLoadFromCache)
    {
        ClearState();
        if (CurrentKey.Length > 0)
        {
            var cts = _currentCTS = new();
            ExecuteWhenIdle(async cancel =>
            {
                _loadTaskProgress = 0;

                using var resetLoadProgress = new OnDispose(() => _loadTaskProgress = -1);

                var waitStart = DateTime.Now;

                while (InCutscene)
                {
                    if ((DateTime.Now - waitStart).TotalSeconds >= 5)
                    {
                        waitStart = DateTime.Now;
                        Log("waiting for cutscene");
                    }
                    await Service.Framework.DelayTicks(1, cancel);
                }

                var (cacheKey, scene) = await Service.Framework.Run(() =>
                {
                    var scene = new SceneDefinition();
                    scene.FillFromActiveLayout();
                    var cacheKey = GetCacheKey(scene);
                    return (cacheKey, scene);
                }, cancel);

                if (cacheKey.Length == 0)
                {
                    // GetCacheKey() only returns an empty string when the layout was unavailable, i.e. it
                    // vanished while this build was queued behind another task or waiting out a cutscene.
                    // Abort rather than build an empty mesh and persist it under a junk cache name, and
                    // re-arm CurrentKey so Update() kicks off a fresh transition once a layout is back.
                    Service.Log.Information($"[NavmeshManager] Layout unavailable when starting build for '{CurrentKey}'; aborting build, will retry once a layout is loaded");
                    CurrentKey = "";
                    return;
                }

                Log($"Kicking off build for '{cacheKey}' (reload={allowLoadFromCache})");
                var navmesh = await Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache, cancel), cancel);
                Log($"Mesh loaded: '{cacheKey}'");
                Navmesh = navmesh;
                Query = new(Navmesh);
                OnNavmeshChanged?.Invoke(Navmesh, Query);
            }, cts.Token);
        }
        return true;
    }

    /// <summary>
    /// 🔴 給 IPC 的 Nav.Rebuild 專用入口：帶最小間隔節流的全量重建（不吃快取）。
    ///
    /// 為什麼要節流：Nav.Rebuild 走的是 Reload(allowLoadFromCache: false)，也就是
    /// BuildNavmesh 裡「跳過 cache.Exists 分支、整個區域逐 tile 重建」的路徑（一個 256 tile
    /// 的區域實測約 1.2 秒，大區更久）。重建期間 Navmesh/Query 會被 ClearState 清成 null，
    /// 玩家因此不會移動 —— 而呼叫端普遍是「偵測到卡住就重建」的形狀，於是：
    ///   卡住 → 要求重建 → 重建期間動不了 → 還是卡住 → 再要求重建 …… 自我維持。
    /// AutoDuty 的實機 log（2026-08-31 20:50 前後）就出現過連續 128 次全量重建，
    /// 每次都印一輪 Queueing state clear / Kicking off build。IPCProvider 上列了 7 個
    /// 呼叫端，所以節流放在 vnavmesh 這端一次保護全部，比逐一去修呼叫端可靠。
    ///
    /// 🔴 刻意只擋 IPC 這條路：使用者自己按 UI 的「Rebuild」或打 /vnav rebuild 走的是
    ///    Reload(false)，語意就是「我現在就要重建」，不受此限、也不更新這裡的時間戳。
    /// 🔴 IPCRebuildHardCap 是安全閥：萬一建置進度旗標因為任何理由卡住不歸位，
    ///    超過這個時間一律放行，免得這個節流本身變成「永遠不能重建」的新故障。
    /// </summary>
    /// <returns>true 表示這次真的送出重建；false 表示被節流略過。</returns>
    public bool RebuildFromIPC()
    {
        var now = DateTime.Now;
        var since = now - _lastIPCRebuild;
        var buildInProgress = _loadTaskProgress >= 0;

        if ((since < IPCRebuildMinInterval || buildInProgress) && since < IPCRebuildHardCap)
        {
            ++_ipcRebuildSkipCount;
            // 診斷寫 Information（使用者跑 LogLevel 2），但節流到最多每 5 秒一行，
            // 免得呼叫端每秒打一次就把 log 洗掉。
            if ((now - _lastIPCRebuildSkipLog).TotalSeconds >= 5)
            {
                _lastIPCRebuildSkipLog = now;
                Service.Log.Information(
                    $"[NavmeshManager] 已略過外掛透過 IPC 要求的全量重建 {_ipcRebuildSkipCount} 次："
                  + $"距上次重建 {since.TotalSeconds:f1} 秒，未達 {IPCRebuildMinInterval.TotalSeconds:f0} 秒的最小間隔"
                  + (buildInProgress ? "，且目前仍在建置中" : "")
                  + "。全量重建期間玩家不會移動，呼叫端若以「卡住」當觸發條件會自我維持。"
                  + "使用者自己按 UI 的 Rebuild 或 /vnav rebuild 不受此限。");
                _ipcRebuildSkipCount = 0;
            }
            return false;
        }

        _lastIPCRebuild = now;
        _lastIPCRebuildSkipLog = DateTime.MinValue; // 讓下一次被略過時立刻有一行說明，不必等 5 秒
        _ipcRebuildSkipCount = 0;
        return Reload(false);
    }

    internal void ReplaceMesh(Navmesh mesh)
    {
        Log($"Mesh replaced");
        Navmesh = mesh;
        Query = new(Navmesh);
        OnNavmeshChanged?.Invoke(Navmesh, Query);
    }

    /// <summary>
    /// IPC 的 Nav.PathfindCancelAll 專用入口：**只取消進行中/排隊中的尋路，不動導航網格**。
    ///
    /// 為什麼不再是 Reload(true)（本函式取代的舊實作）：Reload 會先 ClearState() 把
    /// Navmesh/Query 清成 null，再從快取非同步重新載入。取消的效果確實有達到，但代價是
    /// 整張網格被卸掉再載入 —— 這段期間 Nav.IsReady 回 false、Nav.Pathfind 直接擲例外。
    /// 而呼叫端幾乎清一色是「取消 → 立刻重新規劃路徑」的形狀，於是重試必定先失敗一次，
    /// 要等載入完成才會成功。改成純取消之後，Nav.IsReady 全程維持 true。
    ///
    /// 🔴 這裡**刻意沒有任何節流**。對一個「取消」動作加節流，會讓取消靜默地不發生，
    ///    那比多做幾次工作糟得多。（有節流的是 RebuildFromIPC，那是全量重建，兩者不要混。）
    ///    下面的 early-return **不是節流**：_numActivePathfinds 是在 QueryPath 裡同步遞增的，
    ///    所以它等於 0 就代表真的沒有尋路可取消，跳過是精確的 no-op，順便讓工作佇列不會
    ///    因為呼叫端輪詢式地連打取消而堆積一長串 Dispose 動作。
    /// </summary>
    public void CancelAllPathfinds()
    {
        if (_numActivePathfinds <= 0)
            return; // 沒有進行中或排隊中的尋路（見上面說明：這不是節流）

        var cancelled = _numActivePathfinds;
        var cts = _pathfindCTS;
        _pathfindCTS = new(); // 先換上新的，之後進來的尋路才不會一出生就處於已取消狀態
        cts.Cancel();

        // 舊 CTS 的 Dispose 排到工作佇列尾端 —— 與 ClearState 對 _currentCTS 的處理同一個
        // 理由：QueryPath 建立的 linked CTS 還握著對它的註冊，等佇列排空才釋放最安全。
        ExecuteWhenIdle(cts.Dispose, default);

        Log($"Cancelled {cancelled} pathfind(s); navmesh left loaded");
        var now = DateTime.Now;
        if (now - _lastCancelAllLog >= CancelAllLogMinInterval)
        {
            _lastCancelAllLog = now;
            Service.Log.Information($"[NavmeshManager] Nav.PathfindCancelAll: 已取消 {cancelled} 筆尋路，導航網格保持載入(Nav.IsReady 不會變 false)");
        }
    }

    private static bool InCutscene => Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    // ⚠️ 參數順序刻意與上游一致(range 在 externalCancel 之前),讓日後追上游不必再改一次。
    //    順序換了但型別不相容(float vs CancellationToken),舊的位置引數呼叫會編譯失敗而不是靜默錯位。
    public Task<List<Waypoint>> QueryPath(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        if (_currentCTS == null)
            throw new Exception($"Can't initiate query - navmesh is not loaded");

        // 工作可以被三種來源取消：網格被卸掉(_currentCTS)、外掛端要求取消全部尋路
        // (_pathfindCTS，走 CancelAllPathfinds)、呼叫端自己的 token(externalCancel)。
        var combined = CancellationTokenSource.CreateLinkedTokenSource(_currentCTS.Token, _pathfindCTS.Token, externalCancel);
        Interlocked.Increment(ref _numActivePathfinds);
        var task = ExecuteWhenIdle(async cancel =>
        {
            Log($"Kicking off pathfind from {from} to {to}");
            var path = await Task.Run(() =>
            {
                combined.Token.ThrowIfCancellationRequested();
                if (Query == null)
                    throw new Exception($"Can't pathfind, navmesh did not build successfully");
                Log($"Executing pathfind from {from} to {to}");
                // ⚠️ 迴避圓目前只支援地面路徑。飛行路徑要繞圓得改 VoxelPathfind,那是另一個階段的事,
                //    所以這裡**明講**它沒生效,而不是安靜地忽略參數。
                if (flying && avoidCenter != null && avoidRadius > 0)
                    Service.Log.Information($"[NavmeshManager] 飛行路徑尚未支援迴避圓(中心 {avoidCenter} 半徑 {avoidRadius:f1}),本次忽略該參數。");
                var meshFilter = !flying && avoidCenter != null && avoidRadius > 0
                    ? new NavmeshQuery.AvoidRadiusFilter(avoidCenter.Value, avoidRadius)
                    : null;
                return flying ? Query.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token) : Query.PathfindMesh(from, to, UseRaycasts, UseStringPulling, combined.Token, range, meshFilter);
            }, combined.Token);
            Log($"Pathfinding done: {path.Count} waypoints");
            return path;
        }, combined.Token);

        // 🔴 遞減與 linked CTS 的釋放**必須掛在工作的完成回呼上**，不能只放在 body 裡：
        //    ExecuteWhenIdle 底層是 Service.Framework.Run(...) ＝ TaskFactory.StartNew(delegate, token)，
        //    而 TPL 在工作真正開始執行前發現 token 已取消時，會把工作直接標成 Canceled 而
        //    **完全不執行 delegate** ⇒ 原本寫在 body 裡的 OnDispose 永遠不會跑。
        //    舊碼靠 ClearState 裡的 _numActivePathfinds = 0 幫忙收尾，那在「取消＝順便重載網格」
        //    的年代還過得去；但 CancelAllPathfinds 改成只取消尋路之後，呼叫端的典型形狀是
        //    「取消 → 立刻重新規劃路徑」，硬重設會把**新工作**的計數一起歸零，
        //    讓 Nav.PathfindInProgress 在尋路確實進行中的時候謊報 false。
        //    改成一增一減嚴格配對（無論 body 有沒有跑）就沒有這個問題。
        _ = task.ContinueWith(_ =>
        {
            Interlocked.Decrement(ref _numActivePathfinds);
            combined.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    // 只要座標、不要 area id 的版本。🔴 IPC 的 Nav.Pathfind / PathfindWithTolerance /
    // PathfindCancelable 一直回 List<Vector3>,全艦隊消費端都照這個型別寫,**不可以改**。
    public async Task<List<Vector3>> QueryPathBasic(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        var result = await QueryPath(from, to, flying, range, externalCancel, avoidCenter, avoidRadius);
        return [.. result.Select(w => w.Position)];
    }

    // note: pixelSize should be power-of-2
    public (Vector3 min, Vector3 max) BuildBitmap(Vector3 startingPos, string filename, float pixelSize, AABB? mapBounds = null)
    {
        if (Navmesh == null || Query == null)
            throw new InvalidOperationException($"Can't build bitmap - navmesh creation is in progress");

        bool inBounds(Vector3 vert) => mapBounds is not AABB aabb || vert.X >= aabb.Min.X && vert.Y >= aabb.Min.Y && vert.Z >= aabb.Min.Z && vert.X <= aabb.Max.X && vert.Y <= aabb.Max.Y && vert.Z <= aabb.Max.Z;

        var startPoly = Query.FindNearestMeshPoly(startingPos);
        var reachablePolys = Query.FindReachableMeshPolys(startPoly);

        HashSet<long> polysInbounds = [];

        Vector3 min = new(1024), max = new(-1024);
        foreach (var p in reachablePolys)
        {
            Navmesh.Mesh.GetTileAndPolyByRefUnsafe(p, out var tile, out var poly);
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var v = NavmeshBitmap.GetVertex(tile, poly.verts[i]);
                if (!inBounds(v))
                    goto cont;

                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                //Service.Log.Debug($"{p:X}.{i}= {v}");
            }

            polysInbounds.Add(p);

        cont:;
        }
        //Service.Log.Debug($"bounds: {min}-{max}");

        var bitmap = new NavmeshBitmap(min, max, pixelSize);
        foreach (var p in polysInbounds)
        {
            bitmap.RasterizePolygon(Navmesh.Mesh, p);
        }
        bitmap.Save(filename);
        Service.Log.Debug($"Generated nav bitmap '{filename}' @ {startingPos}: {bitmap.MinBounds}-{bitmap.MaxBounds}");
        return (bitmap.MinBounds, bitmap.MaxBounds);
    }

    // if non-empty string is returned, active layout is ready
    private unsafe string GetCurrentKey()
    {
        // LayoutWorld.Instance() is [StaticAddress(..., isPointer: true)] and legitimately returns null
        // (title screen / between zones). An empty key is the established "nothing loaded" value here.
        // This runs every frame from Update(), so it deliberately does not log.
        var world = LayoutWorld.Instance();
        if (world == null)
            return ""; // layout world not available

        var layout = world->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return ""; // layout not ready

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;

        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        // CE always has a festival layer (i hope). the non-festival layout is briefly loaded when entering the zone, which triggers a useless mesh build (which is also expensive because the zone is large)
        if (terrRow?.TerritoryIntendedUse.RowId == 60)
        {
            var fest = layout->ActiveFestivals[0];
            if (fest.Id == 0 && fest.Phase == 0)
                return "";
        }

        var sgs = LayoutUtils.GetZoneSharedGroupsEnabled(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        return $"{terrRow?.Bg}//{filterKey:X}//{LayoutUtils.FestivalsString(layout->ActiveFestivals)}//{string.Join('.', sgs)}";
    }

    internal static unsafe string GetCacheKey(SceneDefinition scene)
    {
        // note: festivals are active globally, but majority of zones don't have festival-specific layers, so we only want real ones in the cache key
        // LayoutWorld.Instance() is [StaticAddress(..., isPointer: true)] and can legitimately be null.
        // The layout can also be torn down between the reload being queued and the build actually
        // starting (zone change, logout, the cutscene wait above). LayoutUtils.FindFilter() dereferences
        // its argument unconditionally, so both have to be checked here. An empty string is an
        // unambiguous sentinel: a real key always contains the "__" separators below.
        var world = LayoutWorld.Instance();
        var layout = world != null ? world->ActiveLayout : null;
        if (layout == null)
            return "";

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        var terrId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(terrId);

        static string numbers<T>(IEnumerable<T> nums) where T : INumber<T> => string.Join('.', nums.Select(n => n.ToString("X", CultureInfo.InvariantCulture)));

        return $"{terrRow?.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{numbers(scene.FestivalLayers)}__{numbers(scene.ZoneSGs)}";
    }

    private void ClearState()
    {
        if (_currentCTS == null)
            return; // already cleared

        var cts = _currentCTS;
        _currentCTS = null;
        cts.Cancel();
        Log("Queueing state clear");
        ExecuteWhenIdle(() =>
        {
            Log("Clearing state");
            // 🔑 這裡原本有 _numActivePathfinds = 0;（用來補救「工作被取消所以 body 沒跑、
            //    計數沒遞減」）。QueryPath 改成用完成回呼遞減之後，一增一減已嚴格配對，
            //    這行變成多餘 —— 而且是有害的：Reload/CancelAllPathfinds 之後呼叫端會馬上
            //    送出新的尋路，這行會把那筆新工作的計數一起歸零，之後它完成時再遞減就變負數，
            //    Nav.PathfindInProgress 於是在尋路進行中謊報 false。所以刻意移除，不要加回來。
            cts.Dispose();
            OnNavmeshChanged?.Invoke(null, null);
            Query = null;
            Navmesh = null;
        }, default);
    }

    private Navmesh BuildNavmesh(SceneDefinition scene, string cacheKey, bool allowLoadFromCache, CancellationToken cancel)
    {
        Log($"Build task started: '{cacheKey}'");
        var customization = NavmeshCustomizationRegistry.ForTerritory(scene.TerritoryID);
        Log($"Customization for '{scene.TerritoryID}': {customization.GetType()}");
        customization.CurrentTerritory = scene.TerritoryID; // 供 LinkPoints 產生「territory + 座標」的捷徑識別鍵
        customization.CurrentScene = scene; // 供自訂化以「當下 layout 有沒有這個碰撞模型」判斷路線是否開通

        var layers = scene.FestivalLayers.ToList();

        // try reading from cache
        var cache = new FileInfo($"{_cacheDir.FullName}/{cacheKey}.navmesh");
        if (allowLoadFromCache && cache.Exists)
        {
            try
            {
                Log($"Loading cache: {cache.FullName}");
                using var stream = cache.OpenRead();
                using var reader = new BinaryReader(stream);
                var mesh = Navmesh.Deserialize(reader, customization.Version);
                customization.CustomizeMesh(mesh, layers);
                return mesh;
            }
            catch (Exception ex)
            {
                Log($"Failed to load cache: {ex}");
            }
        }
        cancel.ThrowIfCancellationRequested();

        // cache doesn't exist or can't be used for whatever reason - build navmesh from scratch
        var builder = new NavmeshBuilder(scene, customization);
        var deltaProgress = 0.99f / (builder.NumTilesX * builder.NumTilesZ);
        builder.BuildTiles(() =>
        {
            _loadTaskProgress += deltaProgress;
            cancel.ThrowIfCancellationRequested();
        });

        // write results to cache
        {
            Service.Log.Debug($"Writing cache: {cache.FullName}");
            using var stream = cache.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream);
            builder.Navmesh.Serialize(writer);
        }
        customization.CustomizeMesh(builder.Navmesh, layers);
        deltaProgress += 0.01f;
        return builder.Navmesh;
    }

    private void ExecuteWhenIdle(Action task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        _lastLoadQueryTask = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            task();
        }, token);
    }

    private void ExecuteWhenIdle(Func<CancellationToken, Task> task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        _lastLoadQueryTask = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            var t = task(token);
            await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            LogTaskError(t);
        }, token);
    }

    private Task<T> ExecuteWhenIdle<T>(Func<CancellationToken, Task<T>> task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        var res = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            var t = task(token);
            await ((Task)t).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            LogTaskError(t);
            return t.Result;
        }, token);
        _lastLoadQueryTask = res;
        return res;
    }

    private static void Log(string message) => Service.Log.Debug($"[NavmeshManager] [{Thread.CurrentThread.ManagedThreadId}] {message}");
    private static void LogTaskError(Task task)
    {
        if (task.IsFaulted)
            Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
    }
}
