using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Navmesh;

class IPCProvider : IDisposable
{
    private List<Action> _disposeActions = new();

    public IPCProvider(NavmeshManager navmeshManager, FollowPath followPath, AsyncMoveRequest move, MainWindow mainWindow, DTRProvider dtr)
    {
        RegisterFunc("Nav.IsReady", () => navmeshManager.Navmesh != null);
        RegisterFunc("Nav.BuildProgress", () => navmeshManager.LoadTaskProgress);
        RegisterFunc("Nav.Reload", () => navmeshManager.Reload(true));
        // 🔴 刻意不是 Reload(false)：外掛端的重建幾乎都是「偵測到卡住就重建」的形狀，
        //    而全量重建期間玩家本來就動不了 ⇒ 卡住判定不會解除 ⇒ 下一 tick 又要求重建，
        //    形成自我維持迴圈（AutoDuty 實機 log 連打過 128 次）。RebuildFromIPC 帶最小
        //    間隔節流並印 Information 級說明。使用者手動觸發的重建走 Reload(false)，不受影響。
        RegisterFunc("Nav.Rebuild", () => navmeshManager.RebuildFromIPC());
        RegisterFunc("Nav.Pathfind", (Vector3 from, Vector3 to, bool fly) => navmeshManager.QueryPathBasic(from, to, fly));
        RegisterFunc("Nav.PathfindWithTolerance", (Vector3 from, Vector3 to, bool fly, float range) => navmeshManager.QueryPathBasic(from, to, fly, range));
        RegisterFunc("Nav.PathfindAvoid", (Vector3 from, Vector3 to, bool fly, Vector3 avoidCenter, float avoidRadius) => navmeshManager.QueryPathBasic(from, to, fly, avoidCenter: avoidCenter, avoidRadius: avoidRadius));
        RegisterFunc("Nav.PathfindCancelable", (Vector3 from, Vector3 to, bool fly, CancellationToken cancel) => navmeshManager.QueryPathBasic(from, to, fly, externalCancel: cancel));
        // 🔑 只取消尋路，**不動導航網格**。
        //    舊實作是 navmeshManager.Reload(true)：名字叫「取消全部尋路」，做的卻是把整張網格
        //    卸掉再從快取載回來（ClearState 把 Navmesh/Query 清成 null，順便取消綁在 CTS 上的
        //    尋路工作）。取消的效果有達到，但代價是重新載入期間 Nav.IsReady 會短暫回 false、
        //    Nav.Pathfind 會擲例外 —— 而呼叫端幾乎清一色是「取消 → 立刻重新規劃路徑」，
        //    等於每次取消都害對方的第一次重試白跑一趟。
        //    改成 CancelAllPathfinds() 之後 Nav.IsReady 全程維持 true。
        // 🔴 這條路徑上**不可以加節流** —— 對「取消」加節流會讓取消靜默地不發生，比現況更糟。
        //    帶節流的是 Nav.Rebuild（RebuildFromIPC，全量重建），兩者不要混。
        RegisterAction("Nav.PathfindCancelAll", navmeshManager.CancelAllPathfinds);
        RegisterFunc("Nav.PathfindInProgress", () => navmeshManager.PathfindInProgress);
        RegisterFunc("Nav.PathfindNumQueued", () => navmeshManager.NumQueuedPathfindRequests);
        RegisterFunc("Nav.IsAutoLoad", () => Service.Config.AutoLoadNavmesh);
        // 🔴 SetXxxFromIPC 只改執行期的值，**不寫進使用者的設定檔**（見 Config._ipcOverrides）。
        //    舊實作直接寫 Service.Config 再 NotifyModified() ⇒ 別的外掛改一次就永久改掉
        //    使用者的設定，而全艦隊的呼叫端沒有一個會還原。
        RegisterAction("Nav.SetAutoLoad", (bool v) => Service.Config.SetAutoLoadNavmeshFromIPC(v));
        RegisterFunc("Nav.BuildBitmap", (Vector3 startingPos, string filename, float pixelSize) => navmeshManager.BuildBitmap(startingPos, filename, pixelSize));
        RegisterFunc("Nav.BuildBitmapBounded", (Vector3 startingPos, string filename, float pixelSize, Vector3 minBounds, Vector3 maxBounds) => navmeshManager.BuildBitmap(startingPos, filename, pixelSize, new AABB { Min = minBounds, Max = maxBounds }));

        // 🔴🔴 下面三支跑在**呼叫端的執行緒**上，而網格是由框架執行緒換掉的。逐項查證的結果：
        //  ① **不會拿到半個物件**：NavmeshManager.Current 一次取得「同一代」的 Navmesh + Query
        //     （不可變的 MeshGeneration，一次原子的參考讀取），`?.` 與 `is { } q` 也都只讀一次。
        //  ② **重建期間回不可用值**：ClearState 把那一代換成 (null, null) ⇒ 這三支各自回 null，
        //     也就是它們本來就在回的「查不到」。呼叫端一行都不必改。
        //  ③ **不會和尋路搶同一個 DtNavMeshQuery 的可變狀態**：逐行讀過 DotRecast 之後確認
        //     FindNearestPoly / QueryPolygons / ClosestPointOnPoly 這條路徑只讀 m_nav 與堆疊區域變數，
        //     **完全不碰 m_nodePool / m_tinyNodePool / m_openList**（那三個只有 A* 那條路徑會碰）。
        //     所以「查一個點」與「算一條路」並行是安全的；反過來說，**日後若有人把會碰節點池的
        //     函式（FindPath / FindStraightPath / Raycast…）接成 IPC 端點，這個結論就不成立了**。
        //  ④ 殘留的只剩「答案來自剛剛換掉的那一代」——那是切區域瞬間的事，而且值本身是自洽的
        //     （整組都來自同一張網格），不是半舊半新的座標。
        RegisterFunc("Query.Mesh.NearestPoint", (Vector3 p, float halfExtentXZ, float halfExtentY) => navmeshManager.Query?.FindNearestPointOnMesh(p, halfExtentXZ, halfExtentY));
        // 🔴🔴 第 2 個參數(allowUnlandable)**刻意不接進去**,維持「被忽略」。
        //    上游把它接成 FindPointOnFloor 的 allowUnreachable,而那個旗標只有 FloodFill/Prune
        //    (方案 D 的 D3,我方未取)會設。現在接上去是 no-op;但 D3 一旦落地就會**突然開始生效**,
        //    而全艦隊有 7 個 repo 在這個參數傳 false —— 屆時它們會拿到 null 並靜默拒絕出發:
        //      Saucy(IPC 包裝的預設值就是 false)、BOCCHI(PathfindAndMoveToChain)、
        //      AutoDuty(MapHelper)、GatherBuddyReborn(AutoGather.Movement)、visland(GatherRouteExec ×2)、
        //      TCToolbox(FlagCommands)、BossmodReborn(DeepDungeonNav)。Questionable 傳 true,不受影響。
        //    ⇒ 要接這個參數,必須與 D3 同時裁決,並且先把上面那些呼叫點一起處理。
        RegisterFunc("Query.Mesh.PointOnFloor", (Vector3 p, bool allowUnlandable, float halfExtentXZ) => navmeshManager.Query?.FindPointOnFloor(p, halfExtentXZ));
        RegisterFunc("Query.Mesh.FlagToPoint", () => navmeshManager.Query is { } q ? MapUtils.FlagToPoint(q) : null);

        RegisterAction("Path.MoveTo", (List<Vector3> waypoints, bool fly) => followPath.Move(waypoints, !fly));
        RegisterAction("Path.Stop", followPath.Stop);
        // 🔴🔴 這三支跑在**呼叫端的執行緒**上，而框架執行緒每幀在 FollowPath.Update 裡消耗路徑點。
        //    FollowPath 的路徑點已經是**不可變快照**（見 FollowPath.PathSnapshot）：消耗一個點＝
        //    換一個新的快照上去，從來不就地改動已經發佈出去的那一份 ⇒ 這裡讀到的永遠是完整一致
        //    的一份，不會撕裂、也不會在走訪途中擲 InvalidOperationException(集合已變更)。
        // ⚠️ 讀到的那一份最多落後一幀；Path.MoveTo / Path.Stop 是同一個參考指派，所以
        //    「MoveTo 之後立刻問 IsRunning」的既有形狀答案不變。
        RegisterFunc("Path.IsRunning", () => followPath.WaypointCount > 0);
        RegisterFunc("Path.NumWaypoints", () => followPath.WaypointCount);
        // 🔴 對外一律是 List<Vector3>。FollowPath 內部的路徑點是 Waypoint(座標 + 連結種類),
        //    直接回傳會**靜默改變 IPC 型別**,全艦隊消費端(AutoDuty/BOCCHI/Lifestream/…)一起壞。
        RegisterFunc("Path.ListWaypoints", followPath.WaypointPositions);
        // 🔑 Get 回的是**實際生效**的值（租約值 ?? 使用者的值），不是使用者那一格欄位。
        //    型別沒變（bool / float），而且目前一把租約都沒有時兩者恆等 ⇒ 出貨當下行為零改變。
        //    刻意這樣做的理由：會說謊的 getter 正是「路徑照算、角色不動、log 零字」那個
        //    靜默失效的來源 —— 呼叫端問的是「vnavmesh 現在會不會動我」，就該回答那件事。
        RegisterFunc("Path.GetMovementAllowed", () => followPath.EffectiveMovementAllowed);
        RegisterAction("Path.SetMovementAllowed", (bool v) => followPath.MovementAllowed = v);
        RegisterFunc("Path.GetAlignCamera", () => Service.Config.AlignCameraToMovement);
        RegisterAction("Path.SetAlignCamera", (bool v) => Service.Config.SetAlignCameraToMovementFromIPC(v));
        RegisterFunc("Path.GetTolerance", () => followPath.EffectiveTolerance);
        RegisterAction("Path.SetTolerance", (float v) => followPath.Tolerance = v);

        // -- 移動租約（見 Movement/MovementLeases.cs）--------------------------------
        // 🔴 這一組是**純新增**：上面的 Path.SetMovementAllowed / Path.SetTolerance 一個字都沒改，
        //    舊消費端完全不受影響。要改端點的形狀時正解是「換新名字」而不是「同名改型別」——
        //    端點不存在時兩邊都攔得住 IpcNotReadyError 並乾淨落回 fail-safe，
        //    而同名改型別會讓舊消費端撞上 IpcTypeMismatchError（SafeWrapper.IPCException 攔不住它）。
        // 🔑 用法：Acquire 拿一把 Guid 憑證 → SetLeasedMovementAllowed(憑證, false) 壓住 →
        //    每 30 秒 RenewSuppression(憑證) 心跳 → 做完 ReleaseSuppression(憑證)。
        //    ⚠️ 租期上限 5 分鐘，續約間隔必須明顯短於租期（建議 30 秒＝十分之一）；
        //       間隔接近租期時第一次心跳**必定**回 false（那把已經被掃掉了，不是競態）。
        // 🔑 <b>不用記得還</b>：租用者當掉／被卸載／忘了放開，逾時就自動還原成使用者的值，
        //    並在使用者的 log 寫一行 Information 指名是誰。
        // 📌 全部端點回的都是不可為 null 的值型別，失敗回 Guid.Empty / false，**永不回 null**
        //    （回 null 時 CallGateChannel 對值型別擲的是看起來與 IPC 無關的 NullReferenceException）。
        RegisterFunc("Path.AcquireSuppression", (string owner) => MovementLeases.Acquire(owner, MovementLeases.DefaultLeaseMilliseconds));
        RegisterFunc("Path.AcquireSuppressionFor", (string owner, int milliseconds) => MovementLeases.Acquire(owner, milliseconds));
        RegisterFunc("Path.ReleaseSuppression", (Guid lease) => MovementLeases.Release(lease));
        RegisterFunc("Path.RenewSuppression", (Guid lease) => MovementLeases.Renew(lease));
        RegisterFunc("Path.RenewSuppressionFor", (Guid lease, int milliseconds) => MovementLeases.Renew(lease, milliseconds));
        // 🔴 傳 true 的語意是「我這把不再要求別動」，**不是**「我要求放行」——
        //    使用者自己在「Navmesh manager」分頁取消勾選的「Allow movement」不會被 IPC 蓋掉。
        RegisterFunc("Path.SetLeasedMovementAllowed", (Guid lease, bool allowed) => MovementLeases.SetMovementAllowed(lease, allowed));
        RegisterFunc("Path.SetLeasedTolerance", (Guid lease, float tolerance) => MovementLeases.SetTolerance(lease, tolerance));

        RegisterFunc("SimpleMove.PathfindAndMoveTo", (Vector3 dest, bool fly) => move.MoveTo(dest, fly));
        RegisterFunc("SimpleMove.PathfindAndMoveCloseTo", (Vector3 dest, bool fly, float range) => move.MoveTo(dest, fly, range));
        RegisterFunc("SimpleMove.PathfindInProgress", () => move.TaskInProgress);

        RegisterFunc("Window.IsOpen", () => mainWindow.IsOpen);
        RegisterAction("Window.SetOpen", (bool v) => mainWindow.IsOpen = v);

        RegisterFunc("DTR.IsShown", () => Service.Config.EnableDTR);
        RegisterAction("DTR.SetShown", (bool v) => Service.Config.SetEnableDTRFromIPC(v));
    }

    public void Dispose()
    {
        foreach (var a in _disposeActions)
            a();
        // 端點都拆掉了，留著的租約沒有任何人能再放開它 —— 一起丟掉並寫一行 log。
        MovementLeases.ReleaseAll("vnavmesh 正在卸載");
    }

    private void RegisterFunc<TRet>(string name, Func<TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1>(string name, Func<T1, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2>(string name, Func<T1, T2, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3>(string name, Func<T1, T2, T3, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4>(string name, Func<T1, T2, T3, T4, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4, T5>(string name, Func<T1, T2, T3, T4, T5, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, T5, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterAction(string name, Action func)
    {
        var p = Service.PluginInterface.GetIpcProvider<object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1>(string name, Action<T1> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1, T2>(string name, Action<T1, T2> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }
}
