using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

public enum CustomLinkResult
{
    Linked = 0,          // 上次建置成功建立
    SkippedPrecheck = 1, // 端點預檢未通過而略過（見 NavmeshCustomization.TryResolveLinkEndpoint）
    DisabledByUser = 2,  // 使用者在「自訂捷徑」分頁停用
    SkippedDevGrade = 3, // 全服建設階段未達門檻，該路線尚未開通（見 CosmicProgress）
}

// Config.CustomLinkCatalog 的持久化條目（Newtonsoft 以公開欄位序列化；需要無參建構式）
public class CustomLinkRecord
{
    public string Key = "";
    public uint Territory;
    public Vector3 Start;
    public Vector3 End;
    public int LastResult = -1; // -1 = 尚無建置紀錄，否則為 (int)CustomLinkResult
    public string LastReason = "";
    public DateTime LastTime;
}

// 建置期間記錄每條 LinkPoints 自訂捷徑的處置結果（成功／預檢略過／使用者停用），
// 供「自訂捷徑」分頁顯示。Record 由建置（背景）執行緒呼叫、Snapshot/ConsumeDirty 由
// UI（主）執行緒呼叫，以鎖保護。持久化不在這裡做：CustomLinksUI 繪製時把新結果併進
// Config.CustomLinkCatalog（僅主執行緒觸碰 catalog）。
public static class CustomLinkTracker
{
    public record Entry(uint Territory, Vector3 Start, Vector3 End, CustomLinkResult Result, string Reason, DateTime Time);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Entry> _entries = [];
    private static bool _dirty;

    // 跨版本穩定的捷徑識別鍵：territory + 兩端座標。座標取 1 位小數（InvariantCulture）：
    // 不依賴宣告順序，能吸收浮點格式化（如 JIT/FMA）的位元級差異；座標微調超過 0.05
    // 才會變鍵——那本來就該視為不同捷徑、回到預設啟用。
    public static string MakeKey(uint territory, Vector3 start, Vector3 end)
        => FormattableString.Invariant($"{territory}:({start.X:f1},{start.Y:f1},{start.Z:f1})→({end.X:f1},{end.Y:f1},{end.Z:f1})");

    public static void Record(string key, uint territory, Vector3 start, Vector3 end, CustomLinkResult result, string reason)
    {
        lock (_lock)
        {
            _entries[key] = new(territory, start, end, result, reason, DateTime.Now);
            _dirty = true;
        }
    }

    public static Dictionary<string, Entry> Snapshot()
    {
        lock (_lock)
            return new(_entries);
    }

    // 有新結果時回傳 true 並清掉旗標；若消費當下建置仍在寫入，剩餘條目會再次舉旗，
    // 下一幀自然補併，不會遺漏。
    public static bool ConsumeDirty()
    {
        lock (_lock)
        {
            var d = _dirty;
            _dirty = false;
            return d;
        }
    }
}
