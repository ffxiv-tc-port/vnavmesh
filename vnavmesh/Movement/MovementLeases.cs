using System;
using System.Collections.Generic;
using System.Linq;

namespace Navmesh.Movement;

/// <summary>
/// 「請 vnavmesh 在我這段序列期間別動」（或「這段期間改用我的路徑容許值」）的
/// <b>租約（lease）登記處</b>：多個外掛各自持有一把帶到期時間的租約，租約裡帶著它想要的值，
/// 讀取端一律是「租約值 ?? 使用者的值」。放約或逾時就自動還原，<b>不需要任何人記得還</b>。
/// </summary>
/// <remarks>
/// ⚠️ <b>執行緒</b>：IPC 端點跑在<b>呼叫端的執行緒</b>上（沒有任何「一定在 Framework 執行緒」
/// 的保證），而 <see cref="ResolveMovementAllowed"/>／<see cref="ResolveTolerance"/> 每幀從
/// Framework 執行緒讀、<see cref="Snapshot"/> 每幀從繪製執行緒讀 ⇒ <b>全程上鎖</b>。
/// </remarks>
internal static class MovementLeases
{
    /// <summary>沒指定時長時的預設租期（5 分鐘）。與 AutoRetainer／YesAlready 的租約政策一致。</summary>
    public const int DefaultLeaseMilliseconds = 300_000;

    /// <summary>單一把租約的<b>硬性</b>上限（5 分鐘）。要求更長會被夾到這個值。</summary>
    public const int MaxLeaseMilliseconds = 300_000;

    /// <summary>建議的續約間隔（30 秒＝租期的十分之一）。</summary>
    /// <remarks>⚠️ <b>續約間隔不能接近租期</b>：<see cref="Renew"/> 的第一件事是掃除，掃除條件是<c>now &gt;= ExpiresAt</c>
    /// ⇒ 間隔只要接近租期，第一次心跳送到時那把已經被掃掉、續約<b>必定</b>回 <see langword="false"/>（不是競態，是每次都會發生）。</remarks>
    public const int RenewIntervalHintMs = 30_000;

    /// <summary>
    /// 同時存在的租約把數上限。超過就拒絕新的請求（回 <see cref="Guid.Empty"/>）。
    /// </summary>
    public const int LeaseCap = 32;

    /// <summary>租約可以要求的容許值下限。</summary>
    /// <remarks>
    /// 🔴 容許值是「路徑點離『上一幀到這一幀』那段位移的距離超過多少就不消耗這個點」。
    /// </remarks>
    public const float MinTolerance = 0.01f;

    /// <summary>租約可以要求的容許值上限（超過就是「整條路徑一口氣全部消耗掉」）。</summary>
    public const float MaxTolerance = 100f;

    private sealed class Lease(Guid id, string owner, long expiresAt)
    {
        public Guid Id { get; } = id;
        public string Owner { get; } = owner;

        /// <summary><see cref="Environment.TickCount64"/> 座標系的到期時刻。</summary>
        public long ExpiresAt { get; set; } = expiresAt;

        /// <summary>續約時沿用的時長（<see cref="Renew"/> 不帶參數時用）。</summary>
        public int DurationMs { get; set; }

        /// <summary><see langword="null"/>＝這把租約對移動開關沒有意見。</summary>
        public bool? MovementAllowed { get; set; }

        /// <summary><see langword="null"/>＝這把租約對容許值沒有意見。</summary>
        public float? Tolerance { get; set; }

        /// <summary>最後一次設定容許值時取得的遞增序號（最後寫入者優先）。</summary>
        public long ToleranceSeq { get; set; }
    }

    /// <remarks>🔴 <b>鎖內絕不呼叫 ImGui、絕不做檔案 I/O，也不寫 log</b>：逾時訊息在鎖內先收進一個 list，
    /// 出了鎖才 <see cref="Flush"/>。UI 走「鎖內拍快照、鎖外畫」。</remarks>
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, Lease> Leases = [];
    private static long _toleranceSeq;

    /// <remarks>
    /// 🔴 只用來<b>提早否定</b>：<see langword="false"/> 一定代表沒有租約（清空一定在設它之前），
    /// <see langword="true"/> 只代表「可能有」，還是要進鎖裡掃過期。
    /// 反過來寫（樂觀地相信 true）會讓已經到期的租約繼續壓住。
    /// </remarks>
    private static volatile bool _anyLeases;

    /// <summary>目前是否有<b>任何一把</b>沒到期的租約（診斷／UI 用）。</summary>
    public static bool AnyActive
    {
        get
        {
            if (!_anyLeases)
                return false;

            List<string>? logs = null;
            bool any;
            lock (Gate)
            {
                SweepLocked(ref logs);
                any = Leases.Count != 0;
            }

            Flush(logs);
            return any;
        }
    }

    /// <summary>
    /// 把使用者的移動開關與目前的租約疊起來，回傳<b>實際生效</b>的值。
    /// </summary>
    /// <remarks>🔴 只能往「禁止」的方向壓；沒有任何一把說不准動時，照使用者的值。</remarks>
    public static bool ResolveMovementAllowed(bool userValue)
    {
        if (!_anyLeases)
            return userValue;

        List<string>? logs = null;
        var result = userValue;
        lock (Gate)
        {
            SweepLocked(ref logs);
            foreach (var lease in Leases.Values)
                if (lease.MovementAllowed == false)
                {
                    result = false;
                    break;
                }
        }

        Flush(logs);
        return result;
    }

    /// <summary>
    /// 把使用者的路徑容許值與目前的租約疊起來，回傳<b>實際生效</b>的值（最後寫入者優先）。
    /// </summary>
    public static float ResolveTolerance(float userValue)
    {
        if (!_anyLeases)
            return userValue;

        List<string>? logs = null;
        float result;
        lock (Gate)
        {
            SweepLocked(ref logs);
            float? best = null;
            var bestSeq = long.MinValue;
            foreach (var lease in Leases.Values)
                if (lease.Tolerance is { } t && lease.ToleranceSeq > bestSeq)
                {
                    best = t;
                    bestSeq = lease.ToleranceSeq;
                }
            result = best ?? userValue;
        }

        Flush(logs);
        return result;
    }

    /// <summary>
    /// 掃掉已經到期的租約。<b>每幀呼叫一次</b>（<see cref="FollowPath.Update"/> 的開頭）。
    /// </summary>
    public static void Sweep()
    {
        if (!_anyLeases)
            return;

        List<string>? logs = null;
        lock (Gate)
            SweepLocked(ref logs);

        Flush(logs);
    }

    /// <summary>
    /// 目前每一把有效租約的診斷快照：租用者名字、距離逾時還有多久（毫秒）、它押著的兩個值。
    /// </summary>
    /// <remarks>⚠️ 只給 UI／tooltip 用（會配置陣列），呼叫前先判 <see cref="AnyActive"/>。</remarks>
    public static (string Owner, long RemainingMs, bool? MovementAllowed, float? Tolerance)[] Snapshot()
    {
        if (!_anyLeases)
            return [];

        List<string>? logs = null;
        (string Owner, long RemainingMs, bool? MovementAllowed, float? Tolerance)[] snapshot;
        lock (Gate)
        {
            SweepLocked(ref logs);
            if (Leases.Count == 0)
            {
                snapshot = [];
            }
            else
            {
                var now = Environment.TickCount64;
                snapshot = Leases.Values
                    .Select(x => (x.Owner, Math.Max(0, x.ExpiresAt - now), x.MovementAllowed, x.Tolerance))
                    .ToArray();
            }
        }

        Flush(logs);
        return snapshot;
    }

    /// <summary>
    /// 取得一把新的租約。回傳的 <see cref="Guid"/> 就是憑證；<see cref="Guid.Empty"/>＝<b>沒拿到</b>（沒帶名字，或已達 <see cref="LeaseCap"/>），呼叫端必須自己判斷，不要當成拿到了。
    /// </summary>
    /// <param name="owner">租用者名字（慣例是自己的 InternalName）。空白會被拒絕。</param>
    /// <param name="milliseconds">租期毫秒；夾在 <c>1</c> 與 <see cref="MaxLeaseMilliseconds"/> 之間。</param>
    public static Guid Acquire(string? owner, int milliseconds)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            Service.Log.Warning("[MovementLease] 收到沒有帶名字的移動租用請求，已拒絕。租用者必須帶一個識別字串" +
                                "（慣例是自己的 InternalName），否則使用者無從得知是誰讓角色停住。");
            return Guid.Empty;
        }

        var name = owner!.Trim();
        List<string>? logs = null;
        var duration = ClampDuration(milliseconds, name, ref logs);
        var id = Guid.NewGuid();
        string owners;
        bool overCap;

        lock (Gate)
        {
            SweepLocked(ref logs);
            overCap = Leases.Count >= LeaseCap;
            if (!overCap)
            {
                Leases[id] = new Lease(id, name, Environment.TickCount64 + duration) { DurationMs = duration };
                _anyLeases = true;
            }

            owners = DistinctOwnersLocked();
        }

        Flush(logs);

        if (overCap)
        {
            Service.Log.Warning($"[MovementLease] 移動租約已達上限 {LeaseCap} 把，拒絕「{name}」的請求。" +
                                $"目前持有者：{owners}。⇒ 這幾乎一定是某個呼叫端只 Acquire 不 Release。");
            return Guid.Empty;
        }

        Service.Log.Information($"[MovementLease] 「{name}」取得移動租約 {id}（{duration} 毫秒）。目前持有者：{owners}。");
        return id;
    }

    /// <summary>交回一把租約。回 <see langword="false"/>＝這把不存在（已經放開過或已經逾時）。</summary>
    /// <remarks>🔑 放開的那一刻它押著的值就不再參與疊加 ⇒ 使用者的值自動變回權威。</remarks>
    public static bool Release(Guid id)
    {
        string? owner = null;
        var remaining = 0;
        List<string>? logs = null;

        lock (Gate)
        {
            if (Leases.Remove(id, out var lease))
                owner = lease.Owner;

            SweepLocked(ref logs);
            remaining = Leases.Count;
            if (remaining == 0)
                _anyLeases = false;
        }

        Flush(logs);

        if (owner == null)
            return false;

        Service.Log.Information($"[MovementLease] 「{owner}」放開移動租約 {id}，剩餘 {remaining} 把。");
        return true;
    }

    /// <summary>
    /// 續約（心跳）。回 <see langword="false"/>＝這把已經不在了，呼叫端必須重新
    /// <see cref="Acquire"/>，<b>不要當成續約成功</b>。
    /// </summary>
    /// <param name="milliseconds"><see langword="null"/>＝沿用取得時的時長。</param>
    public static bool Renew(Guid id, int? milliseconds = null)
    {
        List<string>? logs = null;
        var found = false;

        lock (Gate)
        {
            SweepLocked(ref logs);
            if (Leases.TryGetValue(id, out var lease))
            {
                found = true;
                var duration = milliseconds is { } ms ? ClampDuration(ms, lease.Owner, ref logs) : lease.DurationMs;
                lease.DurationMs = duration;

                // 🔴 取 max：續約永遠只會往後延，不會把已經談好的到期時間往前搬。
                var until = Environment.TickCount64 + duration;
                if (until > lease.ExpiresAt)
                    lease.ExpiresAt = until;
            }
        }

        Flush(logs);
        return found;
    }

    /// <summary>
    /// 用這把租約押住移動開關。<paramref name="allowed"/> 傳 <see langword="false"/>＝
    /// 「我這把要求別動」；傳 <see langword="true"/>＝「我這把不再要求別動」（<b>不是</b>「要求放行」）。
    /// 回 <see langword="false"/>＝這把租約已經不在了。
    /// </summary>
    public static bool SetMovementAllowed(Guid id, bool allowed)
    {
        string? owner = null;
        var changed = false;
        List<string>? logs = null;

        lock (Gate)
        {
            SweepLocked(ref logs);
            if (Leases.TryGetValue(id, out var lease))
            {
                owner = lease.Owner;
                changed = lease.MovementAllowed != allowed;
                lease.MovementAllowed = allowed;
            }
        }

        Flush(logs);

        if (owner == null)
            return false;

        if (changed)
            Service.Log.Information($"[MovementLease] 「{owner}」的租約 {id} 把移動開關押成 {(allowed ? "允許" : "禁止")}。" +
                                    (allowed ? "" : "⇒ 在這把租約放開或逾時之前，vnavmesh 不會驅動角色移動。"));
        return true;
    }

    /// <summary>
    /// 用這把租約押住路徑容許值（夾在 <see cref="MinTolerance"/>～<see cref="MaxTolerance"/>）。
    /// 回 <see langword="false"/>＝這把租約已經不在了，<b>或是傳進來的值不是有限數</b>。
    /// </summary>
    public static bool SetTolerance(Guid id, float tolerance)
    {
        if (!float.IsFinite(tolerance))
        {
            Service.Log.Warning($"[MovementLease] 租約 {id} 要求的路徑容許值 {tolerance} 不是有限數，已拒絕" +
                                "（NaN／無限大會讓整條路徑在同一幀被消耗光，而且完全沒有訊息）。");
            return false;
        }

        var clamped = Math.Clamp(tolerance, MinTolerance, MaxTolerance);
        string? owner = null;
        string? conflict = null;
        List<string>? logs = null;

        lock (Gate)
        {
            SweepLocked(ref logs);
            if (Leases.TryGetValue(id, out var lease))
            {
                owner = lease.Owner;
                lease.Tolerance = clamped;
                lease.ToleranceSeq = ++_toleranceSeq;

                // 兩把以上的租約同時押著不同的容許值 ⇒ 最後寫入者贏，但那件事要說出來。
                var others = Leases.Values
                    .Where(x => x.Id != id && x.Tolerance is { } t && t != clamped)
                    .Select(x => $"{x.Owner}={x.Tolerance}")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (others.Length > 0)
                    conflict = string.Join("、", others);
            }
        }

        Flush(logs);

        if (owner == null)
            return false;

        if (clamped != tolerance)
            ReportOnce(owner, $"[MovementLease] 「{owner}」要求的路徑容許值 {tolerance} 超出範圍，已夾成 {clamped}" +
                              $"（範圍 {MinTolerance}～{MaxTolerance}）。這行訊息對同一個租用者只會出現一次。");

        if (conflict != null)
            ReportOnce("tolerance-conflict:" + owner,
                $"[MovementLease] 「{owner}」把路徑容許值押成 {clamped}，但同時還有別的租約押著不同的值：{conflict}。" +
                "⇒ 採用<最後寫入者>，也就是這一次的值。這行訊息對同一個租用者只會出現一次。");

        return true;
    }

    /// <summary>把所有租約丟掉（外掛卸載）。</summary>
    public static void ReleaseAll(string reason)
    {
        string owners;

        lock (Gate)
        {
            if (Leases.Count == 0)
            {
                _anyLeases = false;
                return;
            }

            owners = DistinctOwnersLocked();
            Leases.Clear();
            _anyLeases = false;
        }

        Service.Log.Information($"[MovementLease] 丟掉全部移動租約（{reason}）：{owners}。");
    }

    /// <summary>已經回報過的訊息鍵。<b>同一個鍵只寫一次</b>，永不清空。</summary>
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    /// <summary>
    /// 只保護 <see cref="Reported"/>。<b>刻意不共用 <see cref="Gate"/></b>：
    /// <see cref="ClampDuration"/> 會在已經持有 <see cref="Gate"/> 的狀態下被 <see cref="Renew"/> 呼叫進來。
    /// </summary>
    private static readonly object ReportGate = new();

    private static void ReportOnce(string key, string message)
    {
        bool first;
        lock (ReportGate)
            first = Reported.Add(key);
        if (first)
            Service.Log.Information(message);
    }

    /// <summary>
    /// 把要求的租期夾進 <c>1</c>～<see cref="MaxLeaseMilliseconds"/>，並在<b>真的夾到</b>時
    /// 對同一個租用者寫一次 <c>Information</c>。
    /// </summary>
    /// <remarks>🔴 夾值如果是靜默的，呼叫端會以為自己拿到了要求的時長，然後在半路被逾時掃掉。</remarks>
    private static int ClampDuration(int milliseconds, string owner, ref List<string>? logs)
    {
        if (milliseconds >= 1 && milliseconds <= MaxLeaseMilliseconds)
            return milliseconds;

        var clamped = milliseconds < 1 ? 1 : MaxLeaseMilliseconds;

        // 🔴 Renew 是在鎖內呼叫這支的 ⇒ 這裡不能直接寫 log
        //    （Serilog 有自己的鎖，在 Gate 裡面做 I/O 會把死鎖面積擴到別人的元件上），
        //    只能收進 logs，由呼叫端出了鎖再 Flush。
        //    ⚠️ ReportGate 是另一把鎖，而且沒有任何路徑是 ReportGate → Gate，不會死鎖。
        bool first;
        lock (ReportGate)
            first = Reported.Add("duration:" + owner);

        if (!first)
            return clamped;

        (logs ??= []).Add(
            $"[MovementLease] 「{owner}」要求的租期 {milliseconds} 毫秒超出範圍，已夾成 {clamped} 毫秒" +
            $"（上限 {MaxLeaseMilliseconds} 毫秒）。要壓住更久必須自己每 {RenewIntervalHintMs} 毫秒續約一次，" +
            "不要假設拿到了要求的時長。這行訊息對同一個租用者只會出現一次。");
        return clamped;
    }

    /// <summary>清掉已經到期的租約。<b>呼叫端必須先持有 <see cref="Gate"/>。</b></summary>
    /// <remarks>
    /// 🔴 逾時訊息<b>不在這裡寫出去</b>，只收進 <paramref name="logs"/>：這支一定在鎖內被呼叫，
    /// 而鎖內做 I/O（Serilog 有自己的鎖）會把死鎖面積擴大到別人的元件上。
    /// </remarks>
    private static void SweepLocked(ref List<string>? logs)
    {
        if (Leases.Count == 0)
        {
            _anyLeases = false;
            return;
        }

        var now = Environment.TickCount64;
        List<Guid>? expired = null;

        foreach (var (id, lease) in Leases)
            if (now >= lease.ExpiresAt)
                (expired ??= []).Add(id);

        if (expired == null)
            return;

        foreach (var id in expired)
        {
            var lease = Leases[id];
            Leases.Remove(id);

            // 🔴 寫 Information：使用者跑 LogLevel 1。租約逾時＝「有人壓著 vnavmesh 卻沒放開」，
            // 這一行是使用者回報「角色突然不動了／突然又會動了」時唯一的線索。
            (logs ??= []).Add(
                $"[MovementLease] 「{lease.Owner}」的移動租約 {id} 已逾時，自動放開" +
                $"（押著的值：移動={FormatBool(lease.MovementAllowed)}、容許值={FormatFloat(lease.Tolerance)}）。" +
                "租用者沒有續約，可能已經當掉或被卸載 —— vnavmesh 恢復使用者自己的設定。");
        }

        if (Leases.Count == 0)
            _anyLeases = false;
    }

    /// <summary>把收在鎖內的訊息寫出去。<b>一定要在鎖外呼叫。</b></summary>
    private static void Flush(List<string>? logs)
    {
        if (logs == null)
            return;

        foreach (var line in logs)
            Service.Log.Information(line);
    }

    private static string FormatBool(bool? v) => v is null ? "未指定" : v.Value ? "允許" : "禁止";
    private static string FormatFloat(float? v) => v is null ? "未指定" : v.Value.ToString();

    /// <summary>目前持有者名單（去重）。<b>呼叫端必須先持有 <see cref="Gate"/>。</b></summary>
    private static string DistinctOwnersLocked()
    {
        if (Leases.Count == 0)
            return "(無)";
        return string.Join("、", Leases.Values.Select(x => x.Owner).Distinct(StringComparer.Ordinal));
    }
}
