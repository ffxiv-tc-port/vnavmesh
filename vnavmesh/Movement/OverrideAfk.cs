using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Navmesh.Movement;

internal unsafe static class OverrideAFK
{
    public static void ResetTimers()
    {
        // UIModule.Instance() 走 Framework.Instance() 與 Framework.GetUIModule()，任一位址解不出來都會擲例外，
        // 而這裡在每幀的跟隨路徑上（擲出＝角色完全不動）：只停用閒置計時器重置。
        if (Framework.Addresses.Instance.Value == 0 || Framework.Addresses.GetUIModule.Value == 0)
        {
            FeatureHealth.Report("ui.afkTimer",
                "AFK timer reset while following a path is off (address not found): the game may set you to AFK during long automated trips.",
                "[OverrideAFK] Framework／GetUIModule 的位址解不出來（遊戲改版後常見），只停用跟隨路徑時的閒置計時器重置：路徑照常跟隨。");
            return;
        }

        var uiModule = UIModule.Instance();
        // UIModule.Instance() 是手寫的取得子:Framework 還沒建立時回 null。
        // 取不到就安靜跳過、不重置 AFK 計時器,下一次呼叫會再試。
        if (uiModule == null)
            return;

        var module = uiModule->GetInputTimerModule();
        if (module == null)
            return;

        module->AfkTimer = 0;
        module->ContentInputTimer = 0;
        module->InputTimer = 0;
        module->Unk1C = 0;
    }
}
