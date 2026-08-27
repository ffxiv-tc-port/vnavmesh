using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Movement;

// 路徑點 + 它所屬的連結種類。Type 決定走到這一點時要不要停下來等客戶端把固定路徑播完。
// 無參數種類的建構子沿用 AreaId.Default(= 全部位元),CheckCondition 對它一律回 false,
// 也就是「照一般走路處理」—— 從 IPC 進來的舊式 List<Vector3> 就是走這條。
public readonly record struct Waypoint(Vector3 Position, Navmesh.AreaId Type)
{
    public Waypoint(Vector3 Position) : this(Position, Navmesh.AreaId.Default) { }
}

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool IgnoreDeltaY = false;
    public float Tolerance = 0.25f;
    public float DestinationTolerance = 0;
    public List<Waypoint> Waypoints = [];

    // 🔴 台服保險絲:等待客戶端固定路徑(宇宙快線／副本轉場)開始的逾時。
    // 上游用 ConditionFlag.Jumping61 與 Unknown101 判斷「客戶端正在把我搬過去」,那是
    // **國際服客戶端的觀察**;台服對不對得上無法離線證明。若對不上,ClientPath 那一點的
    // proceed 永遠是 false ⇒ 跟隨路徑會**停在出發點一動也不動**,而且完全沒有訊息。
    // 這裡的處置:等超過門檻就記一次 Information(含當下真正亮著的 ConditionFlag 清單,
    // 使用者回報的 log 因此直接告訴我們台服到底是哪個旗標)並放行,退化成一般走路。
    // ⇒ 假設不成立時的後果從「靜默卡死」變成「繞遠路 + 一行診斷」。
    private static readonly TimeSpan ClientPathWaitTimeout = TimeSpan.FromSeconds(15);
    private DateTime? _clientPathWaitSince;
    private bool _clientPathWaitReported;

    private IDalamudPluginInterface _dalamud;
    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private DateTime _nextJump;

    private Vector3? posPreviousFrame;

    private int _millisecondsWithNoSignificantMovement = 0;

    public event Action<Vector3, bool, float>? OnStuck;

    // entries in dalamud shared data cache must be reference types, so we use an array
    private readonly bool[] _sharedPathIsRunning;

    private const string _sharedPathTag = "vnav.PathIsRunning";

    public FollowPath(IDalamudPluginInterface dalamud, NavmeshManager manager)
    {
        _dalamud = dalamud;
        _sharedPathIsRunning = _dalamud.GetOrCreateData<bool[]>(_sharedPathTag, () => [false]);
        _manager = manager;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
        OnNavmeshChanged(_manager.Navmesh, _manager.Query);
        Service.ClientState.Login += OnLogin;
    }

    public void Dispose()
    {
        UpdateSharedState(false);
        _dalamud.RelinquishData(_sharedPathTag);
        _manager.OnNavmeshChanged -= OnNavmeshChanged;
        Service.ClientState.Login -= OnLogin;
        _camera.Dispose();
        _movement.Dispose();
    }

    // A path left over from before a relog/character-switch (e.g. interrupted mid-navigation)
    // otherwise survives Update()'s `player == null` early-return during the login transition
    // and resumes immediately toward the stale destination the instant the new character's
    // LocalPlayer becomes valid - fighting the player's own input (including jump) right at
    // login. Stop() also disables the movement/camera overrides, not just clearing Waypoints.
    private void OnLogin()
    {
        Stop();
        _movement.Enabled = _camera.Enabled = false;
    }

    private void UpdateSharedState(bool isRunning) => _sharedPathIsRunning[0] = isRunning;

    public void Update(IFramework fwk)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        while (Waypoints.Count > 0)
        {
            var (a, areaId) = Waypoints[0];
            var b = player.Position;
            var c = posPreviousFrame ?? b;

            if (DestinationTolerance > 0 && (b - Waypoints[^1].Position).Length() <= DestinationTolerance)
            {
                Waypoints.Clear();
                break;
            }

            // 這一點屬於客戶端固定路徑 ⇒ 不看距離,改看遊戲狀態決定何時前進。
            if (CheckCondition(areaId, out var proceed))
            {
                if (proceed)
                {
                    ResetClientPathWait();
                    Waypoints.RemoveAt(0);
                }
                else if (WaitedTooLongForClientPath(areaId))
                {
                    // 保險絲跳脫:放行這一點,退化成一般走路。
                    Waypoints.RemoveAt(0);
                    continue;
                }

                break;
            }
            ResetClientPathWait();

            if (IgnoreDeltaY)
            {
                a.Y = 0;
                b.Y = 0;
                c.Y = 0;
            }

            if (DistanceToLineSegment(a, b, c) > Tolerance)
                break;

            Waypoints.RemoveAt(0);
        }


        if (Waypoints.Count == 0)
        {
            posPreviousFrame = player.Position;
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            UpdateSharedState(false);
        }
        else
        {
            if (Service.Config.StopOnStuck && posPreviousFrame.HasValue)
            {
                float delta = fwk.UpdateDelta.Milliseconds / 1000f;
                float distance = Vector3.Distance(player.Position, posPreviousFrame.Value) / delta;
                if (distance <= Service.Config.StuckTolerance)
                {
                    _millisecondsWithNoSignificantMovement += fwk.UpdateDelta.Milliseconds;
                }
                else
                {
                    _millisecondsWithNoSignificantMovement = 0;
                }

                if (_millisecondsWithNoSignificantMovement >= Service.Config.StuckTimeoutMs)
                {
                    var destination = Waypoints[^1].Position;
                    Stop();
                    OnStuck?.Invoke(destination, !IgnoreDeltaY, DestinationTolerance);
                    return;
                }
            }

            posPreviousFrame = player.Position;

            if (Service.Config.CancelMoveOnUserInput && _movement.UserInput)
            {
                Stop();
                return;
            }

            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = Waypoints[0].Position;
            if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving] && !IgnoreDeltaY) //Only do this bit if on a flying path
            {
                // walk->fly transition (TODO: reconsider?)
                if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                    ExecuteJump(); // Spam jump to take off
                else
                {
                    _movement.Enabled = false; // Don't move, since it'll just run on the spot
                    return;
                }
            }

            _camera.Enabled = Service.Config.AlignCameraToMovement;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
            _camera.DesiredAltitude = Service.Config.AlignCameraHeight.Degrees();
        }
    }

    // 回傳「這一點要不要交給遊戲狀態決定」;true 代表本點屬於客戶端固定路徑,
    // 此時 proceed 才是「現在可以前進了嗎」。false 代表照一般走路的距離判斷處理。
    private static bool CheckCondition(Navmesh.AreaId areaId, out bool proceed)
    {
        proceed = false;

        switch (areaId)
        {
            case Navmesh.AreaId.Warp:
                // TODO: 以太之光傳送尚未實作
                return false;
            case Navmesh.AreaId.ClientPath:
                // 61 是多數副本的 clientpath,101 是宇宙快線
                proceed = Service.Condition.Any(ConditionFlag.Jumping61, ConditionFlag.Unknown101);
                return true;
            case Navmesh.AreaId.ClientPathEnd:
                // 這裡也要判:否則「連續兩段 clientpath」的路徑會提早結束(宇宙探索很常見)
                proceed = !Service.Condition.Any(ConditionFlag.Jumping61, ConditionFlag.Unknown101);
                return true;
            default:
                return false;
        }
    }

    private void ResetClientPathWait()
    {
        _clientPathWaitSince = null;
        _clientPathWaitReported = false;
    }

    // 見 ClientPathWaitTimeout 的說明:等太久就放行,並把當下真正亮著的 ConditionFlag 印出來。
    // ⚠️ 只對 ClientPath(出發點)計時。ClientPathEnd 的等待長度是「這趟纜車開多久」,
    //    沒有合理的上限;而且旗標判斷若整個對不上,ClientPathEnd 的 !Any(...) 會立刻成立,
    //    根本不會卡住 —— 會卡死的只有出發點這一側。
    private bool WaitedTooLongForClientPath(Navmesh.AreaId areaId)
    {
        if (areaId != Navmesh.AreaId.ClientPath)
            return false;

        var now = DateTime.Now;
        _clientPathWaitSince ??= now;
        if (now - _clientPathWaitSince.Value < ClientPathWaitTimeout)
            return false;

        if (!_clientPathWaitReported)
        {
            _clientPathWaitReported = true;
            var active = Enum.GetValues<ConditionFlag>().Distinct().Where(f => Service.Condition[f]).ToList();
            Service.Log.Information(
                $"[FollowPath] 等待客戶端固定路徑開始已超過 {ClientPathWaitTimeout.TotalSeconds:f0} 秒仍未觸發," +
                $"放行該路徑點並退回一般走路。目前亮著的 ConditionFlag:" +
                $"{(active.Count == 0 ? "(無)" : string.Join("、", active.Select(f => $"{f}({(int)f})")))}。" +
                $"⇒ 若台服的宇宙快線/副本轉場旗標不是 Jumping61(61) 或 Unknown101(101),正確值就在這份清單裡。");
        }
        ResetClientPathWait();
        return true;
    }

    private static float DistanceToLineSegment(Vector3 v, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var av = v - a;

        if (ab.Length() == 0 || Vector3.Dot(av, ab) <= 0)
            return av.Length();

        var bv = v - b;
        if (Vector3.Dot(bv, ab) >= 0)
            return bv.Length();

        return Vector3.Cross(ab, av).Length() / ab.Length();
    }

    public void Stop()
    {
        UpdateSharedState(false);
        _millisecondsWithNoSignificantMovement = 0;
        Waypoints.Clear();
    }

    private unsafe void ExecuteJump()
    {
        // Unable to jump while diving, prevents spamming error messages.
        if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving])
            return;

        if (DateTime.Now >= _nextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            _nextJump = DateTime.Now.AddMilliseconds(100);
        }
    }

    // 舊式入口:整條路徑都當成一般走路點(AreaId.Default)。
    // 🔴 IPC 的 Path.MoveTo 一直是 List<Vector3>,保留這個多載才不會讓既有消費端斷掉。
    public void Move(List<Vector3> waypoints, bool ignoreDeltaY, float destTolerance = 0)
        => Move(waypoints.Select(w => new Waypoint(w)).ToList(), ignoreDeltaY, destTolerance);

    public void Move(List<Waypoint> waypoints, bool ignoreDeltaY, float destTolerance = 0)
    {
        UpdateSharedState(true);
        ResetClientPathWait();
        Waypoints = waypoints;
        IgnoreDeltaY = ignoreDeltaY;
        DestinationTolerance = destTolerance;
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        UpdateSharedState(false);
        Waypoints.Clear();
    }
}
