using System;
using System.Numerics;
using System.Threading;

namespace Navmesh;

public static class MapUtils
{
    /// <summary>
    /// 跨執行緒讀得到的地圖標記(旗子)座標快照。IPC 的 Query.Mesh.FlagToPoint 跑在
    /// <b>呼叫端的執行緒</b>上，在那條執行緒上讀原生的 AgentMap 就是跨執行緒解參。
    /// <para>
    /// 🔴 刻意是 class 而不是 Vector2?：Nullable&lt;Vector2&gt; 是 12 bytes，指派<b>不是原子的</b>，
    ///    撕裂讀出來的會是「一半舊一半新」的座標 ＝ 把角色送往一個不存在的目的地
    ///    (與 AsyncMoveRequest.PositionSnapshot 同一個理由)。參考型別的指派則保證是原子的。
    /// </para>
    /// <para>
    /// 🔑 null 的語意 ＝「拍快照那一刻地圖上沒有標記」，與舊碼 GetFlagPosition() 回 null 的
    ///    分支逐字對應，呼叫端本來就在處理它。
    /// </para>
    /// </summary>
    private sealed class FlagSnapshot(Vector2 position)
    {
        public readonly Vector2 Position = position;
    }

    private static FlagSnapshot? _flag;

    // 一次性熔斷。🔴 volatile：寫它的是框架執行緒，讀它的是 IPC 呼叫端的執行緒。
    private static volatile bool _flagReadBroken;

    /// <summary>
    /// <b>需求窗</b>：只有「最近真的有人跨執行緒查過旗子」時，Update() 才會去碰原生記憶體。
    /// 存的是到期時刻的 Ticks(UTC)，<c>0</c> ＝ 目前沒有任何需求。
    /// <para>
    /// 🔴🔴 <b>存在的理由是一條硬紅線</b>：未證實假設 ＋ 原生指標 ＋ 每幀 ＝ 部署閘門。
    ///    無條件每幀去讀 AgentMap，對<b>從來不用旗子傳送的使用者</b>(艦隊裡目前<b>沒有任何一個
    ///    外掛</b>呼叫 Query.Mesh.FlagToPoint)是<b>純新增的曝險、換不到任何好處</b>：
    ///    若 AgentMap.Instance() 回的是非 null 的壞指標，解參就是 AccessViolationException，
    ///    而 AVE 在 .NET Core 是 corrupted-state exception，<b>try/catch 與「排在最後一行」都救不了</b>。
    /// </para>
    /// <para>
    /// 🔑 <b>判準</b>：一個從不用 FlagToPoint 的使用者，他的 AgentMap 每幀解參次數必須是 <b>0</b>，
    ///    與改動前逐字相同。Update() 的第一件事就是讀這個欄位，為 0 就直接 return。
    /// </para>
    /// <para>
    /// ⚠️ 到期時<b>連快照一起丟掉</b>，不留著。留著的話「隔了五分鐘再查一次」會拿到五分鐘前的
    ///    旗子座標 —— 那比回 null 糟得多(會把角色送去舊目的地)。
    /// </para>
    /// </summary>
    private static readonly long DemandWindowTicks = TimeSpan.FromSeconds(30).Ticks;
    private static long _demandUntilTicks;

    // 冷路徑(需求窗剛開，還沒有任何一幀拍過)向框架執行緒討一次讀取的等待上限。
    // 🔴 一定要有上限：呼叫端的執行緒有可能正是框架執行緒在等的那一條，無上限地等就是死鎖。
    //    逾時的代價只是這一次回 null，下一幀 Update() 就會把快照補上。
    private static readonly TimeSpan ColdReadTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly long ColdTimeoutLogMinIntervalTicks = TimeSpan.FromSeconds(30).Ticks;
    private static long _lastColdTimeoutLogTicks;

    /// <summary>
    /// 在框架執行緒上更新地圖標記座標的快照。每幀由 Plugin.OnUpdate 呼叫。
    /// <para>
    /// 🔑 <b>沒有需求時一個原生存取都不做</b>(見 <see cref="DemandWindowTicks"/>)。
    ///    這一支被呼叫但 early-return 的成本是「讀一個 long 欄位」。
    /// </para>
    /// </summary>
    public static void Update()
    {
        var until = Interlocked.Read(ref _demandUntilTicks);
        if (until == 0)
            return; // 🔑 從來沒有人跨執行緒查過旗子 ⇒ 零原生存取，與改動前逐字相同

        if (DateTime.UtcNow.Ticks > until)
        {
            // 需求過期：停止刷新，並且把快照丟掉(見上面對「不留過期座標」的說明)。
            Interlocked.CompareExchange(ref _demandUntilTicks, 0, until);
            Volatile.Write(ref _flag, null);
            return;
        }

        RefreshSnapshot();
    }

    /// <summary>
    /// 讀一次原生的旗子座標並寫進快照。<b>只有框架執行緒會走到這裡</b>
    /// （Update() 每幀，或冷路徑經由 RunOnFrameworkThread 討的那一次）。
    /// <para>
    /// 🔴 <c>AgentMap.Instance()</c> 一路往下是 <c>AgentModule.Instance()</c> →
    ///    <c>UIModule.Instance()</c> → <c>Framework.Instance()</c>，前兩層是手寫的 null 傳遞包裝，
    ///    最底層那個是 CS 產生的 <c>[StaticAddress]</c> —— <b>解不出位址時它擲
    ///    InvalidOperationException，不是回 null</b>。第一次失敗就永久熔斷並寫一行 Information。
    /// </para>
    /// <para>
    /// ⚠️ <b>攔得住的只有受管理的例外。</b>若 Instance() 回的是非 null 的壞指標，解參是 AVE，
    ///    <c>try/catch</c> 完全無效。這一點與舊碼相同 —— 而因為有需求窗，<b>曝險頻率也與舊碼相同</b>：
    ///    只有在真的有人查旗子的那段期間才會每幀讀。
    /// </para>
    /// </summary>
    private static void RefreshSnapshot()
    {
        if (_flagReadBroken)
            return;

        Vector2? flag;
        try
        {
            flag = ReadFlagPosition();
        }
        catch (Exception ex)
        {
            _flagReadBroken = true;
            Service.Log.Information(
                $"[MapUtils] 讀取地圖標記失敗，已停止更新旗子座標快照(這通常代表 FFXIVClientStructs "
              + $"的某個位址在台服對不上)。影響：其他外掛透過 Query.Mesh.FlagToPoint 查旗子會一律"
              + $"拿到「沒有標記」；你自己打 /vnav moveflag 不受影響。持續出現請連同這一行回報。{ex}");
            return;
        }

        var prev = Volatile.Read(ref _flag);
        if (flag == null)
        {
            if (prev != null)
                Volatile.Write(ref _flag, null);
            return;
        }

        // 標記沒動時不要每幀都配一個新物件。
        if (prev == null || prev.Position != flag.Value)
            Volatile.Write(ref _flag, new FlagSnapshot(flag.Value));
    }

    public static Vector3? FlagToPoint(NavmeshQuery q)
    {
        var flag = CurrentFlagPosition();
        if (flag == null)
            return null;
        return q.FindPointOnFloor(new(flag.Value.X, 1024, flag.Value.Y));
    }

    /// <summary>
    /// 🔑 在框架執行緒上讀實時值，行為與舊碼逐字相同：/vnav moveflag、/vnav flyflag 與除錯視窗
    ///    全都走這一條(IsInFrameworkUpdateThread 比的是<b>執行緒身分</b>，不是「現在在不在
    ///    Update 裡」，所以 UI 的 Draw 回呼也算)。這條路徑<b>不開需求窗</b> —— 它根本不需要快照，
    ///    開了只會讓之後 30 秒白白每幀去讀。
    /// <para>
    /// 只有從 IPC 端點進來、跑在呼叫端執行緒上的那條路徑走快照，並且：
    /// <list type="number">
    /// <item><b>每次查詢都把需求窗往後推 30 秒</b> ⇒ 有人在用的期間快照最多落後一幀。</item>
    /// <item><b>冷路徑(需求窗剛開)不回 null，而是向框架執行緒討一次讀取</b>(有 200ms 上限)。
    ///       回 null 才是糟糕的答案：null 的語意是「地圖上沒有標記」，呼叫端會據此說
    ///       「你沒有設定標記」並停手，而不是重試 —— 那是一個<b>會說謊的</b>答案。
    ///       討一次的代價是第一次查詢阻塞至多一幀，之後 30 秒內都是純記憶體讀。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static Vector2? CurrentFlagPosition()
    {
        if (Service.Framework.IsInFrameworkUpdateThread)
            return ReadFlagPosition();

        var now = DateTime.UtcNow.Ticks;
        var prevUntil = Interlocked.Exchange(ref _demandUntilTicks, now + DemandWindowTicks);
        var warm = prevUntil != 0 && now <= prevUntil;
        if (warm)
        {
            // 🔴 這裡刻意**不**退回去讀原生值 —— 那正是跨執行緒解參的那一行。
            return Volatile.Read(ref _flag)?.Position;
        }

        return ColdRead();
    }

    /// <summary>
    /// 需求窗剛開、還沒有任何一幀拍過快照時，向框架執行緒討一次讀取。
    /// </summary>
    private static Vector2? ColdRead()
    {
        if (_flagReadBroken)
            return null;

        // 🔴 卸載途中 RunOnFrameworkThread 會**就地**執行 delegate(Dalamud/Game/Framework.cs:171-188)，
        //    也就是在呼叫端的執行緒上解參原生記憶體 —— 正是要避免的那件事。直接放棄。
        if (Service.Framework.IsFrameworkUnloading)
            return null;

        try
        {
            var task = Service.Framework.RunOnFrameworkThread(RefreshSnapshot);
            if (!task.Wait(ColdReadTimeout))
            {
                var now = DateTime.UtcNow.Ticks;
                var last = Interlocked.Read(ref _lastColdTimeoutLogTicks);
                if (now - last >= ColdTimeoutLogMinIntervalTicks
                    && Interlocked.CompareExchange(ref _lastColdTimeoutLogTicks, now, last) == last)
                {
                    Service.Log.Information(
                        $"[MapUtils] 有外掛從背景執行緒查地圖標記，但等了 "
                      + $"{ColdReadTimeout.TotalMilliseconds:f0} 毫秒仍沒等到框架執行緒回應(遊戲可能正在卡頓"
                      + $"或載入)，這一次回報「沒有標記」。下一幀起快照就會就緒，通常不會再出現。");
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            // RefreshSnapshot 自己已經攔掉讀取失敗，走到這裡代表排程本身出問題(例如外掛正在卸載)。
            Service.Log.Information($"[MapUtils] 向框架執行緒索取地圖標記時失敗，這一次回報「沒有標記」：{ex}");
            return null;
        }

        return Volatile.Read(ref _flag)?.Position;
    }

    private unsafe static Vector2? ReadFlagPosition()
    {
        var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0)
            return null;
        var marker = map->FlagMapMarkers[0];
        return new(marker.XFloat, marker.YFloat);
    }
}
