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
    // 🔴 反之，動到 CustomizeScene／CustomizeSettings 的自訂化仍然必須 bump（見 Z0959）。

    // 刻意用 None：這是走路路段；ClientPath 會讓 FollowPath 在起點等客戶端路徑，打折的連結還會壓低全區 MinCostMultiplier、把路線拉去繞捷徑。

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        // the tunnel into the island containing tamamizu has some floor that is unlandable
        // 反向也要補：網格上島內那一側（約 500 個多邊形）只有這條連結通往外面，沒有它就走不出來。
        LinkPoints(mesh, new(643.7f, 3.4f, -58.9f), new(636.6f, 3.9f, -63.3f), Navmesh.AreaId.None);
        LinkPoints(mesh, new(636.6f, 3.9f, -63.3f), new(643.7f, 3.4f, -58.9f), Navmesh.AreaId.None);

        // 第二段洞窟，座標取自 AtmoOmen/ffxiv_navmesh-cn；兩端分屬互不相通的區塊。
        // 上游另有一條碧玉水噴泉的客戶端固定路徑：現況已走得到，且觸發方式在台服未證實，不收。
        LinkPoints(mesh, new(544.853f, -61.27358f, -163.34909f), new(545.4251f, -61.478912f, -155.09254f), Navmesh.AreaId.None);
        LinkPoints(mesh, new(545.4251f, -61.478912f, -155.09254f), new(544.853f, -61.27358f, -163.34909f), Navmesh.AreaId.None);
    }
}
