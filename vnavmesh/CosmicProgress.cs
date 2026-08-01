using FFXIVClientStructs.FFXIV.Client.Game.WKS;

namespace Navmesh;

// 宇宙探索（月面基地／渴望灣）的全服建設階段。
//
// 為什麼需要它：Z1237SinusArdorum 的自訂捷徑座標是上游照國際服「完工態」地形寫死的，
// 而建設進度是各伺服器獨立推進的（台服 2026-08-02 實測 DevGrade=15）。端點預檢
// （NavmeshCustomization.TryResolveLinkEndpoint）只擋得掉「地形根本還沒蓋」的情況；
// 「站台地形已經在、但那條宇宙快線還沒通車」預檢是過得了的 —— 捷徑會被建立，尋路
// 就會規劃出一條實際走不通的路（繞路或撞牆）。建設階段是遊戲自己的權威數字，正好
// 補上這一段，使用者也不必在「自訂捷徑」分頁一條一條手動取消勾選。
//
// 資料來源：WKSManager.DevGrade（FFXIVClientStructs 注明為 WKSDevGrade 表的 RowId）。
//
// 🔴 執行緒約定：Update() 只由主（framework）執行緒呼叫（NavmeshManager.Update）；
// DevGrade 由網格建置的背景執行緒讀取。以 int 欄位存放，讀寫本身是原子的，不必上鎖 ——
// 最壞情況是建置當下剛好跨階段而讀到舊值，下一次重建就會修正。
//
// ⚠️ 安全邊界：只讀 WKSManager 的一個 scalar 欄位（0x52 的 ushort），不解參考它底下
// 任何子模組指標（MissionModule／MechaEventModule 之類在台服未逐一驗證過結構）。
// Instance() 為 null 時保留上一次的已知值。
internal static class CosmicProgress
{
    // 0 = 尚未觀察到（未登入，或還沒載入過宇宙探索模組）。
    public static int DevGrade { get; private set; }

    // 各「期」的建設階段門檻，取自 WKSPioneeringTrail 表（2026-08-02 以台服 7.20 的
    // EXD dump 核對過：第1期=0、第2期=4、第3期=8、第4期=14、第5期=18…）。索引＝期數-1。
    // ⚠️ 這張表**只用於顯示**「目前第幾期」。捷徑閘門用的是各群組自己帶的門檻值，
    // 兩者不互相依賴 —— 就算日後資料片改動期數編排，捷徑閘門也不會跟著錯。
    private static readonly int[] PhaseThresholds = [0, 4, 8, 14, 18, 21, 24, 30, 33, 37, 43, 49, 55, 58, 62, 62];

    // 目前是第幾期（1 起算）；尚未觀察到時回 0。
    public static int CurrentPhase => PhaseForGrade(DevGrade);

    // 門檻值對應到第幾期，供顯示用；對不上時回 0。
    public static int PhaseForThreshold(int minDevGrade)
    {
        for (var i = 0; i < PhaseThresholds.Length; ++i)
            if (PhaseThresholds[i] == minDevGrade)
                return i + 1;
        return 0;
    }

    // 建設階段是否還沒達到 minDevGrade。
    // ⚠️ 刻意「不確定就放行」：DevGrade 尚未觀察到（0）時一律回 false，讓端點預檢照常
    // 把關 —— 這樣即使這整套推論是錯的，最壞也只是回到加入閘門之前的行為，不會把本來
    // 能用的捷徑全部關掉。
    public static bool IsBelow(int minDevGrade, out int current)
    {
        current = DevGrade;
        return current > 0 && current < minDevGrade;
    }

    public static unsafe void Update()
    {
        var mgr = WKSManager.Instance();
        if (mgr == null)
            return; // 保留上次已知值

        int grade = mgr->DevGrade;
        if (grade <= 0 || grade == DevGrade)
            return;

        var prev = DevGrade;
        DevGrade = grade;

        // 階段推進代表地形真的變了。網格快取鍵含 festival 層，而宇宙探索區的 festival
        // 子編號是跟著建設階段走的（台服實測 14→15），所以快取會自然失效重建；這裡
        // 只需留下紀錄讓日後查 log 有跡可循。
        if (prev != 0)
            Service.Log.Information($"[CosmicProgress] 宇宙探索建設階段推進：{prev} -> {grade}（第 {CurrentPhase} 期）。自訂捷徑閘門會在下次建置網格時套用新階段。");
        else
            Service.Log.Information($"[CosmicProgress] 宇宙探索建設階段：{grade}（第 {CurrentPhase} 期）。");
    }

    private static int PhaseForGrade(int grade)
    {
        if (grade <= 0)
            return 0;
        var phase = 0;
        for (var i = 0; i < PhaseThresholds.Length; ++i)
            if (grade >= PhaseThresholds[i])
                phase = i + 1;
        return phase;
    }
}
