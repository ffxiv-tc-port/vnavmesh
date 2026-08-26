using System.Collections.Generic;
using DotRecast.Detour;

// ⚠️ 上游這個檔的 namespace 誤植為 vnavmesh.Customizations（註冊表掃的是整個組件裡
// NavmeshCustomization 的子型別，跟 namespace 無關，所以上游沒發現）。此處歸位。
namespace Navmesh.Customizations;

[CustomizationTerritory(613)]
internal class Z0613RubySea : NavmeshCustomization
{
    // 版本策略：本自訂化只覆寫 CustomizeMesh（純 LinkPoints）。本 fork 的
    // NavmeshManager.BuildNavmesh 在「從快取載入之後」也會重跑 CustomizeMesh
    // （NavmeshManager.cs 的 Deserialize → CustomizeMesh 那段），所以自訂捷徑不需要
    // 讓既有快取失效就會生效 —— 維持 Version 0 可讓既有使用者不必重建這張圖。
    // ⚠️ 上游是在建置期才套用 CustomizeMesh，所以上游會 bump Version；語意不同，別照抄。
    // 🔴 反之，動到 CustomizeScene／CustomizeSettings 的自訂化仍然必須 bump（見 Z0959）。

    // 參數對照：上游的 LinkPoints 多一個 Navmesh.AreaId 參數（上游後來加的多邊形區域
    // 分類，用於 FollowPath 的啟發式）。本 fork 沒有 AreaId 這層，所有自訂捷徑端點一律
    // 標 Navmesh.OffMeshEndpoint(5)，所以省略該參數 —— 與既有的 Z0132／Z1291 用法一致。

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        // the tunnel into the island containing tamamizu has some floor that is unlandable
        // 上游只建單向（往島內），這裡照抄不補反向 —— 反向本來就走得回來。
        LinkPoints(mesh, new(643.7f, 3.4f, -58.9f), new(636.6f, 3.9f, -63.3f));
    }
}
