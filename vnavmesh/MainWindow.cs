using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private FollowPath _path;
    private NavmeshManager _manager;
    private AsyncMoveRequest _move;
    private DTRProvider _dtr;
    // 契約：下面五個只在繪製執行緒建立／使用，同生同滅；null＝還沒需要過，或建立失敗（_debugFailed）。
    private DebugDrawer? _dd;
    private DebugGameCollision? _debugGameColl;
    private DebugNavmeshManager? _debugNavmeshManager;
    private DebugNavmeshCustom? _debugNavmeshCustom;
    private DebugLayout? _debugLayout;
    private bool _debugFailed;
    private CustomLinksUI _customLinks;
    private string _configDirectory;

    public MainWindow(NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr, string configDir) : base("Navmesh".Loc())
    {
        _path = path;
        _manager = manager;
        _move = move;
        _dtr = dtr;
        _configDirectory = configDir;
        _customLinks = new(manager);
    }

    public void Dispose()
    {
        DisposeDebug();
    }

    private void DisposeDebug()
    {
        _debugLayout?.Dispose();
        _debugNavmeshCustom?.Dispose();
        _debugNavmeshManager?.Dispose();
        _debugGameColl?.Dispose();
        _dd?.Dispose();
        _debugLayout = null;
        _debugNavmeshCustom = null;
        _debugNavmeshManager = null;
        _debugGameColl = null;
        _dd = null;
    }

    // 除錯繪製（D3D 裝置＋鏡頭）只服務除錯分頁與路徑點：延到第一次需要才建，失敗只停用這些、不讓插件載入失敗。
    // 刻意整個 session 不重試：繪製時遊戲的裝置必定已建好，失敗多半是位址對不上，重試結果相同。
    private bool EnsureDebug()
    {
        if (_dd != null)
            return true;
        if (_debugFailed || (!IsOpen && !Service.Config.ShowWaypoints))
            return false;
        try
        {
            _dd = new();
            _debugGameColl = new(_dd);
            _debugNavmeshManager = new(_dd, _debugGameColl, _manager, _path, _move, _dtr);
            _debugNavmeshCustom = new(_dd, _debugGameColl, _manager, _configDirectory);
            _debugLayout = new(_dd, _debugGameColl);
            return true;
        }
        catch (Exception ex)
        {
            _debugFailed = true;
            DisposeDebug();
            FeatureHealth.Report("debug.renderer",
                "Waypoints, debug overlay and debug tabs are off (graphics device not available).",
                $"[MainWindow] 除錯繪製初始化失敗，只停用路徑點、除錯疊加與除錯分頁（導航與 IPC 不受影響）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void StartFrame()
    {
        if (EnsureDebug())
            _dd!.StartFrame(IsOpen);
    }

    // 清除網格快取：兩段式（按鈕→確認／取消），只在使用者按下「刪除」時才動檔案。
    // 刪檔在執行緒池跑（快取可能上百個檔），結果字串用參考指派交回繪製執行緒。
    private bool _clearCacheConfirm;
    private (int Files, long Bytes) _clearCacheCount;
    private Task? _clearCacheTask;
    private string? _clearCacheResult;

    private void DrawMeshCacheSection()
    {
        ImGui.Separator();
        var busy = _clearCacheTask is { IsCompleted: false };
        if (busy)
        {
            ImGui.TextDisabled("Clearing navmesh cache...".Loc());
        }
        else if (!_clearCacheConfirm)
        {
            if (ImGui.Button("Clear navmesh cache...".Loc()))
            {
                _clearCacheCount = _manager.CountMeshCacheFiles();
                _clearCacheConfirm = true;
                Volatile.Write(ref _clearCacheResult, null);
            }
            ImGuiComponents.HelpMarker("Deletes the navmesh files vnavmesh has cached for each zone (only .navmesh files in its own cache folder; nothing else is touched). The navmesh that is loaded now keeps working. Every zone is built again from scratch the next time it is loaded, which can take from a few seconds to a few minutes. Useful when old cache files left behind by updates take up disk space.".Loc());
        }
        else
        {
            ImGui.TextUnformatted("Delete ?? cached navmesh files (?? MB)? Every zone will be built again the next time it is loaded.".Loc(_clearCacheCount.Files, $"{_clearCacheCount.Bytes / 1048576.0:f1}"));
            if (ImGui.Button("Delete".Loc()))
            {
                _clearCacheConfirm = false;
                _clearCacheTask = Task.Run(ClearMeshCache);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel".Loc()))
                _clearCacheConfirm = false;
        }
        var result = Volatile.Read(ref _clearCacheResult);
        if (result != null)
            ImGui.TextDisabled(result);
    }

    // 執行緒池；整段吞例外（結果只是一行文字），不呼叫別的插件。
    private void ClearMeshCache()
    {
        try
        {
            var (deleted, failed, bytes) = _manager.DeleteMeshCacheFiles();
            var text = "Deleted ?? cache files (?? MB).".Loc(deleted, $"{bytes / 1048576.0:f1}");
            if (failed > 0)
                text += " " + "?? files are in use and were left as they are.".Loc(failed);
            Volatile.Write(ref _clearCacheResult, text);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _clearCacheResult, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly Vector4 ColorDisabledSevere = new(1f, 0.35f, 0.35f, 1);
    private static readonly Vector4 ColorDisabled = new(1f, 0.8f, 0.2f, 1);

    // 改版後被停用的功能（見 FeatureHealth）；導航網格完全建不了的那一項用紅字。
    private static void DrawDisabledFeatures()
    {
        var entries = FeatureHealth.Entries;
        if (entries.Count == 0)
            return;
        for (var i = 0; i < entries.Count; ++i)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, entries[i].Severe ? ColorDisabledSevere : ColorDisabled);
            ImGui.TextWrapped(entries[i].UiText);
        }
        ImGui.Separator();
    }

    public void EndFrame()
    {
        if (_dd == null)
            return;
        _debugGameColl?.DrawVisualizers();
        if (Service.Config.ShowWaypoints)
        {
            var player = Service.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var from = player.Position;
                var color = 0xff00ff00;
                // 📌 這裡走訪路徑點**不是**跨執行緒走訪：UiBuilder.Draw 與 Framework.Update 跑在同一條遊戲主執行緒上。
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
        DrawDisabledFeatures();
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Config".Loc()))
                {
                    if (tab)
                    {
                        Service.Config.Draw();
                        DrawMeshCacheSection();
                    }
                }
                using (var tab = ImRaii.TabItem("Custom links".Loc()))
                    if (tab)
                        _customLinks.Draw();
                if (_debugLayout != null)
                    using (var tab = ImRaii.TabItem("Layout".Loc()))
                        if (tab)
                            _debugLayout.Draw();
                // 「Collision」分頁刻意移除。DebugGameCollision.Draw() 底下的
                // DrawSceneColliders / DrawSceneQuadtree / DrawSceneRaycasts 會逐層走
                // FFXIVClientStructs 的裸指標(Scene->Colliders、Quadtree->NodesAtLevel、
                // SceneWrapper->Raycast),那些層沒有、也沒辦法有有效性保證。
                // 🔴 包 try/catch 不算防護:AccessViolationException 在 .NET Core 是 corrupted-state exception,攔不到 ⇒ 正解是把入口拿掉。
                if (_debugNavmeshManager != null)
                    using (var tab = ImRaii.TabItem("Navmesh manager".Loc()))
                        if (tab)
                            _debugNavmeshManager.Draw();
                if (_debugNavmeshCustom != null)
                    using (var tab = ImRaii.TabItem("Navmesh custom".Loc()))
                        if (tab)
                            _debugNavmeshCustom.Draw();
            }
        }
    }
}
