using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn。四段往下跳的平台與四處副本轉場（客戶端固定路徑），
// 我方網格上每一段的兩端都分屬互不相通的區塊。
[CustomizationTerritory(933)]
internal class Z0933MatoyasRelict : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnectionChecked(new(50.527153f, 234.05f, -158.33136f), new(50.556087f, 219.99991f, -151.703f));
        config.AddOffMeshConnectionChecked(new(51.37818f, 219.9999f, -144.02701f), new(50.02321f, 202f, -136.73102f));
        config.AddOffMeshConnectionChecked(new(-41.663002f, 188f, -119.04332f), new(-37.9675f, 170.09996f, -112.67725f));
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 第三段往下跳；跨 tile 的連結建置期加不進去，改在載入後接上；走路下去，用 None（不打折、不壓低全區成本倍率）。
        LinkPoints(mesh, new(-49.426426f, 202.05f, -130.93295f), new(-46.1742f, 188f, -125.87389f), Navmesh.AreaId.None);

        LinkPoints(mesh, new(-211.99867f, -211.95f, 29.758022f), new(-210.02719f, -216.00003f, -0.32089043f));
        LinkPoints(mesh, new(-180.01707f, -219.95f, -78.88551f), new(-178.044f, -220f, -121.21176f));
        LinkPoints(mesh, new(-19.240154f, 198.3f, -183.30945f), new(-21.766142f, 202f, -189.57169f));
        LinkPoints(mesh, new(0.036924675f, 170.1f, -108.12259f), new(1.9833529f, 150.2f, -134.99567f));
    }
}
