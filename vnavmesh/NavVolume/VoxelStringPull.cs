using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh.NavVolume;

/// <summary>
/// 飛行路徑拉直；移植自社群 fork（d12frosted/ffxiv_navmesh）。從錨點往後找，直線一路看得到就丟掉中間點，
/// 遇到第一個看不到的候選就停（不往後跳，免得從後面又打開的縫穿過去）。判定用搜尋本身驗視線的
/// <see cref="VoxelSearch.LineOfSight(VoxelMap, ulong, ulong, Vector3, Vector3, bool)"/>，所以新產生的每一段都驗過。
/// 契約：回傳的點是原路徑依序的子序列，第一點與最後一點不變；少於 3 點原樣回傳。
/// </summary>
public static class VoxelStringPull
{
    public static List<(ulong voxel, Vector3 p)> Simplify(VoxelMap volume, List<(ulong voxel, Vector3 p)> path, CancellationToken cancel = default)
    {
        if (path.Count < 3)
            return path;

        var res = new List<(ulong voxel, Vector3 p)>(path.Count) { path[0] };
        var anchor = 0;
        while (anchor < path.Count - 1)
        {
            cancel.ThrowIfCancellationRequested();
            // 下一點不必驗：搜尋就是這樣走過去的。
            var best = anchor + 1;
            for (var j = anchor + 2; j < path.Count; ++j)
            {
                if (!Visible(volume, path[anchor], path[j]))
                    break;
                best = j;
            }
            res.Add(path[best]);
            anchor = best;
        }
        return res;
    }

    // 步進原地打轉＝什麼都證明不了，當成看不到（保留中間點一定安全，擲例外會讓已找到的路徑整筆失敗）。
    private static bool Visible(VoxelMap volume, (ulong voxel, Vector3 p) from, (ulong voxel, Vector3 p) to)
        => VoxelSearch.LineOfSight(volume, from.voxel, to.voxel, from.p, to.p, true);
}
