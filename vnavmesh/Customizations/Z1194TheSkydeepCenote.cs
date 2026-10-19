using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn：往下跳的一段與兩處副本轉場（客戶端固定路徑）。
[CustomizationTerritory(1194)]
internal class Z1194TheSkydeepCenote : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnectionChecked(new(-159.43634f, -51.62309f, -209.07713f), new(-169.7469f, -201.3f, -208.59708f));
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        LinkPoints(mesh, new(100.06602f, -189.8996f, -231.05582f), new(139.63399f, -184.84326f, -294.38354f));
        LinkPoints(mesh, new(99.991234f, -194.8996f, -363.95795f), new(99.36945f, -191.99121f, -415.7162f));
    }
}
