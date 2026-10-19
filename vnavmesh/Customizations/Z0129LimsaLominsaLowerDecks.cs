using System.Collections.Generic;
using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(129)]
internal class Z0129LimsaLominsaLowerDecks : NavmeshCustomization
{
    // 版本策略：本自訂化只覆寫 CustomizeMesh（純 LinkPoints）。本 fork 的
    // NavmeshManager.BuildNavmesh 在「從快取載入之後」也會重跑 CustomizeMesh
    // （NavmeshManager.cs 的 Deserialize → CustomizeMesh 那段），所以自訂捷徑不需要
    // 讓既有快取失效就會生效 —— 維持 Version 0 可讓既有使用者不必重建這張圖。
    // 🔴 反之，動到 CustomizeScene／CustomizeSettings 的自訂化仍然必須 bump（見 Z0959）。

    // 刻意用 None：這是走路路段；ClientPath 會讓 FollowPath 在起點等客戶端路徑，打折的連結還會壓低全區 MinCostMultiplier、把路線拉去繞捷徑。

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        // ship interior 1
        LinkPoints(mesh, new(-274.10587f, 11.32725f, 188.9568f), new(-272.5555f, 11.780226f, 188.65962f), Navmesh.AreaId.None);
        LinkPoints(mesh, new(-272.5555f, 11.780226f, 188.65962f), new(-274.10587f, 11.32725f, 188.9568f), Navmesh.AreaId.None);
    }
}
