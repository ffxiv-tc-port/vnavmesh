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
    public bool ShortenPaths = true;
    public bool FastFlyPaths = true;
    // 飛行搜尋調校（NavmeshQuery.FlyPathTuning）：開關關著時下面三個值全部不讀。
    public bool FlyPathTuning;
    public float FlyHeuristicWeight = 1.5f;
    public float FlyRaycastRange = 25;
    public int FlyMaxSteps = 200000;
    public bool FlyMeshFallback;
    public bool FlyStringPull = true;
    // 高空路線（NavmeshQuery.FlyHighRoute）：開關關著時下面兩個值不讀。
    public bool FlyHighRoute = true;
    public float FlyHighRouteMargin = 10;
    public float FlyHighRouteMaxDetour = 25;
    public bool CancelMoveOnUserInput;
    public bool StopOnStuck = false;
    // 🔑 3.0 是同一個換算：0.05 碼/幀 × 60 fps = 3.0 碼/秒，也就是這條判定在改成「每秒」
    //    之前實際使用了很久的那個行為。
    // ⚠️ 這是**改預設值**：既有使用者的設定檔裡已經有 StuckTolerance 這個鍵（Config.Load
    //    逐鍵 SetValue），所以他們的值**不會被改動**，只有全新安裝吃得到。
    //    另外 StopOnStuck 出廠是關的 ⇒ 這個值只影響「自己把它打開、又沒動過滑桿」的人。
    public float StuckTolerance = 3f;
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

    // 🔴 經由 IPC 進來的設定變更**只改執行期的值，不寫進設定檔**。
    // 🔴🔴 這張表被三種執行緒碰：SetFromIPC 走 <b>IPC 端點＝呼叫端的執行緒</b>（沒有任何
    //    「一定在 Framework 執行緒」的保證）、DrawIPCOverrideMarker 每幀從繪製執行緒讀、
    //    Save() 從 Framework 執行緒 foreach 走訪。
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
        if (ImGui.Checkbox("Find shorter ground paths".Loc(), ref ShortenPaths))
            NotifyModified();
        ImGuiComponents.HelpMarker("Ground paths are also searched with two more accurate methods, and the shortest valid result is used. The chosen route is never longer than the previous method's, at the cost of a little extra computation per search. When the destination cannot be reached, the path heads for the closest reachable point right away instead of searching the whole area first. Turn this off to use only the previous method. Flying paths are not affected.".Loc());
        if (ImGui.Checkbox("Faster flying path search".Loc(), ref FastFlyPaths))
            NotifyModified();
        ImGuiComponents.HelpMarker("Flying paths are searched with a faster method: line-of-sight checks are deferred until they are needed, and the search is steered more directly toward the destination. Long flights that used to take several seconds to plan usually take well under a second now; the route may be slightly longer (usually under 1%, at most a few percent). When the destination is sealed off, the path still ends at the closest point found, after the same number of search steps as before but in less time. Turn this off to use only the previous method. Ground paths are not affected.".Loc());
        if (ImGui.Checkbox("Tune flying path search".Loc(), ref FlyPathTuning))
            NotifyModified();
        ImGuiComponents.HelpMarker("Uses the three values below for flying path searches (from a community fork). On large maps a flight that took seconds to plan usually takes a fraction of that, and a destination that cannot be reached is given up on after about a quarter of a second instead of about four; the route is usually 2-3% longer, occasionally up to about a fifth. Meant to be used with faster flying path search on: with the previous method the route can now and then cut through the edge of a rock, which that method already does occasionally. Off = the search is unchanged and the values below are ignored. Ground paths are not affected.".Loc());
        if (FlyPathTuning)
        {
            using var indent = ImRaii.PushIndent();
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Pull toward the destination (heuristic weight)".Loc(), ref FlyHeuristicWeight, 1f, 3f, "%.2f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            ImGuiComponents.HelpMarker("Higher = the search heads more directly for the destination and explores less, but the route may be longer. Without tuning the previous method uses 1 and the faster flying path search uses 1.1. Suggested: 1.5.".Loc());
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Line-of-sight check range during search (yalms)".Loc(), ref FlyRaycastRange, 0f, 100f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            ImGuiComponents.HelpMarker("How far the search may look for a straight shortcut while it runs. 0 = no limit (what is used without tuning). A long check walks every voxel on the line, which is where much of the time goes. Suggested: 25.".Loc());
            ImGui.SetNextItemWidth(200);
            ImGui.SliderInt("Search step limit".Loc(), ref FlyMaxSteps, 10000, 1000000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            ImGuiComponents.HelpMarker("How many steps the search may take before it stops at the closest point found. 1000000 = what is used without tuning. A destination that cannot be reached at all always runs to this limit. Suggested: 200000.".Loc());
        }
        if (ImGui.Checkbox("Straighten flying paths".Loc(), ref FlyStringPull))
            NotifyModified();
        ImGuiComponents.HelpMarker("After a flying path is found, waypoints that can be skipped are removed: from each point the route goes straight to the farthest later point that is in clear line of sight (checked against the same flight map the search uses), stopping at the first point that is blocked. Points are only removed, so the route never gets longer, and the start, the end and whether the destination is reached stay the same. In tests on real maps paths usually lost about one point in eight and were up to about 1% shorter; with flying path search tuning on, paths had less than half as many points and were usually 1-2% shorter, occasionally up to about 14%. The extra planning time is about a millisecond at most. The climb-first high route is already straight and is not changed. Off = unchanged. Ground paths are not affected.".Loc());
        if (ImGui.Checkbox("When a flight cannot reach the destination, follow the walking route".Loc(), ref FlyMeshFallback))
            NotifyModified();
        ImGuiComponents.HelpMarker("When a flying path search stops short of the destination (for example flying out of an underground area whose exit leads away from the destination), the walking navmesh is asked for a route and the flight follows it, low along the ground, one short search per leg. If that route cannot be followed either, the path is the same as with this off: it stops at the closest point found. Only runs after the flying search has already failed, and usually costs under a tenth of a second more in that case (rarely close to a second). Each use writes one line to the log. Off = unchanged.".Loc());
        if (ImGui.Checkbox("When the sky above is clear, climb first and fly over obstacles".Loc(), ref FlyHighRoute))
            NotifyModified();
        ImGuiComponents.HelpMarker("Flights first climb (at most 45 degrees) to a height that clears everything along the way, fly straight across in the air, and descend near the destination, keeping well away from trees, rocks and walls. The usual search is used instead when something is directly above the start or the destination (under a tree, a roof, inside a cave, or at the map's height limit), while diving, when the distance is too short to climb and come back down, or when this route would be too much longer than the usual one. Each use writes one line to the log. Off = unchanged.".Loc());
        if (FlyHighRoute)
        {
            using var indent = ImRaii.PushIndent();
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Clearance around the flight (yalms)".Loc(), ref FlyHighRouteMargin, 2f, 30f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            ImGuiComponents.HelpMarker("While flying level, at least this much empty space is kept above, below and to both sides. Higher = flies higher and is less likely to scrape anything, but fewer flights can use the high route. Suggested: 10.".Loc());
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Use it only if at most this much longer (%)".Loc(), ref FlyHighRouteMaxDetour, 0f, 100f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            ImGuiComponents.HelpMarker("The high route is not used when it is more than this much longer than the usual route. When it is within this much of the straight-line distance it is used right away without running the usual search (so it cannot be more than this much longer than the usual route either). Suggested: 25.".Loc());
        }
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
            // 🔑 下限 0.05、對數刻度，理由是**讓每一個實際存在的值都拉得到**：
            //    舊設定檔（以及所有沒動過這一格的舊使用者）存的是 0.05，
            //    而舊滑桿的下限是 0.5 ⇒ 那些人一旦碰到滑桿就跳到 0.5，再也回不去原本的值。
            //    線性刻度在 0.05~3 之間對小數值完全沒有解析度，所以用 Logarithmic。
            ImGui.SliderFloat("Stuck tolerance (yalms/second)".Loc(), ref StuckTolerance, 0.05f, 3f, "%.2f", ImGuiSliderFlags.Logarithmic);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The minimum speed, in yalms per second, the object must maintain to avoid being considered stuck.".Loc());
            ImGuiComponents.HelpMarker("Speed below which you count as stuck, in yalms per second. Default 3 (about half of normal running speed). Values below ~0.5 mean the character has to be completely frozen before this triggers.".Loc());

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
