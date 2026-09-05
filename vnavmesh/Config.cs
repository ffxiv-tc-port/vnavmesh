using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace Navmesh;

public class Config
{
    private const int _version = 1;

    public bool AutoLoadNavmesh = true;
    public bool EnableDTR = true;
    public bool ShowQueryStatusInDTR = true;
    public bool AlignCameraToMovement;
    public float AlignCameraHeight = -15;
    public bool ShowWaypoints;
    public bool CancelMoveOnUserInput;
    public bool StopOnStuck = false;
    public float StuckTolerance = 0.05f;
    public int StuckTimeoutMs = 500;
    public bool RetryOnStuck = true;
    public float RandomnessMultiplier = 1f;
    public int BuildMaxCores = 1;

    // 使用者停用的自訂捷徑（鍵＝territory + 兩端座標，見 CustomLinkTracker.MakeKey）；
    // 預設全開（空集合）。⚠️ 執行緒約定：網格建置（背景執行緒）只讀取這個欄位的參考；
    // UI 修改時必須整組替換成新的 HashSet（copy-on-write，見 CustomLinksUI.SetLinkEnabled），
    // 不可就地 Add/Remove，否則與背景讀取並行會壞。
    public HashSet<string> DisabledCustomLinks = [];

    // 曾在建置中觀察到的自訂捷徑目錄（含上次建置的預檢結果），僅供「自訂捷徑」分頁
    // 顯示；只在主執行緒讀寫（CustomLinksUI.Draw 併入 CustomLinkTracker 的新結果時更新）。
    public List<CustomLinkRecord> CustomLinkCatalog = [];

    // 依宇宙探索全服建設階段（CosmicProgress.DevGrade）自動略過尚未開通的自訂捷徑，
    // 使用者不必在「自訂捷徑」分頁一條一條手動取消勾選。關閉後只剩端點預檢把關
    // （見 NavmeshCustomization.LinkPoints／TryResolveLinkEndpoint）。
    public bool GateCustomLinksByDevGrade = true;

    private static readonly int realMaxCores = Environment.ProcessorCount;

    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    // -- IPC 覆寫層 ------------------------------------------------------------
    // 🔴 經由 IPC 進來的設定變更**只改執行期的值，不寫進設定檔**。
    //    別的外掛幾乎都是「導航前把某個開關扳到自己要的位置」的形狀，而且**不還原**：
    //      ChilledLeves 每次移動前 SetAlignCamera(false)，從不還原；
    //      AutoDuty 開始導航時 SetAlignCamera(true)，而它的還原路徑要靠
    //      SettingsActive.Vnav_Align_Camera_Off 這個旗標，設旗標的那段目前是註解掉的
    //      ⇒ 兩邊都是單向。使用者的設定被誰改到就永久停在那裡，全程零訊息。
    //    （社群甚至長出一支每幀把它壓回去的 Splatoon 腳本 VnavmeshAlignCameraUnsetter，
    //      那就是「沒有主人的全域開關」會長成的樣子。）
    // 🔑 做法：IPC 改的仍然是欄位本身，所以**所有讀取端一行都不必動**；同時把「使用者
    //    自己設定的值」記進 _ipcOverrides，Save() 存檔時把這些欄位換回使用者的值。
    //    使用者自己在 UI 或 /vnav 指令改同一個設定時，覆寫被清掉，他的值重新成為權威。
    // ⚠️ 這個字典是 private，Newtonsoft 預設只序列化 public 成員 ⇒ 不會進設定檔；
    //    仍然加上 JsonIgnore 當第二道保險。
    // 🔴🔴 這張表被三種執行緒碰：SetFromIPC 走 <b>IPC 端點＝呼叫端的執行緒</b>（沒有任何
    //    「一定在 Framework 執行緒」的保證）、DrawIPCOverrideMarker 每幀從繪製執行緒讀、
    //    Save() 從 Framework 執行緒 foreach 走訪。裸 Dictionary 在這個形狀下的失敗不是
    //    「拿到舊值」而是<b>字典本身壞掉</b>，而且並行改動時 foreach 會擲 InvalidOperationException。
    //    （與 ECommons EzThrottler 那條紅線完全同形狀。）
    // 🔑 一律用 _ipcGate 保護；<b>鎖內絕不呼叫 ImGui、絕不做檔案 I/O</b> —— Save() 只在鎖內拍快照。
    [Newtonsoft.Json.JsonIgnore] private readonly Dictionary<string, bool> _ipcOverrides = [];
    [Newtonsoft.Json.JsonIgnore] private readonly object _ipcGate = new();

    public void SetAutoLoadNavmeshFromIPC(bool v) => SetFromIPC(ref AutoLoadNavmesh, v, nameof(AutoLoadNavmesh));
    public void SetEnableDTRFromIPC(bool v) => SetFromIPC(ref EnableDTR, v, nameof(EnableDTR));
    public void SetAlignCameraToMovementFromIPC(bool v) => SetFromIPC(ref AlignCameraToMovement, v, nameof(AlignCameraToMovement));

    private void SetFromIPC(ref bool field, bool value, string name)
    {
        // 只在第一次覆寫時記下使用者的值；之後 IPC 再怎麼改都不影響存檔內容。
        // 🔴 ContainsKey ＋ 索引指派是兩步，中間被插入就會把 IPC 的值當成「使用者的值」記下去。
        lock (_ipcGate)
        {
            if (!_ipcOverrides.ContainsKey(name))
                _ipcOverrides[name] = field;
        }
        field = value;
        // 🔴 刻意不呼叫 NotifyModified() —— 呼叫它就等於把 IPC 的值寫進設定檔。
    }

    // 使用者自己動了這個設定 ⇒ 他的選擇重新成為權威，丟掉 IPC 覆寫。
    public void ClearIPCOverride(string name)
    {
        lock (_ipcGate)
            _ipcOverrides.Remove(name);
    }

    // 有 IPC 覆寫在生效時，在該列右邊放一個灰字標記 ——「另一個外掛正在暫時改這個設定」
    // 本身要在列上看得見；tooltip 藏的是「為什麼」，不是「有沒有」。
    private void DrawIPCOverrideMarker(string name)
    {
        // 🔑 只有取值這一步進鎖；下面的 ImGui 呼叫全部在鎖外（鎖內呼叫外部程式碼會擴大死鎖面）。
        bool saved;
        bool has;
        lock (_ipcGate)
            has = _ipcOverrides.TryGetValue(name, out saved);
        if (!has)
            return;
        ImGui.SameLine();
        ImGui.TextDisabled("(IPC)");
        if (!ImGui.IsItemHovered())
            return;
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted("Another plugin changed this setting through IPC.".Loc());
        ImGui.TextUnformatted("The change only applies to the current session and is not written to your config file.".Loc());
        ImGui.TextUnformatted("Your saved value: ??".Loc(saved ? "ON" : "OFF"));
        ImGui.TextUnformatted("Toggle it here to make your own choice authoritative again.".Loc());
    }

    public void Draw()
    {
        if (ImGui.Checkbox("Automatically load/build navigation data when changing zones".Loc(), ref AutoLoadNavmesh))
        {
            ClearIPCOverride(nameof(AutoLoadNavmesh));
            NotifyModified();
        }
        DrawIPCOverrideMarker(nameof(AutoLoadNavmesh));
        if (ImGui.Checkbox("Enable DTR bar".Loc(), ref EnableDTR))
        {
            ClearIPCOverride(nameof(EnableDTR));
            NotifyModified();
        }
        DrawIPCOverrideMarker(nameof(EnableDTR));
        if (ImGui.Checkbox("Show detailed query status in DTR".Loc(), ref ShowQueryStatusInDTR))
            NotifyModified();
        if (ImGui.Checkbox("Align camera to movement direction".Loc(), ref AlignCameraToMovement))
        {
            ClearIPCOverride(nameof(AlignCameraToMovement));
            NotifyModified();
        }
        DrawIPCOverrideMarker(nameof(AlignCameraToMovement));
        using (ImRaii.Disabled(!AlignCameraToMovement))
        {
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Camera height (degrees)".Loc(), ref AlignCameraHeight, -75, 75);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
        }
        if (ImGui.Checkbox("Show active waypoints".Loc(), ref ShowWaypoints))
            NotifyModified();
        if (ImGui.Checkbox("Cancel current path on player movement input".Loc(), ref CancelMoveOnUserInput))
            NotifyModified();
        if (ImGui.Checkbox("Stop pathing when stuck".Loc(), ref StopOnStuck))
            NotifyModified();

        ImGui.SetNextItemWidth(200);
        ImGui.SliderInt("Max cores used during mesh build".Loc(), ref BuildMaxCores, -8, realMaxCores);
        if (ImGui.IsItemDeactivatedAfterEdit())
            NotifyModified();
        ImGuiComponents.HelpMarker("0 = use all available; positive number = use that many cores; negative number = leave that many cores idle".Loc());

        if (StopOnStuck)
        {
            ImGui.SliderFloat("Stuck tolerance (yalms/second)".Loc(), ref StuckTolerance, 0.5f, 3f);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The minimum distance the object must move each frame to avoid being considered stuck.".Loc());

            ImGui.SliderInt("Stuck timeout (ms)".Loc(), ref StuckTimeoutMs, 100, 10_000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How long you can remain under the stuck threshold before stopping.".Loc());

            if (ImGui.Checkbox("Retry pathing after stop".Loc(), ref RetryOnStuck))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("If enabled, the agent will attempt to re-path after being considered stuck.".Loc());
        }

        ImGui.SetNextItemWidth(200);
        ImGui.SliderFloat("Randomness Multiplier".Loc(), ref RandomnessMultiplier, 0f, 1.0f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            NotifyModified();
    }

    public void Save(FileInfo file)
    {
        try
        {
            var payload = JObject.FromObject(this);
            // 把被 IPC 暫時覆寫的欄位換回使用者自己設定的值 —— 存檔內容永遠是使用者的選擇。
            // 🔴 鎖內只拍快照：直接 foreach 這張表，被 IPC 端點並行改動時會擲 InvalidOperationException，
            //    而那個例外會被下面的 catch 吞成「存檔失敗」——使用者的設定就這樣沒存到。
            KeyValuePair<string, bool>[] snapshot;
            lock (_ipcGate)
                snapshot = [.. _ipcOverrides];
            foreach (var (name, userValue) in snapshot)
                payload[name] = userValue;
            JObject jContents = new()
            {
                { "Version", _version },
                { "Payload", payload }
            };
            File.WriteAllText(file.FullName, jContents.ToString());
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to save config to {file.FullName}: {e}");
        }
    }

    public void Load(FileInfo file)
    {
        // 載入設定檔＝重新確立「使用者的值」，任何殘留的 IPC 覆寫都作廢。
        lock (_ipcGate)
            _ipcOverrides.Clear();
        try
        {
            var contents = File.ReadAllText(file.FullName);
            var json = JObject.Parse(contents);
            var version = (int?)json["Version"] ?? 0;
            if (json["Payload"] is JObject payload)
            {
                payload = ConvertConfig(payload, version);
                var thisType = GetType();
                foreach (var (f, data) in payload)
                {
                    var thisField = thisType.GetField(f);
                    if (thisField != null)
                    {
                        var value = data?.ToObject(thisField.FieldType);
                        if (value != null)
                        {
                            thisField.SetValue(this, value);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to load config from {file.FullName}: {e}");
        }
    }

    private static JObject ConvertConfig(JObject payload, int version)
    {
        return payload;
    }
}
