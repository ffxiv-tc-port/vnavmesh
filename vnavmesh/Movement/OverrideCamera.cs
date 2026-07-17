using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using System.Runtime.InteropServices;

namespace Navmesh.Movement;

[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public unsafe struct CameraEx
{
    // TC's client predates the game patch that added api13 (see the "api13" commit that shifted these
    // offsets forward by 0x10 for global) - use the pre-api13 offsets matching that patch level, same as
    // the confirmed-working aliceric27/DalamudPlugins-TW TC/TW build (sourced from the actual v0.4.0.2 tag
    // on github.com/awgil/ffxiv_navmesh, not just a decompile).
    [FieldOffset(0x130)] public float DirH; // 0 is north, increases CW
    [FieldOffset(0x134)] public float DirV; // 0 is horizontal, positive is looking up, negative looking down
    [FieldOffset(0x138)] public float InputDeltaHAdjusted;
    [FieldOffset(0x13C)] public float InputDeltaVAdjusted;
    [FieldOffset(0x140)] public float InputDeltaH;
    [FieldOffset(0x144)] public float InputDeltaV;
    [FieldOffset(0x148)] public float DirVMin; // -85deg by default
    [FieldOffset(0x14C)] public float DirVMax; // +45deg by default
}

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

    private delegate void RMICameraDelegate(CameraEx* self, int inputMode, float speedH, float speedV);
    // Global's function-prologue signature doesn't match TC's compiled shape of this function at all
    // (confirmed by scanning TC's ffxiv_dx11.exe directly: zero hits). This call-site signature instead
    // is sourced from a known-working TC/TW build (aliceric27/DalamudPlugins-TW's vnavmesh 0.4.0.2) and
    // verified to match exactly once in TC's binary. Kept fallible regardless, as a safety net.
    [Signature("E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 44 0F 28 4C 24 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera()
    {
        Service.Hook.InitializeFromAttributes(this);
        if (_rmiCameraHook != null)
            Service.Log.Information($"RMICamera address: 0x{_rmiCameraHook.Address:X}");
        else
            Service.Log.Warning("RMICamera signature not found - camera auto-facing disabled");
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    private void RMICameraDetour(CameraEx* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.Original(self, inputMode, speedH, speedV);
        if (IgnoreUserInput || inputMode == 0) // let user override...
        {
            var dt = Framework.Instance()->FrameDeltaTime;
            var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
            var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
            var maxH = SpeedH.Rad * dt;
            var maxV = SpeedV.Rad * dt;
            self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
            self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
        }
    }
}
