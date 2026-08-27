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
    //
    // ⚠️ 2026-08-27 更正：原本這裡寫「上游是在建置期才套用 CustomizeMesh，所以上游會 bump
    //    Version」——**這句是錯的**。merge-base 與上游的 NavmeshManager 在快取載入路徑上
    //    **都有** Deserialize → CustomizeMesh。維持 Version 0 的結論沒錯，但理由不是那個。
    // 🔴 反之，動到 CustomizeScene／CustomizeSettings 的自訂化仍然必須 bump（見 Z0959）。

    // 參數對照：LinkPoints 的第 4 參數是 Navmesh.AreaId（決定尋路成本倍率與
    // FollowPath 的等待條件）。這裡沿用預設值 ClientPath，未改成上游的 Shortcut：
    // 兩者成本比值差很多（3.33:1 vs 1.25:1），改過去等於改變既有路線的選擇偏好。

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        // ship interior 1
        LinkPoints(mesh, new(-274.10587f, 11.32725f, 188.9568f), new(-272.5555f, 11.780226f, 188.65962f));
        LinkPoints(mesh, new(-272.5555f, 11.780226f, 188.65962f), new(-274.10587f, 11.32725f, 188.9568f));
    }
}
