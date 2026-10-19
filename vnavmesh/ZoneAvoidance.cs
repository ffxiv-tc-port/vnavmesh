using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// 本檔假設與契約：純幾何，不碰導航網格、不碰任何 Dalamud 服務（離線工具直接呼叫）。
// 平面一律是 XZ：Vector2 的 X＝世界 X、Y＝世界 Z；角度＝atan2(z, x)。世界 Y 只用來判「是不是同一層」。
// 區域＝「半徑 radius＋bodyRadius 的圓或扇形」聯集「半徑 bodyRadius 的本體圓」（扇形背後貼著本體也算在內）。
public static class ZoneAvoidance
{
    /// <summary>
    /// 呼叫端每筆區域的 float 數，順序 [x, y, z, radius, dirX, dirZ, cosHalfAngle, bodyRadius]。
    /// 朝向 (dirX, dirZ)＝(sin θ, cos θ)，θ 為遊戲物件的 Rotation；cosHalfAngle ≤ −1 表示整圓。
    /// </summary>
    public const int ZoneStride = 8;

    /// <summary>一次最多採用的區域筆數；超過的部分忽略，<see cref="Parse"/> 回 false。</summary>
    public const int MaxZones = 256;

    /// <summary>點與區域中心的高低差超過這個值就不算在區域內（視為另一層地形）。</summary>
    public const float ZoneHalfHeight = 10f;

    private const int CircleSegments = 16;
    private const int BodySegments = 12;
    private const float SampleStep = 0.25f;

    public readonly struct Zone
    {
        /// <summary>這筆在呼叫端陣列裡是第幾筆（不是 float 索引）。</summary>
        public readonly int Index;
        public readonly Vector3 Center;
        /// <summary>外圈半徑＝radius＋bodyRadius。</summary>
        public readonly float Outer;
        public readonly float Body;
        public readonly bool Full;
        public readonly Vector2 Dir;
        public readonly float CosHalf;
        public readonly float HalfAngle;
        public readonly Vector2 Edge1;
        public readonly Vector2 Edge2;

        public Zone(int index, Vector3 center, float outer, float body, bool full, Vector2 dir, float cosHalf)
        {
            Index = index;
            Center = center;
            Outer = outer;
            Body = body;
            Full = full;
            Dir = dir;
            CosHalf = full ? -1 : cosHalf;
            HalfAngle = full ? MathF.PI : MathF.Acos(CosHalf);
            Edge1 = Rotate(dir, HalfAngle);
            Edge2 = Rotate(dir, -HalfAngle);
        }

        public Vector2 Center2 => new(Center.X, Center.Z);
    }

    /// <summary>
    /// 解析呼叫端的區域陣列，追加到 <paramref name="output"/>。半徑與本體半徑都不大於 0 的筆略過（什麼都不擋）；
    /// 朝向長度近 0 的扇形當整圓（保守）。
    /// </summary>
    /// <returns>false＝資料有問題（長度不是 <see cref="ZoneStride"/> 的倍數、含 NaN／無限大、超過 <see cref="MaxZones"/>）；有問題的筆略過，其餘照用。</returns>
    public static bool Parse(ReadOnlySpan<float> data, List<Zone> output)
    {
        var ok = data.Length % ZoneStride == 0;
        var count = data.Length / ZoneStride;
        if (count > MaxZones)
        {
            ok = false;
            count = MaxZones;
        }

        for (var i = 0; i < count; ++i)
        {
            var r = data.Slice(i * ZoneStride, ZoneStride);
            var finite = true;
            foreach (var v in r)
                finite &= float.IsFinite(v);
            if (!finite)
            {
                ok = false;
                continue;
            }

            var body = MathF.Max(r[7], 0);
            var outer = MathF.Max(r[3], 0) + body;
            if (outer <= 0)
                continue;

            var dir = new Vector2(r[4], r[5]);
            var len = dir.Length();
            var full = r[6] <= -1 || len < 1e-4f;
            output.Add(new Zone(i, new Vector3(r[0], r[1], r[2]), outer, body, full, full ? Vector2.UnitY : dir / len, Math.Clamp(r[6], -1f, 1f)));
        }
        return ok;
    }

    public static bool InBand(in Zone z, float y) => MathF.Abs(y - z.Center.Y) <= ZoneHalfHeight;

    /// <summary>XZ 平面上點到區域的距離，在區域內＝0。不看高度。</summary>
    public static float Distance2D(in Zone z, Vector2 p)
    {
        var d = p - z.Center2;
        var r = d.Length();
        var toBody = MathF.Max(r - z.Body, 0);
        float toPie;
        if (z.Full || r < 1e-6f || Vector2.Dot(d, z.Dir) >= z.CosHalf * r)
            toPie = MathF.Max(r - z.Outer, 0);
        else
            toPie = MathF.Min(DistanceToSegmentFromOrigin(d, z.Edge1 * z.Outer), DistanceToSegmentFromOrigin(d, z.Edge2 * z.Outer));
        return MathF.Min(toBody, toPie);
    }

    public static bool Contains(in Zone z, Vector3 p) => InBand(z, p.Y) && Distance2D(z, new(p.X, p.Z)) <= 0;

    /// <summary>
    /// 點在區域內有多危險（米），在區域外或不同層回 0。整圓＝離圓周的距離；扇形＝離弧的距離與離兩側邊的距離（取小）的平均，
    /// 越靠中軸、越靠怪越深；本體圈內一律算離外圈的距離（貼著本體最深）。
    /// </summary>
    public static float Penetration(in Zone z, Vector3 p)
    {
        if (!InBand(z, p.Y))
            return 0;
        var d = new Vector2(p.X - z.Center.X, p.Z - z.Center.Z);
        var r = d.Length();
        var radial = z.Outer - r;
        if (r <= z.Body)
            return MathF.Max(radial, 0);
        if (z.Full || r < 1e-6f)
            return MathF.Max(radial, 0);
        if (Vector2.Dot(d, z.Dir) < z.CosHalf * r)
            return 0;
        var lateral = MathF.Min(EdgeDistance(d, z.Edge1), EdgeDistance(d, z.Edge2));
        return MathF.Max((radial + MathF.Min(radial, lateral)) * 0.5f, 0);
    }

    // 扇形內一點到「從圓心沿 edge 方向的射線」的距離；投影在圓心後方時就是到圓心的距離。
    private static float EdgeDistance(Vector2 d, Vector2 edge)
    {
        var along = Vector2.Dot(d, edge);
        return along <= 0 ? d.Length() : MathF.Abs(Cross(edge, d));
    }

    /// <summary>線段 a→b 第一段落在區域內的參數區間 [t0, t1]（0..1，含高度判定）；整段都不在區域內回 false。</summary>
    public static bool FirstInsideInterval(in Zone z, Vector3 a, Vector3 b, out float t0, out float t1)
    {
        t0 = t1 = 0;
        Span<float> ts = stackalloc float[12];
        var n = 0;
        ts[n++] = 0;
        ts[n++] = 1;
        var f = new Vector2(a.X, a.Z) - z.Center2;
        var d = new Vector2(b.X - a.X, b.Z - a.Z);
        n = AddCircleHits(f, d, z.Outer, ts, n);
        n = AddCircleHits(f, d, z.Body, ts, n);
        if (!z.Full)
        {
            n = AddLineHit(f, d, z.Edge1, ts, n);
            n = AddLineHit(f, d, z.Edge2, ts, n);
        }
        var dy = b.Y - a.Y;
        if (MathF.Abs(dy) > 1e-6f)
        {
            n = AddParam((z.Center.Y + ZoneHalfHeight - a.Y) / dy, ts, n);
            n = AddParam((z.Center.Y - ZoneHalfHeight - a.Y) / dy, ts, n);
        }

        // 斷點之間區域內外不變 ⇒ 每段只驗中點，與區域是否凸無關（扇形大於半圓時不凸）。
        var span = ts[..n];
        span.Sort();
        var found = false;
        for (var i = 0; i + 1 < n; ++i)
        {
            var lo = span[i];
            var hi = span[i + 1];
            if (hi - lo < 1e-7f)
                continue;
            if (Contains(z, Vector3.Lerp(a, b, (lo + hi) * 0.5f)))
            {
                if (!found)
                    t0 = lo;
                found = true;
                t1 = hi;
            }
            else if (found)
            {
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// 線段 a→b 是否與區域保持距離：不進入區域，且沿線每一點離區域不少於 min(margin, 兩端點自己離區域的距離)。
    /// 取兩端點的距離當下限，是讓本來就貼著區域的點仍然可以往外走。
    /// </summary>
    public static bool SegmentClear(in Zone z, Vector3 a, Vector3 b, float margin)
    {
        var len = Vector3.Distance(a, b);
        if (FirstInsideInterval(z, a, b, out var t0, out var t1) && (len < 1e-3f || (t1 - t0) * len > 1e-3f))
            return false;

        var allowed = MathF.Min(margin, MathF.Min(BandDistance(z, a), BandDistance(z, b)));
        if (allowed <= 1e-3f)
            return true;

        // 區域整個在外圈圓內 ⇒ 線段離圓夠遠就不必取樣。
        var a2 = new Vector2(a.X, a.Z);
        var b2 = new Vector2(b.X, b.Z);
        if (DistanceToSegment(z.Center2, a2, b2) - z.Outer >= allowed)
            return true;

        var steps = (int)MathF.Ceiling(Vector2.Distance(a2, b2) / SampleStep);
        for (var k = 1; k < steps; ++k)
        {
            var p = Vector3.Lerp(a, b, (float)k / steps);
            if (InBand(z, p.Y) && Distance2D(z, new(p.X, p.Z)) < allowed - 1e-3f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 把區域外推 <paramref name="clearance"/> 後的外框取樣點加進 <paramref name="output"/>，供求凸包。
    /// 圓弧用外切多邊形（邊到圓心的距離正好是半徑＋clearance）；扇形的弧兩端再多轉 clearance 對應的角度，
    /// 讓扇形側邊附近也留得出 clearance。
    /// </summary>
    public static void AppendHullSamples(in Zone z, float clearance, List<Vector2> output)
    {
        var c = z.Center2;
        var spread = clearance / MathF.Max(z.Outer, clearance);
        if (z.Full || z.HalfAngle + spread >= MathF.PI)
        {
            AppendArc(c, z.Outer + clearance, 0, MathF.Tau, true, CircleSegments, output);
            return;
        }

        var facing = MathF.Atan2(z.Dir.Y, z.Dir.X);
        var half = z.HalfAngle + spread;
        var steps = Math.Max(1, (int)MathF.Ceiling(2 * half / (MathF.Tau / CircleSegments)));
        AppendArc(c, z.Outer + clearance, facing - half, facing + half, false, steps, output);
        AppendArc(c, z.Body + clearance, 0, MathF.Tau, true, BodySegments, output);
    }

    /// <summary>凸包（Andrew 單調鏈），回逆時針（atan2 遞增方向）頂點，不含共線點；點少於 3 或全共線時回空清單。</summary>
    public static List<Vector2> ConvexHull(List<Vector2> points)
    {
        var pts = new List<Vector2>(points);
        pts.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        var hull = new List<Vector2>(pts.Count + 1);
        for (var pass = 0; pass < 2; ++pass)
        {
            var floor = hull.Count;
            for (var k = 0; k < pts.Count; ++k)
            {
                var p = pts[pass == 0 ? k : pts.Count - 1 - k];
                while (hull.Count >= floor + 2 && Cross(hull[^1] - hull[^2], p - hull[^2]) <= 1e-9f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull.Count >= 3 ? hull : [];
    }

    /// <summary>線段 a→b 落在凸多邊形（逆時針）內的參數區間；完全在外回 false。</summary>
    public static bool ClipSegment(List<Vector2> hull, Vector2 a, Vector2 b, out float tEnter, out float tExit)
    {
        tEnter = 0;
        tExit = 1;
        var d = b - a;
        for (var i = 0; i < hull.Count; ++i)
        {
            var v = hull[i];
            var e = hull[(i + 1) % hull.Count] - v;
            var num = Cross(e, a - v);
            var den = Cross(e, d);
            if (MathF.Abs(den) < 1e-12f)
            {
                if (num < 0)
                    return false;
                continue;
            }
            var t = -num / den;
            if (den > 0)
                tEnter = MathF.Max(tEnter, t);
            else
                tExit = MathF.Min(tExit, t);
            if (tEnter > tExit)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 從 a 沿凸包往 b 繞行的點鏈（含頭尾 a、b）：逆時針或順時針，取兩者角度之間的凸包頂點，角度以凸包重心為準。
    /// a、b 應在凸包上或凸包內（修補時是原路進出凸包的點）。
    /// </summary>
    public static List<Vector2> BuildChain(List<Vector2> hull, Vector2 a, Vector2 b, bool counterClockwise)
    {
        var g = Vector2.Zero;
        foreach (var v in hull)
            g += v;
        g /= hull.Count;

        var angA = MathF.Atan2(a.Y - g.Y, a.X - g.X);
        var angB = MathF.Atan2(b.Y - g.Y, b.X - g.X);
        var span = counterClockwise ? Wrap(angB - angA) : Wrap(angA - angB);
        var picked = new List<(float rel, Vector2 v)>();
        foreach (var v in hull)
        {
            var ang = MathF.Atan2(v.Y - g.Y, v.X - g.X);
            var rel = counterClockwise ? Wrap(ang - angA) : Wrap(angA - ang);
            if (rel > 1e-6f && rel < span - 1e-6f)
                picked.Add((rel, v));
        }
        picked.Sort((p, q) => p.rel.CompareTo(q.rel));

        var chain = new List<Vector2>(picked.Count + 2) { a };
        foreach (var (_, v) in picked)
            chain.Add(v);
        chain.Add(b);
        return chain;
    }

    public static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    public static Vector2 Rotate(Vector2 v, float angle)
    {
        var (s, c) = MathF.SinCos(angle);
        return new(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b) => DistanceToSegmentFromOrigin(p - a, b - a);

    private static float BandDistance(in Zone z, Vector3 p) => InBand(z, p.Y) ? Distance2D(z, new(p.X, p.Z)) : float.PositiveInfinity;

    private static float DistanceToSegmentFromOrigin(Vector2 p, Vector2 e)
    {
        var ee = Vector2.Dot(e, e);
        var t = ee > 1e-12f ? Math.Clamp(Vector2.Dot(p, e) / ee, 0f, 1f) : 0f;
        return Vector2.Distance(p, e * t);
    }

    // [0, 2π)
    private static float Wrap(float a)
    {
        a %= MathF.Tau;
        return a < 0 ? a + MathF.Tau : a;
    }

    private static void AppendArc(Vector2 c, float radius, float from, float to, bool closed, int steps, List<Vector2> output)
    {
        var step = (to - from) / steps;
        var r = radius / MathF.Cos(step * 0.5f);
        var count = closed ? steps : steps + 1;
        for (var k = 0; k < count; ++k)
        {
            var (s, co) = MathF.SinCos(from + k * step);
            output.Add(c + new Vector2(co, s) * r);
        }
    }

    private static int AddParam(float t, Span<float> ts, int n)
    {
        if (t > 0 && t < 1 && n < ts.Length)
            ts[n++] = t;
        return n;
    }

    private static int AddCircleHits(Vector2 f, Vector2 d, float radius, Span<float> ts, int n)
    {
        var a = Vector2.Dot(d, d);
        if (radius <= 0 || a < 1e-12f)
            return n;
        var b = 2 * Vector2.Dot(f, d);
        var c = Vector2.Dot(f, f) - radius * radius;
        var disc = b * b - 4 * a * c;
        if (disc <= 0)
            return n;
        var sq = MathF.Sqrt(disc);
        n = AddParam((-b - sq) / (2 * a), ts, n);
        return AddParam((-b + sq) / (2 * a), ts, n);
    }

    private static int AddLineHit(Vector2 f, Vector2 d, Vector2 e, Span<float> ts, int n)
    {
        var den = Cross(e, d);
        return MathF.Abs(den) < 1e-12f ? n : AddParam(-Cross(e, f) / den, ts, n);
    }
}
