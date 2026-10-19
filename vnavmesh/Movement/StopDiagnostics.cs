using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Navmesh.Movement;

// 「停止跟隨路徑」診斷的純邏輯：節流、欄位格式、從堆疊資訊挑出呼叫端。不碰 Dalamud 與遊戲記憶體，
// 離線測試直接編譯這一份原始碼。輸出欄位一律「名=值」、值內不含空白；「?」＝讀不到，「-」＝不適用。

/// <summary>每 interval 毫秒最多放行一次，被擋下的次數留到下一次放行時交出。不是執行緒安全的。</summary>
internal sealed class IntervalLogGate(long intervalMs)
{
    private bool _passedOnce;
    private long _lastPassMs;
    private int _suppressed;

    /// <summary>
    /// true＝這次寫 Information，<paramref name="suppressed"/>＝上一次放行之後被擋下幾次；
    /// false＝這次寫 Debug，<paramref name="suppressed"/>＝這一期到這次為止被擋下第幾次。
    /// </summary>
    public bool TryPass(long nowMs, out int suppressed)
    {
        if (!_passedOnce || nowMs - _lastPassMs >= intervalMs)
        {
            _passedOnce = true;
            _lastPassMs = nowMs;
            suppressed = _suppressed;
            _suppressed = 0;
            return true;
        }
        suppressed = ++_suppressed;
        return false;
    }
}

/// <summary>同一個鍵各自節流。會從 IPC 呼叫端的執行緒進來，所以自帶鎖；鎖內只碰字典與計數。</summary>
internal sealed class KeyedIntervalLogGate(long intervalMs, int maxKeys = 64)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IntervalLogGate> _gates = new(StringComparer.Ordinal);

    public bool TryPass(string key, long nowMs, out int suppressed)
    {
        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out var gate))
            {
                // 鍵是「原因＋呼叫端插件」，正常只有個位數；真的爆量就整批重置，代價只是多寫幾行 Information。
                if (_gates.Count >= maxKeys)
                    _gates.Clear();
                gate = new IntervalLogGate(intervalMs);
                _gates.Add(key, gate);
            }
            return gate.TryPass(nowMs, out suppressed);
        }
    }
}

/// <summary>一個移動相關鍵的狀態：1＝按下、0＝放開、-1＝讀不到。</summary>
internal readonly record struct KeyReading(string Name, sbyte State);

/// <summary>堆疊上一格的歸屬。<see cref="Plugin"/> 為 null＝不屬於任何插件（框架、共用組件或查不到）。</summary>
internal readonly record struct FrameOwner(string? Assembly, string? Plugin, bool IsSelf);

/// <summary>「因移動輸入而取消路徑」那一行要用到的全部值，全部在框架執行緒上收集。</summary>
internal sealed class InputCancelReport
{
    public bool HasSample;
    public bool Fly;
    public float Left, Forward, Up;
    public byte Additive;
    public bool GameReadsInput;
    public long SampleSeq;
    public bool SampleBeforePathStart;
    public bool HasOverride;
    public bool OverrideFly;
    public float OverrideLeft, OverrideForward, OverrideUp;
    public long OverrideSeq;
    public long CallsSincePathStart;
    public long AdditiveCallsSincePathStart;
    public double MsSincePathStart = -1; // <0＝不知道
    public int FramesSincePathStart;
    public int Remaining;
    public bool InFlight, Mounted, Diving;
    public KeyReading[] Keys = [];
    public sbyte AutoRun = -1;      // 1 是、0 否、-1 讀不到
    public sbyte MouseButtons = -1; // -1 讀不到；否則 bit0＝左鍵、bit1＝右鍵
    public Vector2? LeftStick;      // null＝讀不到
}

internal static class StopDiagnostics
{
    public const string InputCancelPrefix = "[FollowPath][輸入取消]";
    public const string StopPrefix = "[FollowPath][停止]";
    public const float SameValueEpsilon = 1e-4f;

    private static readonly string[] FrameworkAssemblyPrefixes =
        ["System", "Microsoft", "Dalamud", "mscorlib", "netstandard", "FFXIVClientStructs", "InteropGenerator", "Lumina", "Serilog", "Reloaded", "Newtonsoft", "ImGui", "Anonymously Hosted"];

    /// <summary>
    /// 由內往外找第一格「屬於別的插件」的堆疊；那就是直接呼叫我們的插件。
    /// 刻意不取最外層：經過別的插件 hook 進來的呼叫，最外層是 hook 的那一方，不是呼叫端。
    /// 都查不到插件時，退回第一個不是框架的組件名，插件欄寫「?」。
    /// </summary>
    public static (string Plugin, string Assembly) PickCaller(IEnumerable<FrameOwner> innermostFirst)
    {
        string? fallback = null;
        foreach (var f in innermostFirst)
        {
            if (f.IsSelf)
                continue;
            if (f.Plugin != null)
                return (Token(f.Plugin), Token(f.Assembly ?? "?"));
            if (fallback == null && f.Assembly != null && !IsFrameworkAssembly(f.Assembly))
                fallback = f.Assembly;
        }
        return ("?", fallback != null ? Token(fallback) : "?");
    }

    public static bool IsFrameworkAssembly(string name)
    {
        foreach (var p in FrameworkAssemblyPrefixes)
            if (name.StartsWith(p, StringComparison.Ordinal))
                return true;
        return false;
    }

    public static string FormatStop(string reason, string plugin, string assembly, int cleared, double msSincePathStart)
        => $"{StopPrefix} 已停止跟隨路徑。原因={Token(reason)} 呼叫端插件={Token(plugin)} 呼叫端組件={Token(assembly)} "
         + $"清掉的路徑點={cleared} 路徑開始後={Ms(msSincePathStart)}";

    public static string CallerFields(string plugin, string assembly)
        => $" 呼叫端插件={Token(plugin)} 呼叫端組件={Token(assembly)}";

    /// <summary>節流欄位：Information 帶「上一行之後被壓成 Debug 幾次」，Debug 帶「這一期第幾次」。</summary>
    public static string SuppressedField(bool information, int suppressed)
        => information ? $" 期間另有={suppressed}次" : $" 節流中=第{suppressed}次";

    public static string FormatInputCancel(InputCancelReport r, bool information, int suppressed)
    {
        var sb = new StringBuilder(640);
        sb.Append(InputCancelPrefix).Append(" 偵測到移動輸入，依設定「玩家輸入移動時取消目前路徑」停止跟隨路徑。");
        if (!r.HasSample)
        {
            sb.Append(" 來源=? sumLeft=? sumForward=? bAdditiveUnk=? 遊戲讀取輸入=?");
        }
        else if (r.Fly)
        {
            sb.Append(" 來源=飛行 Forward=").Append(F3(r.Forward)).Append(" Left=").Append(F3(r.Left)).Append(" Up=").Append(F3(r.Up));
            sb.Append(" bAdditiveUnk=- 遊戲讀取輸入=-");
        }
        else
        {
            sb.Append(" 來源=地面 sumLeft=").Append(F3(r.Left)).Append(" sumForward=").Append(F3(r.Forward));
            sb.Append(" bAdditiveUnk=").Append(r.Additive).Append(" 遊戲讀取輸入=").Append(YesNo(r.GameReadsInput));
        }
        sb.Append(" 取樣序=").Append(r.SampleSeq).Append(" 取樣早於本路徑=").Append(YesNo(r.SampleBeforePathStart));

        if (!r.HasOverride)
        {
            sb.Append(" 上次覆寫=無 上次覆寫序=- 與上次覆寫相同=-");
        }
        else
        {
            sb.Append(" 上次覆寫=").Append(r.OverrideFly ? "飛行(" : "地面(").Append(F3(r.OverrideLeft)).Append(',').Append(F3(r.OverrideForward));
            if (r.OverrideFly)
                sb.Append(',').Append(F3(r.OverrideUp));
            sb.Append(')');
            sb.Append(" 上次覆寫序=").Append(r.OverrideSeq);
            sb.Append(" 與上次覆寫相同=").Append(r.OverrideFly != r.Fly ? "-" : YesNo(SameAsOverride(r)));
        }

        sb.Append(" 路徑開始後=").Append(Ms(r.MsSincePathStart)).Append('/').Append(r.FramesSincePathStart).Append("幀");
        sb.Append(" 本路徑detour呼叫=").Append(r.CallsSincePathStart).Append(" 其中bAdditiveUnk非0=").Append(r.AdditiveCallsSincePathStart);
        sb.Append(" 剩餘路徑點=").Append(r.Remaining);
        sb.Append(" 飛行中=").Append(YesNo(r.InFlight)).Append(" 騎乘=").Append(YesNo(r.Mounted)).Append(" 潛水=").Append(YesNo(r.Diving));
        sb.Append(" 按下的移動鍵=").Append(KeyList(r.Keys, 1)).Append(" 讀不到的鍵=").Append(KeyList(r.Keys, -1));
        sb.Append(" 自動奔跑=").Append(Tri(r.AutoRun));
        sb.Append(" 滑鼠按住=").Append(Mouse(r.MouseButtons));
        sb.Append(" 左搖桿=").Append(r.LeftStick is { } s ? $"({F0(s.X)},{F0(s.Y)})" : "?");
        sb.Append(SuppressedField(information, suppressed));
        return sb.ToString();
    }

    public static bool SameAsOverride(InputCancelReport r)
        => Near(r.Left, r.OverrideLeft) && Near(r.Forward, r.OverrideForward) && (!r.Fly || Near(r.Up, r.OverrideUp));

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= SameValueEpsilon;

    private static string KeyList(KeyReading[] keys, sbyte state)
    {
        List<string>? names = null;
        foreach (var k in keys)
            if (k.State == state)
                (names ??= []).Add(Token(k.Name));
        return names == null ? "無" : string.Join(",", names);
    }

    private static string Mouse(sbyte flags) => flags switch
    {
        < 0 => "?",
        0 => "無",
        1 => "左",
        2 => "右",
        _ => (flags & 3) == 3 ? "左右" : $"0x{flags:X}",
    };

    private static string Tri(sbyte v) => v < 0 ? "?" : v == 0 ? "否" : "是";
    private static string YesNo(bool v) => v ? "是" : "否";
    private static string Ms(double ms) => ms < 0 ? "?" : $"{F0(ms)}ms";
    private static string F3(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string F0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>值內不留空白，讓「名=值」以空白切開時不會斷錯；空字串寫成「?」。</summary>
    public static string Token(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "?";
        foreach (var c in s)
            if (char.IsWhiteSpace(c))
                return string.Create(s.Length, s, static (span, src) =>
                {
                    for (var i = 0; i < src.Length; ++i)
                        span[i] = char.IsWhiteSpace(src[i]) ? '_' : src[i];
                });
        return s;
    }
}
