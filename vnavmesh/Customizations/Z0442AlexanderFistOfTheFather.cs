using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn。上游另有兩處噴氣口的雙向捷徑，我方網格上本來就直線走得到，不收。
[CustomizationTerritory(442)]
internal class Z0442AlexanderFistOfTheFather : NavmeshCustomization
{
    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 輸送帶；跨 tile 的連結建置期加不進去，改在載入後接上；走路下去，用 None（不打折、不壓低全區成本倍率）。
        LinkPoints(mesh, new(0.1f, 12.0f, -106.5f), new(-0.0f, -23.9f, -148.1f), Navmesh.AreaId.None);
    }
}
