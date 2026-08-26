using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;

namespace Navmesh.Movement;

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose.
// It carried hardcoded FieldOffsets that had to be re-guessed every game patch, and got it wrong twice:
// once shifted +0x10 (fixed by 8f00fb2 for TC 7.15), then TC 7.20 shifted the real layout +0x10 again,
// so the 0x130-based offsets were reading FoV/MinFoV/MaxFoV as DirH/DirV/InputDeltaHAdjusted - which is
// why legacy-mode movement steered in a garbage direction (OverrideMovement uses DirH as its reference).
// FFXIVClientStructs.FFXIV.Client.Game.Camera has all the fields we need (DirH 0x140, DirV 0x144,
// InputDeltaHAdjusted 0x148, InputDeltaVAdjusted 0x14C, InputDeltaH 0x150, InputDeltaV 0x154,
// DirVMin 0x158, DirVMax 0x15C) and is maintained/verified against the API13 pin we build on, so use it
// directly and let the pin track layout changes for us. CameraManager::GetActiveCamera() already
// returns Camera*, so no cast is needed either.

public unsafe class OverrideCamera : IDisposable
{
    public bool Enabled
    {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set
        {
            if (_rmiCameraHook == null)
                return;
            if (value)
                _rmiCameraHook.Enable();
            else
                _rmiCameraHook.Disable();
        }
    }

    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Angle DesiredAzimuth;
    public Angle DesiredAltitude;
    public Angle SpeedH = 360.Degrees(); // per second
    public Angle SpeedV = 360.Degrees(); // per second

    private delegate void RMICameraDelegate(Camera* self, int inputMode, float speedH, float speedV);
    // The previous call-site signature (E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 44 0F 28 4C 24 ??) scans zero
    // hits on TC 7.20: the call site still exists, but the trailing movaps changed register allocation
    // (44 0F 28 44 24 70 instead of 44 0F 28 4C 24 ??), so the tail no longer matches.
    // Switched to upstream awgil/ffxiv_navmesh master's function-prologue signature, which does match TC
    // 7.20 exactly once. Verified it is the same function the old signature resolved to: its single direct
    // E8 caller is that exact `E8 ... EB 05 E8 ...` call site, and the body reads/writes the camera fields
    // at 0x140/0x144/0x150/0x154 (DirH/DirV/InputDeltaH/InputDeltaV). Kept fallible as a safety net so a
    // future mismatch degrades to "no camera auto-facing" instead of failing the whole plugin load.
    [Signature("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera()
    {
        Service.Hook.InitializeFromAttributes(this);
        if (_rmiCameraHook != null)
            Service.Log.Information($"RMICamera address: 0x{_rmiCameraHook.Address:X}");
        else
            Service.Log.Error("RMICamera signature not found - camera auto-facing disabled");
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the game's own camera handling passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 2.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        Service.Log.Information($"OverrideCamera: camera override threw, leaving the game's own camera input alone (total {_detourErrors}): {ex}");
    }

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.OriginalDisposeSafe(self, inputMode, speedH, speedV);
        try
        {
            if (self == null)
                return;
            if (IgnoreUserInput || inputMode == 0) // let user override...
            {
                // 🔴 Framework.Instance() 宣告為 [StaticAddress(..., isPointer: true)]:產生器讀
                //    「指標的位址」再解參考一層,所以它會回 null(不帶 isPointer 的那種才保證
                //    非 null)。上面那個 try 擋得住特徵碼失配時擲出的 InvalidOperationException,
                //    但擋不到裸解參考 null —— 那是 AccessViolationException,在 .NET Core 屬
                //    corrupted-state exception,而這裡是原生程式碼直接呼叫的 detour,
                //    AVE 在這裡等於整個遊戲行程當場結束。只能事前判空。
                //    fail-closed:取不到就當這一幀 dt = 0,maxH/maxV 隨之為 0,
                //    InputDeltaH/V 被夾成 0 = 這一幀不介入相機,而不是丟例外或崩潰。
                var framework = Framework.Instance();
                var dt = framework != null ? framework->FrameDeltaTime : 0f;
                var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
                var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
                var maxH = SpeedH.Rad * dt;
                var maxV = SpeedV.Rad * dt;
                self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
                self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }
}
