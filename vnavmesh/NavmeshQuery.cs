using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Navmesh;

public class NavmeshQuery
{
    private class IntersectQuery : IDtPolyQuery
    {
        public readonly List<long> Result = new();
        public void Process(DtMeshTile tile, DtPoly poly, long refs) => Result.Add(refs);
    }

    private class ToleranceHeuristic(float tolerance) : IDtQueryHeuristic
    {
        float IDtQueryHeuristic.GetCost(RcVec3f neighbourPos, RcVec3f endPos)
        {
            var dist = RcVec3f.Distance(neighbourPos, endPos) * DtDefaultQueryHeuristic.H_SCALE;
            return dist < tolerance ? -1 : dist;
        }
    }

    // 上游把這個類別從 TeleportingQueryFilter 改名為 TeleportAwareFilter 並改成 public
    // (AvoidRadiusFilter 要繼承它)。
    //
    // 📌 成本倍率的語意:一律「調高一般連結的成本」而不是「調低 off-mesh 連結的成本」——
    //    調低會干擾 A* 的啟發式(啟發式假設成本不低於直線距離)。
    //
    // ⚠️ 上游把一般連結的倍率從 3 改成 10,並依連結種類給 off-mesh 不同倍率。
    //    真正影響路線的是**比值**不是絕對值,實際變化比數字看起來小:
    //      舊:一般 3 : off-mesh 1                       = 3.00 : 1
    //      新:一般 10 : ClientPath 3                    = 3.33 : 1   <- 我方既有連結全是這一種
    //          一般 10 : Shortcut 8                     = 1.25 : 1
    //          一般 10 : Warp 1                         = 10.0 : 1
    //    我方目前所有自訂連結都走 LinkPoints 的預設 areaId(ClientPath),所以既有路線的
    //    權重只從 3.00 動到 3.33。Shortcut/Warp 兩種倍率在我方還沒有任何呼叫端用到。
    //    🔴 這仍然是全域尋路權重變更,無法離線證明對台服 52 個區域的既有路線沒有影響。
    public class TeleportAwareFilter : IDtQueryFilter
    {
        private readonly DtQueryDefaultFilter _f = new();

        public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        {
            var cst = _f.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);

            var costMulti = 10f;

            var curArea = (Navmesh.AreaId)curPoly.GetArea();
            var nextArea = (Navmesh.AreaId)(nextPoly?.GetArea() ?? 0);

            // 兩者只差 Endpoint 這一個位 ⇒ 這一步正好是在跨越連結本身(起點多邊形 -> 終點多邊形)
            if ((curArea ^ nextArea) == Navmesh.AreaId.Endpoint)
                costMulti = curArea switch
                {
                    Navmesh.AreaId.Warp => 1,
                    Navmesh.AreaId.ClientPath => 3,
                    Navmesh.AreaId.Shortcut => 8,
                    _ => costMulti
                };

            return cst * costMulti;
        }

        public virtual bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) => true;
    }

    // 排除任何與 XZ 平面上一個圓相交的多邊形。起點/終點必須在圓外,否則 FindPath 會失敗。
    public class AvoidRadiusFilter(Vector3 center, float radius) : TeleportAwareFilter
    {
        private readonly float _cx = center.X;
        private readonly float _cz = center.Z;
        private readonly float _radius = radius;

        public override bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
        {
            if (poly.vertCount == 0)
                return true;

            float sumX = 0, sumZ = 0;
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var vi = poly.verts[i] * 3;
                sumX += tile.data.verts[vi];
                sumZ += tile.data.verts[vi + 2];
            }
            var inv = 1f / poly.vertCount;
            var pcx = sumX * inv;
            var pcz = sumZ * inv;

            float extentSq = 0;
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var vi = poly.verts[i] * 3;
                var dx = tile.data.verts[vi] - pcx;
                var dz = tile.data.verts[vi + 2] - pcz;
                extentSq = MathF.Max(extentSq, dx * dx + dz * dz);
            }

            var distX = pcx - _cx;
            var distZ = pcz - _cz;
            var dist = MathF.Sqrt(distX * distX + distZ * distZ);
            // 把多邊形當成以其重心為中心的圓盤處理
            return dist >= _radius + MathF.Sqrt(extentSq);
        }
    }

    // XZ 平面上,線段 from->to 是否比允許的距離更靠近圓心。
    // ⚠️ 允許距離取 min(radius, 起點到圓心的距離):**呼叫端有可能本來就站在圓內**,
    //    這時只要沒有比出發時更靠近圓心就不算「進入」,否則永遠回 true、動彈不得。
    //    (照直覺寫成「線段到圓心的距離 < radius」會正好在這個情況下給出相反的答案。)
    public static bool SegmentEntersAvoid(Vector3 from, Vector3 to, Vector3 center, float radius)
    {
        var abx = to.X - from.X;
        var abz = to.Z - from.Z;
        var lenSq = abx * abx + abz * abz;
        float t;
        if (lenSq < 1e-6f)
            t = 0;
        else
        {
            t = ((center.X - from.X) * abx + (center.Z - from.Z) * abz) / lenSq;
            t = Math.Clamp(t, 0f, 1f);
        }
        var dx = from.X + abx * t - center.X;
        var dz = from.Z + abz * t - center.Z;
        var fromDx = from.X - center.X;
        var fromDz = from.Z - center.Z;
        var minAllowedSq = MathF.Min(radius * radius, fromDx * fromDx + fromDz * fromDz);
        return dx * dx + dz * dz + 1e-3f < minAllowedSq;
    }

    public DtNavMeshQuery MeshQuery;
    public VoxelPathfind? VolumeQuery;
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    private readonly IDtQueryFilter _pathFilter = new TeleportAwareFilter();

    public List<long> LastPath => _lastPath;
    private List<long> _lastPath = [];

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh/*, s => Service.Log.Debug(s)*/);
        if (navmesh.Volume != null)
            VolumeQuery = new(navmesh.Volume);
    }

    // filter 給 null 就用預設的 TeleportAwareFilter;傳 AvoidRadiusFilter 進來即可繞開一個圓。
    public List<Waypoint> PathfindMesh(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel, float range = 0, IDtQueryFilter? filter = null)
    {
        var startRef = FindNearestMeshPoly(from);
        var endRef = FindNearestMeshPoly(to);
        Service.Log.Debug($"[pathfind] poly {startRef:X} -> {endRef:X}");
        if (startRef == 0 || endRef == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find polygon on a mesh");
            return new();
        }

        var timer = Timer.Create();
        _lastPath.Clear();
        var opt = new DtFindPathOption(range > 0 ? new ToleranceHeuristic(range) : DtDefaultQueryHeuristic.Default, useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? 5 : 0);
        MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), filter ?? _pathFilter, ref _lastPath, opt);
        if (_lastPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find path on mesh");
            return new();
        }
        Service.Log.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", _lastPath.Select(r => r.ToString("X")))}");

        // In case of partial path, make sure the end point is clamped to the last polygon.
        var endPos = to.SystemToRecast();
        //if (polysPath.Last() != endRef)
        //    if (MeshQuery.ClosestPointOnPoly(polysPath.Last(), endPos, out var closest, out _).Succeeded())
        //        endPos = closest;

        if (useStringPulling)
        {
            var straightPath = new List<DtStraightPath>();
            var success = MeshQuery.FindStraightPath(from.SystemToRecast(), endPos, _lastPath, ref straightPath, 1024, 0);
            if (success.Failed())
                Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");
            var res = straightPath.Select(p => new Waypoint(p.pos.RecastToSystem(), GetAreaId(p.refs))).ToList();
            res.Add(new(endPos.RecastToSystem()));
            return res;
        }
        else
        {
            var res = _lastPath.Select(r => new Waypoint(MeshQuery.GetAttachedNavMesh().GetPolyCenter(r).RecastToSystem(), GetAreaId(r))).ToList();
            res.Add(new(endPos.RecastToSystem()));
            return res;
        }
    }

    // 路徑點所在多邊形的 area id。自訂連結的兩端會帶 ClientPath / ClientPathEnd,
    // FollowPath 靠它決定「走到這裡要不要停下來等客戶端把路徑播完」。
    // 一般地形多邊形回 0(AreaId.None),CheckCondition 對它回 false = 照一般走路。
    private Navmesh.AreaId GetAreaId(long refs)
    {
        MeshQuery.GetAttachedNavMesh().GetPolyArea(refs, out var area);
        return (Navmesh.AreaId)area;
    }

    public List<Waypoint> PathfindVolume(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel)
    {
        if (VolumeQuery == null)
        {
            Service.Log.Error($"Nav volume was not built");
            return new();
        }

        var startVoxel = FindNearestVolumeVoxel(from);
        var endVoxel = FindNearestVolumeVoxel(to);
        Service.Log.Debug($"[pathfind] voxel {startVoxel:X} -> {endVoxel:X}");
        if (startVoxel == VoxelMap.InvalidVoxel || endVoxel == VoxelMap.InvalidVoxel)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find empty voxel");
            return new();
        }

        var timer = Timer.Create();
        var voxelPath = VolumeQuery.FindPath(startVoxel, endVoxel, from, to, useRaycast, false, cancel); // TODO: do we need intermediate points for string-pulling algo?
        if (voxelPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find path on volume");
            return new();
        }
        Service.Log.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", voxelPath.Select(r => $"{r.p} {r.voxel:X}"))}");

        // TODO: string-pulling support
        // 飛行路徑不經過自訂連結,所以全部都是一般路徑點(Default ⇒ CheckCondition 回 false)。
        var res = voxelPath.Select(r => new Waypoint(r.p)).ToList();
        res.Add(new(to));
        return res;
    }

    // returns 0 if not found, otherwise polygon ref
    public long FindNearestMeshPoly(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5)
    {
        MeshQuery.FindNearestPoly(p.SystemToRecast(), new(halfExtentXZ, halfExtentY, halfExtentXZ), _filter, out var nearestRef, out _, out _);
        return nearestRef;
    }

    public List<long> FindIntersectingMeshPolys(Vector3 p, Vector3 halfExtent)
    {
        IntersectQuery query = new();
        MeshQuery.QueryPolygons(p.SystemToRecast(), halfExtent.SystemToRecast(), _filter, query);
        return query.Result;
    }

    public Vector3? FindNearestPointOnMeshPoly(Vector3 p, long poly) => MeshQuery.ClosestPointOnPoly(poly, p.SystemToRecast(), out var closest, out _).Succeeded() ? closest.RecastToSystem() : null;

    public Vector3? FindNearestPointOnMesh(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5) => FindNearestPointOnMeshPoly(p, FindNearestMeshPoly(p, halfExtentXZ, halfExtentY));

    // finds the point on the mesh within specified x/z tolerance and with largest Y that is still smaller than p.Y
    // ⚠️ 上游這個函式多一個 allowUnreachable 參數,轉給 FindIntersectingMeshPolys 過濾
    //    FLAG_UNREACHABLE。那個旗標由 FloodFill/Prune(方案 D 的 D3)設定,我方未取 D3,
    //    所以沒有任何多邊形會帶那個旗標,參數加了也是 no-op。細節見 IPCProvider 的
    //    Query.Mesh.PointOnFloor 註冊處(那裡列了會被影響的 7 個消費端)。
    public Vector3? FindPointOnFloor(Vector3 p, float halfExtentXZ = 5)
    {
        IEnumerable<long> polys = FindIntersectingMeshPolys(p, new(halfExtentXZ, 2048, halfExtentXZ));
        return polys.Select(poly => FindNearestPointOnMeshPoly(p, poly)).Where(pt => pt != null && pt.Value.Y <= p.Y).MaxBy(pt => pt!.Value.Y);
    }

    // returns VoxelMap.InvalidVoxel if not found, otherwise voxel index
    public ulong FindNearestVolumeVoxel(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5) => VolumeQuery != null ? VoxelSearch.FindNearestEmptyVoxel(VolumeQuery.Volume, p, new(halfExtentXZ, halfExtentY, halfExtentXZ)) : VoxelMap.InvalidVoxel;

    // collect all mesh polygons reachable from specified polygon
    public HashSet<long> FindReachableMeshPolys(long starting)
    {
        HashSet<long> result = [];
        if (starting == 0)
            return result;

        List<long> queue = [starting];
        while (queue.Count > 0)
        {
            var next = queue[^1];
            queue.RemoveAt(queue.Count - 1);

            if (!result.Add(next))
                continue; // already visited

            MeshQuery.GetAttachedNavMesh().GetTileAndPolyByRefUnsafe(next, out var nextTile, out var nextPoly);
            for (int i = nextTile.polyLinks[nextPoly.index]; i != DtNavMesh.DT_NULL_LINK; i = nextTile.links[i].next)
            {
                long neighbourRef = nextTile.links[i].refs;
                if (neighbourRef != 0)
                    queue.Add(neighbourRef);
            }
        }

        return result;
    }
}
