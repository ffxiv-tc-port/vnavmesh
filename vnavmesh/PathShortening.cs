using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh;

/// <summary>
/// 找更短的路（<see cref="NavmeshQuery.ShortenPaths"/>）的輔助：網格的最小成本倍率、走廊合法性、
/// 每次比較的 Debug 行與整場統計（第一次及每 <see cref="SummaryEvery"/> 次一行 Information）。
/// 統計可能同時被尋路工作鏈與偵錯視窗的同步尋路碰到，所以進鎖；鎖內只累加與組字串。
/// </summary>
public static class PathShortening
{
    public const int SummaryEvery = 50;
    /// <summary>新路線至少短這麼多（米）才換，免得浮點誤差來回換。</summary>
    public const float MinGain = 0.01f;

    private static readonly object _statsLock = new();
    private static int _compared, _adopted, _rejected, _notCompared;
    private static double _savedSum, _savedMax, _extraMsSum, _extraMsMax;

    /// <summary>網格裡「跨越連結本身」的最小成本倍率；沒有這種連結＝10（與 TeleportAwareFilter 同一條式子）。</summary>
    public static float MinCostMultiplier(DtNavMesh nav)
    {
        var min = 10f;
        for (int i = 0; i < nav.GetMaxTiles(); ++i)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            for (int p = 0; p < tile.data.header.polyCount; ++p)
            {
                var poly = tile.data.polys[p];
                var area = poly.GetArea();
                for (int l = tile.polyLinks[poly.index]; l != DtNavMesh.DT_NULL_LINK; l = tile.links[l].next)
                {
                    var nb = tile.links[l].refs;
                    if (nb == 0)
                        continue;
                    nav.GetTileAndPolyByRefUnsafe(nb, out _, out var nbPoly);
                    min = MathF.Min(min, NavmeshQuery.TeleportAwareFilter.CostMultiplier(area, nbPoly.GetArea()));
                }
            }
        }
        return min;
    }

    /// <summary>走廊裡每相鄰兩格都要有連結；2D 射線在上下兩層重疊處停錯層時會接出不相連的兩格。</summary>
    public static bool CorridorConnected(DtNavMesh nav, List<long> corridor)
    {
        for (int i = 0; i + 1 < corridor.Count; ++i)
        {
            nav.GetTileAndPolyByRefUnsafe(corridor[i], out var tile, out var poly);
            var linked = false;
            for (int l = tile.polyLinks[poly.index]; l != DtNavMesh.DT_NULL_LINK; l = tile.links[l].next)
            {
                if (tile.links[l].refs == corridor[i + 1])
                {
                    linked = true;
                    break;
                }
            }
            if (!linked)
                return false;
        }
        return true;
    }

    /// <summary>拉直路徑點再加上最後補到 end 的那一段，與尋路回傳的路徑點同一個量法。</summary>
    public static float Length(List<DtStraightPath> straight, Vector3 end)
    {
        if (straight.Count == 0)
            return 0;
        var len = 0f;
        for (int i = 0; i + 1 < straight.Count; ++i)
            len += Vector3.Distance(straight[i].pos.RecastToSystem(), straight[i + 1].pos.RecastToSystem());
        return len + Vector3.Distance(straight[^1].pos.RecastToSystem(), end);
    }

    /// <summary>
    /// 跟 <see cref="Length"/> 同一條路線，但跨越連結本身那一段乘上它的成本倍率 ÷ 10（與尋路成本同比例）：
    /// 用來確保換上的路線不會為了短幾米放棄舊算法刻意偏好的打折連結（ClientPath 等）。
    /// </summary>
    public static float WeightedLength(DtNavMesh nav, List<DtStraightPath> straight, Vector3 end)
    {
        if (straight.Count == 0)
            return 0;
        var len = 0f;
        for (int i = 0; i + 1 < straight.Count; ++i)
        {
            var d = Vector3.Distance(straight[i].pos.RecastToSystem(), straight[i + 1].pos.RecastToSystem());
            if ((straight[i].flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0 && straight[i].refs != 0 && straight[i + 1].refs != 0
                && nav.GetPolyArea(straight[i].refs, out var a).Succeeded() && nav.GetPolyArea(straight[i + 1].refs, out var b).Succeeded())
                d *= NavmeshQuery.TeleportAwareFilter.CostMultiplier(a, b) / 10f;
            len += d;
        }
        return len + Vector3.Distance(straight[^1].pos.RecastToSystem(), end);
    }

    private static int _exceptionsLogged;
    private static int _unreachable, _unreachableFallback;

    /// <summary>每個多邊形的無向連通區編號（[tile.index][poly.index]）；連結任一方向都算相連。sizes[編號]＝那一區的多邊形數。</summary>
    public static int[][] BuildComponents(DtNavMesh nav, out int[] sizes)
    {
        var maxTiles = nav.GetMaxTiles();
        var bases = new int[maxTiles];
        var total = 0;
        for (int i = 0; i < maxTiles; ++i)
        {
            var tile = nav.GetTile(i);
            if (tile == null)
                continue;
            bases[tile.index] = total;
            total += tile.data?.header?.polyCount ?? 0;
        }
        var parent = new int[total];
        for (int i = 0; i < total; ++i)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        for (int i = 0; i < maxTiles; ++i)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            for (int p = 0; p < tile.data.header.polyCount; ++p)
            {
                var poly = tile.data.polys[p];
                for (int l = tile.polyLinks[poly.index]; l != DtNavMesh.DT_NULL_LINK; l = tile.links[l].next)
                {
                    var nb = tile.links[l].refs;
                    if (nb == 0)
                        continue;
                    nav.GetTileAndPolyByRefUnsafe(nb, out var nbTile, out var nbPoly);
                    var a = Find(bases[tile.index] + poly.index);
                    var b = Find(bases[nbTile.index] + nbPoly.index);
                    if (a != b)
                        parent[a] = b;
                }
            }
        }
        sizes = new int[total];
        var labels = new int[maxTiles][];
        for (int i = 0; i < maxTiles; ++i)
        {
            var tile = nav.GetTile(i);
            var count = tile?.data?.header?.polyCount ?? 0;
            var row = new int[count];
            for (int p = 0; p < count; ++p)
                ++sizes[row[p] = Find(bases[tile!.index] + p)];
            labels[tile?.index ?? i] = row;
        }
        return labels;
    }

    public static void NoteUnreachable(Vector3 from, Vector3 to, Vector3 near, bool used, TimeSpan elapsed)
    {
        lock (_statsLock)
        {
            ++_unreachable;
            if (!used)
                ++_unreachableFallback;
        }
        Service.Log.Debug(used
            ? $"[vnav找更短的路] 終點走不到（不在起點的連通區）：改走到同一區離終點最近的點 {near:f1}（離終點 {Vector3.Distance(near, to):f1} 米、尋路 {elapsed.TotalMilliseconds:f2} ms），不再把整區搜完；{from:f1} -> {to:f1}"
            : $"[vnav找更短的路] 終點走不到，最近點 {near:f1} 也只走得到一部分（單向連結），照舊算法對原終點搜；{from:f1} -> {to:f1}");
    }

    // 只記前幾次全文，免得每次尋路都洗一份堆疊。
    public static void NoteException(Exception ex)
    {
        if (Interlocked.Increment(ref _exceptionsLogged) <= 3)
            Service.Log.Error($"[vnav找更短的路] 新搜尋擲例外，這次沿用舊路線：{ex}");
    }

    public static void NotCompared(string kind, string reason, Vector3 from, Vector3 to)
    {
        lock (_statsLock)
            ++_notCompared;
        Service.Log.Debug($"[vnav找更短的路] {kind}：{reason}，沿用舊算法、不另算；{from:f1} -> {to:f1}");
    }

    public static void Report(string kind, Vector3 from, Vector3 to, float oldLength, float newLength, bool adopted, string winner, string? reject, TimeSpan legacyTime, TimeSpan newTime)
    {
        var extraMs = newTime.TotalMilliseconds;
        var saved = adopted ? oldLength - newLength : 0;
        var verdict = adopted ? $"採用新路線（{winner}）、少走 {saved:f1} 米" : winner.Length > 0 ? "沿用舊路線（新的沒有更短）" : $"沿用舊路線（{reject}）";
        var newText = float.IsNaN(newLength) ? "?" : $"{newLength:f1}";
        Service.Log.Debug($"[vnav找更短的路] {kind}：新 {newText} 米／舊 {oldLength:f1} 米，{verdict}；尋路 舊 {legacyTime.TotalMilliseconds:f2} ms＋新 {extraMs:f2} ms；{from:f1} -> {to:f1}");

        string? summary = null;
        lock (_statsLock)
        {
            ++_compared;
            if (adopted)
                ++_adopted;
            if (winner.Length == 0)
                ++_rejected;
            _savedSum += saved;
            _savedMax = Math.Max(_savedMax, saved);
            _extraMsSum += extraMs;
            _extraMsMax = Math.Max(_extraMsMax, extraMs);
            if (_compared == 1 || _compared % SummaryEvery == 0)
                summary = $"[vnav找更短的路] 本次啟動累計比較新舊算法 {_compared} 次：採用新路線 {_adopted} 次、共少走 {_savedSum:f0} 米"
                    + $"（採用時平均少走 {(_adopted > 0 ? _savedSum / _adopted : 0):f1} 米、單次最多 {_savedMax:f1} 米）；沿用舊路線 {_compared - _adopted} 次（其中新算法不合法 {_rejected} 次）；"
                    + $"另有 {_notCompared} 次終點走不到或帶 range 沒比較；新算法平均多花 {_extraMsSum / _compared:f2} ms、最多 {_extraMsMax:f1} ms；"
                    + $"終點走不到改走最近可達點 {_unreachable - _unreachableFallback} 次（最近點也走不到而照舊 {_unreachableFallback} 次）";
        }
        if (summary != null)
            Service.Log.Information(summary);
    }
}
