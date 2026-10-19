using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh.NavVolume;

/// <summary>
/// 高空路線：起點以不超過 <see cref="MaxClimbSlope"/> 的坡度爬到巡航高度、沿起終點連線平飛、接近終點再以同樣坡度下降。
/// 巡航高度＝不低於兩端較高者、而且平飛段上下各 margin、左右各 margin 都是空體素的最低高度。
/// 起點／終點上方到巡航高度之間有實心（樹冠、屋簷、洞頂、地圖的天花板）、距離不夠爬升／下降、或任一段視線不通，就不給路線。
/// 純查詢：不動 <see cref="VoxelPathfind"/> 的節點表、不用亂數；與它一樣只在尋路工作鏈上呼叫。
/// </summary>
public static class VoxelHighRoute
{
    /// <summary>爬升／下降的坡度上限（垂直÷水平）；1＝45 度。</summary>
    public const float MaxClimbSlope = 1;
    /// <summary>沿起終點連線取樣的間距（米）；最底層體素是 2 米。</summary>
    public const float SampleStep = 1;
    /// <summary>平飛段左右取樣的間距（米），兩側各取到 margin。</summary>
    public const float LateralStep = 2;
    /// <summary>起點／終點水平這麼多米內不檢查上方淨空：角色站在地上時腳下那一格常是實心。</summary>
    public const float EndpointSkip = 3;
    private const int MaxPasses = 16;

    public enum Outcome { Ok, TooShort, BlockedAboveStart, BlockedAboveEnd, NoCruiseAltitude, LineBlocked }

    public sealed class Route
    {
        public Outcome Outcome;
        /// <summary>Outcome＝Ok 時的路徑點：起點、（起點在實心格裡時）起點那一格的起飛點、爬升頂點、下降起點、
        /// （終點在實心格裡時）終點那一格的落點、終點；爬升或下降不需要時少一點。視線只驗起飛點到落點之間。</summary>
        public List<(ulong voxel, Vector3 p)>? Path;
        public float CruiseY = float.NaN;
        /// <summary>平飛段讓巡航高度抬升的最高實心頂面；沒有抬升＝負無限大。</summary>
        public float ObstacleTop = float.NegativeInfinity;
        public float Length = float.NaN;
        public int Passes;
        /// <summary>失敗時：卡住的位置離起點的水平距離（米）；NaN＝不是某一柱擋住。</summary>
        public float BlockedAt = float.NaN;
        /// <summary>起點／終點正上方第一個實心的底面高度。</summary>
        public float CeilingStart = float.NaN, CeilingEnd = float.NaN;
    }

    public static string Describe(Outcome o) => o switch
    {
        Outcome.Ok => "可行",
        Outcome.TooShort => "距離太短，爬升與下降接不起來",
        Outcome.BlockedAboveStart => "起點上方被擋",
        Outcome.BlockedAboveEnd => "終點上方被擋",
        Outcome.NoCruiseAltitude => "找不到可以平飛的高度",
        Outcome.LineBlocked => "路線上視線不通",
        _ => o.ToString(),
    };

    public static Route Build(VoxelMap vol, ulong startVoxel, ulong endVoxel, Vector3 from, Vector3 to, float margin, CancellationToken cancel)
    {
        var r = new Route();
        margin = MathF.Max(margin, 0.5f);
        var o = Endpoint(vol, startVoxel, from);
        var e = Endpoint(vol, endVoxel, to);
        var flat = new Vector2(e.X - o.X, e.Z - o.Z);
        var dist = flat.Length();
        if (dist < 2 * EndpointSkip)
            return Fail(r, Outcome.TooShort);
        var dir = flat / dist;
        var side = new Vector2(-dir.Y, dir.X);
        var ceilO = r.CeilingStart = CeilingAbove(vol, o);
        var ceilE = r.CeilingEnd = CeilingAbove(vol, e);
        var h = r.CruiseY = MathF.Max(o.Y, e.Y);

        var settled = false;
        while (!settled)
        {
            cancel.ThrowIfCancellationRequested();
            if (++r.Passes > MaxPasses)
                return Fail(r, Outcome.NoCruiseAltitude);
            settled = true;
            for (var s = 0f; ; s = MathF.Min(s + SampleStep, dist))
            {
                if (h + margin > ceilO)
                    return Fail(r, Outcome.BlockedAboveStart);
                if (h + margin > ceilE)
                    return Fail(r, Outcome.BlockedAboveEnd);
                var climb = (h - o.Y) / MaxClimbSlope;
                var descend = (h - e.Y) / MaxClimbSlope;
                if (climb + descend > dist)
                    return Fail(r, Outcome.TooShort);
                var c = new Vector2(o.X, o.Z) + dir * s;
                if (s < climb)
                {
                    if (s >= EndpointSkip && !ColumnClear(vol, c, o.Y + s * MaxClimbSlope, h + margin))
                        return Fail(r, Outcome.BlockedAboveStart, s);
                }
                else if (s > dist - descend)
                {
                    if (dist - s >= EndpointSkip && !ColumnClear(vol, c, e.Y + (dist - s) * MaxClimbSlope, h + margin))
                        return Fail(r, Outcome.BlockedAboveEnd, s);
                }
                else
                {
                    var lanes = (int)MathF.Ceiling(margin / LateralStep);
                    for (var k = 0; k <= lanes; ++k)
                    {
                        var l = MathF.Min(k * LateralStep, margin);
                        foreach (var sign in k == 0 ? Center : Sides)
                        {
                            var cc = c + side * (l * sign);
                            if (!LowestClear(vol, cc, h, margin, out var nh, out var top))
                                return Fail(r, Outcome.NoCruiseAltitude, s);
                            if (nh > h)
                            {
                                h = r.CruiseY = nh;
                                r.ObstacleTop = MathF.Max(r.ObstacleTop, top);
                                settled = false;
                            }
                        }
                    }
                }
                if (s >= dist)
                    break;
            }
        }

        var climbLen = (h - o.Y) / MaxClimbSlope;
        var descendLen = (h - e.Y) / MaxClimbSlope;
        var path = new List<(ulong voxel, Vector3 p)>(6) { (startVoxel, from) };
        if (o != from)
            path.Add((startVoxel, o));
        var prev = (voxel: startVoxel, p: o);
        if (climbLen > 0.01f && !AddCorner(vol, path, ref prev, new Vector2(o.X, o.Z) + dir * climbLen, h))
            return Fail(r, Outcome.LineBlocked);
        if (dist - climbLen - descendLen > 0.01f && descendLen > 0.01f && !AddCorner(vol, path, ref prev, new Vector2(e.X, e.Z) - dir * descendLen, h))
            return Fail(r, Outcome.LineBlocked);
        if (prev.voxel != endVoxel && !VoxelSearch.LineOfSight(vol, prev.voxel, endVoxel, prev.p, e, true))
            return Fail(r, Outcome.LineBlocked);
        if (e != to)
            path.Add((endVoxel, e));
        path.Add((endVoxel, to));

        var len = 0f;
        for (var i = 1; i < path.Count; ++i)
            len += Vector3.Distance(path[i - 1].p, path[i].p);
        r.Length = len;
        r.Path = path;
        r.Outcome = Outcome.Ok;
        return r;
    }

    private static readonly float[] Center = [0];
    private static readonly float[] Sides = [-1, 1];

    private static Route Fail(Route r, Outcome o, float at = float.NaN)
    {
        r.Outcome = o;
        r.Path = null;
        r.BlockedAt = at;
        return r;
    }

    // 角色站在地上時 from 常落在實心格裡：改從起點那一格（最近的空體素）裡離它最近的點算幾何與視線。
    private static Vector3 Endpoint(VoxelMap vol, ulong voxel, Vector3 p)
        => vol.FindLeafVoxel(p) is var (v, empty) && empty && v == voxel ? p : VoxelSearch.FindClosestVoxelPoint(vol, voxel, p);

    private static bool AddCorner(VoxelMap vol, List<(ulong voxel, Vector3 p)> path, ref (ulong voxel, Vector3 p) prev, Vector2 xz, float y)
    {
        var p = new Vector3(xz.X, y, xz.Y);
        var (v, empty) = vol.FindLeafVoxel(p);
        if (v == VoxelMap.InvalidVoxel || !empty)
            return false;
        if (v != prev.voxel && !VoxelSearch.LineOfSight(vol, prev.voxel, v, prev.p, p, true))
            return false;
        path.Add((v, p));
        prev = (v, p);
        return true;
    }

    // p 正上方第一個實心（或體素圖外）的底面高度。
    private static float CeilingAbove(VoxelMap vol, Vector3 p)
    {
        var y = p.Y;
        while (true)
        {
            var (v, empty) = vol.FindLeafVoxel(new Vector3(p.X, y, p.Z));
            if (v == VoxelMap.InvalidVoxel)
                return MathF.Max(y, p.Y);
            var (bmin, bmax) = vol.VoxelBounds(v, 0);
            if (!empty)
                return MathF.Max(bmin.Y, p.Y);
            y = bmax.Y + 0.01f;
        }
    }

    // (x, z) 這一柱在 [y0, y1] 之間全是空體素；碰到體素圖外也算不空。
    private static bool ColumnClear(VoxelMap vol, Vector2 xz, float y0, float y1)
    {
        var y = y0;
        while (y <= y1)
        {
            var (v, empty) = vol.FindLeafVoxel(new Vector3(xz.X, y, xz.Y));
            if (v == VoxelMap.InvalidVoxel || !empty)
                return false;
            y = vol.VoxelBounds(v, 0).max.Y + 0.01f;
        }
        return true;
    }

    // 不低於 h、而且 [H − margin, H + margin] 在這一柱全空的最低 H；往上一路到體素圖外都找不到回 false。
    // top＝最後一段擋住它的實心頂面（沒被擋＝負無限大）。
    private static bool LowestClear(VoxelMap vol, Vector2 xz, float h, float margin, out float nh, out float top)
    {
        nh = h;
        top = float.NegativeInfinity;
        var y = h - margin;
        while (y <= nh + margin)
        {
            var (v, empty) = vol.FindLeafVoxel(new Vector3(xz.X, y, xz.Y));
            if (v == VoxelMap.InvalidVoxel)
                return false;
            var bmax = vol.VoxelBounds(v, 0).max.Y;
            if (!empty)
            {
                top = bmax;
                nh = bmax + margin;
            }
            y = bmax + 0.01f;
        }
        return true;
    }
}
