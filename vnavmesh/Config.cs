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
    public bool ForceShowGameCollision;
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

    private static readonly int realMaxCores = Environment.ProcessorCount;

    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    public void Draw()
    {
        if (ImGui.Checkbox("Automatically load/build navigation data when changing zones".Loc(), ref AutoLoadNavmesh))
            NotifyModified();
        if (ImGui.Checkbox("Enable DTR bar".Loc(), ref EnableDTR))
            NotifyModified();
        if (ImGui.Checkbox("Show detailed query status in DTR".Loc(), ref ShowQueryStatusInDTR))
            NotifyModified();
        if (ImGui.Checkbox("Align camera to movement direction".Loc(), ref AlignCameraToMovement))
            NotifyModified();
        using (ImRaii.Disabled(!AlignCameraToMovement))
        {
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("Camera height (degrees)".Loc(), ref AlignCameraHeight, -75, 75);
            if (ImGui.IsItemDeactivatedAfterEdit())
                NotifyModified();
        }
        if (ImGui.Checkbox("Show active waypoints".Loc(), ref ShowWaypoints))
            NotifyModified();
        if (ImGui.Checkbox("Always visualize game collision".Loc(), ref ForceShowGameCollision))
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
            JObject jContents = new()
            {
                { "Version", _version },
                { "Payload", JObject.FromObject(this) }
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
