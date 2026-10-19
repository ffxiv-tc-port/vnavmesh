using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Navmesh;

/// <summary>
/// 飛行搜尋走不到終點時，向步行網格要一條路線、回傳拉直後的轉角，交給 <see cref="VoxelCorridor.Follow"/> 沿著飛。
/// 移植自社群 fork（d12frosted/ffxiv_navmesh）。與 <see cref="NavmeshQuery"/> 同樣不可重入：只在尋路工作鏈上呼叫。
/// 兩端取「飛得到的最近網格點」（離飛行者最近的多邊形常是正下方隔著岩層的洞穴地板）；
/// 網格路線不完整時另問反方向，取離另一端較近的那條。
/// </summary>
public static class MeshCorridor
{
    private static readonly RcVec3f Reach = new(150, 400, 150);

    // 端點候選：最多試幾個、每個試幾步、候選依 16 米格子去重（一片飛不到的網格不會用光所有次數）。
    private const int Candidates = 32;
    private const int CandidateBudget = 2000;
    private const float CandidateSpacing = 16;

    /// <summary>一次網格搜尋最多的迭代數；用完就取目前最好的部分路線。</summary>
    public const int Iterations = 20000;

    public enum Outcome { Route, NoStartMesh, NoEndMesh, NoRoute }

    public static List<Vector3>? Corners(DtNavMeshQuery query, IDtQueryFilter filter, VoxelPathfind search, Vector3 from, Vector3 to, bool useRaycast, CancellationToken cancel, out Outcome outcome)
    {
        var start = NearestReachable(query, filter, search, from, useRaycast, cancel);
        if (start == null)
        {
            outcome = Outcome.NoStartMesh;
            return null;
        }
        var end = NearestReachable(query, filter, search, to, useRaycast, cancel);
        if (end == null)
        {
            outcome = Outcome.NoEndMesh;
            return null;
        }

        outcome = Outcome.NoRoute;
        var forward = Route(query, filter, start.Value, end.Value);
        if (forward == null)
            return null;
        outcome = Outcome.Route;
        if (forward.Value.complete)
            return forward.Value.corners;

        var backward = Route(query, filter, end.Value, start.Value);
        if (backward is { } b && b.gap < forward.Value.gap)
        {
            b.corners.Reverse();
            return b.corners;
        }
        return forward.Value.corners;
    }

    private static (long poly, RcVec3f point)? NearestReachable(DtNavMeshQuery query, IDtQueryFilter filter, VoxelPathfind search, Vector3 p, bool useRaycast, CancellationToken cancel)
    {
        var volume = search.Volume;
        var fromVoxel = VoxelSearch.FindNearestEmptyVoxel(volume, p, new(5, 5, 5));
        if (fromVoxel == VoxelMap.InvalidVoxel)
            return null;

        var polys = new Collector();
        query.QueryPolygons(p.SystemToRecast(), Reach, filter, polys);
        var candidates = new List<(long poly, RcVec3f point, float dist)>(polys.Refs.Count);
        foreach (var poly in polys.Refs)
        {
            if (query.ClosestPointOnPoly(poly, p.SystemToRecast(), out var closest, out _).Failed())
                continue;
            candidates.Add((poly, closest, Vector3.DistanceSquared(closest.RecastToSystem(), p)));
        }
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        var budget = Math.Min(search.Tuning.MaxSteps, CandidateBudget);
        var tried = new HashSet<(int, int, int)>();
        foreach (var (poly, point, _) in candidates)
        {
            if (tried.Count >= Candidates)
                break;
            var target = point.RecastToSystem();
            var cell = ((int)MathF.Floor(target.X / CandidateSpacing), (int)MathF.Floor(target.Y / CandidateSpacing), (int)MathF.Floor(target.Z / CandidateSpacing));
            if (!tried.Add(cell))
                continue;
            var toVoxel = VoxelSearch.FindNearestEmptyVoxel(volume, target, new(5, 5, 5));
            if (toVoxel == VoxelMap.InvalidVoxel)
                continue;
            if (fromVoxel == toVoxel)
                return (poly, point);
            cancel.ThrowIfCancellationRequested();
            var lifted = VoxelSearch.FindClosestVoxelPoint(volume, toVoxel, target);
            var leg = search.FindPath(fromVoxel, toVoxel, p, lifted, useRaycast, false, cancel, budget);
            if (leg.Count > 0 && leg[^1].voxel == toVoxel)
                return (poly, point);
        }
        return null;
    }

    private static (List<Vector3> corners, bool complete, float gap)? Route(DtNavMeshQuery query, IDtQueryFilter filter, (long poly, RcVec3f point) start, (long poly, RcVec3f point) end)
    {
        var status = query.InitSlicedFindPath(start.poly, end.poly, start.point, end.point, filter, 0);
        for (var left = Iterations; status.InProgress() && left > 0;)
        {
            status = query.UpdateSlicedFindPath(Math.Min(left, 1000), out var done);
            left -= Math.Max(done, 1);
        }
        var polys = new List<long>();
        status = query.FinalizeSlicedFindPath(ref polys); // 還在搜的會收尾成目前最好的部分路線
        if (status.Failed() || polys.Count == 0)
            return null;

        var straight = new List<DtStraightPath>();
        if (query.FindStraightPath(start.point, end.point, polys, ref straight, 1024, 0).Failed() || straight.Count == 0)
            return null;

        var corners = straight.Select(s => s.pos.RecastToSystem()).ToList();
        return (corners, !status.IsPartial(), RcVec3f.Distance(corners[^1].SystemToRecast(), end.point));
    }

    private sealed class Collector : IDtPolyQuery
    {
        public readonly List<long> Refs = [];
        public void Process(DtMeshTile tile, DtPoly poly, long refs) => Refs.Add(refs);
    }
}
