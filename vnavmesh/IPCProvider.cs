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
        RegisterFunc("Path.IsRunning", () => followPath.Waypoints.Count > 0);
        RegisterFunc("Path.NumWaypoints", () => followPath.Waypoints.Count);
        // 🔴 對外一律是 List<Vector3>。FollowPath.Waypoints 內部已改成 List<Waypoint>,
        //    直接回傳會**靜默改變 IPC 型別**,全艦隊消費端(AutoDuty/BOCCHI/Lifestream/…)一起壞。
        RegisterFunc("Path.ListWaypoints", () => followPath.Waypoints.Select(w => w.Position).ToList());
        RegisterFunc("Path.GetMovementAllowed", () => followPath.MovementAllowed);
        RegisterAction("Path.SetMovementAllowed", (bool v) => followPath.MovementAllowed = v);
        RegisterFunc("Path.GetAlignCamera", () => Service.Config.AlignCameraToMovement);
        RegisterAction("Path.SetAlignCamera", (bool v) => Service.Config.SetAlignCameraToMovementFromIPC(v));
        RegisterFunc("Path.GetTolerance", () => followPath.Tolerance);
        RegisterAction("Path.SetTolerance", (float v) => followPath.Tolerance = v);

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
