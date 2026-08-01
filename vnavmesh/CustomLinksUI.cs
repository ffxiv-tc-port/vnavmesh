using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

// 「自訂捷徑」分頁：列出各區域由 customization 以 LinkPoints 建立的自訂連結，讓使用者
// 個別停用（預設全開）。停用集合存在 Config.DisabledCustomLinks；清單與「上次建置的
// 預檢結果」來自 CustomLinkTracker（建置期間記錄），繪製時併入 Config.CustomLinkCatalog
// 持久化，跨重啟仍能顯示與重新啟用。
// 快取一致性說明：LinkPoints 的捷徑「不會」寫進網格快取檔——BuildNavmesh 是先序列化
// 快取再跑 CustomizeMesh，載入快取後也會重跑 CustomizeMesh——所以勾選狀態改變不需要
// 讓快取失效，只要重載網格（Reload(true)）讓 CustomizeMesh 帶著新的停用集合重跑即可。
public class CustomLinksUI
{
    private static readonly Vector4 ColorPass = new(0.4f, 0.9f, 0.4f, 1);
    private static readonly Vector4 ColorWarn = new(1f, 0.8f, 0.2f, 1);
    private static readonly Vector4 ColorMuted = new(0.6f, 0.6f, 0.6f, 1);

    private readonly NavmeshManager _manager;

    public CustomLinksUI(NavmeshManager manager) => _manager = manager;

    public void Draw()
    {
        MergeTrackerIntoCatalog();

        ImGui.TextWrapped("Custom navmesh links added per territory (e.g. cosmoliner shortcuts). Unchecked links are skipped the next time that territory's navmesh is built; a territory's links appear in this list after its navmesh has been built at least once.".Loc());
        ImGui.Spacing();

        var progress = _manager.LoadTaskProgress;
        if (progress >= 0)
        {
            ImGui.ProgressBar(progress, new Vector2(200, 0));
            ImGui.SameLine();
            ImGui.TextUnformatted("Rebuilding...".Loc());
        }
        else
        {
            using var disabled = ImRaii.Disabled(_manager.CurrentKey.Length == 0);
            if (ImGui.Button("Apply and rebuild current zone navmesh".Loc()))
                _manager.Reload(true); // 捷徑是在載入快取後才套用的，重載即可讓新勾選生效，不需重建快取
        }

        ImGui.Separator();

        var catalog = Service.Config.CustomLinkCatalog;
        if (catalog.Count == 0)
        {
            ImGui.TextColored(ColorMuted, "No custom links observed yet.".Loc());
        }
        else
        {
            var currentTerritory = (uint)Service.ClientState.TerritoryType;
            foreach (var group in catalog.GroupBy(r => r.Territory).OrderByDescending(g => g.Key == currentTerritory).ThenBy(g => g.Key))
            {
                var isCurrent = group.Key == currentTerritory;
                var name = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(group.Key)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
                var header = $"[{group.Key}] {name}{(isCurrent ? " (current zone)".Loc() : "")}###linkterr{group.Key}";
                if (ImGui.CollapsingHeader(header, isCurrent ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                {
                    using var indent = ImRaii.PushIndent();
                    foreach (var rec in group)
                        DrawRow(rec);
                }
            }
        }

        // 防呆：停用集合裡存在、但目錄中沒有的鍵（例如目錄遺失或手改設定檔）也要列出來，
        // 否則使用者停用過的捷徑會變成永遠無法重新啟用的隱形項目。
        var unknown = Service.Config.DisabledCustomLinks.Where(k => catalog.All(r => r.Key != k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0 && ImGui.CollapsingHeader("Disabled entries not observed in any build yet".Loc() + "###linkunknown"))
        {
            using var indent = ImRaii.PushIndent();
            foreach (var key in unknown)
            {
                var enabled = false;
                if (ImGui.Checkbox($"{key.Replace("→", " -> ")}###unk{key}", ref enabled) && enabled)
                    SetLinkEnabled(key, true);
            }
        }
    }

    private void DrawRow(CustomLinkRecord rec)
    {
        var enabled = !Service.Config.DisabledCustomLinks.Contains(rec.Key);
        if (ImGui.Checkbox($"{FormatPoint(rec.Start)} -> {FormatPoint(rec.End)}###{rec.Key}", ref enabled))
            SetLinkEnabled(rec.Key, enabled);

        ImGui.SameLine();
        switch (rec.LastResult)
        {
            case (int)CustomLinkResult.Linked:
                ImGui.TextColored(ColorPass, "Last build: linked".Loc());
                break;
            case (int)CustomLinkResult.SkippedPrecheck:
                ImGui.TextColored(ColorWarn, "Last build: failed precheck, skipped".Loc());
                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(ColorWarn, FontAwesomeIcon.ExclamationTriangle.ToIconString());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This link failed the endpoint precheck on the last build (terrain likely not constructed yet on this server) - consider disabling it.\nReason: ??".Loc(rec.LastReason));
                break;
            case (int)CustomLinkResult.DisabledByUser:
                ImGui.TextColored(ColorMuted, "Last build: disabled by user".Loc());
                break;
            default:
                ImGui.TextColored(ColorMuted, "No build record yet".Loc());
                break;
        }

        if (rec.LastTime != default)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, rec.LastTime.ToString("MM-dd HH:mm"));
        }
    }

    // 把建置執行緒記錄的最新結果併進持久化目錄。只在主執行緒（Draw）呼叫；
    // 若消費 dirty 當下建置仍在寫入，剩餘條目會再次舉旗、下一幀補併。
    private static void MergeTrackerIntoCatalog()
    {
        if (!CustomLinkTracker.ConsumeDirty())
            return;

        var catalog = Service.Config.CustomLinkCatalog;
        foreach (var (key, e) in CustomLinkTracker.Snapshot())
        {
            var rec = catalog.FirstOrDefault(r => r.Key == key);
            if (rec == null)
                catalog.Add(rec = new() { Key = key });
            rec.Territory = e.Territory;
            rec.Start = e.Start;
            rec.End = e.End;
            rec.LastResult = (int)e.Result;
            rec.LastReason = e.Reason;
            rec.LastTime = e.Time;
        }
        catalog.Sort((a, b) => a.Territory != b.Territory ? a.Territory.CompareTo(b.Territory) : string.CompareOrdinal(a.Key, b.Key));
        Service.Config.NotifyModified();
    }

    private static void SetLinkEnabled(string key, bool enabled)
    {
        // copy-on-write：整組替換而非就地增刪——LinkPoints 在建置（背景）執行緒讀這個欄位，
        // 見 Config.DisabledCustomLinks 的執行緒約定。
        var set = new HashSet<string>(Service.Config.DisabledCustomLinks);
        if (enabled)
            set.Remove(key);
        else
            set.Add(key);
        Service.Config.DisabledCustomLinks = set;
        Service.Config.NotifyModified();
    }

    private static string FormatPoint(Vector3 p) => FormattableString.Invariant($"({p.X:f0}, {p.Y:f0}, {p.Z:f0})");
}
