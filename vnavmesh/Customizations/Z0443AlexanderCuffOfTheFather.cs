using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn：從高處往下跳。
[CustomizationTerritory(443)]
internal class Z0443AlexanderCuffOfTheFather : NavmeshCustomization
{
    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 跨 tile 的連結建置期加不進去，改在載入後接上；走路下去，用 None（不打折、不壓低全區成本倍率）。
        LinkPoints(mesh, new(9.0f, 12.1f, 36.5f), new(-3.3f, -18.2f, 37.5f), Navmesh.AreaId.None);
    }
}
