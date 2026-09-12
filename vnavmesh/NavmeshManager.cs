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

    // 🔴 CurrentKey 不是「只有框架執行緒碰」的狀態 —— 讀取點橫跨三條執行緒。
    // 刻意**不**把欄位標成 volatile：與 _generation / _loadTaskProgress / _numActivePathfinds
    // 統一採「欄位是普通的、每個存取點自己講清楚」這一種形狀（那三個是 CS0420 迫使的，
    // 這個為了一致）。
    private string _currentKey = "";

    /// <summary>unique string representing currently loaded navmesh（空字串＝什麼都沒載入）</summary>
    public string CurrentKey => Volatile.Read(ref _currentKey);

    /// <summary>
    /// 同一代的導航網格與查詢物件，<b>永遠成對</b>。
    /// 🔑 參考型別的指派是原子的 ⇒ 讀到的永遠是完整的一代，不會是半舊半新。
    /// </summary>
    public sealed class MeshGeneration(Navmesh? mesh, NavmeshQuery? query)
    {
        public readonly Navmesh? Mesh = mesh;
        public readonly NavmeshQuery? Query = query;
    }

    private static readonly MeshGeneration NoMesh = new(null, null);
    private MeshGeneration _generation = NoMesh;

    /// <summary>一次取得同一代的網格與查詢物件。<b>同一個流程裡要用到兩者時一律走這支</b>，不要分兩次讀下面兩個屬性。</summary>
    public MeshGeneration Current => Volatile.Read(ref _generation);

    public Navmesh? Navmesh => Current.Mesh;
    public NavmeshQuery? Query => Current.Query;
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;

    /// <summary>
    /// 發佈新的一代，然後才通知訂閱者。
    /// ⚠️ 順序刻意是「先發佈再通知」。
    /// </summary>
    private void PublishMesh(Navmesh? mesh, NavmeshQuery? query)
    {
        Volatile.Write(ref _generation, mesh == null && query == null ? NoMesh : new MeshGeneration(mesh, query));
        OnNavmeshChanged?.Invoke(mesh, query);
    }

    // 建置進度被「N 條建置執行緒」寫、被框架／繪製／IPC 呼叫端讀。
    // float 沒有 Interlocked.Increment/Add，但有 Interlocked.CompareExchange(ref float,...)
    // ⇒ 用 CAS 迴圈累加(見 AddLoadProgress)。
    private float _loadTaskProgress = -1;

    // negative if load task is not running, otherwise in [0, 1] range
    public float LoadTaskProgress => Volatile.Read(ref _loadTaskProgress);

    /// <summary>
    /// 把建置進度往上加。<b>會從多條建置執行緒同時被呼叫</b>，所以走 CAS 迴圈而不是 +=。
    /// </summary>
    private void AddLoadProgress(float delta)
    {
        // 讀到的舊值是別條執行緒剛寫的也沒關係：CompareExchange 回傳「比較當時的實際值」，
        // 與預期不符就代表有人插隊，重跑一次即可。競爭者最多 BuildMaxCores 條，而且
        // **每塊 tile 才呼叫一次(不是每幀)**，所以這個迴圈實際上幾乎不會轉第二圈。
        float old, updated;
        do
        {
            old = Volatile.Read(ref _loadTaskProgress);
            updated = old + delta;
        }
        while (Interlocked.CompareExchange(ref _loadTaskProgress, updated, old) != old);
    }

    // 🔴 這個欄位的每一個存取點都自己標明同步手法，不要再加裸讀寫：
    //   換上新的 ＝ Reload 的 Interlocked.Exchange（順手接手孤兒）
    //   清掉     ＝ ClearState 的 Interlocked.Exchange（贏者負責取消）
    //   讀       ＝ QueryPath 的 Volatile.Read（只讀一次，之後全程用區域變數）
    private CancellationTokenSource? _currentCTS; // this is signalled when mesh is unloaded, all pathfinding tasks that use it are then cancelled

    // 兩個 CTS 分工，不可合併成一個：
    //   _currentCTS  ＝「網格生命週期」。ClearState/Reload 用它，**載入工作本身也綁在它上面**。
    //   _pathfindCTS ＝「尋路批次」。CancelAllPathfinds 只取消這一個。
    // 尋路工作同時連結兩者，所以「網格被卸掉時尋路也要一起取消」的原有語意完全保留。
    private CancellationTokenSource _pathfindCTS = new();
    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    // _numActivePathfinds 被三種執行緒碰，所以讀寫兩側都要明確同步。
    // 讀取點跑在呼叫端執行緒上也**不套任何閘門**：這是純量原子讀，而 Nav.PathfindInProgress
    // 是會被輪詢的布林端點 —— 對它套會阻塞的閘門本身就是紅線。
    private int _numActivePathfinds;

    public bool PathfindInProgress => Volatile.Read(ref _numActivePathfinds) > 0;

    /// <summary>
    /// 進行中以外、還在排隊的尋路筆數。
    /// </summary>
    public int NumQueuedPathfindRequests
    {
        get
        {
            var active = Volatile.Read(ref _numActivePathfinds);
            return active > 0 ? active - 1 : 0;
        }
    }

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

    // 🔑 鎖只蓋「取舊值＋排程＋寫回」三件事。Service.Framework.Run 在本 pin 一律是
    //    FrameworkThreadTaskFactory.StartNew(...)，
    //    **永遠只把工作排進佇列、不會就地執行 delegate**，所以持鎖期間不碰 ImGui、
    //    不做檔案 I/O、也不會重入這把鎖。
    private readonly object _taskChainLock = new();

    // -- 「尋路送出後遲遲沒有結果」診斷（見 ReportPathfindStall）-------------------
    // 🔴 純觀測：不取消、不重試、不動網格。
    private static readonly TimeSpan PathfindStallThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PathfindStallReportInterval = TimeSpan.FromSeconds(30);
    private DateTime? _pathfindBusySince;
    private DateTime _lastPathfindStallReport = DateTime.MinValue;

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
        ReportPathfindStall();

        var curKey = GetCurrentKey();
        var prevKey = CurrentKey;
        if (curKey != prevKey)
        {
            // navmesh needs to be reloaded
            if (!Service.Config.AutoLoadNavmesh)
            {
                if (prevKey.Length == 0)
                    return; // nothing is loaded, and auto-load is forbidden
                curKey = ""; // just unload existing mesh
            }
            Log($"Starting transition from '{prevKey}' to '{curKey}'");
            Volatile.Write(ref _currentKey, curKey);
            Reload(true);
            // mesh load is now in progress
        }
    }

    public bool Reload(bool allowLoadFromCache)
    {
        ClearState();
        if (CurrentKey.Length > 0)
        {
            var cts = new CancellationTokenSource();
            // 🔴 上一行的 ClearState() 已經把 _currentCTS 換成 null，所以**序列化執行時這個
            //    交換一定回 null**，下面那個 if 是死路 —— 加它是為了競爭時不要漏掉一個孤兒。
            // 🔑 用 Exchange 接手：語意與 ClearState 一致 —— 誰裝上新的，就負責取消前一個。
            //    Dispose 同樣排到工作佇列尾端（linked CTS 還握著對它的註冊，等佇列排空才釋放）。
            var orphan = Interlocked.Exchange(ref _currentCTS, cts);
            if (orphan != null)
            {
                // 走到這裡代表真的有兩處同時要求重新載入。診斷寫 Information（使用者跑
                // LogLevel 1），與本檔其他診斷一致；這條路徑不該常見，出現就值得追。
                Service.Log.Information(
                    "[NavmeshManager] 偵測到同時有兩處要求重新載入導航網格："
                  + "前一個生命週期 token 沒有經過 ClearState 就被取代，已就地取消它，"
                  + "避免它綁著的建置工作把過期的網格發佈出來。");
                orphan.Cancel();
                ExecuteWhenIdle(orphan.Dispose, default);
            }
            ExecuteWhenIdle(async cancel =>
            {
                Volatile.Write(ref _loadTaskProgress, 0f);

                using var resetLoadProgress = new OnDispose(() => Volatile.Write(ref _loadTaskProgress, -1f));

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
                    Volatile.Write(ref _currentKey, "");
                    return;
                }

                Log($"Kicking off build for '{cacheKey}' (reload={allowLoadFromCache})");
                var navmesh = await Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache, cancel), cancel);
                Log($"Mesh loaded: '{cacheKey}'");
                PublishMesh(navmesh, new(navmesh));
            }, cts.Token);
        }
        return true;
    }

    /// <summary>
    /// 🔴 給 IPC 的 Nav.Rebuild 專用入口：帶最小間隔節流的全量重建（不吃快取）。
    /// </summary>
    /// <returns>true 表示這次真的送出重建；false 表示被節流略過。</returns>
    public bool RebuildFromIPC()
    {
        var now = DateTime.Now;
        var since = now - _lastIPCRebuild;
        var buildInProgress = Volatile.Read(ref _loadTaskProgress) >= 0;

        if ((since < IPCRebuildMinInterval || buildInProgress) && since < IPCRebuildHardCap)
        {
            // 這支跑在 IPC 呼叫端的執行緒上(Nav.Rebuild)，兩個外掛同時打就是兩條執行緒。
            // ++ 是讀-改-寫 ⇒ 裸寫會少算。這只是診斷用的次數，但少算沒有任何好處。
            var skipped = Interlocked.Increment(ref _ipcRebuildSkipCount);
            // 診斷寫 Information（使用者跑 LogLevel 1），但節流到最多每 5 秒一行，
            // 免得呼叫端每秒打一次就把 log 洗掉。
            if ((now - _lastIPCRebuildSkipLog).TotalSeconds >= 5)
            {
                _lastIPCRebuildSkipLog = now;
                Service.Log.Information(
                    $"[NavmeshManager] 已略過外掛透過 IPC 要求的全量重建 {skipped} 次："
                  + $"距上次重建 {since.TotalSeconds:f1} 秒，未達 {IPCRebuildMinInterval.TotalSeconds:f0} 秒的最小間隔"
                  + (buildInProgress ? "，且目前仍在建置中" : "")
                  + "。全量重建期間玩家不會移動，呼叫端若以「卡住」當觸發條件會自我維持。"
                  + "使用者自己按 UI 的 Rebuild 或 /vnav rebuild 不受此限。");
                Interlocked.Exchange(ref _ipcRebuildSkipCount, 0);
            }
            return false;
        }

        _lastIPCRebuild = now;
        _lastIPCRebuildSkipLog = DateTime.MinValue; // 讓下一次被略過時立刻有一行說明，不必等 5 秒
        Interlocked.Exchange(ref _ipcRebuildSkipCount, 0);
        return Reload(false);
    }

    internal void ReplaceMesh(Navmesh mesh)
    {
        Log($"Mesh replaced");
        PublishMesh(mesh, new(mesh));
    }

    /// <summary>
    /// IPC 的 Nav.PathfindCancelAll 專用入口：**只取消進行中/排隊中的尋路，不動導航網格**。
    /// 🔴 這裡**刻意沒有任何節流**。對一個「取消」動作加節流，會讓取消靜默地不發生，
    ///    那比多做幾次工作糟得多。（有節流的是 RebuildFromIPC，那是全量重建，兩者不要混。）
    /// </summary>
    public void CancelAllPathfinds()
    {
        // 只讀一次：這支跑在 IPC 呼叫端的執行緒上，而計數是別的執行緒在遞減。
        // 舊碼讀兩次(判斷一次、印訊息時又一次)，中間遞減完就會印出「已取消 0 筆」。
        var cancelled = Volatile.Read(ref _numActivePathfinds);
        if (cancelled <= 0)
            return; // 沒有進行中或排隊中的尋路（見上面說明：這不是節流）

        // 換 CTS 是**讀-改-寫**，而這支可以被兩條 IPC 呼叫端執行緒同時踩到。
        // Interlocked.Exchange 讓「取舊值」與「寫新值」變成一步 ⇒ 每個舊 CTS 只會有
        // 一條執行緒拿到，Cancel/Dispose 也就只會各做一次。
        var cts = Interlocked.Exchange(ref _pathfindCTS, new CancellationTokenSource());
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
        var meshCTS = Volatile.Read(ref _currentCTS);
        if (meshCTS == null)
            throw new Exception($"Can't initiate query - navmesh is not loaded");

        // 工作可以被三種來源取消：網格被卸掉(_currentCTS)、外掛端要求取消全部尋路
        // (_pathfindCTS，走 CancelAllPathfinds)、呼叫端自己的 token(externalCancel)。
        // _pathfindCTS 同樣只讀一次：CancelAllPathfinds 會用 Interlocked.Exchange 換掉它，
        // 讀兩次有可能一次拿到舊的、一次拿到新的。
        var pathfindCTS = Volatile.Read(ref _pathfindCTS);
        var combined = CancellationTokenSource.CreateLinkedTokenSource(meshCTS.Token, pathfindCTS.Token, externalCancel);
        Interlocked.Increment(ref _numActivePathfinds);
        var task = ExecuteWhenIdle(async cancel =>
        {
            Log($"Kicking off pathfind from {from} to {to}");
            var path = await Task.Run(() =>
            {
                combined.Token.ThrowIfCancellationRequested();
                // 🔴 這一段跑在執行緒池上，而 ClearState 跑在框架執行緒 —— 所以只讀一次。
                //    分兩次讀（檢查一次、使用一次）時，中間被清掉就會擲 NullReferenceException，
                //    把下面那句寫給呼叫端看的說明蓋掉。取到之後那個物件是不可變的一代，繼續用它安全。
                var q = Query;
                if (q == null)
                    throw new Exception($"Can't pathfind, navmesh did not build successfully");
                Log($"Executing pathfind from {from} to {to}");
                // ⚠️ 迴避圓目前只支援地面路徑。飛行路徑要繞圓得改 VoxelPathfind,那是另一個階段的事,
                //    所以這裡**明講**它沒生效,而不是安靜地忽略參數。
                if (flying && avoidCenter != null && avoidRadius > 0)
                    Service.Log.Information($"[NavmeshManager] 飛行路徑尚未支援迴避圓(中心 {avoidCenter} 半徑 {avoidRadius:f1}),本次忽略該參數。");
                var meshFilter = !flying && avoidCenter != null && avoidRadius > 0
                    ? new NavmeshQuery.AvoidRadiusFilter(avoidCenter.Value, avoidRadius)
                    : null;
                return flying ? q.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token) : q.PathfindMesh(from, to, UseRaycasts, UseStringPulling, combined.Token, range, meshFilter);
            }, combined.Token);
            Log($"Pathfinding done: {path.Count} waypoints");
            return path;
        }, combined.Token);

        // 🔴 遞減與 linked CTS 的釋放**必須掛在工作的完成回呼上**，不能只放在 body 裡：
        //    ExecuteWhenIdle 底層是 Service.Framework.Run(...) ＝ TaskFactory.StartNew(delegate, token)，
        //    而 TPL 在工作真正開始執行前發現 token 已取消時，會把工作直接標成 Canceled 而
        //    **完全不執行 delegate** ⇒ 原本寫在 body 裡的 OnDispose 永遠不會跑。
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
    // 🔴 IPC 的 Nav.BuildBitmap / Nav.BuildBitmapBounded 會從**呼叫端的執行緒**進來，而這支要
    //    同時用到網格與查詢物件 ⇒ 一開始就把整代抓進區域變數，之後全程用那一份。
    public (Vector3 min, Vector3 max) BuildBitmap(Vector3 startingPos, string filename, float pixelSize, AABB? mapBounds = null)
    {
        var gen = Current;
        if (gen.Mesh is not { } navmesh || gen.Query is not { } query)
            throw new InvalidOperationException($"Can't build bitmap - navmesh creation is in progress");

        bool inBounds(Vector3 vert) => mapBounds is not AABB aabb || vert.X >= aabb.Min.X && vert.Y >= aabb.Min.Y && vert.Z >= aabb.Min.Z && vert.X <= aabb.Max.X && vert.Y <= aabb.Max.Y && vert.Z <= aabb.Max.Z;

        var startPoly = query.FindNearestMeshPoly(startingPos);
        var reachablePolys = query.FindReachableMeshPolys(startPoly);

        HashSet<long> polysInbounds = [];

        Vector3 min = new(1024), max = new(-1024);
        foreach (var p in reachablePolys)
        {
            navmesh.Mesh.GetTileAndPolyByRefUnsafe(p, out var tile, out var poly);
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
            bitmap.RasterizePolygon(navmesh.Mesh, p);
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
        // 🔑 Interlocked.Exchange 把「取舊值」與「寫 null」合成一步 ⇒ 舊 CTS 只會有一條
        //    執行緒拿到，Cancel/Dispose 也就各只做一次。
        //    ⚠️ 語意**沒有**改成「兩邊都取消」：仍然是「第一個到的人負責取消，其他人提前
        //    返回」，只是「第一個」的裁決權從「誰先讀到非 null」換成「誰贏得原子交換」。
        var cts = Interlocked.Exchange(ref _currentCTS, null);
        if (cts == null)
            return; // already cleared

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
            PublishMesh(null, null);
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
            AddLoadProgress(deltaProgress);
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

    // 見 _taskChainLock 的說明：三個多載的讀-改-寫都必須在同一把鎖裡。
    private void ExecuteWhenIdle(Action task, CancellationToken token)
    {
        lock (_taskChainLock)
        {
            var prev = _lastLoadQueryTask;
            _lastLoadQueryTask = Service.Framework.Run(async () =>
            {
                await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                _ = prev.Exception;
                task();
            }, token);
        }
    }

    private void ExecuteWhenIdle(Func<CancellationToken, Task> task, CancellationToken token)
    {
        lock (_taskChainLock)
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
    }

    private Task<T> ExecuteWhenIdle<T>(Func<CancellationToken, Task<T>> task, CancellationToken token)
    {
        lock (_taskChainLock)
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
    }

    /// <summary>
    /// 「尋路送出後遲遲沒有結果」的診斷。每幀由框架執行緒呼叫(見 Update)。
    /// </summary>
    private void ReportPathfindStall()
    {
        var active = Volatile.Read(ref _numActivePathfinds);
        if (active <= 0)
        {
            _pathfindBusySince = null;
            _lastPathfindStallReport = DateTime.MinValue; // 下一次卡住立刻有一行，不必等節流窗
            return;
        }

        var now = DateTime.Now;
        _pathfindBusySince ??= now;
        var stalled = now - _pathfindBusySince.Value;
        if (stalled < PathfindStallThreshold || now - _lastPathfindStallReport < PathfindStallReportInterval)
            return;
        _lastPathfindStallReport = now;

        var progress = Volatile.Read(ref _loadTaskProgress);
        var progressStr = progress < 0 ? "未在建置" : $"建置中 {progress * 100:f0}%";
        var meshStr = Navmesh != null ? "已載入" : "未載入";
        Service.Log.Information(
            $"[vnav卡住診斷] 尋路佇列已經 {stalled.TotalSeconds:f0} 秒沒有排空：進行中＋排隊中共 {active} 筆"
          + $"(其中排隊 {active - 1} 筆)。導航網格={meshStr}，區域鍵「{CurrentKey}」，{progressStr}，"
          + $"過場動畫中={InCutscene}。"
          + $"⇒ 顯示「建置中」時尋路是排在建置後面等，屬正常；顯示「未在建置」而這一行持續出現，"
          + $"代表尋路工作本身沒有完成(飛行路徑在大區域可能真的要跑很久)。");
    }

    private static void Log(string message) => Service.Log.Debug($"[NavmeshManager] [{Thread.CurrentThread.ManagedThreadId}] {message}");
    private static void LogTaskError(Task task)
    {
        if (task.IsFaulted)
            Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
    }
}
