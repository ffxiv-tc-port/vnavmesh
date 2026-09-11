using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public class AsyncMoveRequest : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _follow;
    // 改成 Waypoint 清單：路徑點要帶著 AreaId 一起交給 FollowPath，
    // 否則自訂連結的「等客戶端把路徑播完」邏輯永遠不會生效。
    private Task<List<Waypoint>>? _pendingTask;
    private CancellationTokenSource? _pendingCts;
    private bool _pendingFly;
    private float _pendingDestRange;

    /// <summary>
    /// 「上一筆還在跑的時候又進來的請求」暫存格,單格、後到的蓋掉先到的。
    /// 🔴 刻意是 class 而不是可為 null 的 tuple/struct:這個欄位會被兩條執行緒寫
    /// (IPC 端點跑在呼叫端的執行緒上,Update() 跑在框架執行緒),而 24 bytes 的
    /// 結構指派**不是原子的** —— 撕裂讀出來的會是「一半舊一半新」的座標,
    /// 也就是把角色送往一個不存在的目的地。參考型別的指派則保證是原子的。
    /// </summary>
    private sealed class QueuedRequest(Vector3 dest, bool fly, float range)
    {
        public readonly Vector3 Dest = dest;
        public readonly bool Fly = fly;
        public readonly float Range = range;
    }

    private QueuedRequest? _queued;

    /// <summary>
    /// 每幀由框架執行緒拍下的本機角色座標。IPC 端點在**呼叫端的執行緒**上需要尋路起點時讀這裡。
    ///
    /// 🔴 為什麼不能在呼叫端的執行緒直接讀 <c>Service.ObjectTable.LocalPlayer?.Position</c>：
    ///    本 Dalamud pin 的 ObjectTable 是「每格×每種 kind 預配一個包裝、存取時就地改寫 Address」
    ///    (Dalamud/Game/ClientState/Objects/ObjectTable.cs:198-231)，而 LocalPlayer ＝ this[0]。
    ///    跨執行緒取那個共用包裝再解參，拿到的可能是遊戲執行緒剛換掉／剛釋放掉的位址
    ///    ⇒ AccessViolationException，而 AVE 在 .NET Core 是 corrupted-state exception，
    ///    **try/catch 攔不到、遊戲當場崩**。索引子雖然有 ThreadSafety.AssertMainThread()，
    ///    但本 fork 只寫一行警告不擲例外 —— 它是偵測器，不是防護。
    ///
    /// 🔴 刻意是 class 而不是一個 Vector3 欄位：Vector3 是 12 bytes，指派**不是原子的**
    ///    (與上面 QueuedRequest 同一個理由)，撕裂讀出來的會是「一半舊一半新」的起點座標。
    ///
    /// 🔑 null 的語意 ＝「拍快照那一刻沒有本機角色」，對應舊碼 <c>LocalPlayer?.Position</c> 的
    ///    null 分支(起點退回 default)。
    /// </summary>
    private sealed class PositionSnapshot(Vector3 position)
    {
        public readonly Vector3 Position = position;
    }

    private PositionSnapshot? _playerPos;

    // 「沒有快照可用」的說明訊息節流。🔴 刻意不用 ECommons 的 EzThrottler：那是整個外掛共用的
    //    靜態 Dictionary 且零同步，從 IPC 端點(呼叫端執行緒)碰它會把字典本身弄壞。
    private static readonly long NoSnapshotLogMinIntervalTicks = TimeSpan.FromSeconds(10).Ticks;
    private long _lastNoSnapshotLogTicks;

    // 排隊中的請求也算「進行中」:呼叫端拿 SimpleMove.PathfindInProgress 來決定要不要
    // 重下請求,回 false 會讓它們以為上一筆已經做完。
    public bool TaskInProgress => _pendingTask != null || Volatile.Read(ref _queued) != null;

    public AsyncMoveRequest(NavmeshManager manager, FollowPath follow)
    {
        _manager = manager;
        _follow = follow;

        _follow.OnStuck += (dest, fly, range) =>
        {
            if (!Service.Config.RetryOnStuck)
                return;

            MoveTo(dest, fly, range);
        };
    }

    public void Dispose()
    {
        // 卸載途中不要再接手任何排隊的請求。
        Volatile.Write(ref _queued, null);

        if (_pendingTask != null)
        {
            // Request cancellation first so a still-running pathfind (especially flying/volume
            // queries, which poll the token from inside their search loop) has a chance to stop
            // quickly on its own. Even so, mesh pathfinds don't check the token mid-search, so
            // still bound the wait defensively rather than blocking the game indefinitely - on
            // timeout we drop the task without observing its result rather than race it.
            _pendingCts?.Cancel();
            if (!_pendingTask.IsCompleted && !_pendingTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Service.Log.Warning("[navmesh] Timed out waiting for in-progress pathfind to finish; abandoning it");
                _pendingCts?.Dispose();
                _pendingCts = null;
                _pendingTask = null;
                return;
            }
            _pendingTask.Dispose();
            _pendingTask = null;
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    public void Update()
    {
        // 🔴 一定要在這裡(框架執行緒)拍快照：從 IPC 端點進來的 MoveTo 跑在**呼叫端的執行緒**上，
        //    它需要尋路起點座標，而在那條執行緒上讀原生物件表就是 AVE(見 PositionSnapshot)。
        RefreshPlayerPositionSnapshot();

        if (_pendingTask != null && _pendingTask.IsCompleted)
        {
            QueuedRequest? superseding = Volatile.Read(ref _queued);

            if (superseding != null)
            {
                // 這一筆已經被新的請求取代,結果不再有人要。
                // 🔴 刻意不碰 _pendingTask.Result —— 被取消的工作在那裡會擲例外,而下面
                //    那條 catch 走的是 Plugin.DuoLog,它**每次都會印進使用者的聊天視窗**
                //    (ECommons 的 DuoLog 在每一個等級都無條件 Svc.Chat.Print)。
                //    照原路走等於每接手一次就對使用者噴一行「Failed to find path」。
                //    這裡只把例外觀察掉,不要留成未觀察的 Task 例外。
                _ = _pendingTask.Exception;
            }
            else
            {
                Service.Log.Information($"Pathfinding complete");
                try
                {
                    _follow.Move(_pendingTask.Result, !_pendingFly, _pendingDestRange);
                }
                catch (Exception ex)
                {
                    Plugin.DuoLog(ex, "Failed to find path");
                }
            }

            _pendingTask.Dispose();
            _pendingTask = null;
            _pendingCts?.Dispose();
            _pendingCts = null;
        }

        // 接手排隊中的請求。這一段每幀都跑,順便關掉一個競態:MoveTo 有可能在「讀到
        // _pendingTask 非 null」之後、寫 _queued 之前,被框架執行緒把那筆任務收乾淨 ⇒
        // 排下去的請求就沒人接。每幀在這裡檢查一次,那個窗口最多只延後一幀。
        if (_pendingTask == null)
        {
            QueuedRequest? next = Volatile.Read(ref _queued);
            if (next != null)
            {
                Volatile.Write(ref _queued, null);
                try
                {
                    StartMove(next.Dest, next.Fly, next.Range);
                }
                catch (Exception ex)
                {
                    // QueryPath 在導航網格沒載入時會擲例外。這裡是框架執行緒的每幀路徑,
                    // 讓例外逃出去會打斷 Plugin.Update ⇒ 攔下來寫一行 Information。
                    Service.Log.Information($"[AsyncMoveRequest] 接手排隊中的移動請求失敗(導航網格可能尚未載入):{ex}");
                }
            }
        }
    }

    public bool MoveTo(Vector3 dest, bool fly, float range = 0)
    {
        if (_pendingTask != null)
        {
            // 新請求「取代」仍在跑的舊請求,而不是整個拒絕。舊行為是回 false 並寫一行
            // Error:艦隊裡多數 SimpleMove.PathfindAndMoveTo 的呼叫端不會先查
            // SimpleMove.PathfindInProgress,對它們來說就是「移動靜默沒發生」。
            //
            // 🔴 刻意**不**在這裡直接改寫 _pendingTask,也刻意不採用上游下游那種
            //    「放生舊任務、當場接上新的」的寫法。MoveTo 會從 IPC 端點進來,而 IPC
            //    實作跑在**呼叫端的執行緒**上;Update() 跑在框架執行緒。目前碼裡的不變式是
            //    「_pendingTask 非 null 時只有框架執行緒會寫它」——在這裡改寫會打破它:
            //    Update() 可能剛通過 IsCompleted 檢查、還沒讀 .Result,這時把欄位換成新任務,
            //    框架執行緒就會在 .Result **阻塞等待新的尋路**(遊戲當場卡住),
            //    然後把新任務 Dispose 掉當成舊結果丟棄 ⇒ 新請求靜默消失。比現況更糟。
            //    所以這裡只做兩件對並行安全的事:①取消舊工作的 token ②把新請求寫進單格佇列。
            //    真正的接手在 Update()(框架執行緒)裡做。
            Volatile.Write(ref _queued, new QueuedRequest(dest, fly, range));

            // 取消是為了讓舊工作盡快結束、縮短新請求的等待。
            // ⚠️ 網格尋路不會在搜尋途中檢查 token(見 Dispose 的說明),所以這是「盡快」
            //    不是「立刻」;最壞情況是新請求等舊的跑完才出發 —— 仍然比被整個拒絕好。
            // ⚠️ Cancel() 本身執行緒安全,但對已經 Dispose 的實例會擲 ObjectDisposedException
            //    (框架執行緒可能剛好正在釋放它)⇒ 攔掉。
            CancellationTokenSource? cts = Volatile.Read(ref _pendingCts);
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 舊工作已經收乾淨了,不必取消。
                }
            }

            var supersedeToleranceStr = range > 0 ? $" within {range}y" : "";
            Service.Log.Information($"Superseding in-progress pathfind with {(fly ? "fly" : "move")}-to {dest:f3}{supersedeToleranceStr}");
            return true;
        }

        Volatile.Write(ref _queued, null);
        return StartMove(dest, fly, range);
    }

    private bool StartMove(Vector3 dest, bool fly, float range)
    {
        var toleranceStr = range > 0 ? $" within {range}y" : "";

        Service.Log.Info($"Queueing {(fly ? "fly" : "move")}-to {dest:f3}{toleranceStr}");
        _pendingCts = new CancellationTokenSource();
        _pendingTask = _manager.QueryPath(CurrentPlayerPosition(), dest, fly, range, _pendingCts.Token);
        _pendingFly = fly;
        _pendingDestRange = range;
        return true;
    }

    /// <summary>
    /// 在框架執行緒上拍下本機角色座標。每幀跑一次(見 Update)。
    /// </summary>
    private void RefreshPlayerPositionSnapshot()
    {
        var player = Service.ObjectTable.LocalPlayer;
        var prev = Volatile.Read(ref _playerPos);
        if (player == null)
        {
            if (prev != null)
                Volatile.Write(ref _playerPos, null);
            return;
        }

        var pos = player.Position;
        // 站著不動時不要每幀都配一個新物件。
        if (prev == null || prev.Position != pos)
            Volatile.Write(ref _playerPos, new PositionSnapshot(pos));
    }

    /// <summary>
    /// 尋路的起點座標。
    /// 🔑 在框架執行緒上讀實時值，行為與舊碼逐字相同(指令、OnStuck 重試、Update 接手排隊請求
    ///    全都走這一條)；只有從 IPC 端點進來、跑在呼叫端執行緒上的那條路徑改讀快照。
    /// ⚠️ 快照最多落後一幀(約 16~33ms，跑步速度下不到 0.2 碼)。起點會被尋路器貼到最近的
    ///    網格多邊形上，那點誤差不影響結果 —— 用一幀的誤差換掉一個會把遊戲弄崩的跨執行緒解參。
    /// </summary>
    private Vector3 CurrentPlayerPosition()
    {
        if (Service.Framework.IsInFrameworkUpdateThread)
            return Service.ObjectTable.LocalPlayer?.Position ?? default;

        var snapshot = Volatile.Read(ref _playerPos);
        if (snapshot != null)
            return snapshot.Position;

        // 還沒有任何一幀拍到本機角色(外掛剛載入、正在切圖，或沒登入)。
        // 🔴 這裡刻意**不**退回去讀原生值 —— 那正是會崩遊戲的那一行。
        var now = DateTime.Now.Ticks;
        var last = Interlocked.Read(ref _lastNoSnapshotLogTicks);
        if (now - last >= NoSnapshotLogMinIntervalTicks && Interlocked.CompareExchange(ref _lastNoSnapshotLogTicks, now, last) == last)
            Service.Log.Information("[AsyncMoveRequest] 收到來自其他執行緒的移動請求，但還沒有任何一幀拍到本機角色座標(外掛剛載入、正在切圖，或沒登入)；這次的尋路起點退回原點，通常會直接失敗並由呼叫端重試。持續出現請連同這一行回報。");
        return default;
    }
}
