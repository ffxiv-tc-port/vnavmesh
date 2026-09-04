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
        _pendingTask = _manager.QueryPath(Service.ObjectTable.LocalPlayer?.Position ?? default, dest, fly, range, _pendingCts.Token);
        _pendingFly = fly;
        _pendingDestRange = range;
        return true;
    }
}
