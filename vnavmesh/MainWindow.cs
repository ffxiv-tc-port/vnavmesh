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
                foreach (var wp in _path.Waypoints)
                {
                    var to = wp.Position;
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
                // ⚠️ 連帶影響:設定裡的「Always visualize game collision」
                //    (Service.Config.ForceShowGameCollision,也可用指令切換)只在
                //    DrawSceneColliders 裡生效,入口沒了之後它不再有作用。刻意不一併移除
                //    (沒有指示要動設定),但它現在是個不會有反應的開關。
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
