using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh;

// 本檔假設與契約：凸包繞行兩側都不通時的後備，同樣不可重入、不碰 Dalamud 服務。
// 以 A（原路進入凸包處）為原點、間隔 GridCell 的格點，相鄰格點用網格射線確認走得通，A* 走到 B 或 B 之後原路上的點。
// strict＝不進任何不豁免的區域；mitigate＝這一群與已放棄的區域可以穿、成本依穿越深度加重，其他區域仍不可進，
// 新的一段曝露（∫ 深度加權）要比原路那一段少到 MitigateKeep 以下才換。展開數每次與每次修補都有上限。
public static partial class ZoneDetour
{
    private const float GridCell = 1f;
    private const float GridPad = 12f;
    // 格點離不可進的區域至少這麼遠：大於 ValidateMargin，拉直時的線段檢查才不會被弦的內凹卡住。
    private const float GridMargin = 1f;
    private const int GridMaxExpansions = 8000;
    private const int GridMaxExpansionsPerRepair = 16000;
    private const int GridSmoothLookahead = 64;
    // 往後試到連續這麼多點接不上就停：格點鏈的鋸齒只會讓少數幾點接不上，整段擋住時不必試完 GridSmoothLookahead。
    private const int GridSmoothMaxMisses = 8;
    // 格點離目標點最遠約 GridCell × 0.71，要接得上。
    private const float GoalReach = 1f;
    // 穿越成本：每米 MitigateBase × (1 + MitigateDepth × 深度)，深度＝離區域外的距離（米）。
    private const float MitigateBase = 3f;
    private const float MitigateDepth = 0.5f;
    private const float MitigateKeep = 0.8f;
    private const float ExposureStep = 0.25f;

    private struct GNode
    {
        public Pt P;
        public int Ix, Iz;
        public float G;
        public int Parent;
        public bool Closed;
    }

    private readonly struct Goal(Pt p, int resume, float ahead)
    {
        public readonly Pt P = p;
        public readonly int Resume = resume;
        public readonly float Ahead = ahead;
    }

    private static Outcome GridDetour(Ctx c, List<Pt> pts, int seg, List<int> cluster, float budget, bool mitigate, CancellationToken cancel)
    {
        if (c.GridLeft <= 0)
            return Outcome.Blocked;
        var found = FindSpan(c, pts, seg, cluster, Clearance, out var span);
        if (found != Outcome.Ok)
            return found;

        var candidates = BuildGoals(c, pts, span);
        // 可以穿的只限原路這一段本來就穿過的（這一群或已放棄的）區域：不可為了減傷走進原本沒碰到的區域。
        var oldSpan = OldSection(pts, span, candidates[^1]);
        var hard = new List<int>();
        var soft = new List<int>();
        for (var z = 0; z < c.Zones.Count; ++z)
        {
            if (c.State[z] == ZoneState.Exempt)
                continue;
            if (mitigate && (cluster.Contains(z) || c.State[z] == ZoneState.GivenUp) && CrossesPath(c.Zones[z], oldSpan))
                soft.Add(z);
            else
                hard.Add(z);
        }
        if (mitigate && soft.Count == 0)
            return Outcome.Blocked;

        var goals = candidates.FindAll(g => !Blocked(c, g.P.Pos, hard));
        if (goals.Count == 0)
            return Outcome.Blocked;

        var chain = GridSearch(c, span, goals, hard, soft, out var goal, cancel);
        if (chain == null)
        {
            c.Trace?.Add($"  grid{(mitigate ? " mitigate" : "")}: no path (left {c.GridLeft})");
            return Outcome.Blocked;
        }

        var smooth = Smooth(c, chain, hard, soft);
        var spliced = Splice(pts, span.IA, smooth, goal.Resume);
        var total = PathLength(spliced);
        if (total > budget)
        {
            c.Trace?.Add($"  grid{(mitigate ? " mitigate" : "")}: total {total:f1}/{budget:f1}");
            return Outcome.OverBudget;
        }

        if (mitigate)
        {
            var before = Exposure(c, OldSection(pts, span, goal), soft);
            var after = Exposure(c, smooth.ConvertAll(p => p.Pos), soft);
            c.Trace?.Add($"  grid mitigate: exposure {before:f1} -> {after:f1}, {smooth.Count} pts, total {total:f1}/{budget:f1}");
            if (!(after < before * MitigateKeep) || CountCrossedZones(Positions(spliced), c.Zones, c.State, c.Limit) > CountCrossedZones(Positions(pts), c.Zones, c.State, c.Limit))
                return Outcome.Blocked;
        }
        else
        {
            c.Trace?.Add($"  grid: ok {smooth.Count} pts, total {total:f1}/{budget:f1}");
        }

        pts.Clear();
        pts.AddRange(spliced);
        return Outcome.Ok;
    }

    // 原路被換掉的那一段：A → 原路轉折點 → 目標點。
    private static List<Vector3> OldSection(List<Pt> pts, in Span span, in Goal goal)
    {
        var old = new List<Vector3> { span.APos };
        for (var k = span.IA + 1; k <= goal.Resume - 1; ++k)
            old.Add(pts[k].Pos);
        old.Add(goal.P.Pos);
        return old;
    }

    private static bool CrossesPath(in ZoneAvoidance.Zone z, List<Vector3> path)
    {
        for (var i = 0; i + 1 < path.Count; ++i)
            if (Crosses(z, path[i], path[i + 1], Vector3.Distance(path[i], path[i + 1]), out _))
                return true;
        return false;
    }

    // 目標候選：B 與 B 之後原路上的點。Ahead＝從該點沿原路走到前瞻窗盡頭的長度，讓接回早晚可以比較。
    private static List<Goal> BuildGoals(Ctx c, List<Pt> pts, in Span span)
    {
        var resumes = new List<int>();
        var forward = ForwardWindow(c, pts, span.IB, span.BPos, out _, resumes);
        var all = new List<(Pt p, int resume)>(forward.Count + 1) { (span.B, span.IB + 1) };
        for (var k = 0; k < forward.Count; ++k)
            all.Add((forward[k], resumes[k]));

        var ahead = new float[all.Count];
        for (var k = all.Count - 2; k >= 0; --k)
            ahead[k] = ahead[k + 1] + Vector3.Distance(all[k].p.Pos, all[k + 1].p.Pos);

        var goals = new List<Goal>(all.Count);
        for (var k = 0; k < all.Count; ++k)
            goals.Add(new(all[k].p, all[k].resume, ahead[k]));
        return goals;
    }

    private static bool Blocked(Ctx c, Vector3 p, List<int> hard)
    {
        foreach (var z in hard)
        {
            var zone = c.Zones[z];
            if (ZoneAvoidance.InBand(zone, p.Y) && ZoneAvoidance.Distance2D(zone, new(p.X, p.Z)) < GridMargin)
                return true;
        }
        return false;
    }

    private static float Penalty(Ctx c, Vector3 p, List<int> soft)
    {
        var pen = 0f;
        foreach (var z in soft)
        {
            var depth = ZoneAvoidance.Penetration(c.Zones[z], p);
            if (depth > 0)
                pen += MitigateBase * (1 + MitigateDepth * depth);
        }
        return pen;
    }

    // 線段成本＝長度＋∫ 穿越成本（每 ExposureStep 取樣中點）。soft 為空時就是長度。
    private static float SegmentCost(Ctx c, Vector3 a, Vector3 b, List<int> soft)
    {
        var len = Vector3.Distance(a, b);
        if (soft.Count == 0 || len < 1e-4f)
            return len;
        var steps = Math.Max(1, (int)MathF.Ceiling(len / ExposureStep));
        var cost = len;
        for (var k = 0; k < steps; ++k)
            cost += len / steps * Penalty(c, Vector3.Lerp(a, b, (k + 0.5f) / steps), soft);
        return cost;
    }

    private static float Exposure(Ctx c, List<Vector3> path, List<int> soft)
    {
        var e = 0f;
        for (var i = 0; i + 1 < path.Count; ++i)
            e += SegmentCost(c, path[i], path[i + 1], soft) - Vector3.Distance(path[i], path[i + 1]);
        return e;
    }

    // 回傳格點鏈（含頭 A、尾＝目標點）；找不到或展開數用完回 null。
    private static List<Pt>? GridSearch(Ctx c, in Span span, List<Goal> goals, List<int> hard, List<int> soft, out Goal goal, CancellationToken cancel)
    {
        goal = default;
        var origin = Flat(span.A.Pos);
        var min = origin;
        var max = origin;
        foreach (var v in span.Hull)
        {
            min = Vector2.Min(min, v);
            max = Vector2.Max(max, v);
        }
        foreach (var g in goals)
        {
            min = Vector2.Min(min, Flat(g.P.Pos));
            max = Vector2.Max(max, Flat(g.P.Pos));
        }
        min -= new Vector2(GridPad);
        max += new Vector2(GridPad);

        float H(Vector3 p)
        {
            var h = float.MaxValue;
            foreach (var g in goals)
                h = MathF.Min(h, Vector2.Distance(Flat(p), Flat(g.P.Pos)) + g.Ahead);
            return h;
        }

        var nodes = new List<GNode> { new() { P = span.A, G = 0, Parent = -1 } };
        var ids = new Dictionary<(int, int, int), int> { [(0, 0, Layer(span.A.Pos.Y))] = 0 };
        var open = new PriorityQueue<int, float>();
        open.Enqueue(0, H(span.A.Pos));
        var mq = c.Query.MeshQuery;
        var best = float.MaxValue;
        var bestNode = -1;
        var bestGoal = -1;
        var limit = Math.Min(GridMaxExpansions, c.GridLeft);
        var expanded = 0;
        while (open.TryDequeue(out var id, out var f))
        {
            var n = nodes[id];
            if (n.Closed)
                continue;
            if (f >= best || expanded >= limit)
                break;
            n.Closed = true;
            nodes[id] = n;
            if ((++expanded & 1023) == 0)
                cancel.ThrowIfCancellationRequested();

            for (var k = 0; k < goals.Count; ++k)
            {
                var gp = goals[k].P;
                if (Vector2.Distance(Flat(n.P.Pos), Flat(gp.Pos)) > GoalReach || MathF.Abs(n.P.Pos.Y - gp.Pos.Y) > LevelTolerance)
                    continue;
                if (!Walkable(c, n.P, gp) || !ClearOf(c, n.P.Pos, gp.Pos, hard))
                    continue;
                var score = n.G + SegmentCost(c, n.P.Pos, gp.Pos, soft) + goals[k].Ahead;
                if (score < best)
                {
                    best = score;
                    bestNode = id;
                    bestGoal = k;
                }
            }

            for (var dx = -1; dx <= 1; ++dx)
            {
                for (var dz = -1; dz <= 1; ++dz)
                {
                    if (dx == 0 && dz == 0)
                        continue;
                    var ix = n.Ix + dx;
                    var iz = n.Iz + dz;
                    var target = origin + new Vector2(ix, iz) * GridCell;
                    if (target.X < min.X || target.Y < min.Y || target.X > max.X || target.Y > max.Y || ClosedNear(nodes, ids, ix, iz, n.P.Pos.Y))
                        continue;
                    if (!GridStep(c, mq, n.P, target, out var next) || Blocked(c, next.Pos, hard))
                        continue;
                    var key = (ix, iz, Layer(next.Pos.Y));
                    var g = n.G + SegmentCost(c, n.P.Pos, next.Pos, soft);
                    if (ids.TryGetValue(key, out var existing))
                    {
                        var e = nodes[existing];
                        if (e.Closed || e.G <= g)
                            continue;
                        e.G = g;
                        e.Parent = id;
                        e.P = next;
                        nodes[existing] = e;
                        open.Enqueue(existing, g + H(next.Pos));
                    }
                    else
                    {
                        ids[key] = nodes.Count;
                        nodes.Add(new() { P = next, Ix = ix, Iz = iz, G = g, Parent = id });
                        open.Enqueue(nodes.Count - 1, g + H(next.Pos));
                    }
                }
            }
        }
        c.GridLeft -= expanded;
        c.Trace?.Add($"  grid search: expanded {expanded}, nodes {nodes.Count}, best {(bestNode >= 0 ? best.ToString("f1") : "none")}");
        if (bestNode < 0)
            return null;

        goal = goals[bestGoal];
        var chain = new List<Pt> { goal.P };
        for (var k = bestNode; k >= 0; k = nodes[k].Parent)
            chain.Add(nodes[k].P);
        chain.Reverse();
        return chain;
    }

    // 這些區域有沒有任何一個在 horizon 內被路徑穿過。
    private static bool AnyCrossed(Ctx c, List<Pt> pts, List<int> zones)
    {
        foreach (var z in zones)
        {
            var s = 0f;
            for (var i = 0; i + 1 < pts.Count && s <= c.Limit; ++i)
            {
                var len = Vector3.Distance(pts[i].Pos, pts[i + 1].Pos);
                if (Crosses(c.Zones[z], pts[i].Pos, pts[i + 1].Pos, len, out var t0) && s + t0 * len <= c.Limit)
                    return true;
                s += len;
            }
        }
        return false;
    }

    private static List<Vector3> Positions(List<Pt> path) => path.ConvertAll(p => p.Pos);

    // 相鄰格點若已展開過（同一格、高度差在 LevelTolerance 內）就不再射線：一致的啟發式下它的成本不會再變小。
    private static bool ClosedNear(List<GNode> nodes, Dictionary<(int, int, int), int> ids, int ix, int iz, float y)
    {
        var layer = Layer(y);
        for (var l = layer - 1; l <= layer + 1; ++l)
            if (ids.TryGetValue((ix, iz, l), out var id) && nodes[id].Closed && MathF.Abs(nodes[id].P.Pos.Y - y) <= LevelTolerance)
                return true;
        return false;
    }

    private static int Layer(float y) => (int)MathF.Floor(y / 2.5f);

    // 從 a 沿網格射線走到格點 target（XZ）；要走得到、落在同一層附近（高度差 ≤ LevelTolerance）。
    private static bool GridStep(Ctx c, DtNavMeshQuery mq, in Pt a, Vector2 target, out Pt next)
    {
        next = default;
        var to = new Vector3(target.X, a.Pos.Y, target.Y);
        var status = mq.Raycast(a.Ref, a.Pos.SystemToRecast(), to.SystemToRecast(), c.Filter, 0, ref c.Hit, 0);
        if (status.Failed() || c.Hit.t != float.MaxValue || c.Hit.path.Count == 0)
            return false;
        var poly = c.Hit.path[^1];
        if (mq.ClosestPointOnPoly(poly, to.SystemToRecast(), out var cp, out _).Failed())
            return false;
        var pos = cp.RecastToSystem();
        if (Vector2.Distance(Flat(pos), target) > 0.05f || MathF.Abs(pos.Y - a.Pos.Y) > LevelTolerance)
            return false;
        next = new() { Pos = pos, Ref = poly };
        return true;
    }

    // 貪婪拉直：從 i 往後逐點試，取最後一個直線接得上的（走得通、不碰不可進的區域、穿越成本不高於格點鏈原本那一段）；
    // 相鄰格點本來就接得上。
    private static List<Pt> Smooth(Ctx c, List<Pt> chain, List<int> hard, List<int> soft)
    {
        var cum = new float[chain.Count];
        for (var k = 1; k < chain.Count; ++k)
            cum[k] = cum[k - 1] + SegmentCost(c, chain[k - 1].Pos, chain[k].Pos, soft);

        var res = new List<Pt> { chain[0] };
        var i = 0;
        while (i < chain.Count - 1)
        {
            var next = i + 1;
            var misses = 0;
            for (var j = i + 2; j < chain.Count && j <= i + GridSmoothLookahead; ++j)
            {
                var ok = ClearOf(c, chain[i].Pos, chain[j].Pos, hard) && Walkable(c, chain[i], chain[j])
                    && (soft.Count == 0 || SegmentCost(c, chain[i].Pos, chain[j].Pos, soft) <= cum[j] - cum[i] + 1e-3f);
                if (ok)
                {
                    next = j;
                    misses = 0;
                }
                else if (++misses >= GridSmoothMaxMisses)
                {
                    break;
                }
            }
            res.Add(chain[next]);
            i = next;
        }
        return res;
    }
}
