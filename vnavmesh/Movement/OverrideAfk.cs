using FFXIVClientStructs.FFXIV.Client.UI;

namespace Navmesh.Movement;

internal unsafe static class OverrideAFK
{
    public static void ResetTimers()
    {
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
