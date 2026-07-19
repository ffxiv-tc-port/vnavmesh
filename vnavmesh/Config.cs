using ImGuiNET;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Newtonsoft.Json.Linq;
using System;
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

    private static readonly int realMaxCores = Environment.ProcessorCount;

    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    public void Draw()
    {
        if (ImGui.Checkbox("切換區域時自動載入/建立導航資料", ref AutoLoadNavmesh))
            NotifyModified();
        if (ImGui.Checkbox("啟用 DTR 伺服器資訊列", ref EnableDTR))
            NotifyModified();
        if (ImGui.Checkbox("在 DTR 中顯示詳細查詢狀態", ref ShowQueryStatusInDTR))
            NotifyModified();
        if (ImGui.Checkbox("將鏡頭對齊移動方向", ref AlignCameraToMovement))
            NotifyModified();
        using (ImRaii.Disabled(!AlignCameraToMovement))
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("鏡頭高度（角度）", ref AlignCameraHeight, -75, 75))
                NotifyModified();
        }
        if (ImGui.Checkbox("顯示目前的路徑點", ref ShowWaypoints))
            NotifyModified();
        if (ImGui.Checkbox("永遠顯示遊戲碰撞範圍", ref ForceShowGameCollision))
            NotifyModified();
        if (ImGui.Checkbox("玩家輸入移動時取消目前路徑", ref CancelMoveOnUserInput))
            NotifyModified();
        if (ImGui.Checkbox("卡住時停止導航", ref StopOnStuck))
            NotifyModified();

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("建立導航網格時使用的最大核心數", ref BuildMaxCores, -8, realMaxCores))
            NotifyModified();
        ImGuiComponents.HelpMarker("0 = 使用所有可用核心；正數 = 使用該數量的核心；負數 = 保留該數量的核心不使用");

        if (StopOnStuck)
        {
            if (ImGui.SliderFloat("卡住判定容許值（雅魯/秒）", ref StuckTolerance, 0.5f, 3f))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("物件每幀必須移動的最小距離，低於此值才會被視為卡住。");

            if (ImGui.SliderInt("卡住逾時時間（毫秒）", ref StuckTimeoutMs, 100, 10_000))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("在停止前，可維持在卡住判定閾值以下的時間長度。");

            if (ImGui.Checkbox("停止後重試導航", ref RetryOnStuck))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("啟用後，代理程式在被判定為卡住之後將嘗試重新規劃路徑。");
        }

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("隨機性倍率", ref RandomnessMultiplier, 0f, 1.0f, "%.2f"))
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
