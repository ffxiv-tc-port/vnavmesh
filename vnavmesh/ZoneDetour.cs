using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh;

/// <summary>Nav.PathfindAroundZones 回傳的 Flags 位元。🔴 跨插件契約：數值只能新增、不能改。</summary>
public static class ZoneDetourFlags
{
    /// <summary>終點走不到：原路最後一段是直線補上的（可能穿牆），那段不修補。</summary>
    public const int PartialPath = 1 << 0;
    /// <summary>至少一處繞行因總長會超過預算而放棄：該處保留原路、計入 Unavoidable。</summary>
    public const int OverBudget = 1 << 1;
    /// <summary>修補輪數用完，horizon 內仍有沒處理到的穿越（計入 Unavoidable）。</summary>
    public const int IterationLimit = 1 << 2;
    /// <summary>路徑含「要等遊戲把人送過去」的連結點（ClientPath）；Path 已丟掉這資訊，不可直接 Path.MoveTo。</summary>
    public const int ClientPathLinks = 1 << 3;
    /// <summary>起點已在某些區域內：那些區域整個略過（不計入 Unavoidable）。</summary>
    public const int StartExempt = 1 << 4;
    /// <summary>終點在某些區域內：那些區域整個略過（不計入 Unavoidable）。</summary>
    public const int EndExempt = 1 << 5;
    /// <summary>修補時擲例外：Path＝原路、Repaired＝0、Unavoidable＝−1（不知道）。</summary>
    public const int ExceptionFallback = 1 << 6;
    /// <summary>路徑含 off-mesh 連結（跳下的捷徑等）：Path.MoveTo 照常走得過去；修補不會跨過它。</summary>
    public const int OffMeshLinks = 1 << 7;
    /// <summary>區域資料有問題（長度不是 ZoneStride 的倍數、含 NaN／無限大、超過上限）：有問題的筆被略過。</summary>
    public const int InvalidZoneData = 1 << 8;
    /// <summary>找不到路：Path 為空。</summary>
    public const int NoPath = 1 << 9;
    /// <summary>至少一處避不開的區域改走穿越較淺的路線（Path 在那裡與原路不同）；那些區域仍計入 Unavoidable。</summary>
    public const int Mitigated = 1 << 10;
}

public sealed class ZoneDetourResult
{
    /// <summary>與 Nav.Pathfind 同形狀的路徑點；沒有區域要繞時與它逐點相同。</summary>
    public List<Vector3> Path = [];
    public int Flags;
    /// <summary>接上了幾段繞行（含 <see cref="Mitigated"/>）。</summary>
    public int Repaired;
    /// <summary>最終路徑在 horizon 內仍穿過的區域數（豁免的不算）；−1＝不知道。</summary>
    public int Unavoidable;
    /// <summary>其中仍穿過區域、只是改走穿越較淺路線的段數（見 <see cref="ZoneDetourFlags.Mitigated"/>）。</summary>
    public int Mitigated;
    public int Zones;
    public int ExemptZones;
    public int Rounds;
    public float OriginalLength;
    public float FinalLength;
    /// <summary>尋路失敗原因或例外全文，給呼叫端寫 log。</summary>
    public string? Failure;
    public List<string>? Trace;
}

/// <summary>
/// 在既有地面路徑上繞開仇恨區域：先照常尋路，再只修補 horizon 內穿過區域的路段。
/// 每段繞行沿「區域群凸包」的左或右側走，路徑點貼網格、每一小段用網格射線驗證走得通；兩側都不通再做格點搜尋（ZoneDetourGrid.cs），
/// 仍找不到＝避不開：改走穿越較淺的路線，連那也換不了才保留原段。
/// 拉直時連原路前後一段一起考慮：提早切出去、繞過後直接接到更後面的路，不必先回到原路線再走。
/// 🔴 不可重入：用的是傳入的 <see cref="NavmeshQuery"/>，呼叫端負責與其他尋路互斥（NavmeshManager 的工作鏈）。
/// 不碰任何 Dalamud 服務，離線工具可直接呼叫。
/// </summary>
public static partial class ZoneDetour
{
    public const int MaxRounds = 12;
    /// <summary>總長上限＝原長＋max(BudgetExtraMin, 原長 × BudgetFraction)：短路徑最多多走 BudgetExtraMin 米，長路徑按比例。</summary>
    public const float BudgetFraction = 0.25f;
    public const float BudgetExtraMin = 15f;
    /// <summary>繞行點離區域邊緣的距離（米）。</summary>
    public const float Clearance = 1.5f;
    /// <summary>貼著繞兩側都不通時，第二圈繞行點離區域邊緣的距離（米）。</summary>
    public const float WideClearance = 4.5f;
    /// <summary>拉直繞行時，線段離區域至少要保留的距離（米）；比 Clearance 小，留給貼網格的偏移。</summary>
    public const float ValidateMargin = 0.75f;

    private const float SnapHalfXZ = 2f;
    private const float SnapHalfY = 8f;
    private const float EndpointSnapMaxShift = 0.75f;
    private const float LevelTolerance = 1f;
    private const float GrazeLength = 1e-3f;
    private const float SamePoint = 0.01f;
    // 相鄰兩個繞行點之間直線被小障礙（樹、石頭）擋住時，就地找一段短路接起來；搜尋與繞遠都有上限。
    private const int LocalSearchIterations = 256;
    private const float LocalDetourFactor = 2.5f;
    private const float LocalDetourExtra = 10f;
    // 拉直時納入的原路範圍：繞行鏈之前／之後各多少米，以及沿原路取樣的間隔。
    private const float LookBack = 45f;
    private const float LookAhead = 90f;
    private const float WindowStep = 3f;
    private const float OnLineXZ = 0.01f;

    private enum ZoneState : byte { Pending, Cleared, GivenUp, Exempt }

    private enum Outcome { Ok, Blocked, OverBudget, Locked, SnapFailed, NoHull }

    private struct Pt
    {
        public Vector3 Pos;
        public long Ref;
        public bool LockOut; // 從這一點出發的那一段不可替換（off-mesh／特殊連結／部分路徑的直線尾段）
    }

    private sealed class Ctx(NavmeshQuery query, List<ZoneAvoidance.Zone> zones, ZoneState[] state, List<string>? trace)
    {
        public readonly NavmeshQuery Query = query;
        public readonly List<ZoneAvoidance.Zone> Zones = zones;
        public readonly ZoneState[] State = state;
        public readonly List<string>? Trace = trace;
        public readonly IDtQueryFilter Filter = new NavmeshQuery.TeleportAwareFilter();
        public DtRaycastHit Hit = new() { path = [] };
        public readonly IDtQueryFilter LocalFilter = new DtQueryDefaultFilter();
        public List<long> Corridor = [];
        public List<DtStraightPath> Straight = [];
        public int GridLeft = GridMaxExpansionsPerRepair;
        public float Limit = float.PositiveInfinity;
    }

    public static ZoneDetourResult Repair(NavmeshQuery q, NavmeshQuery.MeshPathDetail original, Vector3 from, Vector3 to, float[]? zoneData, float horizon, CancellationToken cancel, bool trace = false)
    {
        var res = new ZoneDetourResult { Trace = trace ? [] : null };
        if (original.Failure != null || original.Waypoints.Count == 0)
        {
            res.Flags |= ZoneDetourFlags.NoPath;
            res.Failure = original.Failure ?? "empty path";
            return res;
        }

        var originalPath = new List<Vector3>(original.Waypoints.Count);
        foreach (var w in original.Waypoints)
            originalPath.Add(w.Position);
        res.Path = originalPath;
        res.OriginalLength = res.FinalLength = PathLength(originalPath);

        try
        {
            var pts = BuildPoints(original, ref res.Flags);
            var zones = new List<ZoneAvoidance.Zone>();
            if (zoneData != null && !ZoneAvoidance.Parse(zoneData, zones))
                res.Flags |= ZoneDetourFlags.InvalidZoneData;
            res.Zones = zones.Count;

            var state = new ZoneState[zones.Count];
            var active = 0;
            for (var i = 0; i < zones.Count; ++i)
            {
                var z = zones[i];
                var atStart = ZoneAvoidance.Contains(z, from) || ZoneAvoidance.Contains(z, pts[0].Pos);
                var atEnd = ZoneAvoidance.Contains(z, to) || ZoneAvoidance.Contains(z, pts[^1].Pos);
                if (atStart)
                    res.Flags |= ZoneDetourFlags.StartExempt;
                if (atEnd)
                    res.Flags |= ZoneDetourFlags.EndExempt;
                if (atStart || atEnd)
                {
                    state[i] = ZoneState.Exempt;
                    ++res.ExemptZones;
                }
                else
                {
                    ++active;
                }
            }
            if (active == 0)
                return res;

            var limit = horizon > 0 ? horizon : float.PositiveInfinity;
            var budget = res.OriginalLength + MathF.Max(BudgetExtraMin, res.OriginalLength * BudgetFraction);
            var ctx = new Ctx(q, zones, state, res.Trace) { Limit = limit };
            var roundsLeft = true;
            for (var round = 0; round < MaxRounds; ++round)
            {
                cancel.ThrowIfCancellationRequested();
                if (!FindFirstCrossing(ctx, pts, limit, out var seg, out var tIn, out var zi))
                {
                    roundsLeft = false;
                    break;
                }

                ++res.Rounds;
                var cluster = BuildCluster(ctx, zi);
                var outcome = TryDetour(ctx, pts, seg, tIn, cluster, budget, cancel);
                ctx.Trace?.Add($"round {round}: seg {seg} t {tIn:f3} zone #{zones[zi].Index} cluster [{string.Join(",", cluster.ConvertAll(c => zones[c].Index))}] -> {outcome}");
                if (outcome == Outcome.Ok)
                {
                    ++res.Repaired;
                    foreach (var c in cluster)
                        state[c] = ZoneState.Cleared;
                }
                else
                {
                    foreach (var c in cluster)
                        state[c] = ZoneState.GivenUp;
                    if (outcome == Outcome.OverBudget)
                        res.Flags |= ZoneDetourFlags.OverBudget;
                    // 避不開：改走穿越較淺的路線；換上後這一群剛好都不穿過了就跟一般繞開一樣算。
                    if (outcome != Outcome.Locked && GridDetour(ctx, pts, seg, cluster, budget, true, cancel) == Outcome.Ok)
                    {
                        ++res.Repaired;
                        if (AnyCrossed(ctx, pts, cluster))
                        {
                            ++res.Mitigated;
                            res.Flags |= ZoneDetourFlags.Mitigated;
                        }
                        else
                        {
                            foreach (var c in cluster)
                                state[c] = ZoneState.Cleared;
                        }
                    }
                }
            }
            if (roundsLeft && FindFirstCrossing(ctx, pts, limit, out _, out _, out _))
                res.Flags |= ZoneDetourFlags.IterationLimit;

            var final = new List<Vector3>(pts.Count);
            foreach (var p in pts)
                final.Add(p.Pos);
            res.Unavoidable = CountCrossedZones(final, zones, state, limit);
            res.Path = final;
            res.FinalLength = PathLength(final);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            res.Path = originalPath;
            res.FinalLength = res.OriginalLength;
            res.Flags |= ZoneDetourFlags.ExceptionFallback;
            res.Repaired = 0;
            res.Mitigated = 0;
            res.Flags &= ~ZoneDetourFlags.Mitigated;
            res.Unavoidable = -1;
            res.Failure = ex.ToString();
        }
        return res;
    }

    public static float PathLength(IReadOnlyList<Vector3> path)
    {
        var len = 0f;
        for (var i = 0; i + 1 < path.Count; ++i)
            len += Vector3.Distance(path[i], path[i + 1]);
        return len;
    }

    // 路徑在弧長 limit 以內開始穿過的區域數（豁免的不算）。
    private static int CountCrossedZones(List<Vector3> path, List<ZoneAvoidance.Zone> zones, ZoneState[] state, float limit)
    {
        var count = 0;
        for (var z = 0; z < zones.Count; ++z)
        {
            if (state[z] == ZoneState.Exempt)
                continue;
            var s = 0f;
            for (var i = 0; i + 1 < path.Count && s <= limit; ++i)
            {
                var len = Vector3.Distance(path[i], path[i + 1]);
                if (Crosses(zones[z], path[i], path[i + 1], len, out var t0) && s + t0 * len <= limit)
                {
                    ++count;
                    break;
                }
                s += len;
            }
        }
        return count;
    }

    private static List<Pt> BuildPoints(NavmeshQuery.MeshPathDetail original, ref int flags)
    {
        var n = original.Waypoints.Count;
        var pts = new List<Pt>(n);
        for (var i = 0; i < n; ++i)
            pts.Add(new() { Pos = original.Waypoints[i].Position, Ref = original.Refs[i] });

        for (var i = 0; i < n; ++i)
        {
            if (original.OffMeshStart[i])
            {
                flags |= ZoneDetourFlags.OffMeshLinks;
                Lock(pts, i);
            }
            var type = original.Waypoints[i].Type;
            if (type is not (Navmesh.AreaId.None or Navmesh.AreaId.Default))
            {
                if ((type & Navmesh.AreaId.ClientPath) != 0)
                    flags |= ZoneDetourFlags.ClientPathLinks;
                Lock(pts, i - 1);
                Lock(pts, i);
            }
        }
        if (original.Partial)
        {
            flags |= ZoneDetourFlags.PartialPath;
            Lock(pts, n - 2);
        }
        return pts;
    }

    private static void Lock(List<Pt> pts, int i)
    {
        if (i < 0 || i + 1 >= pts.Count)
            return;
        var p = pts[i];
        p.LockOut = true;
        pts[i] = p;
    }

    private static bool Crosses(in ZoneAvoidance.Zone z, Vector3 a, Vector3 b, float len, out float t0)
        => ZoneAvoidance.FirstInsideInterval(z, a, b, out t0, out var t1) && (t1 - t0) * len > GrazeLength;

    private static bool FindFirstCrossing(Ctx c, List<Pt> pts, float limit, out int seg, out float tIn, out int zone)
    {
        seg = zone = -1;
        tIn = 0;
        var s = 0f;
        for (var i = 0; i + 1 < pts.Count && s <= limit; ++i)
        {
            var a = pts[i].Pos;
            var b = pts[i + 1].Pos;
            var len = Vector3.Distance(a, b);
            var bestT = float.PositiveInfinity;
            var best = -1;
            for (var z = 0; z < c.Zones.Count; ++z)
            {
                if (c.State[z] is ZoneState.GivenUp or ZoneState.Exempt)
                    continue;
                if (Crosses(c.Zones[z], a, b, len, out var t0) && t0 < bestT)
                {
                    bestT = t0;
                    best = z;
                }
            }
            if (best >= 0 && s + bestT * len <= limit)
            {
                seg = i;
                tIn = bestT;
                zone = best;
                return true;
            }
            s += len;
        }
        return false;
    }

    // 彼此間隙窄到留不出兩側 Clearance 的區域併成一群，整群一起繞。
    private static List<int> BuildCluster(Ctx c, int seed)
    {
        var cluster = new List<int> { seed };
        var inCluster = new bool[c.Zones.Count];
        inCluster[seed] = true;
        for (var k = 0; k < cluster.Count; ++k)
        {
            var a = c.Zones[cluster[k]];
            for (var j = 0; j < c.Zones.Count; ++j)
            {
                if (inCluster[j] || c.State[j] is ZoneState.GivenUp or ZoneState.Exempt)
                    continue;
                var b = c.Zones[j];
                if (Vector2.Distance(a.Center2, b.Center2) < a.Outer + b.Outer + 2 * Clearance && MathF.Abs(a.Center.Y - b.Center.Y) < 2 * ZoneAvoidance.ZoneHalfHeight)
                {
                    inCluster[j] = true;
                    cluster.Add(j);
                }
            }
        }
        return cluster;
    }

    // 先貼著區域繞（Clearance）；兩側都被地形擋住時再試一圈更外面的（WideClearance），常能避開貼在區域邊上的樹或石頭。
    // 凸包兩側都不通（夾在兩個區域或區域與牆之間的窄縫、凸包把唯一的路口整個蓋住）時再做格點搜尋。
    private static Outcome TryDetour(Ctx c, List<Pt> pts, int seg, float tIn, List<int> cluster, float budget, CancellationToken cancel)
    {
        var first = TryDetour(c, pts, seg, tIn, cluster, budget, Clearance, cancel);
        if (first is Outcome.Ok or Outcome.Locked)
            return first;
        c.Trace?.Add($"  retry with clearance {WideClearance}");
        var wide = TryDetour(c, pts, seg, tIn, cluster, budget, WideClearance, cancel);
        if (wide == Outcome.Ok)
            return Outcome.Ok;
        var grid = GridDetour(c, pts, seg, cluster, budget, false, cancel);
        if (grid == Outcome.Ok)
            return Outcome.Ok;
        return first == Outcome.OverBudget || wide == Outcome.OverBudget || grid == Outcome.OverBudget ? Outcome.OverBudget : first;
    }

    // 原路被替換的那一段：A 在線段 IA 上（原路進入凸包處）、B 在線段 IB 上（離開凸包處），兩點已貼網格。
    private struct Span
    {
        public int IA, IB;
        public Vector3 APos, BPos;
        public Pt A, B;
        public List<Vector2> Hull;
    }

    private static Outcome FindSpan(Ctx c, List<Pt> pts, int seg, List<int> cluster, float clearance, out Span span)
    {
        span = default;
        var samples = new List<Vector2>();
        foreach (var zi in cluster)
            ZoneAvoidance.AppendHullSamples(c.Zones[zi], clearance, samples);
        var hull = ZoneAvoidance.ConvexHull(samples);
        if (hull.Count < 3)
            return Outcome.NoHull;

        // 往回找原路進入凸包的點 A、往前找離開凸包的點 B；兩者之間整段換掉。
        var iA = seg;
        float tA;
        while (true)
        {
            if (!ZoneAvoidance.ClipSegment(hull, Flat(pts[iA].Pos), Flat(pts[iA + 1].Pos), out var te, out _))
            {
                if (iA == seg)
                    return Outcome.NoHull;
                ++iA;
                tA = 0;
                break;
            }
            if (te > 1e-5f || iA == 0)
            {
                tA = te;
                break;
            }
            --iA;
        }

        var iB = seg;
        float tB;
        while (true)
        {
            if (!ZoneAvoidance.ClipSegment(hull, Flat(pts[iB].Pos), Flat(pts[iB + 1].Pos), out _, out var tx))
            {
                if (iB == seg)
                    return Outcome.NoHull;
                tB = 0;
                break;
            }
            if (tx < 1 - 1e-5f || iB == pts.Count - 2)
            {
                tB = tx;
                break;
            }
            ++iB;
        }
        if (iA > iB || iA == iB && tA > tB)
            return Outcome.NoHull;

        for (var k = iA; k <= iB; ++k)
            if (pts[k].LockOut)
                return Outcome.Locked;

        var aPos = Vector3.Lerp(pts[iA].Pos, pts[iA + 1].Pos, tA);
        var bPos = Vector3.Lerp(pts[iB].Pos, pts[iB + 1].Pos, tB);
        if (!SnapOnPath(c, pts[iA], aPos, out var aSnap) || !SnapOnPath(c, pts[iB], bPos, out var bSnap))
            return Outcome.SnapFailed;
        span = new() { IA = iA, IB = iB, APos = aPos, BPos = bPos, A = aSnap, B = bSnap, Hull = hull };
        return Outcome.Ok;
    }

    // 原路上一點貼網格。長的斜坡段用兩端內插的高度可能離地面超過 SnapHalfY（找不到面、或貼到別層），
    // 所以就近貼不上時改從該段起點沿網格射線走過去取地面。
    private static bool SnapOnPath(Ctx c, in Pt segStart, Vector3 p, out Pt snapped)
    {
        if (Snap(c, p, out snapped) && Flat(snapped.Pos - p).Length() <= EndpointSnapMaxShift)
            return true;
        return SampleOnSegment(c, segStart, p, out snapped);
    }

    private static Outcome TryDetour(Ctx c, List<Pt> pts, int seg, float tIn, List<int> cluster, float budget, float clearance, CancellationToken cancel)
    {
        var found = FindSpan(c, pts, seg, cluster, clearance, out var span);
        if (found != Outcome.Ok)
            return found;
        var (iA, iB, aPos, bPos, aSnap, bSnap, hull) = (span.IA, span.IB, span.APos, span.BPos, span.A, span.B, span.Hull);

        var obstacles = new List<int>(cluster);
        var strict = new List<int>();
        for (var z = 0; z < c.Zones.Count; ++z)
        {
            if (c.State[z] == ZoneState.Cleared && !obstacles.Contains(z))
                obstacles.Add(z);
            if (c.State[z] != ZoneState.Exempt)
                strict.Add(z);
        }

        // 原路在 A 之前、B 之後的一段也放進候選：拉直時可以提早切出去、繞過之後直接往後面的路走，不必先回到 B。
        var back = BackWindow(c, pts, iA, aPos, strict, out var keep);
        var forward = ForwardWindow(c, pts, iB, bPos, out var resume);

        List<Pt>? best = null;
        var bestCross = int.MaxValue;
        var bestTotal = float.PositiveInfinity;
        var anyValid = false;
        foreach (var ccw in (ReadOnlySpan<bool>)[true, false])
        {
            cancel.ThrowIfCancellationRequested();
            var chain = ZoneAvoidance.BuildChain(hull, Flat(aSnap.Pos), Flat(bSnap.Pos), ccw);
            var cand = new List<Pt>(chain.Count) { aSnap };
            var prevY = aSnap.Pos.Y;
            var dropped = 0;
            for (var k = 1; k + 1 < chain.Count; ++k)
            {
                if (Snap(c, new Vector3(chain[k].X, prevY, chain[k].Y), out var v))
                {
                    cand.Add(v);
                    prevY = v.Pos.Y;
                }
                else
                {
                    ++dropped;
                }
            }
            cand.Add(bSnap);

            var dense = Densify(c, cand, out var localPaths);
            List<Pt>? pulled = null;
            if (dense != null)
            {
                var full = new List<Pt>(back.Count + dense.Count + forward.Count);
                full.AddRange(back);
                full.AddRange(dense);
                full.AddRange(forward);
                pulled = Pull(c, full, back.Count, back.Count + dense.Count - 1, obstacles, strict);
            }
            if (pulled == null)
            {
                c.Trace?.Add($"  {(ccw ? "ccw" : "cw")}: blocked (chain {chain.Count}, dropped {dropped}, local {localPaths}{(dense == null ? ", ring broken" : "")})");
                continue;
            }
            anyValid = true;
            var spliced = Splice(pts, keep, pulled, resume);
            var total = PathLength(spliced);
            var crossings = CountOtherCrossings(c, pulled, cluster);
            c.Trace?.Add($"  {(ccw ? "ccw" : "cw")}: ok {pulled.Count} pts (window -{back.Count}/+{forward.Count}), total {total:f1}/{budget:f1}, other crossings {crossings}");
            if (total > budget)
                continue;
            if (crossings < bestCross || crossings == bestCross && total < bestTotal)
            {
                best = spliced;
                bestCross = crossings;
                bestTotal = total;
            }
        }
        if (best == null)
            return anyValid ? Outcome.OverBudget : Outcome.Blocked;

        pts.Clear();
        pts.AddRange(best);
        return Outcome.Ok;
    }

    // pts[0..keep]＋拉直後的那段＋pts[resume..]；相鄰重複點併掉時留後面那一點（終點、原路點保留原值與 LockOut）。
    private static List<Pt> Splice(List<Pt> pts, int keep, List<Pt> pulled, int resume)
    {
        var spliced = new List<Pt>(pts.Count + pulled.Count);
        for (var k = 0; k <= keep; ++k)
            spliced.Add(pts[k]);
        foreach (var p in pulled)
        {
            if (spliced.Count == 0 || Vector3.Distance(p.Pos, spliced[^1].Pos) > SamePoint)
                spliced.Add(p);
            else if (spliced.Count > 1)
                spliced[^1] = p;
        }
        for (var k = resume; k < pts.Count; ++k)
        {
            if (k == resume && spliced.Count > 1 && Vector3.Distance(pts[k].Pos, spliced[^1].Pos) <= SamePoint)
                spliced[^1] = pts[k];
            else
                spliced.Add(pts[k]);
        }
        return spliced;
    }

    // A 之前沿原路往回最多 LookBack 米的點（由遠到近，含轉折點與每 WindowStep 米一個取樣）。
    // 遇到不可替換的路段、或原路本身穿過任何區域就停。keep＝拼接時原路保留到哪一點（含）。
    private static List<Pt> BackWindow(Ctx c, List<Pt> pts, int iA, Vector3 aPos, List<int> strict, out int keep)
    {
        var rev = new List<Pt>();
        keep = iA;
        var cur = aPos;
        var left = LookBack;
        for (var k = iA; k >= 0; --k)
        {
            if (k < iA && pts[k].LockOut)
                break;
            var start = pts[k].Pos;
            var len = Vector3.Distance(start, cur);
            if (CrossesAny(c, start, cur, strict))
                break;
            for (var d = WindowStep; d < len - SamePoint && d <= left; d += WindowStep)
            {
                if (SampleOnSegment(c, pts[k], Vector3.Lerp(cur, start, d / len), out var s))
                {
                    rev.Add(s);
                    keep = k;
                }
            }
            if (len > left)
                break;
            left -= len;
            rev.Add(pts[k]);
            keep = k - 1;
            cur = start;
        }
        rev.Reverse();
        return rev;
    }

    // B 之後沿原路往前最多 LookAhead 米的點（由近到遠）；遇到不可替換的路段就停。resume＝拼接後從原路的哪一點接回。
    // resumes 非 null 時逐點記下「拼接到這一點為止時從原路的哪一點接回」。
    private static List<Pt> ForwardWindow(Ctx c, List<Pt> pts, int iB, Vector3 bPos, out int resume, List<int>? resumes = null)
    {
        var res = new List<Pt>();
        resume = iB + 1;
        var cur = bPos;
        var left = LookAhead;
        for (var m = iB + 1; m < pts.Count; ++m)
        {
            if (m - 1 > iB && pts[m - 1].LockOut)
                break;
            var end = pts[m].Pos;
            var len = Vector3.Distance(cur, end);
            for (var d = WindowStep; d < len - SamePoint && d <= left; d += WindowStep)
            {
                if (SampleOnSegment(c, pts[m - 1], Vector3.Lerp(cur, end, d / len), out var s))
                {
                    res.Add(s);
                    resume = m;
                    resumes?.Add(m);
                }
            }
            if (len > left)
                break;
            left -= len;
            res.Add(pts[m]);
            resume = m + 1;
            resumes?.Add(m + 1);
            cur = end;
        }
        return res;
    }

    // 原路線段上的取樣點：從該段起點沿網格射線走過去取多邊形（不用最近多邊形，免得貼到上下別層），XZ 必須不動。
    private static bool SampleOnSegment(Ctx c, in Pt from, Vector3 p, out Pt s)
    {
        s = default;
        if (from.Ref == 0)
            return false;
        var mq = c.Query.MeshQuery;
        var status = mq.Raycast(from.Ref, from.Pos.SystemToRecast(), p.SystemToRecast(), c.Filter, 0, ref c.Hit, 0);
        if (status.Failed() || c.Hit.t != float.MaxValue || c.Hit.path.Count == 0)
            return false;
        var poly = c.Hit.path[^1];
        if (mq.ClosestPointOnPoly(poly, p.SystemToRecast(), out var cp, out _).Failed())
            return false;
        var pos = cp.RecastToSystem();
        if (Flat(pos - p).LengthSquared() > OnLineXZ * OnLineXZ)
            return false;
        s = new() { Pos = pos, Ref = poly };
        return true;
    }

    private static bool CrossesAny(Ctx c, Vector3 a, Vector3 b, List<int> zones)
    {
        var len = Vector3.Distance(a, b);
        foreach (var z in zones)
            if (Crosses(c.Zones[z], a, b, len, out _))
                return true;
        return false;
    }

    // 相鄰兩點直線走不通時插入一段就地尋路的結果；接不起來回 null（這一側不通）。
    private static List<Pt>? Densify(Ctx c, List<Pt> cand, out int localPaths)
    {
        localPaths = 0;
        var res = new List<Pt>(cand.Count) { cand[0] };
        for (var k = 0; k + 1 < cand.Count; ++k)
        {
            if (!Walkable(c, cand[k], cand[k + 1]))
            {
                if (!AppendLocalPath(c, cand[k], cand[k + 1], res))
                    return null;
                ++localPaths;
            }
            res.Add(cand[k + 1]);
        }
        return res;
    }

    // 有上限的切片 A*（不經 off-mesh 連結、不能是部分路徑、長度不超過直線的 LocalDetourFactor 倍＋LocalDetourExtra），
    // 把中間的拉直轉折點加進 output（不含頭尾）。
    private static bool AppendLocalPath(Ctx c, in Pt a, in Pt b, List<Pt> output)
    {
        if (a.Ref == 0 || b.Ref == 0)
            return false;
        var mq = c.Query.MeshQuery;
        var status = mq.InitSlicedFindPath(a.Ref, b.Ref, a.Pos.SystemToRecast(), b.Pos.SystemToRecast(), c.LocalFilter, 0);
        if (status.InProgress())
            status = mq.UpdateSlicedFindPath(LocalSearchIterations, out _);
        if (status.InProgress() || status.Failed())
            return false;
        var fin = mq.FinalizeSlicedFindPath(ref c.Corridor);
        if (fin.Failed() || fin.IsPartial() || c.Corridor.Count == 0 || c.Corridor[^1] != b.Ref)
            return false;
        if (mq.FindStraightPath(a.Pos.SystemToRecast(), b.Pos.SystemToRecast(), c.Corridor, ref c.Straight, 64, 0).Failed() || c.Straight.Count < 2)
            return false;

        var start = output.Count;
        var prev = a.Pos;
        var len = 0f;
        for (var k = 1; k + 1 < c.Straight.Count; ++k)
        {
            var sp = c.Straight[k];
            if ((sp.flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0 || sp.refs == 0)
            {
                output.RemoveRange(start, output.Count - start);
                return false;
            }
            var pos = sp.pos.RecastToSystem();
            len += Vector3.Distance(prev, pos);
            prev = pos;
            output.Add(new() { Pos = pos, Ref = sp.refs });
        }
        len += Vector3.Distance(prev, b.Pos);
        if ((c.Straight[0].flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0 || len > Vector3.Distance(a.Pos, b.Pos) * LocalDetourFactor + LocalDetourExtra)
        {
            output.RemoveRange(start, output.Count - start);
            return false;
        }
        return true;
    }

    // 由遠到近找第一個接得上的點，貪婪拉直；某一點之後一個都接不上＝這一側不通。
    // [chainFrom, chainTo] 是繞行鏈（頭尾＝A、B）：鏈內兩點之間只避開這群與已繞開的區域（其餘留給下一輪）；
    // 碰到鏈外的新線段要避開所有不豁免的區域；鏈外相鄰兩點本來就在原路上，照原路接。
    private static List<Pt>? Pull(Ctx c, List<Pt> cand, int chainFrom, int chainTo, List<int> obstacles, List<int> strict)
    {
        var result = new List<Pt> { cand[0] };
        var i = 0;
        while (i < cand.Count - 1)
        {
            var next = -1;
            for (var j = cand.Count - 1; j > i; --j)
            {
                if (j == i + 1 && (i < chainFrom || i >= chainTo))
                {
                    next = j;
                    break;
                }
                var zones = i >= chainFrom && j <= chainTo ? obstacles : strict;
                if (ClearOf(c, cand[i].Pos, cand[j].Pos, zones) && Walkable(c, cand[i], cand[j]))
                {
                    next = j;
                    break;
                }
            }
            if (next < 0)
            {
                c.Trace?.Add($"    stuck at {i}/{cand.Count - 1}: next clear={ClearOf(c, cand[i].Pos, cand[i + 1].Pos, obstacles)} walkable={Walkable(c, cand[i], cand[i + 1])}");
                return null;
            }
            result.Add(cand[next]);
            i = next;
        }
        return result;
    }

    private static bool ClearOf(Ctx c, Vector3 a, Vector3 b, List<int> obstacles)
    {
        foreach (var z in obstacles)
            if (!ZoneAvoidance.SegmentClear(c.Zones[z], a, b, ValidateMargin))
                return false;
        return true;
    }

    // 網格射線是 2D 的：抵達終點的 XZ 之後，再確認停下的多邊形與終點是同一層。
    private static bool Walkable(Ctx c, in Pt a, in Pt b)
    {
        if (a.Ref == 0 || b.Ref == 0)
            return false;
        var mq = c.Query.MeshQuery;
        var status = mq.Raycast(a.Ref, a.Pos.SystemToRecast(), b.Pos.SystemToRecast(), c.Filter, 0, ref c.Hit, 0);
        if (status.Failed() || c.Hit.t != float.MaxValue || c.Hit.path.Count == 0)
            return false;
        var last = c.Hit.path[^1];
        if (last == b.Ref)
            return true;
        // 終點常落在兩個多邊形的共用邊上（拉直的轉折點），GetPolyHeight 對邊上的點會失敗，所以用最近點判斷。
        if (mq.ClosestPointOnPoly(last, b.Pos.SystemToRecast(), out var cp, out var over).Failed())
            return false;
        return (over || Vector2.Distance(Flat(b.Pos), new(cp.X, cp.Z)) <= 0.05f) && MathF.Abs(cp.Y - b.Pos.Y) <= LevelTolerance;
    }

    private static bool Snap(Ctx c, Vector3 p, out Pt snapped)
    {
        snapped = default;
        var poly = c.Query.FindNearestMeshPoly(p, SnapHalfXZ, SnapHalfY);
        if (poly == 0 || c.Query.FindNearestPointOnMeshPoly(p, poly) is not { } pos)
            return false;
        snapped = new() { Pos = pos, Ref = poly };
        return true;
    }

    private static int CountOtherCrossings(Ctx c, List<Pt> path, List<int> cluster)
    {
        var count = 0;
        for (var z = 0; z < c.Zones.Count; ++z)
        {
            if (c.State[z] != ZoneState.Pending || cluster.Contains(z))
                continue;
            for (var i = 0; i + 1 < path.Count; ++i)
            {
                if (Crosses(c.Zones[z], path[i].Pos, path[i + 1].Pos, Vector3.Distance(path[i].Pos, path[i + 1].Pos), out _))
                {
                    ++count;
                    break;
                }
            }
        }
        return count;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static float PathLength(List<Pt> path)
    {
        var len = 0f;
        for (var i = 0; i + 1 < path.Count; ++i)
            len += Vector3.Distance(path[i].Pos, path[i + 1].Pos);
        return len;
    }
}
