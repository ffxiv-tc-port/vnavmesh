using DotRecast.Core.Numerics;
using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh;

/// <summary>
/// 地面走廊的另一種搜尋（找更短的路用）：狀態＝經某條連結進入某多邊形時、落在那條相鄰邊上的取樣點，
/// 同一多邊形內任兩個邊界點直線相連（多邊形在 XZ 上是凸的），不走回剛離開的多邊形。
/// 成本照傳入的 filter，啟發式＝直線 × hScale。只回傳走廊（相鄰兩格必有連結）；路徑點由呼叫端拉直。
/// 🔴 不可重入：重用內部緩衝，與擁有它的 NavmeshQuery 同一條約定。
/// </summary>
public sealed class PortalSampleSearch(DtNavMesh nav)
{
    /// <summary>相鄰邊上取樣點的間距（米）；短邊只取兩端，長邊最多 <see cref="MaxSamplesPerPortal"/> 段。</summary>
    public const float SampleSpacing = 4f;
    public const int MaxSamplesPerPortal = 16;

    private struct Node
    {
        public Vector3 Pos;
        public float G;
        public int Parent;
        public long Poly;
        public long CamePoly;
        public bool Closed;
    }

    // 只借用 DtNavMeshQuery 受保護的 GetPortalPoints（含 tile 邊界的部分邊、off-mesh 連結端點）。
    private sealed class PortalAccess(DtNavMesh mesh) : DtNavMeshQuery(mesh)
    {
        public bool Portal(long from, DtPoly fromPoly, DtMeshTile fromTile, long to, DtPoly toPoly, DtMeshTile toTile, out RcVec3f left, out RcVec3f right)
            => GetPortalPoints(from, fromPoly, fromTile, to, toPoly, toTile, out left, out right).Succeeded();
    }

    private readonly DtNavMesh _nav = nav;
    private readonly PortalAccess _portals = new(nav);
    private readonly List<Node> _nodes = [];
    // 有向連結（tile、連結索引）→ 它的取樣點在 _nodes 裡的起點與個數；同一條連結的取樣點連號。
    private readonly Dictionary<long, (int First, int Count)> _portalNodes = [];
    private readonly PriorityQueue<int, float> _open = new();

    /// <summary>上一次 <see cref="Find"/> 建立的狀態數。</summary>
    public int NodeCount => _nodes.Count;
    /// <summary>上一次 <see cref="Find"/> 因為狀態數到上限而放棄。</summary>
    public bool HitLimit { get; private set; }

    /// <summary>
    /// 找到完整走廊回 true（寫進 corridor，首尾＝startRef／endRef）；走不到或狀態數會超過 maxNodes 回 false。
    /// hScale 必須不高估（≤ 0.999 × 網格最小成本倍率），否則結果不保證是這張圖上最便宜的。
    /// </summary>
    public bool Find(long startRef, long endRef, Vector3 from, Vector3 to, IDtQueryFilter filter, float hScale, int maxNodes, List<long> corridor)
    {
        corridor.Clear();
        HitLimit = false;
        _nodes.Clear();
        _portalNodes.Clear();
        _open.Clear();
        if (startRef == endRef)
        {
            corridor.Add(startRef);
            return true;
        }

        _nodes.Add(new() { Pos = from, G = 0, Parent = -1, Poly = startRef, CamePoly = 0 });
        _open.Enqueue(0, hScale * Vector3.Distance(from, to));
        var best = float.PositiveInfinity;
        var bestNode = -1;
        var toRc = to.SystemToRecast();
        _nav.GetTileAndPolyByRefUnsafe(endRef, out var endTile, out var endPoly);
        while (_open.TryDequeue(out var cur, out var f))
        {
            if (f >= best)
                break;
            var node = _nodes[cur];
            if (node.Closed)
                continue;
            node.Closed = true;
            _nodes[cur] = node;
            var pos = node.Pos.SystemToRecast();

            _nav.GetTileAndPolyByRefUnsafe(node.Poly, out var tile, out var poly);
            if (node.Poly == endRef)
            {
                var total = node.G + filter.GetCost(pos, toRc, 0, null, null, endRef, endTile, endPoly, 0, null, null);
                if (total < best)
                {
                    best = total;
                    bestNode = cur;
                }
            }

            for (int l = tile.polyLinks[poly.index]; l != DtNavMesh.DT_NULL_LINK; l = tile.links[l].next)
            {
                var next = tile.links[l].refs;
                if (next == 0 || next == node.CamePoly)
                    continue;
                _nav.GetTileAndPolyByRefUnsafe(next, out var nextTile, out var nextPoly);
                if (!filter.PassFilter(next, nextTile, nextPoly))
                    continue;

                var key = ((long)tile.index << 32) | (uint)l;
                if (!_portalNodes.TryGetValue(key, out var range))
                {
                    if (!_portals.Portal(node.Poly, poly, tile, next, nextPoly, nextTile, out var left, out var right))
                        continue;
                    var a = left.RecastToSystem();
                    var b = right.RecastToSystem();
                    var len = Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
                    var k = len < 1e-3f ? 0 : Math.Clamp((int)MathF.Ceiling(len / SampleSpacing), 1, MaxSamplesPerPortal);
                    if (_nodes.Count + k + 1 > maxNodes)
                    {
                        HitLimit = true;
                        return false;
                    }
                    range = (_nodes.Count, k + 1);
                    for (int s = 0; s <= k; ++s)
                        _nodes.Add(new() { Pos = k == 0 ? a : Vector3.Lerp(a, b, (float)s / k), G = float.PositiveInfinity, Parent = -1, Poly = next, CamePoly = node.Poly });
                    _portalNodes[key] = range;
                }

                var nodes = CollectionsMarshal.AsSpan(_nodes);
                for (int i = range.First; i < range.First + range.Count; ++i)
                {
                    ref var target = ref nodes[i];
                    if (target.Closed)
                        continue;
                    var g = node.G + filter.GetCost(pos, target.Pos.SystemToRecast(), 0, null, null, node.Poly, tile, poly, next, nextTile, nextPoly);
                    if (g >= target.G)
                        continue;
                    target.G = g;
                    target.Parent = cur;
                    _open.Enqueue(i, g + hScale * Vector3.Distance(target.Pos, to));
                }
            }
        }
        if (bestNode < 0)
            return false;

        for (var n = bestNode; n >= 0; n = _nodes[n].Parent)
            corridor.Add(_nodes[n].Poly);
        corridor.Reverse();
        return true;
    }
}
