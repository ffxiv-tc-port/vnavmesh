using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn。上游另有兩處門前的雙向捷徑，我方網格上本來就直線走得到，不收；
// 上游把本區的 CellSize／CellHeight／AgentHeight 調細，那會重算整區網格，沒有一起搬；
// 上游另加的三個不可行走方盒會讓我方網格從地面再也走不回兩層高台，無法離線判斷哪邊才對，不收。
// 往下跳與輸送帶都是照一般走路前進，用 None（不打折、不壓低全區成本倍率）。
[CustomizationTerritory(444)]
internal class Z0444AlexanderArmOfTheFather : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        // 往下跳 2
        config.AddOffMeshConnectionChecked(new(16.790548f, 18f, -18.193995f), new(20.274364f, 8.826821f, -23.925808f), radius: 2f);
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 往下跳 1
        LinkPoints(mesh, new(20.97561f, 35.141724f, -15.054151f), new(24.470856f, 23.111866f, -19.177319f), Navmesh.AreaId.None);
        // 輸送帶
        LinkPoints(mesh, new(58.524216f, 0.011983752f, -21.014153f), new(58.992924f, -9f, -42.920166f), Navmesh.AreaId.None);
    }
}
