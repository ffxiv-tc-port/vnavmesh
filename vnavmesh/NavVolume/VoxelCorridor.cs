using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh.NavVolume;

/// <summary>
/// 把一串轉角點（來自步行網格的路線）一段一段接成飛行路徑；移植自社群 fork（d12frosted/ffxiv_navmesh）。
/// 轉角在地板上，先抬到最近的空體素；看得到就直接連，看不到就跑一次短搜尋。任何一段接不起來就整條放棄（回 null）。
/// 與 <see cref="VoxelPathfind"/> 同樣不可重入：只在尋路工作鏈上呼叫。
/// </summary>
public static class VoxelCorridor
{
    private static readonly Vector3 CornerReach = new(5, 5, 5);

    /// <summary>每一段最多展開的節點數（呼叫端的上限更小時用呼叫端的）。</summary>
    public const int LegBudget = 20000;

    public static List<(ulong voxel, Vector3 p)>? Follow(VoxelPathfind search, (ulong voxel, Vector3 p) from, (ulong voxel, Vector3 p) to, IReadOnlyList<Vector3> corners, bool useRaycast, CancellationToken cancel)
    {
        var volume = search.Volume;
        var stops = new List<(ulong voxel, Vector3 p)>(corners.Count + 2) { from };
        foreach (var corner in corners)
        {
            var voxel = VoxelSearch.FindNearestEmptyVoxel(volume, corner, CornerReach);
            if (voxel == VoxelMap.InvalidVoxel)
                return null;
            if (voxel == stops[^1].voxel)
                continue;
            stops.Add((voxel, VoxelSearch.FindClosestVoxelPoint(volume, voxel, corner)));
        }
        if (stops.Count > 1 && stops[^1].voxel == to.voxel)
            stops.RemoveAt(stops.Count - 1); // 與終點同一格的轉角讓給終點
        stops.Add(to);

        var legBudget = Math.Min(search.Tuning.MaxSteps, LegBudget);
        var path = new List<(ulong voxel, Vector3 p)> { from };
        for (var i = 1; i < stops.Count; ++i)
        {
            cancel.ThrowIfCancellationRequested();
            var a = stops[i - 1];
            var b = stops[i];
            if (a.voxel == b.voxel || VoxelSearch.LineOfSight(volume, a.voxel, b.voxel, a.p, b.p, true))
            {
                path.Add(b);
                continue;
            }
            var leg = search.FindPath(a.voxel, b.voxel, a.p, b.p, useRaycast, false, cancel, legBudget);
            if (leg.Count == 0 || leg[^1].voxel != b.voxel)
                return null;
            for (var j = 1; j < leg.Count; ++j)
                path.Add(leg[j]);
        }
        return path;
    }
}
