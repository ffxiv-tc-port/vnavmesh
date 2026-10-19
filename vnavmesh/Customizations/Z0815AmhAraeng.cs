using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn。上游把同一個缺口寫了三次（兩條 LinkPoints＋一條建置期連結），
// 這裡只留一組雙向；走路就過得去，用 None（不打折、不壓低全區成本倍率）。
[CustomizationTerritory(815)]
internal class Z0815AmhAraeng : NavmeshCustomization
{
    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 尾之道：從高處往下跳；跨 tile 的連結建置期加不進去，改在載入後接上；走路下去，用 None（不打折、不壓低全區成本倍率）。
        LinkPoints(mesh, new(122.31265f, 26.299103f, -194.78415f), new(140.46594f, 7.1558275f, -194.42236f), Navmesh.AreaId.None);

        // 安穆馬利克新宮殿
        LinkPoints(mesh, new(434.5f, -106.0f, 590.0f), new(422.7f, -105.3f, 589.6f), Navmesh.AreaId.None);
        LinkPoints(mesh, new(422.7f, -105.3f, 589.6f), new(434.5f, -106.0f, 590.0f), Navmesh.AreaId.None);
    }
}
