using System.Collections.Generic;

namespace Navmesh.Customizations;

// Occult Crescent: North Horn（上游 73c3433a22a594c98a789b64d79fb1194e1434bd + 6fc80725eb8290472eee433fc4be7ee06ec79357）。
// ⚠️ 台服 7.20 的 TerritoryType 表只到 1333，根本沒有 1346 這一列；
// 註冊是按 TerritoryType id 查表（NavmeshCustomizationRegistry.ForTerritory），
// 所以在台服上這整個類別是死碼，先放著等該區上線。
[CustomizationTerritory(1346)]
internal class Z1346OccultCrescentNorthHorn : NavmeshCustomization
{
    public override int Version => 1;

    public Z1346OccultCrescentNorthHorn()
    {
        // Watershed REALLY doesn't like the spiral staircase near Sinking Sanctuary and i am not skilled enough to tweak the config to fix it
        Settings.Partitioning = DotRecast.Recast.RcPartition.LAYERS;
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // island up (E)
        LinkPoints(mesh, new(-471.645f, 96.432f, 885.058f), new(-502.403f, 158.678f, 880.735f));
        // island down (E)
        LinkPoints(mesh, new(-502.411f, 158.576f, 894.453f), new(-452.72f, 96.33f, 886.656f));

        // island up (W)
        LinkPoints(mesh, new(-833.534f, 97.623f, 553.106f), new(-912.932f, 157.793f, 630.335f));
        // island down (W)
        LinkPoints(mesh, new(-900.858f, 157.8f, 629.249f), new(-823.331f, 94.5f, 543.053f));

        // sinking sanctuary chest tower to ground
        LinkPoints(mesh, new(289.216f, 143, -366.471f), new(326.546f, 10.368f, -411.02f), Navmesh.AreaId.Shortcut);
    }
}
