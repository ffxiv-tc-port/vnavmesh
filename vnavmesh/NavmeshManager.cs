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
    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    private int _numActivePathfinds;
    public bool PathfindInProgress => _numActivePathfinds > 0;
    public int NumQueuedPathfindRequests => _numActivePathfinds > 0 ? _numActivePathfinds - 1 : 0;

    private DirectoryInfo _cacheDir;

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

    internal void ReplaceMesh(Navmesh mesh)
    {
        Log($"Mesh replaced");
        Navmesh = mesh;
        Query = new(Navmesh);
        OnNavmeshChanged?.Invoke(Navmesh, Query);
    }

    private static bool InCutscene => Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    // ⚠️ 參數順序刻意與上游一致(range 在 externalCancel 之前),讓日後追上游不必再改一次。
    //    順序換了但型別不相容(float vs CancellationToken),舊的位置引數呼叫會編譯失敗而不是靜默錯位。
    public Task<List<Waypoint>> QueryPath(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        if (_currentCTS == null)
            throw new Exception($"Can't initiate query - navmesh is not loaded");

        // task can be cancelled either by internal request (i.e. when navmesh is reloaded) or external
        var combined = CancellationTokenSource.CreateLinkedTokenSource(_currentCTS.Token, externalCancel);
        ++_numActivePathfinds;
        return ExecuteWhenIdle(async cancel =>
        {
            using var autoDisposeCombined = combined;
            using var autoDecrementCounter = new OnDispose(() => --_numActivePathfinds);
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
            _numActivePathfinds = 0;
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
