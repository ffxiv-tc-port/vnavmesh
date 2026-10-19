using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh.Debug;

class DebugNavmeshCustom : IDisposable
{
    private record struct HeightfieldComparison(float DurationOld, float DurationNew, bool Identical);

    public class Customization : NavmeshCustomization
    {
        public bool Flyable;
        public bool LoadExisting = true;

        public override int Version => 1;
        public override bool IsFlyingSupported(SceneDefinition definition) => Flyable;

        private static NavmeshCustomization? Existing => NavmeshCustomizationRegistry.ForTerritory(Service.ClientState.TerritoryType) is { } t && t.Version > 0 ? t : null;

        public override void CustomizeScene(SceneExtractor scene)
        {
            if (LoadExisting)
                Existing?.CustomizeScene(scene);
        }

        public override void CustomizeSettings(DtNavMeshCreateParams config)
        {
            if (LoadExisting)
                Existing?.CustomizeSettings(config);
        }

        public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
        {
            if (LoadExisting && Existing is { } existing)
            {
                existing.CurrentTerritory = Service.ClientState.TerritoryType; // 供 LinkPoints 產生捷徑識別鍵（Existing getter 本來就用同一來源查表）
                existing.CurrentScene = CurrentScene; // 與 NavmeshManager 那一側對齊；漏掉會讓場景偵測靜默失效
                existing.CustomizeMesh(mesh, festivalLayers);
            }
        }
    }

    // async navmesh builder
    public class AsyncBuilder(NavmeshManager manager) : IDisposable
    {
        public enum State { NotBuilt, InProgress, Failed, Ready }

        public class IntermediateData
        {
            public int NumTilesX;
            public int NumTilesZ;
            public RcBuilderResult?[,] Tiles;
            // 建了圖塊但沒保留各塊中間資料（Tiles 全空）；與「只重新擷取場景」分開，畫面上要說明。
            public bool TilesDiscarded;

            public IntermediateData(int numTilesX, int numTilesZ)
            {
                NumTilesX = numTilesX;
                NumTilesZ = numTilesZ;
                Tiles = new RcBuilderResult?[numTilesX, numTilesZ];
            }
        }

        // results - should not be accessed while task is running
        private SceneDefinition? _scene;
        private NavmeshBuilder? _builder;
        private NavmeshQuery? _query;
        private IntermediateData? _intermediates;
        private Task? _task;
        private NavmeshManager _manager = manager;

        public State CurrentState => _task == null ? State.NotBuilt : !_task.IsCompleted ? State.InProgress : _task.IsFaulted ? State.Failed : State.Ready;
        public SceneDefinition? Scene => _task != null && _task.IsCompletedSuccessfully ? _scene : null;
        public SceneExtractor? Extractor => _task != null && _task.IsCompletedSuccessfully ? _builder?.Scene : null;
        public IntermediateData? Intermediates => _task != null && _task.IsCompletedSuccessfully ? _intermediates : null;
        public NavmeshQuery? Query => _task != null && _task.IsCompletedSuccessfully ? _query : null;
        public DtNavMesh? Navmesh => _task != null && _task.IsCompletedSuccessfully ? _builder?.Navmesh.Mesh : null;
        public DtNavMeshQuery? MeshQuery => _task != null && _task.IsCompletedSuccessfully ? _query?.MeshQuery : null;
        public VoxelMap? Volume => _task != null && _task.IsCompletedSuccessfully ? _builder?.Navmesh.Volume : null;
        public VoxelPathfind? VolumeQuery => _task != null && _task.IsCompletedSuccessfully ? _query?.VolumeQuery : null;

        public void Dispose()
        {
            Clear();
        }

        public void Rebuild(Customization settings, bool includeTiles, bool keepIntermediates)
        {
            Clear();
            Service.Log.Debug("[navmesh] extract from scene");
            _scene = new();
            _scene.FillFromActiveLayout();
            Service.Log.Debug("[navmesh] schedule async build");
            _task = Task.Run(() => BuildNavmesh(_scene, settings, includeTiles, keepIntermediates));
        }

        public void Clear()
        {
            if (_task != null)
            {
                // Normally reached only once the build has already finished (the "Rebuild"
                // buttons are disabled while InProgress), but bound the wait defensively so an
                // unexpected mid-build call (e.g. window close) can't freeze the game
                // indefinitely - fields are only safe to touch once the task has stopped writing
                // to them, so on timeout we leave everything in place rather than race it.
                if (!_task.IsCompleted && !_task.Wait(TimeSpan.FromSeconds(5)))
                {
                    Service.Log.Warning("[navmesh] Timed out waiting for in-progress build to finish; leaving previous state in place");
                    return;
                }
                _task.Dispose();
                _task = null;
            }
            _scene = null;
            _builder = null;
            _query = null;
            _intermediates = null;
            //GC.Collect();
        }

        private void BuildNavmesh(SceneDefinition scene, NavmeshCustomization customization, bool includeTiles, bool keepIntermediates)
        {
            try
            {
                var timer = Timer.Create();
                _builder = new(scene, customization);

                List<((int X, int Z), Task<NavmeshBuilder.Intermediates> Task)> _tiles = [];

                // create tile data and add to navmesh
                _intermediates = new(_builder.NumTilesX, _builder.NumTilesZ);
                if (includeTiles)
                {
                    // 不保留時走與網格管理器相同的低配置路徑（回傳空清單），建出的網格不變。
                    foreach (var result in _builder.BuildTiles(keepIntermediates: keepIntermediates))
                        _intermediates.Tiles[result.tileX, result.tileZ] = result;
                    _intermediates.TilesDiscarded = !keepIntermediates;

                    //int x = 9, z = 15;
                    //_intermediates.Tiles[x, z] = _builder.BuildTile(x, z);
                    Service.Log.Debug("running customization code");
                    // 與 NavmeshManager.BuildNavmesh 那一側對齊：場景偵測型的自訂化靠這個欄位，
                    // 漏設的話偵錯建置器會靜默地全部退回 DevGrade fallback。
                    customization.CurrentScene = scene;
                    customization.CustomizeMesh(_builder.Navmesh, [.. scene.FestivalLayers]);
                }

                _query = new(_builder.Navmesh);
                Service.Log.Debug($"navmesh build time: {timer.Value().TotalMilliseconds}ms");
                _manager.ReplaceMesh(_builder.Navmesh);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"Error building navmesh: {ex}");
                throw;
            }
        }
    }

    // TODO: should each debug drawer handle tiled geometry itself?
    private class PerTile : IDisposable
    {
        public DebugSolidHeightfield? DrawSolidHeightfield;
        public DebugCompactHeightfield? DrawCompactHeightfield;
        public DebugContourSet? DrawContourSet;
        public DebugPolyMesh? DrawPolyMesh;
        public DebugPolyMeshDetail? DrawPolyMeshDetail;
        public HeightfieldComparison? HFC;

        public void Dispose()
        {
            DrawSolidHeightfield?.Dispose();
            DrawCompactHeightfield?.Dispose();
            DrawContourSet?.Dispose();
            DrawPolyMesh?.Dispose();
            DrawPolyMeshDetail?.Dispose();
        }
    }

    private Customization _settings = new();
    private bool _keepIntermediates; // 不存檔；關＝與網格管理器相同的低配置建置
    private AsyncBuilder _navmesh;
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private DebugExtractedCollision? _drawExtracted;
    private HeightfieldComparison? _globalHFC;
    private PerTile[,]? _debugTiles;

    private Vector3 _dest = new();

    private string _configDirectory;

    public DebugNavmeshCustom(DebugDrawer dd, DebugGameCollision coll, NavmeshManager manager, string configDir)
    {
        _dd = dd;
        _coll = coll;
        _navmesh = new(manager);
        _configDirectory = configDir;
    }

    public void Dispose()
    {
        _drawExtracted?.Dispose();
        if (_debugTiles != null)
            foreach (var t in _debugTiles)
                t?.Dispose();
    }

    public void Draw()
    {
        using (var nsettings = _tree.Node("Navmesh properties".Loc()))
        {
            if (nsettings.Opened)
            {
                ImGui.Checkbox("Support flying".Loc(), ref _settings.Flyable);
                ImGui.Checkbox("Load existing territory customization".Loc(), ref _settings.LoadExisting);
                _settings.Settings.Draw();
            }
        }

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == AsyncBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh".Loc()))
            {
                Clear();
                _navmesh.Rebuild(_settings, true, _keepIntermediates);
            }
            DrawItemTooltip("Builds the current zone with the Navmesh properties above, for debugging: the result replaces the loaded mesh but is not written to the cache.\nFor a normal rebuild of the current zone, use Rebuild on the Navmesh manager tab.".Loc());
            ImGui.SameLine();
            if (ImGui.Button("Rebuild scene extract only".Loc()))
            {
                Clear();
                _navmesh.Rebuild(_settings, false, false);
            }
            ImGui.SameLine();
            // 鍵就是原本畫出來的英文（State: NotBuilt／InProgress／Failed／Ready），沒有翻譯時顯示不變。
            ImGui.TextUnformatted($"State: {_navmesh.CurrentState}".Loc());
            ImGui.Checkbox("Keep intermediates (debug)".Loc(), ref _keepIntermediates);
            DrawItemTooltip("When ticked, the next rebuild keeps every tile's heightfields, contours and polygon meshes so they can be inspected under Intermediates below. On large zones this takes several GB of extra memory and slows the build down; the data stays in memory until the next rebuild or plugin reload.\nLeave it off for a normal build: the resulting navmesh is the same either way.".Loc());
        }

        if (_navmesh.CurrentState != AsyncBuilder.State.Ready)
            return;

        ImGui.InputFloat("X", ref _dest.X);
        ImGui.InputFloat("Y", ref _dest.Y);
        ImGui.InputFloat("Z", ref _dest.Z);
        if (ImGui.Button("Pathfind".Loc()))
        {
            var player = Service.ObjectTable.LocalPlayer;
            var playerPos = player?.Position ?? default;
            _navmesh.Query!.PathfindMesh(playerPos, _dest, true, true, new());
        }

        var navmesh = _navmesh.Navmesh!;
        navmesh.CalcTileLoc((Service.ObjectTable.LocalPlayer?.Position ?? default).SystemToRecast(), out var playerTileX, out var playerTileZ);
        _tree.LeafNode("Player tile: ??x??".Loc(playerTileX, playerTileZ));

        _drawExtracted ??= new(_navmesh.Scene!, _navmesh.Extractor!, _tree, _dd, _coll, _configDirectory);
        _drawExtracted.Draw();
        var intermediates = _navmesh.Intermediates;
        if (intermediates != null)
        {
            using var n = _tree.Node("Intermediates".Loc());
            if (n.Opened)
            {
                _debugTiles ??= new PerTile[intermediates.NumTilesX, intermediates.NumTilesZ];
                using (var ng = _tree.Node("Global".Loc()))
                {
                    if (ng.Opened)
                    {
                        _globalHFC ??= CompareAllHeightfields(_navmesh.Extractor!);
                        DrawHeightfieldComparison(_globalHFC.Value);
                    }
                }

                if (intermediates.TilesDiscarded)
                {
                    using var wrap = ImRaii.TextWrapPos(0);
                    ImGui.TextDisabled("This build did not keep per-tile intermediates. Tick \"Keep intermediates (debug)\" and rebuild to inspect individual tiles.".Loc());
                }

                // 每個圖塊一個節點，只在迴圈外查一次翻譯。
                var tileWord = "Tile".Loc();
                for (int z = 0; z < intermediates.NumTilesZ; ++z)
                {
                    for (int x = 0; x < intermediates.NumTilesX; ++x)
                    {
                        var inter = intermediates.Tiles[x, z];
                        if (inter == null)
                            continue;

                        using var nt = _tree.Node($"{tileWord} {x}x{z}");
                        if (!nt.Opened)
                            continue;

                        var debug = _debugTiles[x, z] ??= new();
                        debug.DrawSolidHeightfield ??= new(inter.GetSolidHeightfield(), _tree, _dd);
                        debug.DrawSolidHeightfield.Draw();
                        debug.DrawCompactHeightfield ??= new(inter.GetCompactHeightfield(), _tree, _dd);
                        debug.DrawCompactHeightfield.Draw();
                        debug.DrawContourSet ??= new(inter.GetContourSet(), _tree, _dd);
                        debug.DrawContourSet.Draw();
                        debug.DrawPolyMesh ??= new(inter.GetMesh(), _tree, _dd);
                        debug.DrawPolyMesh.Draw();
                        if (inter.GetMeshDetail() is { } det)
                        {
                            debug.DrawPolyMeshDetail ??= new(det, _tree, _dd);
                            debug.DrawPolyMeshDetail.Draw();
                        }

                        using (var nhfc = _tree.Node("HF comparison".Loc()))
                        {
                            if (nhfc.Opened)
                            {
                                debug.HFC ??= CompareHeightfields(x, z, _navmesh.Extractor!);
                                DrawHeightfieldComparison(debug.HFC.Value);
                            }
                        }
                    }
                }
            }
        }

        using var dt = _tree.Node("Detour navmesh".Loc());
        if (dt.Opened)
            _tree.LeafNode("Loaded mesh replaced with custom build, check Navmesh Manager tab".Loc());
    }

    // 建置中整排停用，說明仍要看得到；停用時 Style.Alpha 被調淡，只在停用時推回全亮。
    private static void DrawItemTooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f, !ImGui.IsItemHovered());
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextUnformatted(text);
    }

    // 鍵與原本畫出來的英文逐字相同（數字照舊用目前文化的 f3），沒有翻譯時顯示不變。
    private void DrawHeightfieldComparison(HeightfieldComparison hfc)
    {
        _tree.LeafNode("Old: ??".Loc(hfc.DurationOld.ToString("f3")));
        _tree.LeafNode("New: ??".Loc(hfc.DurationNew.ToString("f3")));
        _tree.LeafNode($"Match: {hfc.Identical}".Loc());
    }

    private void Clear()
    {
        _drawExtracted?.Dispose();
        _drawExtracted = null;
        _globalHFC = null;
        if (_debugTiles != null)
            foreach (var t in _debugTiles)
                t?.Dispose();
        _debugTiles = null;
        _navmesh.Clear();
    }

    private HeightfieldComparison CompareHeightfields(int tx, int tz, SceneExtractor scene)
    {
        var telemetry = new RcContext();
        var boundsMin = new Vector3(-1024);
        var boundsMax = new RcVec3f(1024);
        var numTilesXZ = _settings.Settings.NumTiles[0];
        var tileWidth = (boundsMax.X - boundsMin.X) / numTilesXZ;
        var tileHeight = (boundsMax.Z - boundsMin.Z) / numTilesXZ;
        var walkableClimbVoxels = (int)MathF.Floor(_settings.Settings.AgentMaxClimb / _settings.Settings.CellHeight);
        var walkableRadiusVoxels = (int)MathF.Ceiling(_settings.Settings.AgentRadius / _settings.Settings.CellSize);
        var walkableNormalThreshold = _settings.Settings.AgentMaxSlopeDeg.Degrees().Cos();
        var borderSizeVoxels = 3 + walkableRadiusVoxels;
        var borderSizeWorld = borderSizeVoxels * _settings.Settings.CellSize;
        var tileSizeXVoxels = (int)MathF.Ceiling(tileWidth / _settings.Settings.CellSize) + 2 * borderSizeVoxels;
        var tileSizeZVoxels = (int)MathF.Ceiling(tileHeight / _settings.Settings.CellSize) + 2 * borderSizeVoxels;
        var tileBoundsMin = new Vector3(boundsMin.X + tx * tileWidth, boundsMin.Y, boundsMin.Z + tz * tileHeight);
        var tileBoundsMax = new Vector3(tileBoundsMin.X + tileWidth, boundsMax.Y, tileBoundsMin.Z + tileHeight);
        tileBoundsMin.X -= borderSizeWorld;
        tileBoundsMin.Z -= borderSizeWorld;
        tileBoundsMax.X += borderSizeWorld;
        tileBoundsMax.Z += borderSizeWorld;

        var timer = Timer.Create();
        var shfOld = new RcHeightfield(tileSizeXVoxels, tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), _settings.Settings.CellSize, _settings.Settings.CellHeight, borderSizeVoxels);
        var rasterizerOld = new NavmeshRasterizer(shfOld, walkableNormalThreshold, walkableClimbVoxels, 0, false, null, telemetry);
        rasterizerOld.RasterizeOld(scene, SceneExtractor.MeshType.All);
        var dur1 = (float)timer.Value().TotalSeconds;

        var shfNew = new RcHeightfield(tileSizeXVoxels, tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), _settings.Settings.CellSize, _settings.Settings.CellHeight, borderSizeVoxels);
        var rasterizerNew = new NavmeshRasterizer(shfNew, walkableNormalThreshold, walkableClimbVoxels, 0, false, null, telemetry);
        rasterizerNew.Rasterize(scene, SceneExtractor.MeshType.All, false, false);
        var dur2 = (float)timer.Value().TotalSeconds;

        bool identical = true;
        int ispan = 0;
        for (int z = 0; z < tileSizeZVoxels; ++z)
        {
            for (int x = 0; x < tileSizeXVoxels; ++x)
            {
                var so = shfOld.spans[ispan];
                var sn = shfNew.spans[ispan];
                while (so != 0 && sn != 0)
                {
                    ref var spanOld = ref shfOld.Span(so);
                    ref var spanNew = ref shfNew.Span(sn);
                    identical &= (spanOld.smin == spanNew.smin) && (spanOld.smax == spanNew.smax) && (spanOld.area == spanNew.area);
                    so = spanOld.next;
                    sn = spanNew.next;
                }
                identical &= (so == 0) && (sn == 0);
                ispan++;
            }
        }
        return new(dur1, dur2, identical);
    }

    private HeightfieldComparison CompareAllHeightfields(SceneExtractor scene)
    {
        float dur1 = 0, dur2 = 0;
        bool identical = true;
        var numTilesXZ = _settings.Settings.NumTiles[0];
        for (int tz = 0; tz < numTilesXZ; ++tz)
        {
            for (int tx = 0; tx < numTilesXZ; ++tx)
            {
                var hfc = CompareHeightfields(tx, tz, scene);
                dur1 += hfc.DurationOld;
                dur2 += hfc.DurationNew;
                identical &= hfc.Identical;
            }
        }
        return new(dur1, dur2, identical);
    }
}
