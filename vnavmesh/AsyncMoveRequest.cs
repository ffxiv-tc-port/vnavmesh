using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public class AsyncMoveRequest : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _follow;
    // 路徑點帶著 AreaId 一起交給 FollowPath，
    // 否則自訂連結的「等客戶端把路徑播完」邏輯永遠不會生效。
    private Task<List<Waypoint>>? _pendingTask;
    private CancellationTokenSource? _pendingCts;
    private bool _pendingFly;
    private float _pendingDestRange;

    /// <summary>
    /// 「上一筆還在跑的時候又進來的請求」暫存格,單格、後到的蓋掉先到的。
    /// 🔴 刻意是 class 而不是可為 null 的 tuple/struct:這個欄位會被兩條執行緒寫
    /// (IPC 端點跑在呼叫端的執行緒上,Update() 跑在框架執行緒),而 24 bytes 的結構指派**不是原子的**,參考型別的指派則保證是原子的。
    /// </summary>
    private sealed class QueuedRequest(Vector3 dest, bool fly, float range, PendingRequest? duplicateOf = null)
    {
        public readonly Vector3 Dest = dest;
        public readonly bool Fly = fly;
        public readonly float Range = range;
        /// <summary>非 null＝這是對那一筆算路中請求的同一終點重送：用那一筆的結果，它被取消時才拿這筆重算。</summary>
        public readonly PendingRequest? DuplicateOf = duplicateOf;
    }

    private QueuedRequest? _queued;

    /// <summary>算路中的那一筆（StartMove 寫、Update 收完清掉，MoveTo 從呼叫端執行緒讀；參考指派是原子的）。Start＝送出時的角色位置。</summary>
    private sealed class PendingRequest(Vector3 dest, bool fly, float range, Vector3 start)
    {
        public readonly Vector3 Dest = dest;
        public readonly bool Fly = fly;
        public readonly float Range = range;
        public readonly Vector3 Start = start;
    }

    private PendingRequest? _pendingRequest;
    // 上一筆用到結果的請求（只在框架執行緒讀寫）：晚一幀才到的同一終點重送也一併丟掉。
    private PendingRequest? _lastConsumed;

    // 同一終點＝終點相差 ≤ 0.5 米、fly 與 range 相同，而且角色離送出那一刻 ≤ 2 米（被擊退、傳送之後照舊重算）。
    private const float SameDestinationTolerance = 0.5f;
    private const float SameStartTolerance = 2f;
    private static readonly long DuplicateLogMinIntervalTicks = TimeSpan.FromSeconds(10).Ticks;
    private long _lastDuplicateLogTicks;
    private int _duplicatesSinceLog;

    /// <summary>
    /// 每幀由框架執行緒拍下的本機角色座標。IPC 端點在**呼叫端的執行緒**上需要尋路起點時讀這裡。
    /// 🔴 為什麼不能在呼叫端的執行緒直接讀 <c>Service.ObjectTable.LocalPlayer?.Position</c>：
    ///    跨執行緒取那個共用包裝再解參，拿到的可能是遊戲執行緒剛換掉／剛釋放掉的位址 ⇒ AccessViolationException，而 AVE 在 .NET Core 是 corrupted-state exception，**try/catch 攔不到、遊戲當場崩**。
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
        Volatile.Write(ref _pendingRequest, null);

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
            var pending = Volatile.Read(ref _pendingRequest);
            // 同一終點的重送不算取代：照常用這一筆的結果；這一筆被取消時下面接手那段會拿重送那筆再算一次。
            var duplicate = superseding?.DuplicateOf != null && ReferenceEquals(superseding.DuplicateOf, pending);
            PendingRequest? consumed = null;

            if (superseding != null && !duplicate)
            {
                // 這一筆已經被新的請求取代,結果不再有人要。
                // 🔴 刻意不碰 _pendingTask.Result —— 被取消的工作在那裡會擲例外,而下面
                //    那條 catch 走的是 Plugin.DuoLog,它**每次都會印進使用者的聊天視窗**
                //    照原路走等於每接手一次就對使用者噴一行「Failed to find path」。
                //    這裡只把例外觀察掉,不要留成未觀察的 Task 例外。
                _ = _pendingTask.Exception;
                // 取代它的請求一律接手，即使是更早那一筆的重送（最後到的請求為準）。
                _lastConsumed = null;
            }
            else if (IsCancellation(_pendingTask))
            {
                // 被取消（Nav.PathfindCancelAll、網格卸載或重新載入）是正常收場，不是尋路失敗：
                // 不走 DuoLog（它每一次都會印進使用者的聊天視窗），也不印「Pathfinding complete」。
                _ = _pendingTask.Exception;
                Service.Log.Information("[AsyncMoveRequest] 移動請求的尋路在算完之前被取消（Nav.PathfindCancelAll、導航網格卸載或重新載入），這次不移動。");
            }
            else
            {
                // 失敗也算用過：同一終點再算一次多半一樣失敗，交給呼叫端下一次的請求。
                consumed = pending;
                try
                {
                    var path = _pendingTask.Result;
                    Service.Log.Information($"Pathfinding complete");
                    _follow.Move(path, !_pendingFly, _pendingDestRange);
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
            Volatile.Write(ref _pendingRequest, null);
            if (consumed != null)
                _lastConsumed = consumed;
        }

        // 接手排隊中的請求。這一段每幀都跑,順便關掉一個競態:MoveTo 有可能在「讀到
        // _pendingTask 非 null」之後、寫 _queued 之前,被框架執行緒把那筆任務收乾淨 ⇒
        // 排下去的請求就沒人接。每幀在這裡檢查一次,那個窗口最多只延後一幀。
        if (_pendingTask == null)
        {
            QueuedRequest? next = Volatile.Read(ref _queued);
            if (next?.DuplicateOf != null && ReferenceEquals(next.DuplicateOf, _lastConsumed))
            {
                // 重送的那一筆已經拿到結果（現在照它走）：丟掉。CAS 失敗＝期間換成了別的請求，下一幀照常接手。
                Interlocked.CompareExchange(ref _queued, null, next);
                next = null;
            }
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
            var pending = Volatile.Read(ref _pendingRequest);
            if (pending != null && IsSameRequest(pending, dest, fly, range))
            {
                Volatile.Write(ref _queued, new QueuedRequest(dest, fly, range, pending));
                NoteDuplicate(dest, fly);
                return true;
            }

            // 🔴 刻意**不**在這裡直接改寫 _pendingTask,也刻意不採用上游下游那種
            //    「放生舊任務、當場接上新的」的寫法。MoveTo 會從 IPC 端點進來,實作跑在**呼叫端的執行緒**上;Update() 跑在框架執行緒。目前碼裡的不變式是
            //    「_pendingTask 非 null 時只有框架執行緒會寫它」——在這裡改寫會打破它。
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
        var start = CurrentPlayerPosition();
        // 先寫請求再寫工作：MoveTo 看到工作時就讀得到它屬於哪個終點。
        Volatile.Write(ref _pendingRequest, new PendingRequest(dest, fly, range, start));
        try
        {
            _pendingTask = _manager.QueryPath(start, dest, fly, range, _pendingCts.Token);
        }
        catch
        {
            Volatile.Write(ref _pendingRequest, null);
            throw;
        }
        _pendingFly = fly;
        _pendingDestRange = range;
        return true;
    }

    private bool IsSameRequest(PendingRequest p, Vector3 dest, bool fly, float range)
        => p.Fly == fly && MathF.Abs(p.Range - range) < 0.01f && Vector3.Distance(p.Dest, dest) <= SameDestinationTolerance
            && TryPlayerPosition(out var pos) && Vector3.Distance(pos, p.Start) <= SameStartTolerance;

    // 與 CurrentPlayerPosition 同一條規則（非框架執行緒只讀快照），但拿不到就回 false、不寫說明訊息。
    private bool TryPlayerPosition(out Vector3 pos)
    {
        if (Service.Framework.IsInFrameworkUpdateThread)
        {
            var player = Service.ObjectTable.LocalPlayer;
            pos = player?.Position ?? default;
            return player != null;
        }
        var snapshot = Volatile.Read(ref _playerPos);
        pos = snapshot?.Position ?? default;
        return snapshot != null;
    }

    // 每一次都寫 Debug；Information 最多 10 秒一行（呼叫端可能每幀重送）。跑在呼叫端執行緒，所以只用 Interlocked。
    private void NoteDuplicate(Vector3 dest, bool fly)
    {
        Interlocked.Increment(ref _duplicatesSinceLog);
        Service.Log.Debug($"[AsyncMoveRequest] 算路中又收到同一終點的{(fly ? "飛行" : "移動")}請求 {dest:f2}，併進進行中的那一筆（不取消、不重算）");
        var now = DateTime.Now.Ticks;
        var last = Interlocked.Read(ref _lastDuplicateLogTicks);
        if (now - last >= DuplicateLogMinIntervalTicks && Interlocked.CompareExchange(ref _lastDuplicateLogTicks, now, last) == last)
            Service.Log.Information($"[AsyncMoveRequest] 算路進行中又收到同一終點的移動請求，已併進進行中的那一筆、不取消也不重算（近 10 秒 {Interlocked.Exchange(ref _duplicatesSinceLog, 0)} 次；終點 {dest:f2}）。呼叫端只看 Path.IsRunning 就重送時，算路期間會一直重送；也判 SimpleMove.PathfindInProgress 可以省掉這些請求。");
    }

    // 取消有兩種樣子：工作開始前就取消 ⇒ IsCanceled；已經在跑的工作讀到被取消的內層工作
    // （NavmeshManager.ExecuteWhenIdle 的 t.Result）⇒ IsFaulted、例外裡只有取消。
    private static bool IsCancellation(Task task)
    {
        if (task.IsCanceled)
            return true;
        if (!task.IsFaulted)
            return false;
        var inner = task.Exception!.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(e => e is OperationCanceledException);
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
    /// 🔑 在框架執行緒上讀實時值(指令、OnStuck 重試、Update 接手排隊請求
    ///    全都走這一條)；只有從 IPC 端點進來、跑在呼叫端執行緒上的那條路徑改讀快照。
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
            Service.Log.Information("[AsyncMoveRequest] 收到來自其他執行緒的移動請求，但還沒有任何一幀拍到本機角色座標(插件剛載入、正在切圖，或沒登入)；這次的尋路起點退回原點，通常會直接失敗並由呼叫端重試。持續出現請連同這一行回報。");
        return default;
    }
}
