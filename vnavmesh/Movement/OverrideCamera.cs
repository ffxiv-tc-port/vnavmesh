using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;

namespace Navmesh.Movement;

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose.
// It carried hardcoded FieldOffsets that had to be re-guessed every game patch, and got it wrong twice.
// FFXIVClientStructs.FFXIV.Client.Game.Camera is maintained/verified against the API13 pin we build on, so use it
// directly and let the pin track layout changes for us.

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
    // Kept fallible as a safety net so a
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
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in .NET Core). What it catches is managed exceptions - most importantly the InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 1 - Debug is captured too, but drowned by the 100k+ Debug lines a single log file holds.
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
                //    「指標的位址」再解參考一層,所以它會回 null。
                //    裸解參考 null 是 AccessViolationException,在 .NET Core 屬 corrupted-state exception。
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
