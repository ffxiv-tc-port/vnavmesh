using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(959)]
internal class Z0959MareLamentorum : NavmeshCustomization
{
    // 🔴 這裡動的是 CustomizeSettings —— 只有「建置期」會套用（NavmeshBuilder 產生每塊
    // tile 的 DtNavMeshCreateParams 時呼叫），快取載入路徑不會重跑。所以與純 LinkPoints
    // 的自訂化不同，必須 bump Version 讓既有快取失效、重建一次，這些連結才會出現。
    // （既有使用者第一次進嘆息海會多花一次建置時間，屬預期行為。）
    public override int Version => 1;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        // all the little allagan bridges are too steep
        // ⚠️ 用 Checked 版本而不是上游的 AddOffMeshConnection：後者在連結跨越 tile 邊界時
        // 會擲例外，而這是在每塊 tile 的建置任務裡跑的 —— 例外會讓整張圖的建置中止
        // （Z1237 已實證過同一形狀的事故：一條寫死座標的自訂連結對不上台服地形，結果
        // 整個區域完全無法尋路，下游只看得到「Nav 永遠不 ready」）。這些座標是照國際服
        // 寫死的，台服的 tile 網格由場景包圍盒推導，不保證逐位元相同，所以採 fail-safe。
        config.AddOffMeshConnectionChecked(new(-51, 42.5f, 466.6f), new(-52.4f, 43.8f, 472.3f), bidirectional: true);
        config.AddOffMeshConnectionChecked(new(112.9f, 45.5f, 460.9f), new(109.4f, 43.3f, 457.2f), bidirectional: true);
        config.AddOffMeshConnectionChecked(new(128.7f, 52.8f, 465.5f), new(131.5f, 54.5f, 467.8f), bidirectional: true);
        config.AddOffMeshConnectionChecked(new(308.9f, 108.5f, 26.4f), new(307.1f, 107.5f, 23.6f), bidirectional: true);
    }
}
