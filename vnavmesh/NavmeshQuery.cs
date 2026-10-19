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

/// <summary>
/// 🔴 不可重入、非執行緒安全：同一顆實例同時只能有一個尋路。<see cref="PathfindMesh"/> 就地清空並
/// 改寫 <see cref="LastPath"/>，<see cref="PathfindVolume"/> 轉呼叫的 <see cref="VoxelPathfind"/> 亦
/// 重用內部緩衝。呼叫端一律走 <c>NavmeshManager.QueryPath</c>（任務鏈序列化），繞過者自負互斥。
/// </summary>
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

    // public：AvoidRadiusFilter 要繼承它。
    // 📌 成本倍率的語意:一律「調高一般連結的成本」而不是「調低 off-mesh 連結的成本」——
    //    調低會干擾 A* 的啟發式(啟發式假設成本不低於直線距離)。
    //    🔴 這仍然是全域尋路權重變更,無法離線證明對台服 52 個區域的既有路線沒有影響。
    public class TeleportAwareFilter : IDtQueryFilter
    {
        private readonly DtQueryDefaultFilter _f = new();

        public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        {
            var cst = _f.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
            return cst * CostMultiplier(curPoly.GetArea(), nextPoly?.GetArea() ?? 0);
        }

        /// <summary>GetCost 的倍率：一般 10；正在跨越連結本身時按連結種類打折。</summary>
        public static float CostMultiplier(int curPolyArea, int nextPolyArea)
        {
            var costMulti = 10f;

            var curArea = (Navmesh.AreaId)curPolyArea;
            var nextArea = (Navmesh.AreaId)nextPolyArea;

            // 兩者只差 Endpoint 這一個位 ⇒ 這一步正好是在跨越連結本身(起點多邊形 -> 終點多邊形)
            if ((curArea ^ nextArea) == Navmesh.AreaId.Endpoint)
                costMulti = curArea switch
                {
                    Navmesh.AreaId.Warp => 1,
                    Navmesh.AreaId.ClientPath => 3,
                    Navmesh.AreaId.Shortcut => 8,
                    _ => costMulti
                };

            return costMulti;
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

    /// <summary>上一次 <see cref="PathfindMesh"/> 走過的多邊形串。🔴 這是內部緩衝的直接參照而非
    /// 快照：下一次 <see cref="PathfindMesh"/> 會就地清空並改寫它 ⇒ 只在該次呼叫完成後、下一次
    /// 開始前有效，跨尋路保存這個參照等於讀到一份正在被改的清單。</summary>
    public List<long> LastPath => _lastPath;
    private List<long> _lastPath = [];
    private List<long> _detailPath = [];
    private List<long> _candidatePath = [];
    private List<DtStraightPath> _candidateStraight = [];
    private readonly List<long> _bestPath = [];
    private readonly List<DtStraightPath> _bestStraight = [];
    private PortalSampleSearch? _sampleSearch;
    private readonly BudgetFilter _budgetFilter = new();

    /// <summary>
    /// true＝地面尋路找更短的路：舊算法照跑；結果完整（終點走得到、沒帶 range、有拉直）時再用新搜尋各找一次，
    /// 比舊路線短的最短那條才採用，所以不會比舊算法長。新搜尋見 <see cref="PortalSampleSearch"/> 與
    /// <see cref="ShorterOption"/>；走廊不相連、拉直失敗或搜尋到上限還沒找到就不採用。
    /// false＝只跑舊算法。
    /// </summary>
    public bool ShortenPaths { get; set; } = true;

    /// <summary>
    /// true＝飛行尋路用快速搜尋（<see cref="VoxelPathfind.FastSearch"/>）：路線可能比舊搜尋略長，
    /// 換來耗時大幅下降；false＝只跑舊搜尋。
    /// </summary>
    public bool FastFlyPaths { get; set; } = true;

    /// <summary>
    /// true＝飛行搜尋改用下面三個值（<see cref="VoxelPathfindTuning"/>）；false＝<see cref="VoxelPathfindTuning.Default"/>，
    /// 與沒有這組設定之前逐點相同、三個值全部不讀。
    /// </summary>
    public bool FlyPathTuning { get; set; }
    /// <summary>啟發式權重；≤ 0＝各模式原本的值。</summary>
    public float FlyHeuristicWeight { get; set; } = 1.5f;
    /// <summary>搜尋中視線檢查的距離上限（米）；≤ 0＝不限。</summary>
    public float FlyRaycastRange { get; set; } = 25;
    /// <summary>搜尋步數上限；≤ 0＝<see cref="VoxelPathfind.MaxSteps"/>。</summary>
    public int FlyMaxSteps { get; set; } = 200000;
    /// <summary>true＝飛行搜尋沒走到終點時改沿步行網格的路線飛（<see cref="MeshCorridor"/>）；false＝照舊停在最近點。</summary>
    public bool FlyMeshFallback { get; set; }

    /// <summary>
    /// true＝飛行尋路先試高空路線（<see cref="VoxelHighRoute"/>）：比直線長不超過 <see cref="FlyHighRouteMaxDetour"/>% 就直接採用、
    /// 不跑搜尋；否則照跑搜尋，搜尋沒到終點、或高空路線比搜尋結果長不超過該比例時改用高空路線。false＝完全不碰、結果與沒有這組設定之前逐點相同。
    /// </summary>
    public bool FlyHighRoute { get; set; }
    /// <summary>平飛段上下左右至少留的空間（米）。</summary>
    public float FlyHighRouteMargin { get; set; } = 10;
    /// <summary>高空路線最多比原路線長幾 %。</summary>
    public float FlyHighRouteMaxDetour { get; set; } = 25;

    /// <summary>上一次 <see cref="PathfindVolume"/> 的高空路線判定；null＝沒試（關閉）。只在尋路工作鏈上寫。</summary>
    public HighRouteReport? LastHighRoute { get; private set; }

    /// <param name="CurrentLength">一般搜尋的路長；沒跑搜尋＝NaN。</param>
    public sealed record class HighRouteReport(bool Adopted, string Reason, float CruiseY, float Length, float CurrentLength, bool CurrentReached, double Ms);

    private VoxelPathfindTuning CurrentFlyTuning => FlyPathTuning
        ? new(FlyHeuristicWeight > 0 ? FlyHeuristicWeight : 0, FlyRaycastRange > 0 ? FlyRaycastRange : 0, FlyMaxSteps > 0 ? FlyMaxSteps : VoxelPathfind.MaxSteps)
        : VoxelPathfindTuning.Default;

    /// <summary>邊取樣搜尋一次最多建立的狀態數；超過就放棄這個候選（新增耗時的上限）。</summary>
    public const int MaxSearchNodes = 60000;
    /// <summary>any-angle 那次搜尋的 PassFilter 呼叫上限（A* 每個鄰居、射線每跨一格各算一次）。</summary>
    public const int MaxAnyAngleWork = 100000;

    // DotRecast 的 FindPath 沒有次數上限：額度用完後一律拒絕，搜尋就地收尾（多半變成部分路徑而不被採用）。
    private sealed class BudgetFilter : IDtQueryFilter
    {
        public IDtQueryFilter Inner = null!;
        public int Remaining;

        public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) => --Remaining >= 0 && Inner.PassFilter(refs, tile, poly);

        public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
            => Inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
    }

    // 每個多邊形的無向連通區編號，延後到第一次用到才建（在尋路工作鏈上，不在框架執行緒）。
    private int[][]? _components;
    private int[] _componentSizes = [];

    // 延後到第一次用到才掃（在尋路工作鏈上，不在框架執行緒）；-1＝還沒掃。
    private float _minCostMultiplier = -1;
    private float MinCostMultiplier => _minCostMultiplier >= 0 ? _minCostMultiplier : (_minCostMultiplier = PathShortening.MinCostMultiplier(MeshQuery.GetAttachedNavMesh()));

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh/*, s => Service.Log.Debug(s)*/);
        if (navmesh.Volume != null)
            VolumeQuery = new(navmesh.Volume);
    }

    private static DtFindPathOption LegacyOption(float range, bool useRaycast)
        => new(range > 0 ? new ToleranceHeuristic(range) : DtDefaultQueryHeuristic.Default, useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? 5 : 0);

    // 成本＝距離×倍率（≥ 最小倍率），所以啟發式乘上最小倍率仍不高估；any-angle 捷徑不限距離。
    private DtFindPathOption ShorterOption(bool useRaycast)
        => new(new DtDefaultQueryHeuristic(DtDefaultQueryHeuristic.H_SCALE * MinCostMultiplier), useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? float.MaxValue : 0);

    // straight／corridor 是舊算法的完整結果；候選裡最短的那條比它短、而且按連結倍率加權也不比它長，才就地換掉兩者。
    private void ShortenIfBetter(long startRef, long endRef, Vector3 from, Vector3 to, IDtQueryFilter filter, bool useRaycast, List<DtStraightPath> straight, List<long> corridor, TimeSpan legacyTime, string kind)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var nav = MeshQuery.GetAttachedNavMesh();
        var oldLength = PathShortening.Length(straight, to);
        var oldWeighted = PathShortening.WeightedLength(nav, straight, to);
        var bestLength = float.PositiveInfinity;
        string? winner = null, reject = null;

        void Consider(string name)
        {
            if (!PathShortening.CorridorConnected(nav, _candidatePath))
                reject = $"{name}走廊不相連";
            else if (MeshQuery.FindStraightPath(from.SystemToRecast(), to.SystemToRecast(), _candidatePath, ref _candidateStraight, 1024, 0).Failed())
                reject = $"{name}拉直失敗";
            else if (PathShortening.WeightedLength(nav, _candidateStraight, to) > oldWeighted + PathShortening.MinGain)
                reject = $"{name}會少走打折的連結";
            else if (PathShortening.Length(_candidateStraight, to) is var len && len < bestLength)
            {
                bestLength = len;
                winner = name;
                _bestPath.Clear();
                _bestPath.AddRange(_candidatePath);
                _bestStraight.Clear();
                _bestStraight.AddRange(_candidateStraight);
            }
        }

        // 新搜尋出任何例外都只放棄候選、沿用舊路線（純受控碼，不碰遊戲記憶體）。
        try
        {
            _sampleSearch ??= new(nav);
            if (_sampleSearch.Find(startRef, endRef, from, to, filter, DtDefaultQueryHeuristic.H_SCALE * MinCostMultiplier, MaxSearchNodes, _candidatePath))
                Consider("邊取樣");
            else
                reject = _sampleSearch.HitLimit ? "邊取樣超過搜尋上限" : "邊取樣沒有完整路徑";

            _budgetFilter.Inner = filter;
            _budgetFilter.Remaining = MaxAnyAngleWork;
            var status = MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), _budgetFilter, ref _candidatePath, ShorterOption(useRaycast));
            if (_candidatePath.Count > 0 && !status.IsPartial())
                Consider("any-angle");
            else
                reject = _budgetFilter.Remaining < 0 ? "any-angle 超過搜尋上限" : "any-angle 沒有完整路徑";
        }
        catch (Exception ex)
        {
            winner = null;
            reject = "新搜尋擲例外";
            PathShortening.NoteException(ex);
        }
        var newTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        var adopt = winner != null && bestLength < oldLength - PathShortening.MinGain;
        if (adopt)
        {
            straight.Clear();
            straight.AddRange(_bestStraight);
            corridor.Clear();
            corridor.AddRange(_bestPath);
        }
        PathShortening.Report(kind, from, to, oldLength, winner != null ? bestLength : float.NaN, adopt, winner ?? "", reject, legacyTime, newTime);
    }

    private int ComponentOf(long polyRef)
    {
        var nav = MeshQuery.GetAttachedNavMesh();
        _components ??= PathShortening.BuildComponents(nav, out _componentSizes);
        nav.GetTileAndPolyByRefUnsafe(polyRef, out var tile, out var poly);
        return _components[tile.index][poly.index];
    }

    /// <summary>搜尋最近可達點時依序試的立方體半邊長（米）；都找不到就照舊算法搜。</summary>
    public static readonly float[] NearestReachableRadii = [4, 16, 64];

    /// <summary>起點那一區少於這麼多個多邊形時舊算法本來就很快（搜完也只要幾毫秒），不改走最近點。</summary>
    public const int MinComponentPolysForNearest = 1000;

    // 起點與終點不在同一個無向連通區＝一定走不到：舊算法要把起點那一區整個搜完才回部分路徑。
    // 改成直接找同一區離終點最近的點當這次的目標；半徑 r 內找到且距離 ≤ r 才算數（更近的點一定也在框內）。
    private bool TryNearestReachable(long startRef, long endRef, Vector3 to, out long nearRef, out Vector3 nearPoint)
    {
        nearRef = 0;
        nearPoint = default;
        var comp = ComponentOf(startRef);
        if (comp == ComponentOf(endRef) || _componentSizes[comp] < MinComponentPolysForNearest)
            return false;
        foreach (var r in NearestReachableRadii)
        {
            var best = float.MaxValue;
            foreach (var p in FindIntersectingMeshPolys(to, new(r, r, r)))
            {
                if (ComponentOf(p) != comp || FindNearestPointOnMeshPoly(to, p) is not { } c)
                    continue;
                var d = Vector3.Distance(c, to);
                if (d < best)
                {
                    best = d;
                    nearRef = p;
                    nearPoint = c;
                }
            }
            if (best <= r)
                return true;
        }
        nearRef = 0;
        return false;
    }

    // 終點走不到時改用最近可達點當目標（goal），最後一點仍補原始終點；單向連結讓那一點其實也走不到時照舊對原終點搜。
    private DtStatus FindLegacyCorridor(long startRef, long endRef, Vector3 from, Vector3 to, IDtQueryFilter filter, float range, bool useRaycast, ref List<long> corridor, out long goalRef, out Vector3 goal)
    {
        goalRef = endRef;
        goal = to;
        if (ShortenPaths && range <= 0 && TryNearestReachable(startRef, endRef, to, out var nearRef, out var nearPoint))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var near = MeshQuery.FindPath(startRef, nearRef, from.SystemToRecast(), nearPoint.SystemToRecast(), filter, ref corridor, LegacyOption(range, useRaycast));
            var ok = corridor.Count > 0 && !near.IsPartial();
            PathShortening.NoteUnreachable(from, to, nearPoint, ok, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            if (ok)
            {
                goalRef = nearRef;
                goal = nearPoint;
                return near;
            }
        }
        return MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), filter, ref corridor, LegacyOption(range, useRaycast));
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
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastPath.Clear();
        var pathFilter = filter ?? _pathFilter;
        var status = FindLegacyCorridor(startRef, endRef, from, to, pathFilter, range, useRaycast, ref _lastPath, out var goalRef, out var goal);
        var legacyTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
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
            var success = MeshQuery.FindStraightPath(from.SystemToRecast(), goal.SystemToRecast(), _lastPath, ref straightPath, 1024, 0);
            if (success.Failed())
                Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");
            else if (ShortenPaths)
                ShortenOrExplain(startRef, goalRef, from, goal, pathFilter, useRaycast, range, status, straightPath, _lastPath, legacyTime, "一般尋路");
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

    // 刻意不比較：帶 range 時舊算法本來就提早停在容許範圍邊上（部分路徑），終點走不到時兩邊都要把整個連通區搜完。
    private void ShortenOrExplain(long startRef, long endRef, Vector3 from, Vector3 to, IDtQueryFilter filter, bool useRaycast, float range, DtStatus legacyStatus, List<DtStraightPath> straight, List<long> corridor, TimeSpan legacyTime, string kind)
    {
        if (range > 0)
            PathShortening.NotCompared(kind, "帶 range（走到附近就停）", from, to);
        else if (legacyStatus.IsPartial())
            PathShortening.NotCompared(kind, "終點走不到（部分路徑）", from, to);
        else
            ShortenIfBetter(startRef, endRef, from, to, filter, useRaycast, straight, corridor, legacyTime, kind);
    }

    /// <summary><see cref="PathfindMeshDetailed"/> 的結果。路徑點與同輸入的 <see cref="PathfindMesh"/> 逐點相同。</summary>
    public sealed class MeshPathDetail
    {
        public readonly List<Waypoint> Waypoints = [];
        /// <summary>每個路徑點所在的多邊形；0＝不知道（最後補上的終點、拉直路徑的終點）。</summary>
        public readonly List<long> Refs = [];
        /// <summary>該點是 off-mesh 連結的起點：下一段是跳躍／捷徑，不是貼地走的直線。</summary>
        public readonly List<bool> OffMeshStart = [];
        /// <summary>終點走不到：最後一段是從最後一個可達多邊形直線接到終點補上的。</summary>
        public bool Partial;
        public bool StraightPathFailed;
        /// <summary>非 null＝尋路失敗的原因，此時沒有路徑點。</summary>
        public string? Failure;
    }

    // 與 PathfindMesh（不帶 range／filter）同一套地面尋路，差別只在：不寫尋路 log、回報部分路徑與每點的多邊形、
    // 用自己的多邊形緩衝（LastPath 不變）。給「繞開區域」修補用，它要知道最後一段是不是直線補上的。
    public MeshPathDetail PathfindMeshDetailed(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling)
    {
        var res = new MeshPathDetail();
        var startRef = FindNearestMeshPoly(from);
        var endRef = FindNearestMeshPoly(to);
        if (startRef == 0 || endRef == 0)
        {
            res.Failure = $"failed to find polygon on a mesh ({startRef:X} -> {endRef:X})";
            return res;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var status = FindLegacyCorridor(startRef, endRef, from, to, _pathFilter, 0, useRaycast, ref _detailPath, out var goalRef, out var goal);
        var legacyTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (_detailPath.Count == 0)
        {
            res.Failure = $"failed to find path on mesh ({startRef:X} -> {endRef:X})";
            return res;
        }
        res.Partial = status.IsPartial() || goalRef != endRef;

        var endPos = to.SystemToRecast();
        if (useStringPulling)
        {
            var straightPath = new List<DtStraightPath>();
            res.StraightPathFailed = MeshQuery.FindStraightPath(from.SystemToRecast(), goal.SystemToRecast(), _detailPath, ref straightPath, 1024, 0).Failed();
            if (!res.StraightPathFailed && ShortenPaths)
                ShortenOrExplain(startRef, goalRef, from, goal, _pathFilter, useRaycast, 0, status, straightPath, _detailPath, legacyTime, "繞開仇恨範圍");
            foreach (var p in straightPath)
            {
                res.Waypoints.Add(new(p.pos.RecastToSystem(), GetAreaId(p.refs)));
                res.Refs.Add(p.refs);
                res.OffMeshStart.Add((p.flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0);
            }
        }
        else
        {
            foreach (var r in _detailPath)
            {
                res.Waypoints.Add(new(MeshQuery.GetAttachedNavMesh().GetPolyCenter(r).RecastToSystem(), GetAreaId(r)));
                res.Refs.Add(r);
                res.OffMeshStart.Add(false);
            }
        }
        res.Waypoints.Add(new(endPos.RecastToSystem()));
        res.Refs.Add(0);
        res.OffMeshStart.Add(false);
        return res;
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
        => PathfindVolume(from, to, useRaycast, useStringPulling, cancel, false);

    /// <param name="diving">呼叫端在框架執行緒上讀好的「正在潛水」；true 時不試高空路線（往上爬會浮出水面）。</param>
    public List<Waypoint> PathfindVolume(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel, bool diving)
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

        LastHighRoute = null;
        VoxelHighRoute.Route? high = null;
        var highMs = 0.0;
        if (FlyHighRoute)
        {
            var highStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            high = diving ? null : VoxelHighRoute.Build(VolumeQuery.Volume, startVoxel, endVoxel, from, to, FlyHighRouteMargin, cancel);
            highMs = System.Diagnostics.Stopwatch.GetElapsedTime(highStarted).TotalMilliseconds;
            if (high?.Outcome == VoxelHighRoute.Outcome.Ok && high.Length <= MaxHighRouteLength(Vector3.Distance(from, to)))
                return ToWaypoints(AdoptHighRoute(from, to, high, float.NaN, false, highMs), to);
        }

        var timer = Timer.Create();
        VolumeQuery.FastSearch = FastFlyPaths;
        VolumeQuery.Tuning = CurrentFlyTuning;
        var voxelPath = VolumeQuery.FindPath(startVoxel, endVoxel, from, to, useRaycast, false, cancel); // TODO: do we need intermediate points for string-pulling algo?
        var elapsed = timer.Value();
        if (voxelPath.Count == 0)
        {
            if (high?.Outcome == VoxelHighRoute.Outcome.Ok)
                return ToWaypoints(AdoptHighRoute(from, to, high, float.NaN, false, highMs), to);
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find path on volume");
            return new();
        }
        Service.Log.Debug($"Pathfind took {elapsed.TotalSeconds:f3}s: {string.Join(", ", voxelPath.Select(r => $"{r.p} {r.voxel:X}"))}");
        ReportVolumeQuery(from, to, voxelPath[^1].p, elapsed);
        if (FlyHighRoute)
        {
            var reached = VolumeQuery.LastReached;
            var current = PolylineLength(voxelPath);
            if (high?.Outcome == VoxelHighRoute.Outcome.Ok && (!reached || high.Length <= MaxHighRouteLength(current)))
                return ToWaypoints(AdoptHighRoute(from, to, high, current, reached, highMs), to);
            RejectHighRoute(from, to, high, current, reached, highMs);
        }
        if (FlyMeshFallback && !VolumeQuery.LastReached)
            voxelPath = MeshFallback(startVoxel, endVoxel, from, to, useRaycast, voxelPath, cancel);

        return ToWaypoints(voxelPath, to);
    }

    private float MaxHighRouteLength(float reference) => reference * (1 + MathF.Max(FlyHighRouteMaxDetour, 0) / 100);

    private static float PolylineLength(List<(ulong voxel, Vector3 p)> path)
    {
        var len = 0f;
        for (var i = 1; i < path.Count; ++i)
            len += Vector3.Distance(path[i - 1].p, path[i].p);
        return len;
    }

    private static List<Waypoint> ToWaypoints(List<(ulong voxel, Vector3 p)> voxelPath, Vector3 to)
    {
        // TODO: string-pulling support
        // 飛行路徑不經過自訂連結,所以全部都是一般路徑點(Default ⇒ CheckCondition 回 false)。
        var res = voxelPath.Select(r => new Waypoint(r.p)).ToList();
        res.Add(new(to));
        return res;
    }

    // 只在尋路工作鏈上呼叫（一次一筆），所以節流欄位不必同步。
    private DateTime _lastVolumeInfoLog = DateTime.MinValue;
    private int _volumeInfoSuppressed;
    private DateTime _lastFallbackInfoLog = DateTime.MinValue;
    private int _fallbackInfoSuppressed;
    private DateTime _lastHighRouteInfoLog = DateTime.MinValue;
    private int _highRouteInfoSuppressed;

    // current＝一般搜尋的路長（NaN＝沒跑搜尋，因為高空路線不比直線長超過上限、一定也不比搜尋結果長超過上限）。
    private List<(ulong voxel, Vector3 p)> AdoptHighRoute(Vector3 from, Vector3 to, VoxelHighRoute.Route high, float current, bool reached, double ms)
    {
        LastHighRoute = new(true, "採用", high.CruiseY, high.Length, current, reached, ms);
        var straight = Vector3.Distance(from, to);
        var why = float.IsNaN(current) ? $"不超過直線的 {100 + FlyHighRouteMaxDetour:f0}%，沒跑一般搜尋"
            : !reached ? "一般搜尋沒到終點" : $"一般搜尋 {current:f1} 米";
        var obstacle = float.IsNegativeInfinity(high.ObstacleTop) ? "走廊上沒有比兩端高的障礙" : $"走廊最高障礙頂 {high.ObstacleTop:f1}";
        var line = $"[pathfind] 飛行尋路高空路線 {from} -> {to}：採用；巡航高度 {high.CruiseY:f1}（比起點高 {high.CruiseY - from.Y:f1} 米、比終點高 {high.CruiseY - to.Y:f1} 米，{obstacle}，餘裕 {FlyHighRouteMargin:f0} 米）；路長 {high.Length:f1} 米＝直線 {straight:f1} 米的 {(straight > 0 ? high.Length / straight * 100 : 0):f0}%（{why}）；高空路線耗時 {ms:f1} ms";
        var now = DateTime.Now;
        if (now - _lastHighRouteInfoLog < TimeSpan.FromSeconds(10))
        {
            ++_highRouteInfoSuppressed;
            Service.Log.Debug(line);
        }
        else
        {
            _lastHighRouteInfoLog = now;
            Service.Log.Information(_highRouteInfoSuppressed > 0 ? $"{line}（上一行之後另有 {_highRouteInfoSuppressed} 筆同類只寫在 Debug）" : line);
            _highRouteInfoSuppressed = 0;
        }
        return high.Path!;
    }

    private void RejectHighRoute(Vector3 from, Vector3 to, VoxelHighRoute.Route? high, float current, bool reached, double ms)
    {
        var reason = high == null ? "潛水中"
            : high.Outcome != VoxelHighRoute.Outcome.Ok ? $"{VoxelHighRoute.Describe(high.Outcome)}（{(float.IsNaN(high.BlockedAt) ? "" : $"離起點水平 {high.BlockedAt:f0} 米處、")}當時巡航高度 {high.CruiseY:f1}、起點上方 {high.CeilingStart - from.Y:f0} 米／終點上方 {high.CeilingEnd - to.Y:f0} 米有實心）"
            : $"比一般搜尋長太多（{high.Length:f1} 米對 {current:f1} 米）";
        LastHighRoute = new(false, reason, high?.CruiseY ?? float.NaN, high?.Length ?? float.NaN, current, reached, ms);
        Service.Log.Debug($"[pathfind] 飛行尋路高空路線 {from} -> {to}：不採用（{reason}），照一般搜尋");
    }

    // 飛行搜尋沒走到終點：改問步行網格、沿它的轉角飛。走廊接不起來或出例外就回傳原本停在半路的路徑（＝關閉時的結果）。
    private List<(ulong voxel, Vector3 p)> MeshFallback(ulong startVoxel, ulong endVoxel, Vector3 from, Vector3 to, bool useRaycast, List<(ulong voxel, Vector3 p)> partial, CancellationToken cancel)
    {
        var vq = VolumeQuery!;
        var shortBy = Vector3.Distance(partial[^1].p, to);
        var steps = vq.LastSteps;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<Vector3>? corners = null;
        List<(ulong voxel, Vector3 p)>? corridor = null;
        var outcome = MeshCorridor.Outcome.NoRoute;
        string? error = null;
        try
        {
            corners = MeshCorridor.Corners(MeshQuery, _pathFilter, vq, from, to, useRaycast, cancel, out outcome);
            if (corners != null)
                corridor = VoxelCorridor.Follow(vq, (startVoxel, from), (endVoxel, to), corners, useRaycast, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            corridor = null;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var mesh = error != null ? $"擲例外（{error}）" : outcome switch
        {
            MeshCorridor.Outcome.NoStartMesh => "起點附近沒有飛得到的步行網格",
            MeshCorridor.Outcome.NoEndMesh => "終點附近沒有飛得到的步行網格",
            MeshCorridor.Outcome.NoRoute => "步行網格找不到路線",
            _ => $"步行網格給了 {corners!.Count} 個轉角",
        };
        var result = corridor != null ? $"沿走廊飛 {corridor.Count} 點，採用" : "走廊接不起來，沿用原本停在半路的路線";
        var line = $"[pathfind] 飛行尋路步行網格回退 {from} -> {to}：原因＝飛行搜尋停在離終點 {shortBy:f1} 米處（{steps} 步／上限 {vq.Tuning.MaxSteps}）；{mesh}、{result}；回退耗時 {ms:f0} ms";
        var now = DateTime.Now;
        if (now - _lastFallbackInfoLog < TimeSpan.FromSeconds(10))
        {
            ++_fallbackInfoSuppressed;
            Service.Log.Debug(line);
        }
        else
        {
            _lastFallbackInfoLog = now;
            Service.Log.Information(_fallbackInfoSuppressed > 0 ? $"{line}（上一行之後另有 {_fallbackInfoSuppressed} 筆同類只寫在 Debug）" : line);
            _fallbackInfoSuppressed = 0;
        }
        return corridor ?? partial;
    }

    // 飛行尋路摘要：慢（≥ 1 秒）或終點不可達時寫 Information（使用者回報用，10 秒內只寫一行），其餘寫 Debug。
    private void ReportVolumeQuery(Vector3 from, Vector3 to, Vector3 end, TimeSpan elapsed)
    {
        var vq = VolumeQuery!;
        var result = vq.LastReached ? "到達終點" : $"終點不可達，改飛到離終點 {Vector3.Distance(end, to):f1} 米的最近點";
        var summary = $"[pathfind] 飛行尋路 {from} -> {to}：{elapsed.TotalMilliseconds:f0} ms、{vq.LastSteps} 步、{result}、{(vq.FastSearch ? "快速搜尋" : "舊搜尋")}{(FlyPathTuning ? $"、調校（權重 {(vq.Tuning.HeuristicWeight > 0 ? $"{vq.Tuning.HeuristicWeight:f2}" : "原值")}、視線 {(vq.Tuning.RaycastRange > 0 ? $"{vq.Tuning.RaycastRange:f0} 米" : "不限")}、上限 {vq.Tuning.MaxSteps} 步）" : "")}";
        var notable = !vq.LastReached || elapsed.TotalSeconds >= 1;
        var now = DateTime.Now;
        if (!notable || now - _lastVolumeInfoLog < TimeSpan.FromSeconds(10))
        {
            if (notable)
                ++_volumeInfoSuppressed;
            Service.Log.Debug(summary);
            return;
        }
        _lastVolumeInfoLog = now;
        Service.Log.Information(_volumeInfoSuppressed > 0 ? $"{summary}（上一行之後另有 {_volumeInfoSuppressed} 筆同類只寫在 Debug）" : summary);
        _volumeInfoSuppressed = 0;
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
    //    FLAG_UNREACHABLE。那個旗標由 FloodFill/Prune 設定,我方未取,
    //    所以沒有任何多邊形會帶那個旗標,參數加了也是 no-op。接上去的風險見 IPCProvider 的
    //    Query.Mesh.PointOnFloor 註冊處。
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
