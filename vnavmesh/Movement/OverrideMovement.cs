using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Movement;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct PlayerMoveControllerFlyInput
{
    [FieldOffset(0x0)] public float Forward;
    [FieldOffset(0x4)] public float Left;
    [FieldOffset(0x8)] public float Up;
    [FieldOffset(0xC)] public float Turn;
    [FieldOffset(0x10)] public float u10;
    [FieldOffset(0x14)] public byte DirMode;
    [FieldOffset(0x15)] public byte HaveBackwardOrStrafe;
}

public unsafe class OverrideMovement : IDisposable
{
    public bool Enabled
    {
        get => _rmiWalkHook?.IsEnabled ?? false;
        set
        {
            if (value)
            {
                _rmiWalkHook?.Enable();
                _rmiFlyHook?.Enable();
            }
            else
            {
                UserInput = false;
                _rmiWalkHook?.Disable();
                _rmiFlyHook?.Disable();
            }
        }
    }

    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Vector3 DesiredPosition;
    public float Precision = 0.01f;

    // true if player (or some other plugin) is pressing keys
    public bool UserInput { get; private set; }

    private bool _legacyMode;
    private DateTime _lastCameraDebugLog;

    private delegate bool RMIWalkIsInputEnabled(void* self);
    private readonly RMIWalkIsInputEnabled? _rmiWalkIsInputEnabled1;
    private readonly RMIWalkIsInputEnabled? _rmiWalkIsInputEnabled2;

    // Fallible on purpose: a signature that stops matching after a game patch has to degrade to
    // "movement assist is off", not throw out of the ctor. This ctor runs from Service.Init, so an
    // exception here takes down the *whole* plugin - and half the fleet (AutoDuty/Questionable/GBR/
    // Lifestream) depends on vnavmesh's pathfinding IPC, so that failure cascades.
    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", Fallibility = Fallibility.Fallible)]
    private Hook<RMIWalkDelegate>? _rmiWalkHook;

    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);
    [Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", Fallibility = Fallibility.Fallible)]
    private Hook<RMIFlyDelegate>? _rmiFlyHook;

    public OverrideMovement()
    {
        if (Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C", out var rmiWalkIsInputEnabled1Addr) &&
            Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F", out var rmiWalkIsInputEnabled2Addr))
        {
            Service.Log.Information($"RMIWalkIsInputEnabled1 address: 0x{rmiWalkIsInputEnabled1Addr:X}");
            Service.Log.Information($"RMIWalkIsInputEnabled2 address: 0x{rmiWalkIsInputEnabled2Addr:X}");
            _rmiWalkIsInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(rmiWalkIsInputEnabled1Addr);
            _rmiWalkIsInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(rmiWalkIsInputEnabled2Addr);
        }
        else
        {
            Service.Log.Error("RMIWalkIsInputEnabled signature(s) not found - walk movement override disabled");
        }

        Service.Hook.InitializeFromAttributes(this);

        // The walk detour calls both IsInputEnabled delegates, so a hook without them would be a
        // guaranteed NRE per frame - drop the hook entirely instead.
        if (_rmiWalkIsInputEnabled1 == null && _rmiWalkHook != null)
        {
            _rmiWalkHook.Dispose();
            _rmiWalkHook = null;
        }

        if (_rmiWalkHook != null)
            Service.Log.Information($"RMIWalk address: 0x{_rmiWalkHook.Address:X}");
        else
            Service.Log.Error("RMIWalk hook unavailable - walk movement override disabled");
        if (_rmiFlyHook != null)
            Service.Log.Information($"RMIFly address: 0x{_rmiFlyHook.Address:X}");
        else
            Service.Log.Error("RMIFly signature not found - fly movement override disabled");

        Service.GameConfig.UiControlChanged += OnConfigChanged;
        UpdateLegacyMode();
    }

    public void Dispose()
    {
        Service.GameConfig.UiControlChanged -= OnConfigChanged;
        _rmiWalkHook?.Dispose();
        _rmiFlyHook?.Dispose();
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the player's own movement input passes through intact.
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
        // users run at LogLevel 1 - Debug is captured too, but drowned by the 100k+ Debug lines a single log file holds.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        Service.Log.Information($"OverrideMovement: movement override threw, leaving the game's own movement input alone (total {_detourErrors}): {ex}");
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        _rmiWalkHook!.OriginalDisposeSafe(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        try
        {
            // 防護性早退:玩家昏迷(Unconscious)時不改動移動狀態,也不呼叫下面那兩個原生的
            // IsInputEnabled。Original 已經跑過,玩家自己的輸入原樣通過。
            // 🔴 來源是下游社群回報的**懷疑**(okaminico/ffxiv_navmesh@38da2512),
            //    對方沒有附 log 或崩潰 dump,我方也沒有自己的崩潰證據 ⇒ 不宣稱它會崩潰。
            //    採用的理由是這是純粹的提早 return,因果推論就算錯也不會讓行為變糟。
            if (Service.Condition[ConditionFlag.Unconscious])
            {
                UserInput = false;
                return;
            }

            // TODO: we really need to introduce some extra checks that PlayerMoveController::readInput does - sometimes it skips reading input, and returning something non-zero breaks stuff...
            bool movementAllowed = bAdditiveUnk == 0 && _rmiWalkIsInputEnabled1!(self) && _rmiWalkIsInputEnabled2!(self); //&& !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BeingMoved];
            UserInput = *sumLeft != 0 || *sumForward != 0;
            if (movementAllowed && (IgnoreUserInput || *sumLeft == 0 && *sumForward == 0) && DirectionToDestination(false) is var relDir && relDir != null)
            {
                var dir = relDir.Value.h.ToDirection();
                *sumLeft = dir.X;
                *sumForward = dir.Y;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
    {
        _rmiFlyHook!.OriginalDisposeSafe(self, result);
        try
        {
            // 同 RMIWalkDetour 的說明:昏迷時提早 return,不改動移動狀態。
            if (Service.Condition[ConditionFlag.Unconscious])
            {
                UserInput = false;
                return;
            }

            UserInput = result->Forward != 0 || result->Left != 0 || result->Up != 0;
            // TODO: we really need to introduce some extra checks that PlayerMoveController::readInput does - sometimes it skips reading input, and returning something non-zero breaks stuff...
            if ((IgnoreUserInput || result->Forward == 0 && result->Left == 0 && result->Up == 0) && DirectionToDestination(true) is var relDir && relDir != null)
            {
                var dir = relDir.Value.h.ToDirection();
                result->Forward = dir.Y;
                result->Left = dir.X;
                result->Up = relDir.Value.v.Rad;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private (Angle h, Angle v)? DirectionToDestination(bool allowVertical)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var dist = DesiredPosition - player.Position;
        if (dist.LengthSquared() <= Precision * Precision)
            return null;

        var dirH = Angle.FromDirectionXZ(dist);
        var dirV = allowVertical ? Angle.FromDirection(new(dist.Y, new Vector2(dist.X, dist.Z).Length())) : default;

        Angle refDir;
        var activeCamera = _legacyMode ? TryGetActiveCamera() : null;
        if (activeCamera != null)
        {
            var camDirH = activeCamera->DirH;
            if (DateTime.Now - _lastCameraDebugLog > TimeSpan.FromSeconds(1))
            {
                _lastCameraDebugLog = DateTime.Now;
                Service.Log.Debug($"[diag] legacy-mode Camera.DirH raw={camDirH:F3} rad ({camDirH.Radians().Deg:F1} deg)");
            }
            refDir = camDirH.Radians() + 180.Degrees();
        }
        else
        {
            refDir = player.Rotation.Radians();
        }
        return (dirH - refDir, dirV);
    }

    // CameraManager.GetActiveCamera() is a ClientStructs [MemberFunction], and CameraManager.Instance()
    // just forwards to Control.Instance(), a [StaticAddress]. When either signature stops resolving
    // they *throw* InvalidOperationException (InteropGenerator's ThrowHelper.ThrowNullAddress) instead
    // of returning null - so `CameraManager.Instance() != null` was never a guard against a broken
    // signature. This path is reached from the RMIWalk/RMIFly detours, i.e. it would be a managed
    // exception thrown inside a detour on every single frame. Check the resolved addresses up front
    // and skip the whole camera-reference path instead; legacy mode then falls back to the
    // character's own facing (steering is wrong-ish rather than fatal).
    private static bool CameraApiResolved
        => FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Addresses.Instance.Value != 0
        && CameraManager.Addresses.GetActiveCamera.Value != 0;

    private static FFXIVClientStructs.FFXIV.Client.Game.Camera* TryGetActiveCamera()
    {
        if (!CameraApiResolved)
            return null;
        var mgr = CameraManager.Instance();
        return mgr != null ? mgr->GetActiveCamera() : null;
    }

    // 上一次寫進 log 的 legacy mode 值；null ＝ 還沒印過任何一行。
    private bool? _loggedLegacyMode;

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();
    private void UpdateLegacyMode()
    {
        // UiControlChanged 會對「任何」UI 設定變更觸發，所以這個方法被呼叫得非常頻繁。
        // _legacyMode 必須每次重讀（那是行為），但 log 只在值真的變了時才印：
        // 無條件重印在實機兩天累積了 74,373 行，佔全部 log 的 11.7%（曾同一毫秒印 6 行）。
        _legacyMode = Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;
        if (_loggedLegacyMode == _legacyMode)
            return;
        var firstLegacyModeLog = _loggedLegacyMode is null;
        _loggedLegacyMode = _legacyMode;
        Service.Log.Debug(firstLegacyModeLog
            ? $"Legacy mode is initially {(_legacyMode ? "enabled" : "disabled")}"
            : $"Legacy mode is now {(_legacyMode ? "enabled" : "disabled")}");
    }
}
