using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private FollowPath _path;
    private DebugDrawer _dd = new();
    private DebugGameCollision _debugGameColl;
    private DebugNavmeshManager _debugNavmeshManager;
    private DebugNavmeshCustom _debugNavmeshCustom;
    private DebugLayout _debugLayout;
    private CustomLinksUI _customLinks;
    private string _configDirectory;

    public MainWindow(NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr, string configDir) : base("Navmesh".Loc())
    {
        _path = path;
        _configDirectory = configDir;
        _debugGameColl = new(_dd);
        _debugNavmeshManager = new(_dd, _debugGameColl, manager, path, move, dtr);
        _debugNavmeshCustom = new(_dd, _debugGameColl, manager, _configDirectory);
        _debugLayout = new(_dd, _debugGameColl);
        _customLinks = new(manager);
    }

    public void Dispose()
    {
        _debugLayout.Dispose();
        _debugNavmeshCustom.Dispose();
        _debugNavmeshManager.Dispose();
        _debugGameColl.Dispose();
        _dd.Dispose();
    }

    public void StartFrame()
    {
        _dd.StartFrame();
    }

    public void EndFrame()
    {
        _debugGameColl.DrawVisualizers();
        if (Service.Config.ShowWaypoints)
        {
            var player = Service.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var from = player.Position;
                var color = 0xff00ff00;
                // 📌 這裡走訪路徑點**不是**跨執行緒走訪：UiBuilder.Draw 與 Framework.Update 跑在
                //    同一條遊戲主執行緒上（Dalamud 只在 Framework.HandleFrameworkUpdate 裡呼叫
                //    ThreadSafety.MarkMainThread()，而它標的是 [ThreadStatic] 旗標；上面那行
                //    Service.ObjectTable.LocalPlayer 內部會 AssertMainThread，若 Draw 不是主執行緒
                //    使用者的 log 每一次開圖都會出現一行 [ThreadSafety] vnavmesh 警告 —— 實機 log
                //    從來沒有過）。⇒ 它與 Update 不並行，本來就不需要為了執行緒安全而改。
                //    現在 _path.Waypoints 回的是不可變快照，順帶連「同一幀中途被 IPC 的
                //    Path.MoveTo 換掉」也不再可能畫出半舊半新的線段。
                // 🔑 刻意用索引而不是 foreach：宣告型別是 IReadOnlyList<T>，foreach 會走介面的
                //    GetEnumerator ⇒ **每幀配一個列舉器物件**。這一段在開著「顯示目前的路徑點」時
                //    每幀都會跑，索引式走訪是零配置。
                var waypoints = _path.Waypoints;
                for (var i = 0; i < waypoints.Count; ++i)
                {
                    var to = waypoints[i].Position;
                    _dd.DrawWorldLine(from, to, color);
                    _dd.DrawWorldPointFilled(to, 3, 0xff0000ff);
                    from = to;
                    color = 0xff00ffff;
                }
            }
        }
        _dd.EndFrame();
    }

    public override void Draw()
    {
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Config".Loc()))
                    if (tab)
                        Service.Config.Draw();
                using (var tab = ImRaii.TabItem("Custom links".Loc()))
                    if (tab)
                        _customLinks.Draw();
                using (var tab = ImRaii.TabItem("Layout".Loc()))
                    if (tab)
                        _debugLayout.Draw();
                // 「Collision」分頁刻意移除。DebugGameCollision.Draw() 底下的
                // DrawSceneColliders / DrawSceneQuadtree / DrawSceneRaycasts 會逐層走
                // FFXIVClientStructs 的裸指標(Scene->Colliders、Quadtree->NodesAtLevel、
                // SceneWrapper->Raycast),那些層沒有、也沒辦法有有效性保證。
                // 🔴 包 try/catch 不算防護:AccessViolationException 在 .NET Core 是
                //    corrupted-state exception,攔不到 ⇒ 正解是把入口拿掉。
                // 📌 _debugGameColl 本身**保留**:DebugNavmeshManager / DebugNavmeshCustom /
                //    DebugLayout 三個分頁都吃它當相依,EndFrame() 也要呼叫它的
                //    DrawVisualizers()(那支只碰受管的算繪狀態)。這裡拿掉的只有這個入口。
                // 📌 連帶清理:設定裡的「Always visualize game collision」開關,以及用來切換它的
                //    /vnav collider 指令(欄位 Service.Config.ForceShowGameCollision),已一併移除。
                //    它只在 DrawSceneColliders 裡生效,入口拿掉之後按了不會有任何反應。
                //    既有使用者設定檔裡殘留的那個鍵無害:Config.Load 是逐鍵查 GetField,
                //    找不到同名欄位就略過。
                // 想法來源:okaminico/ffxiv_navmesh@38da2512。
                using (var tab = ImRaii.TabItem("Navmesh manager".Loc()))
                    if (tab)
                        _debugNavmeshManager.Draw();
                using (var tab = ImRaii.TabItem("Navmesh custom".Loc()))
                    if (tab)
                        _debugNavmeshCustom.Draw();
            }
        }
    }
}
