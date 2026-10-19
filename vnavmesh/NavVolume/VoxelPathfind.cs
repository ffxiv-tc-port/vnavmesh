using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Navmesh.NavVolume;

// 本檔契約：
// - FastSearch=false 與舊版逐點相同（同一個亂數序列下）。鄰居列舉與視線檢查改成不配置，走訪順序不變。
// - FastSearch=true：Lazy Theta*（產生鄰居時先假設看得到父節點的父節點，展開時才驗視線）、啟發式＝直線距離
//   ×FastHeuristicWeight＋垂直懲罰的下界（只放大距離項）、「最近點」一律用直線距離挑、視線步進原地打轉當成看不到。
// - FastSearch 與 Tuning 在 Start 時讀一次，搜尋途中改它們不影響這一次。
// - Tuning＝VoxelPathfindTuning.Default 時兩種模式都與沒有 Tuning 之前逐點相同。
public class VoxelPathfind
{
    public struct Node
    {
        public float GScore;
        public float HScore;
        public ulong Voxel; // voxel map index corresponding to this node
        public int ParentIndex; // index in the node list of the node we entered from
        public int OpenHeapIndex; // -1 if in closed list, otherwise index in open list
        public Vector3 Position;
        public float GoalDistance; // 到終點的直線距離 ×0.999，終點那一格＝0；挑「最近點」與判斷到達都用它
        public int ViaIndex; // FastSearch：產生這個節點的那個已展開節點
        public bool ParentVerified; // FastSearch：到 ParentIndex 的視線已經驗過
    }

    public const int MaxSteps = 1000000;
    public const float FastHeuristicWeight = 1.1f;
    // 成本與 FastSearch 啟發式共用：任何路徑的垂直懲罰總和 ≥ 係數 × 起終點高度差，所以加進啟發式不會高估。
    private const float VerticalPenaltyFactor = 0.2f;

    private VoxelMap _volume;
    private List<Node> _nodes = new(); // grow only (TODO: consider chunked vector)
    private Dictionary<ulong, int> _nodeLookup = new(); // voxel -> node index
    private List<int> _openList = new(); // heap containing node indices
    private int _bestNodeIndex;
    private ulong _goalVoxel;
    private Vector3 _goalPos;
    private bool _useRaycast;
    private bool _allowReopen = false; // this is extremely expensive and doesn't seem to actually improve the result
    private float _raycastLimitSq = float.MaxValue;
    private bool _fast;
    private float _heuristicWeight; // 0＝舊搜尋原本的式子（不乘權重）
    private bool _collectNeighbours;
    private readonly List<ulong> _neighbourScratch = new();
    private readonly List<(float cost, int index)> _repairScratch = new();
    private static readonly Comparison<(float cost, int index)> ByCost = (a, b) => a.cost.CompareTo(b.cost);

    public VoxelMap Volume => _volume;
    public Span<Node> NodeSpan => CollectionsMarshal.AsSpan(_nodes);

    /// <summary>true＝快速搜尋（見檔頭）；false＝與舊版逐點相同。</summary>
    public bool FastSearch { get; set; }
    /// <summary>啟發式權重、搜尋中視線檢查的距離上限、步數上限（見 <see cref="VoxelPathfindTuning"/>）。</summary>
    public VoxelPathfindTuning Tuning { get; set; } = VoxelPathfindTuning.Default;
    /// <summary>上一次搜尋執行的步數（展開的節點數）。</summary>
    public int LastSteps { get; private set; }
    /// <summary>上一次搜尋是否走到終點那一格；false＝路徑停在找到的最近點。</summary>
    public bool LastReached => _nodes.Count > 0 && NodeSpan[_bestNodeIndex].GoalDistance <= 0;

    public VoxelPathfind(VoxelMap volume)
    {
        _volume = volume;
    }

    /// <summary>
    /// 🔴 不可重入、非執行緒安全：<see cref="Start"/> 會清空並重用 <c>_nodes</c>／<c>_nodeLookup</c>／
    /// <c>_openList</c>，同一顆實例同時有兩個呼叫會互相吃掉對方的節點表。呼叫端一律經
    /// <c>NavmeshManager.QueryPath</c>（任務鏈序列化）或自行保證同一時間只有一個呼叫。
    /// </summary>
    /// <param name="maxSteps">0＝用 <see cref="Tuning"/> 的步數上限；正數＝這一次改用它。</param>
    public List<(ulong voxel, Vector3 p)> FindPath(ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos, bool useRaycast, bool returnIntermediatePoints, CancellationToken cancel, int maxSteps = 0)
    {
        _useRaycast = useRaycast;
        Start(fromVoxel, toVoxel, fromPos, toPos);
        Execute(cancel, maxSteps > 0 ? maxSteps : Tuning.MaxSteps);
        return BuildPathToVisitedNode(_bestNodeIndex, returnIntermediatePoints);
    }

    public void Start(ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        _nodes.Clear();
        _nodeLookup.Clear();
        _openList.Clear();
        _bestNodeIndex = 0;
        LastSteps = 0;
        _fast = FastSearch;
        var tuning = Tuning;
        _heuristicWeight = tuning.HeuristicWeight > 0 ? tuning.HeuristicWeight : _fast ? FastHeuristicWeight : 0;
        _raycastLimitSq = tuning.RaycastRange > 0 ? tuning.RaycastRange * tuning.RaycastRange : float.MaxValue;
        if (fromVoxel == VoxelMap.InvalidVoxel || toVoxel == VoxelMap.InvalidVoxel)
        {
            Service.Log.Error($"Bad input cells: {fromVoxel:X} -> {toVoxel:X}");
            return;
        }

        _goalVoxel = toVoxel;
        _goalPos = toPos;

        _nodes.Add(new() { HScore = HeuristicDistance(fromVoxel, fromPos), GoalDistance = GoalDistance(fromVoxel, fromPos), Voxel = fromVoxel, ParentIndex = 0, OpenHeapIndex = -1, Position = fromPos, ParentVerified = true }); // start's parent is self
        _nodeLookup[fromVoxel] = 0;
        AddToOpen(0);
        //Service.Log.Debug($"volume pathfind: {fromPos} ({fromVoxel:X}) to {toPos} ({toVoxel:X})");
    }

    public void Execute(CancellationToken cancel, int maxSteps = MaxSteps)
    {
        for (int i = 0; i < maxSteps; ++i)
        {
            if (!ExecuteStep())
                return;
            LastSteps = i + 1;
            if ((i & 0x3ff) == 0)
                cancel.ThrowIfCancellationRequested();
        }
    }

    // returns whether search is to be terminated; on success, first node of the open list would contain found goal
    public bool ExecuteStep()
    {
        if (_openList.Count == 0 || NodeSpan[_bestNodeIndex].GoalDistance <= 0)
            return false;

        var curNodeIndex = PopMinOpen();
        if (_fast)
            VerifyParent(curNodeIndex);
        //Service.Log.Debug($"volume pathfind: considering {curNode.Voxel:X} (#{curNodeIndex}), g={curNode.GScore:f3}, h={curNode.HScore:f3}");

        var curVoxel = NodeSpan[curNodeIndex].Voxel;
        VisitNeighbours(curNodeIndex, curVoxel, 0, -1, 0);
        VisitNeighbours(curNodeIndex, curVoxel, 0, +1, 0);
        VisitNeighbours(curNodeIndex, curVoxel, -1, 0, 0);
        VisitNeighbours(curNodeIndex, curVoxel, +1, 0, 0);
        VisitNeighbours(curNodeIndex, curVoxel, 0, 0, -1);
        VisitNeighbours(curNodeIndex, curVoxel, 0, 0, +1);
        return true;
    }

    private List<(ulong voxel, Vector3 p)> BuildPathToVisitedNode(int nodeIndex, bool returnIntermediatePoints)
    {
        var res = new List<(ulong voxel, Vector3 p)>();
        if (nodeIndex < _nodes.Count)
        {
            if (_fast)
                VerifyParent(nodeIndex);
            var nodeSpan = NodeSpan;
            ref var lastNode = ref nodeSpan[nodeIndex];
            res.Add((lastNode.Voxel, lastNode.Position));
            //Service.Log.Debug($"volume pathfind: backpath from {lastNode.Voxel:X} (#{nodeIndex})");
            while (nodeSpan[nodeIndex].ParentIndex != nodeIndex)
            {
                ref var prevNode = ref nodeSpan[nodeIndex];
                var nextIndex = prevNode.ParentIndex;
                ref var nextNode = ref nodeSpan[nextIndex];
                //Service.Log.Debug($"volume pathfind: backpath next {nextNode.Voxel:X} (#{nextIndex})");
                if (returnIntermediatePoints)
                {
                    var delta = nextNode.Position - prevNode.Position;
                    foreach (var v in VoxelSearch.EnumerateVoxelsInLine(_volume, prevNode.Voxel, nextNode.Voxel, prevNode.Position, nextNode.Position))
                    {
                        //Service.Log.Debug($"volume pathfind: intermediate {v}");
                        res.Add((v.voxel, prevNode.Position + v.t * delta));
                    }
                }
                else
                {
                    res.Add((nextNode.Voxel, nextNode.Position));
                }
                nodeIndex = nextIndex;
            }
            res.Reverse();
        }
        return res;
    }

    // 舊版 IEnumerable 列舉的不配置版：同一組鄰居、同一個先後順序（FastSearch=false 逐點相同靠的就是這個）。
    private void VisitNeighbours(int cur, ulong voxel, int dx, int dy, int dz)
    {
        var l0Desc = _volume.Levels[0];
        var l1Desc = _volume.Levels[1];
        var l2Desc = _volume.Levels[2];
        var l0Index = VoxelMap.DecodeIndex(ref voxel); // should always be valid
        var l1Index = VoxelMap.DecodeIndex(ref voxel);
        var l2Index = VoxelMap.DecodeIndex(ref voxel);
        var l0Coords = l0Desc.IndexToVoxel(l0Index);
        var l1Coords = l1Desc.IndexToVoxel(l1Index); // not valid if l1 is invalid
        var l2Coords = l2Desc.IndexToVoxel(l2Index); // not valid if l2 is invalid

        if (l2Index != VoxelMap.IndexLevelMask)
        {
            // starting from L2 node
            var l2Neighbour = (l2Coords.x + dx, l2Coords.y + dy, l2Coords.z + dz);
            if (l2Desc.InBounds(l2Neighbour))
            {
                // L2->L2 in same L1 tile
                var neighbourVoxel = VoxelMap.EncodeIndex(l2Desc.VoxelToIndex(l2Neighbour));
                neighbourVoxel = VoxelMap.EncodeIndex(l1Index, neighbourVoxel);
                neighbourVoxel = VoxelMap.EncodeIndex(l0Index, neighbourVoxel);
                //Service.Log.Debug($"L2->L2 within L1: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                    Emit(cur, neighbourVoxel);
                // else: L2 is occupied, so we can't go there
                return;
            }
        }

        if (l1Index != VoxelMap.IndexLevelMask)
        {
            // starting from L1 node -or- L2 node at the boundary
            var l1Neighbour = (l1Coords.x + dx, l1Coords.y + dy, l1Coords.z + dz);
            if (l1Desc.InBounds(l1Neighbour))
            {
                // L1/L2->L1 in same L0 tile
                var neighbourVoxel = VoxelMap.EncodeIndex(l1Desc.VoxelToIndex(l1Neighbour));
                neighbourVoxel = VoxelMap.EncodeIndex(l0Index, neighbourVoxel);
                //Service.Log.Debug($"L1/L2->L1 within L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                {
                    // destination L1 is fully empty
                    Emit(cur, neighbourVoxel);
                }
                else if (l2Index != VoxelMap.IndexLevelMask)
                {
                    // L2->L2 across L1 border (but in same L0)
                    int l2X = dx == 0 ? l2Coords.x : dx > 0 ? 0 : l2Desc.NumCellsX - 1;
                    int l2Y = dy == 0 ? l2Coords.y : dy > 0 ? 0 : l2Desc.NumCellsY - 1;
                    int l2Z = dz == 0 ? l2Coords.z : dz > 0 ? 0 : l2Desc.NumCellsZ - 1;
                    var l2NeighbourVoxel = VoxelMap.EncodeSubIndex(neighbourVoxel, l2Desc.VoxelToIndex(l2X, l2Y, l2Z), 2);
                    //Service.Log.Debug($"- L2->L1 within L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l2NeighbourVoxel:X}");
                    if (_volume.IsEmpty(l2NeighbourVoxel))
                        Emit(cur, l2NeighbourVoxel);
                }
                else
                {
                    // L1->L2 is same L0, enumerate all empty border voxels
                    VisitBorder(cur, neighbourVoxel, 2, dx, dy, dz, false);
                }
                return;
            }
        }

        //if (l0Index != VoxelMap.IndexLevelMask) - this is always true
        {
            // starting from L0 node -or- L1/L2 node at the boundary
            var l0Neighbour = (l0Coords.x + dx, l0Coords.y + dy, l0Coords.z + dz);
            if (l0Desc.InBounds(l0Neighbour))
            {
                var neighbourVoxel = VoxelMap.EncodeIndex(l0Desc.VoxelToIndex(l0Neighbour));
                //Service.Log.Debug($"L0/L1/L2->L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                {
                    // destination L0 is fully empty
                    Emit(cur, neighbourVoxel);
                }
                else if (l1Index != VoxelMap.IndexLevelMask)
                {
                    // L1/L2 across L0 border
                    int l1X = dx == 0 ? l1Coords.x : dx > 0 ? 0 : l1Desc.NumCellsX - 1;
                    int l1Y = dy == 0 ? l1Coords.y : dy > 0 ? 0 : l1Desc.NumCellsY - 1;
                    int l1Z = dz == 0 ? l1Coords.z : dz > 0 ? 0 : l1Desc.NumCellsZ - 1;
                    var l1NeighbourVoxel = VoxelMap.EncodeSubIndex(neighbourVoxel, l1Desc.VoxelToIndex(l1X, l1Y, l1Z), 1);
                    //Service.Log.Debug($"- L1/L2->L1: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l1NeighbourVoxel:X}");
                    if (_volume.IsEmpty(l1NeighbourVoxel))
                    {
                        // L1/L2 -> L1
                        Emit(cur, l1NeighbourVoxel);
                    }
                    else if (l2Index != VoxelMap.IndexLevelMask)
                    {
                        // L2->L2 across L0 border
                        int l2X = dx == 0 ? l2Coords.x : dx > 0 ? 0 : l2Desc.NumCellsX - 1;
                        int l2Y = dy == 0 ? l2Coords.y : dy > 0 ? 0 : l2Desc.NumCellsY - 1;
                        int l2Z = dz == 0 ? l2Coords.z : dz > 0 ? 0 : l2Desc.NumCellsZ - 1;
                        var l2NeighbourVoxel = VoxelMap.EncodeSubIndex(l1NeighbourVoxel, l2Desc.VoxelToIndex(l2X, l2Y, l2Z), 2);
                        //Service.Log.Debug($"- L2->L2: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l2NeighbourVoxel:X}");
                        if (_volume.IsEmpty(l2NeighbourVoxel))
                            Emit(cur, l2NeighbourVoxel);
                    }
                    else
                    {
                        // L1->L2 across L0 border
                        VisitBorder(cur, l1NeighbourVoxel, 2, dx, dy, dz, false);
                    }
                }
                else
                {
                    // L0->L1/L2
                    VisitBorder(cur, neighbourVoxel, 1, dx, dy, dz, true);
                }
            }
        }
    }

    // 舊版 EnumerateBorder＋呼叫端「空就收、不空就（L0->L1 時）再往下一層」合在一起；descend＝要不要往下一層。
    private void VisitBorder(int cur, ulong voxel, int level, int dx, int dy, int dz, bool descend)
    {
        var ld = _volume.Levels[level];
        var (xmin, xmax) = dx == 0 ? (0, ld.NumCellsX - 1) : dx > 0 ? (0, 0) : (ld.NumCellsX - 1, ld.NumCellsX - 1);
        var (ymin, ymax) = dy == 0 ? (0, ld.NumCellsY - 1) : dy > 0 ? (0, 0) : (ld.NumCellsY - 1, ld.NumCellsY - 1);
        var (zmin, zmax) = dz == 0 ? (0, ld.NumCellsZ - 1) : dz > 0 ? (0, 0) : (ld.NumCellsZ - 1, ld.NumCellsZ - 1);
        //Service.Log.Debug($"enum border: {voxel:X} @ {level} + ({dx}, {dy}, {dz}): {xmin}-{xmax}, {ymin}-{ymax}, {zmin}-{zmax}");
        for (int z = zmin; z <= zmax; ++z)
        {
            for (int x = xmin; x <= xmax; ++x)
            {
                for (int y = ymin; y <= ymax; ++y)
                {
                    var v = VoxelMap.EncodeSubIndex(voxel, ld.VoxelToIndex(x, y, z), level);
                    if (_volume.IsEmpty(v))
                        Emit(cur, v);
                    else if (descend)
                        VisitBorder(cur, v, level + 1, dx, dy, dz, false);
                }
            }
        }
    }

    private void Emit(int parentIndex, ulong voxel)
    {
        if (_collectNeighbours)
            _neighbourScratch.Add(voxel);
        else
            VisitNeighbour(parentIndex, voxel);
    }

    private void VisitNeighbour(int parentIndex, ulong nodeVoxel)
    {
        var nodeIndex = _nodeLookup.GetValueOrDefault(nodeVoxel, -1);
        if (nodeIndex < 0)
        {
            // first time we're visiting this node, calculate heuristic
            nodeIndex = _nodes.Count;
            _nodes.Add(new() { GScore = float.MaxValue, HScore = float.MaxValue, GoalDistance = float.MaxValue, Voxel = nodeVoxel, ParentIndex = parentIndex, OpenHeapIndex = -1 });
            _nodeLookup[nodeVoxel] = nodeIndex;
        }
        else if (!_allowReopen && _nodes[nodeIndex].OpenHeapIndex < 0)
        {
            // in closed list already - TODO: is it possible to visit again with lower cost?..
            return;
        }

        var nodeSpan = NodeSpan;
        ref var parentNode = ref nodeSpan[parentIndex];
        var enterPos = nodeVoxel == _goalVoxel ? _goalPos : VoxelSearch.FindClosestVoxelPoint(_volume, nodeVoxel, parentNode.Position);
        var via = parentIndex;
        var capped = false;
        var nodeG = _fast ? CalculateLazyGScore(ref parentNode, enterPos, ref parentIndex, ref capped) : CalculateGScore(ref parentNode, nodeVoxel, enterPos, ref parentIndex);
        ref var curNode = ref nodeSpan[nodeIndex];
        if (nodeG + 0.00001f < curNode.GScore)
        {
            // new path is better
            curNode.GScore = nodeG;
            curNode.HScore = HeuristicDistance(nodeVoxel, enterPos);
            curNode.GoalDistance = GoalDistance(nodeVoxel, enterPos);
            curNode.ParentIndex = parentIndex;
            curNode.ViaIndex = via;
            curNode.ParentVerified = parentIndex == via && !capped;
            curNode.Position = enterPos;
            AddToOpen(nodeIndex);
            //Service.Log.Debug($"volume pathfind: adding {nodeVoxel:X} (#{nodeIndex}), parent={parentIndex}, g={nodeG:f3}, h={_nodes[nodeIndex].HScore:f3}");

            if (curNode.GoalDistance < _nodes[_bestNodeIndex].GoalDistance)
                _bestNodeIndex = nodeIndex;
        }
    }

    private Random _rng = new();
    private float CalculateGScore(ref Node parent, ulong destVoxel, Vector3 destPos, ref int parentIndex)
    {
        float randomFactor = (float)_rng.NextDouble() * Service.Config.RandomnessMultiplier;

        float baseDistance;
        float parentBaseG;
        Vector3 fromPos;

        if (_useRaycast)
        {
            // check LoS from grandparent
            int grandParentIndex = parent.ParentIndex;
            ref var grandParentNode = ref NodeSpan[grandParentIndex];
            // TODO: invert LoS check to match path reconstruction step?
            var distanceSquared = (grandParentNode.Position - destPos).LengthSquared();
            if (distanceSquared <= _raycastLimitSq && VoxelSearch.LineOfSight(_volume, grandParentNode.Voxel, destVoxel, grandParentNode.Position, destPos))
            {
                parentIndex = grandParentIndex;
                baseDistance = MathF.Sqrt(distanceSquared);
                parentBaseG = grandParentNode.GScore;
                fromPos = grandParentNode.Position;
            }
            else
            {
                baseDistance = (parent.Position - destPos).Length();
                parentBaseG = parent.GScore;
                fromPos = parent.Position;
            }
        }
        else
        {
            baseDistance = (parent.Position - destPos).Length();
            parentBaseG = parent.GScore;
            fromPos = parent.Position;
        }

        float verticalDifference = MathF.Abs(fromPos.Y - destPos.Y);
        float verticalPenalty = VerticalPenaltyFactor * verticalDifference;

        return parentBaseG + baseDistance + randomFactor + verticalPenalty;
    }

    // FastSearch：先假設看得到父節點的父節點（起點的父節點就是自己），視線留到展開時由 VerifyParent 驗。
    // capped＝父節點的父節點超出視線距離上限而改連相鄰格：那一段也要驗（大小不同的相鄰格直連會斜切穿實心）。
    private float CalculateLazyGScore(ref Node parent, Vector3 destPos, ref int parentIndex, ref bool capped)
    {
        float randomFactor = (float)_rng.NextDouble() * Service.Config.RandomnessMultiplier;
        if (_useRaycast && (NodeSpan[parent.ParentIndex].Position - destPos).LengthSquared() <= _raycastLimitSq)
            parentIndex = parent.ParentIndex;
        else
            capped = _useRaycast;
        ref var from = ref NodeSpan[parentIndex];
        return from.GScore + (from.Position - destPos).Length() + randomFactor + VerticalPenaltyFactor * MathF.Abs(from.Position.Y - destPos.Y);
    }

    // FastSearch：節點展開（或拿來建路徑）前驗它到父節點的視線。不通就在已展開的鄰居裡挑成本最低、而且
    // 看得到它的當父節點；一個都沒有才退回產生它的那個節點（＝舊版沒有捷徑時的父節點，舊版也不驗那一段）。
    private void VerifyParent(int index)
    {
        var span = NodeSpan;
        ref var n = ref span[index];
        if (n.ParentVerified)
            return;
        n.ParentVerified = true;
        ref var p = ref span[n.ParentIndex];
        if (VoxelSearch.LineOfSight(_volume, p.Voxel, n.Voxel, p.Position, n.Position, true))
            return;

        var voxel = n.Voxel;
        var pos = n.Position;
        _neighbourScratch.Clear();
        _collectNeighbours = true;
        VisitNeighbours(index, voxel, 0, -1, 0);
        VisitNeighbours(index, voxel, 0, +1, 0);
        VisitNeighbours(index, voxel, -1, 0, 0);
        VisitNeighbours(index, voxel, +1, 0, 0);
        VisitNeighbours(index, voxel, 0, 0, -1);
        VisitNeighbours(index, voxel, 0, 0, +1);
        _collectNeighbours = false;

        _repairScratch.Clear();
        foreach (var v in _neighbourScratch)
        {
            var mi = _nodeLookup.GetValueOrDefault(v, -1);
            if (mi < 0 || mi == index)
                continue;
            ref var m = ref span[mi];
            if (m.OpenHeapIndex >= 0 || m.GScore == float.MaxValue)
                continue; // 只接已展開的節點：它們的位置與成本不會再變
            _repairScratch.Add((m.GScore + (m.Position - pos).Length() + VerticalPenaltyFactor * MathF.Abs(m.Position.Y - pos.Y), mi));
        }
        _repairScratch.Sort(ByCost);
        var best = -1;
        foreach (var (_, mi) in _repairScratch)
        {
            ref var m = ref span[mi];
            if (VoxelSearch.LineOfSight(_volume, m.Voxel, voxel, m.Position, pos, true))
            {
                best = mi;
                break;
            }
        }
        if (best < 0)
        {
            best = n.ViaIndex;
            if (TryElbow(best, index, out var elbow))
            {
                // 轉折點是一個不進查表、不進 open list 的已展開節點，只當路徑上的一個點。
                ref var s = ref span[best];
                var elbowNode = new Node { GScore = s.GScore + (s.Position - elbow).Length() + VerticalPenaltyFactor * MathF.Abs(s.Position.Y - elbow.Y), HScore = float.MaxValue, GoalDistance = float.MaxValue, Voxel = s.Voxel, ParentIndex = best, ViaIndex = best, OpenHeapIndex = -1, Position = elbow, ParentVerified = true };
                best = _nodes.Count;
                _nodes.Add(elbowNode);
                span = NodeSpan;
            }
        }

        ref var nn = ref span[index];
        ref var b = ref span[best];
        float randomFactor = (float)_rng.NextDouble() * Service.Config.RandomnessMultiplier;
        nn.ParentIndex = best;
        nn.GScore = b.GScore + (b.Position - pos).Length() + randomFactor + VerticalPenaltyFactor * MathF.Abs(b.Position.Y - pos.Y);
    }

    // 相鄰兩格大小不同時，s 的位置直線連到 n 的位置可能斜切過旁邊的實心格。改走 s 位置 → 轉折點 → n 位置：
    // 轉折點＝n 的位置沿共用面的法向移回 s 那一側 0.1 米（切向夾進 s），前一段在 s 裡面，後一段要驗視線。
    private bool TryElbow(int sIndex, int nIndex, out Vector3 elbow)
    {
        ref var s = ref NodeSpan[sIndex];
        ref var n = ref NodeSpan[nIndex];
        elbow = default;
        var (smin, smax) = _volume.VoxelBounds(s.Voxel, 0);
        var (nmin, nmax) = _volume.VoxelBounds(n.Voxel, 0);
        int axis = -1;
        float face = 0;
        for (var a = 0; a < 3; ++a)
        {
            if (MathF.Abs(smax[a] - nmin[a]) < 1e-3f)
            {
                axis = a;
                face = smax[a] - 0.1f;
                break;
            }
            if (MathF.Abs(smin[a] - nmax[a]) < 1e-3f)
            {
                axis = a;
                face = smin[a] + 0.1f;
                break;
            }
        }
        if (axis < 0)
            return false;
        elbow = Vector3.Clamp(n.Position, smin + new Vector3(0.1f), smax - new Vector3(0.1f));
        elbow[axis] = face;
        return VoxelSearch.LineOfSight(_volume, s.Voxel, n.Voxel, elbow, n.Position, true);
    }

    private float HeuristicDistance(ulong nodeVoxel, Vector3 v)
    {
        if (nodeVoxel == _goalVoxel)
            return 0;
        if (!_fast)
            return _heuristicWeight > 0 ? (v - _goalPos).Length() * 0.999f * _heuristicWeight : (v - _goalPos).Length() * 0.999f;
        return ((v - _goalPos).Length() * _heuristicWeight + VerticalPenaltyFactor * MathF.Abs(v.Y - _goalPos.Y)) * 0.999f;
    }

    private float GoalDistance(ulong nodeVoxel, Vector3 v) => nodeVoxel != _goalVoxel ? (v - _goalPos).Length() * 0.999f : 0;

    private void AddToOpen(int nodeIndex)
    {
        ref var node = ref NodeSpan[nodeIndex];
        if (node.OpenHeapIndex < 0)
        {
            node.OpenHeapIndex = _openList.Count;
            _openList.Add(nodeIndex);
        }
        // update location
        PercolateUp(node.OpenHeapIndex);
    }

    // remove first (minimal) node from open heap and mark as closed
    private int PopMinOpen()
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[0];
        _openList[0] = _openList[_openList.Count - 1];
        _openList.RemoveAt(_openList.Count - 1);
        nodeSpan[nodeIndex].OpenHeapIndex = -1;
        if (_openList.Count > 0)
        {
            nodeSpan[_openList[0]].OpenHeapIndex = 0;
            PercolateDown(0);
        }
        return nodeIndex;
    }

    private void PercolateUp(int heapIndex)
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[heapIndex];
        int parent = (heapIndex - 1) >> 1;
        while (heapIndex > 0 && HeapLess(ref nodeSpan[nodeIndex], ref nodeSpan[_openList[parent]]))
        {
            _openList[heapIndex] = _openList[parent];
            nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
            heapIndex = parent;
            parent = (heapIndex - 1) >> 1;
        }
        _openList[heapIndex] = nodeIndex;
        nodeSpan[nodeIndex].OpenHeapIndex = heapIndex;
    }

    private void PercolateDown(int heapIndex)
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[heapIndex];
        int maxSize = _openList.Count;
        while (true)
        {
            int child1 = (heapIndex << 1) + 1;
            if (child1 >= maxSize)
                break;
            int child2 = child1 + 1;
            if (child2 == maxSize || HeapLess(ref nodeSpan[_openList[child1]], ref nodeSpan[_openList[child2]]))
            {
                if (HeapLess(ref nodeSpan[_openList[child1]], ref nodeSpan[nodeIndex]))
                {
                    _openList[heapIndex] = _openList[child1];
                    nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
                    heapIndex = child1;
                }
                else
                {
                    break;
                }
            }
            else if (HeapLess(ref nodeSpan[_openList[child2]], ref nodeSpan[nodeIndex]))
            {
                _openList[heapIndex] = _openList[child2];
                nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
                heapIndex = child2;
            }
            else
            {
                break;
            }
        }
        _openList[heapIndex] = nodeIndex;
        nodeSpan[nodeIndex].OpenHeapIndex = heapIndex;
    }

    private bool HeapLess(ref Node nodeL, ref Node nodeR)
    {
        var fl = nodeL.GScore + nodeL.HScore;
        var fr = nodeR.GScore + nodeR.HScore;
        if (fl + 0.00001f < fr)
            return true;
        else if (fr + 0.00001f < fl)
            return false;
        else
            return nodeL.GScore > nodeR.GScore; // tie-break towards larger g-values
    }
}
