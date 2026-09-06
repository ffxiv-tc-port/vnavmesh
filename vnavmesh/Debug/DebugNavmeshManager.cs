using Dalamud.Bindings.ImGui;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

class DebugNavmeshManager : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _path;
    private AsyncMoveRequest _asyncMove;
    private DTRProvider _dtr;
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private Vector3 _target;

    private DebugDetourNavmesh? _drawNavmesh;
    private DebugVoxelMap? _debugVoxelMap;

    public DebugNavmeshManager(DebugDrawer dd, DebugGameCollision coll, NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr)
    {
        _manager = manager;
        _path = path;
        _asyncMove = move;
        _dtr = dtr;
        _dd = dd;
        _coll = coll;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
    }

    public void Dispose()
    {
        _manager.OnNavmeshChanged -= OnNavmeshChanged;
        _drawNavmesh?.Dispose();
        _debugVoxelMap?.Dispose();
    }

    public void Draw()
    {
        var progress = _manager.LoadTaskProgress;
        if (progress >= 0)
        {
            ImGui.ProgressBar(progress, new Vector2(200, 0));
        }
        else
        {
            ImGui.SetNextItemWidth(100);
            if (ImGui.Button("Reload"))
                _manager.Reload(true);
            ImGui.SameLine();
            if (ImGui.Button("Rebuild"))
                _manager.Reload(false);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(_manager.CurrentKey);
        ImGui.TextUnformatted($"Num pathfinding tasks: {(_manager.PathfindInProgress ? 1 : 0)} in progress, {_manager.NumQueuedPathfindRequests} queued");

        if (_manager.Navmesh == null || _manager.Query == null)
            return;

        var player = Service.ObjectTable.LocalPlayer;
        var playerPos = player?.Position ?? default;
        ImGui.TextUnformatted($"Player pos: {playerPos}");
        if (ImGui.Button("Set target to current pos"))
            _target = player?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to target pos"))
            _target = player?.TargetObject?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to flag position"))
            _target = MapUtils.FlagToPoint(_manager.Query) ?? default;
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current target: {_target}");

        if (ImGui.Button("Export bitmap"))
            ExportBitmap(_manager.Navmesh, _manager.Query, playerPos);

        ImGui.Checkbox("Allow movement", ref _path.MovementAllowed);
        DrawMovementLeaseMarker();
        ImGui.Checkbox("Use raycasts", ref _manager.UseRaycasts);
        ImGui.Checkbox("Use string pulling", ref _manager.UseStringPulling);
        if (ImGui.Button("Pathfind to target using navmesh"))
            _asyncMove.MoveTo(_target, false);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _asyncMove.MoveTo(_target, true);

        DrawPosition("Player", playerPos);
        DrawPosition("Target", _target);
        DrawPosition("Flag", MapUtils.FlagToPoint(_manager.Query) ?? default);
        DrawPosition("Floor", _manager.Query.FindPointOnFloor(playerPos) ?? default);

        _drawNavmesh ??= new(_manager.Navmesh.Mesh, _manager.Query.MeshQuery, _manager.Query.LastPath, _tree, _dd);
        _drawNavmesh.Draw();
        if (_manager.Navmesh.Volume != null)
        {
            _debugVoxelMap ??= new(_manager.Navmesh.Volume, _manager.Query.VolumeQuery, _tree, _dd);
            _debugVoxelMap.Draw();
        }
    }

    // 有租約押著移動開關時，在勾勾右邊放一個灰字標記。
    // 🔑「另一個外掛正在讓角色停住」本身要在列上看得見 —— tooltip 藏的是「是誰、還剩多久」，
    //    不是「有沒有問題」。把它藏起來就退回這整份改動要修掉的那個靜默失效。
    // 🔴 鎖內只拍快照（MovementLeases.Snapshot），所有 ImGui 呼叫都在鎖外。
    private static void DrawMovementLeaseMarker()
    {
        if (!MovementLeases.AnyActive)
            return;

        var snapshot = MovementLeases.Snapshot();
        var denying = snapshot.Where(x => x.MovementAllowed == false).ToArray();
        if (denying.Length == 0)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled("(lease)");
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("另一個外掛正透過移動租約要求 vnavmesh 暫時不要移動角色。");
        foreach (var (owner, remainingMs, _, _) in denying)
            ImGui.TextUnformatted($"  {owner}：還有 {remainingMs / 1000.0:f0} 秒自動解除");
        ImGui.TextUnformatted("租約逾時或被放開之後會自動恢復，不需要重載外掛。");
        ImGui.EndTooltip();
    }

    private void DrawPosition(string tag, Vector3 position)
    {
        _manager.Navmesh!.Mesh.CalcTileLoc(position.SystemToRecast(), out var tileX, out var tileZ);
        _tree.LeafNode($"{tag} position: {position:f3}, tile: {tileX}x{tileZ}, poly: {_manager.Query!.FindNearestMeshPoly(position):X}");
        var voxel = _manager.Query.FindNearestVolumeVoxel(position);
        if (_tree.LeafNode($"{tag} voxel: {voxel:X}###{tag}voxel").SelectedOrHovered && voxel != VoxelMap.InvalidVoxel)
            _debugVoxelMap?.VisualizeVoxel(voxel);
    }

    private void ExportBitmap(Navmesh navmesh, NavmeshQuery query, Vector3 startingPos)
    {
        _manager.BuildBitmap(startingPos, "D:\\navmesh.bmp", 0.5f);
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
    }
}
