using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

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

    // 🔴 上面兩個公開欄位（MovementAllowed / Tolerance）是**使用者的值**。
    // 🔑 真正驅動行為的是下面這兩個唯讀屬性：**租約值 ?? 使用者的值**。
    //    別的外掛改走租約端點之後，放約／逾時就自動還原，不需要任何人記得還。
    // 🔴 讀取端**全部**都必須走這兩個屬性 —— 漏掉任何一個讀取點，
    //    表現就是「租約壓著，但那條路徑照跑」＝壓制部分失效，而且完全靜默。
    public bool EffectiveMovementAllowed => MovementLeases.ResolveMovementAllowed(MovementAllowed);
    public float EffectiveTolerance => MovementLeases.ResolveTolerance(Tolerance);

    // 🔴 台服保險絲:等待客戶端固定路徑(宇宙快線／副本轉場)開始的逾時。
    // 上游用 ConditionFlag.Jumping61 與 Unknown101 判斷「客戶端正在把我搬過去」,那是
    // **國際服客戶端的觀察**;台服對不對得上無法離線證明。若對不上,ClientPath 那一點的
    // proceed 永遠是 false ⇒ 跟隨路徑會**停在出發點一動也不動**,而且完全沒有訊息。
    // ⇒ 假設不成立時的後果從「靜默卡死」變成「繞遠路 + 一行診斷」。
    private static readonly TimeSpan ClientPathWaitTimeout = TimeSpan.FromSeconds(15);
    private DateTime? _clientPathWaitSince;
    private bool _clientPathWaitReported;

    private IDalamudPluginInterface _dalamud;
    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private DateTime _nextJump;

    // 「路徑要飛但沒上坐騎」的診斷節流。這個分支每幀都會走到,無節流地寫 log 就是洗版。
    // Information(不是 Debug)—— 會回報問題的使用者跑 LogLevel 1,盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
    private static readonly TimeSpan NeedMountLogInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastNeedMountLog = DateTime.MinValue;

    private Vector3? posPreviousFrame;

    private int _millisecondsWithNoSignificantMovement = 0;

    /// <summary>
    /// 目前的路徑點序列。<b>不可變</b>：消耗一個點＝換一個新的 <see cref="PathSnapshot"/> 上去，
    /// 從來不就地改動已經發佈出去的那一份。
    /// 🔑 參考型別的指派是原子的 ⇒ 任何讀者拿到的永遠是完整一致的一份，不會是半舊半新。
    /// </summary>
    private sealed class PathSnapshot : IReadOnlyList<Waypoint>
    {
        public static readonly PathSnapshot Empty = new([], 0);

        public readonly Waypoint[] All;
        public readonly int Consumed;

        private PathSnapshot(Waypoint[] all, int consumed)
        {
            All = all;
            Consumed = consumed;
        }

        public static PathSnapshot Create(List<Waypoint> waypoints)
            => waypoints.Count == 0 ? Empty : new([.. waypoints], 0);

        public int Count => All.Length - Consumed;

        /// <summary>剩下的第 <paramref name="index"/> 個路徑點（0 ＝ 隊首）。</summary>
        public Waypoint this[int index] => All[Consumed + index];

        /// <summary>終點（＝剩下的最後一個）。只在 <see cref="Count"/> &gt; 0 時有意義。</summary>
        public Waypoint Last => All[^1];

        /// <summary>吃掉隊首一個點。</summary>
        public PathSnapshot Advance() => Consumed + 1 >= All.Length ? Empty : new(All, Consumed + 1);

        public List<Vector3> Positions()
        {
            var res = new List<Vector3>(Count);
            for (var i = Consumed; i < All.Length; ++i)
                res.Add(All[i].Position);
            return res;
        }

        public IEnumerator<Waypoint> GetEnumerator()
        {
            for (var i = Consumed; i < All.Length; ++i)
                yield return All[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private PathSnapshot _path = PathSnapshot.Empty;

    /// <summary>
    /// 目前剩下的路徑點。<b>回的是一份不可變快照</b>，拿到之後想怎麼走訪都安全，
    /// 也不會因為框架執行緒同時在消耗路徑點而變短。
    /// </summary>
    public IReadOnlyList<Waypoint> Waypoints => Volatile.Read(ref _path);

    /// <summary>還剩幾個路徑點。IPC 的 Path.IsRunning / Path.NumWaypoints 用這個。</summary>
    public int WaypointCount => Volatile.Read(ref _path).Count;

    /// <summary>剩下的路徑點座標。IPC 的 Path.ListWaypoints 用這個。</summary>
    public List<Vector3> WaypointPositions() => Volatile.Read(ref _path).Positions();

    // -- 「路徑在跑卻沒有進展」診斷（見 WatchForStuck）------------------------------
    // 🔑 刻意**不**掛在 Service.Config.StopOnStuck 底下 —— 那個開關預設是 false，
    //    掛上去等於這份診斷對絕大多數使用者永遠不會跑。
    // 兩軸都只在「本來就應該在動」的時候累計，排除條件見 WatchForStuck。
    private static readonly TimeSpan StuckReportThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StuckReportInterval = TimeSpan.FromSeconds(30);
    private const float StuckMoveEpsilon = 1.0f; // 碼；從錨點算起小於這個距離就視為沒有前進
    private DateTime? _noProgressSince;
    private Vector3 _noProgressAnchor;
    private DateTime? _sameHeadSince;
    private Vector3 _sameHeadWaypoint;
    private DateTime _lastStuckReport = DateTime.MinValue;
    // 「路徑在跑但移動被關掉／被租約壓住」是另一種形狀：那種情況角色本來就不該動，
    // 上面兩軸會排除掉它，所以獨立計時、門檻放寬。
    private static readonly TimeSpan SuppressedReportThreshold = TimeSpan.FromSeconds(30);
    private DateTime? _suppressedSince;
    private DateTime _lastSuppressedReport = DateTime.MinValue;

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
        // 每幀掃一次逾時的租約。🔑 不能只靠讀取端順便掃：讀取端只在路徑跑著時才會被走到，
        // 而「被壓著不動」正是最沒有讀取的狀態 ⇒ 逾時訊息會遲到很久甚至永遠不出現。
        MovementLeases.Sweep();

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
        {
            ResetStuckWatch(); // 沒有角色時不累計「沒有進展」，否則登入/切角完成的那一刻會補噴一行
            return;
        }

        // 防護性早退:玩家昏迷(Unconscious)時不驅動移動/鏡頭,也不會走到下面的
        // ExecuteJump() —— 那支是直接呼叫原生的 ActionManager::UseAction,不在
        // OverrideMovement 的守衛範圍內(那裡只蓋 RMIWalk / RMIFly 兩個輸入 hook)。
        // 路徑點刻意不清掉:復活之後自己接著走完,昏迷期間只是「什麼都不做」。
        if (Service.Condition[ConditionFlag.Unconscious])
        {
            _movement.Enabled = _camera.Enabled = false;
            ResetStuckWatch(); // 昏迷期間不動是正常的
            return;
        }

        // 這一幀固定用同一個容許值（迴圈中途被別的執行緒改掉的話，
        // 同一幀內前後幾個路徑點會用不同的判定標準）。
        var tolerance = EffectiveTolerance;

        // 🔑 整幀只取一次路徑快照，之後全程用這份區域變數 —— 下半段的每一個判斷
        //    （剩幾個點、隊首在哪、終點在哪）因此一定描述同一份路徑。
        var started = Volatile.Read(ref _path);
        var path = started;

        while (path.Count > 0)
        {
            var (a, areaId) = path[0];
            var b = player.Position;
            var c = posPreviousFrame ?? b;

            if (DestinationTolerance > 0 && (b - path.Last.Position).Length() <= DestinationTolerance)
            {
                path = PathSnapshot.Empty;
                break;
            }

            // 這一點屬於客戶端固定路徑 ⇒ 不看距離,改看遊戲狀態決定何時前進。
            if (CheckCondition(areaId, out var proceed))
            {
                if (proceed)
                {
                    ResetClientPathWait();
                    path = path.Advance();
                }
                else if (WaitedTooLongForClientPath(areaId))
                {
                    // 保險絲跳脫:放行這一點,退化成一般走路。
                    path = path.Advance();
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

            if (DistanceToLineSegment(a, b, c) > tolerance)
                break;

            path = path.Advance();
        }

        // 路徑點只在上面那個迴圈裡被消耗，所以發佈點放這裡就蓋得到 Update 的所有分支
        //（包含下面 need-mount / 使用者輸入 / OnStuck 三個中途 return）。
        // 🔴 條件式發佈：這一幀進行到一半時，別的執行緒（Path.MoveTo / Path.Stop）可能已經
        //    整份換掉了 _path。那時我們手上這份是舊路徑的後綴，寫回去等於把對方的新路徑吃掉。
        //    CompareExchange 讓「對方後來居上」這件事變成：我們這一幀的消耗作廢、對方的路徑留著。
        if (!ReferenceEquals(path, started))
            Interlocked.CompareExchange(ref _path, path, started);
        WatchForStuck(path, player.Position, tolerance);

        if (path.Count == 0)
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
                    var destination = path.Last.Position;
                    // 這是既有的「卡住就停下」機制(預設關閉)。行為一個字都沒改，只是它以前
                    // 完全不留痕跡 —— 使用者看到的是路徑忽然被清掉、外掛重新規劃一次。
                    Service.Log.Information(
                        $"[vnav卡住診斷] 既有的 StopOnStuck 機制觸發：連續 {_millisecondsWithNoSignificantMovement} 毫秒"
                      + $"的速度低於 {Service.Config.StuckTolerance:f2} 碼/秒(門檻 {Service.Config.StuckTimeoutMs} 毫秒)，"
                      + $"已停止跟隨並清空剩餘的 {path.Count} 個路徑點。目前位置 {player.Position:f1}，終點 {destination:f1}。"
                      + $"⇒ 接下來會不會自動重算路徑，取決於設定裡的「停止後重試」(目前 {(Service.Config.RetryOnStuck ? "開" : "關")})。");
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
            _movement.Enabled = EffectiveMovementAllowed;
            _movement.DesiredPosition = path[0].Position;
            if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving] && !IgnoreDeltaY) //Only do this bit if on a flying path
            {
                // walk->fly transition (TODO: reconsider?)
                if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                    ExecuteJump(); // Spam jump to take off
                else
                {
                    // 🔴 vnavmesh 不會替使用者上坐騎。上游在這裡直接 return,使用者看到的是
                    // 「角色站著不動、完全沒有訊息」—— 而艦隊裡多個消費端(TCToolbox / GatherBuddyReborn /
                    // Saucy)會傳 fly:true 卻不自己召喚坐騎,這條分支因此是實務上最常見的「不會動」來源。
                    // 行為完全不變(仍然不自動上坐騎、仍然停住),只是不再靜默。
                    ReportNeedMount(player.Position.Y, path.Count);
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
        // 🔴 Stop() 也會從 IPC 的 Path.Stop 進來，也就是**呼叫端的執行緒**。一次參考指派就
        //    同時完成「清空」與「對外發佈」，所以 Path.Stop 之後 Path.IsRunning 立刻回 false，
        //    不必等到下一幀。
        Volatile.Write(ref _path, PathSnapshot.Empty);
        ResetStuckWatch();
    }

    // 路徑要飛、但玩家沒上坐騎 ⇒ 停在原地。這個方法每幀都會被呼叫,所以照本 repo 既有慣例
    //(OverrideMovement.OnDetourError)用時間戳節流,不引入新相依。
    private void ReportNeedMount(float currentY, int remaining)
    {
        var now = DateTime.UtcNow;
        if (now - _lastNeedMountLog < NeedMountLogInterval)
            return;
        _lastNeedMountLog = now;
        Service.Log.Information(
            $"[FollowPath] 這是一條飛行路徑,下一個路徑點比你高 {_movement.DesiredPosition.Y - currentY:f1} 公尺," +
            $"但你沒有騎乘坐騎,所以停在原地不動(剩餘路徑點 {remaining} 個)。" +
            $"⇒ vnavmesh 不會自動幫你上坐騎:請自己召喚坐騎起飛,移動就會繼續。");
    }

    // 「角色現在不該動」是正常的那些狀態。卡住偵測在這些狀態下不累計。
    private static bool InTransition =>
        Service.Condition[ConditionFlag.BetweenAreas]
        || Service.Condition[ConditionFlag.BetweenAreas51]
        || Service.Condition[ConditionFlag.WatchingCutscene]
        || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        || Service.Condition[ConditionFlag.Casting]
        || Service.Condition[ConditionFlag.Jumping61]
        || Service.Condition[ConditionFlag.Unknown101];

    private void ResetStuckWatch()
    {
        _noProgressSince = null;
        _sameHeadSince = null;
        _suppressedSince = null;
        _lastStuckReport = DateTime.MinValue;
        _lastSuppressedReport = DateTime.MinValue;
    }

    /// <summary>
    /// 路徑卡住偵測。每幀由框架執行緒呼叫（見 <see cref="Update"/>，在消耗路徑點的迴圈之後）。
    /// 🔴 <b>純觀測</b>：偵測到也不中止、不重試、不改路徑，只寫一行 Information。
    /// </summary>
    private void WatchForStuck(PathSnapshot path, Vector3 playerPos, float tolerance)
    {
        if (path.Count == 0)
        {
            ResetStuckWatch();
            return;
        }

        var now = DateTime.Now;

        // (a) 移動被關掉／被租約壓住：角色本來就不該動，所以不算「卡住」。
        //     但這是「路徑照算、Path.IsRunning 回 true、角色站著不動、log 零字」的頭號來源，
        //     所以獨立計時、獨立記一行，把「是誰壓著」寫出來。
        if (!EffectiveMovementAllowed)
        {
            _noProgressSince = null;
            _sameHeadSince = null;
            _suppressedSince ??= now;
            var suppressed = now - _suppressedSince.Value;
            if (suppressed >= SuppressedReportThreshold && now - _lastSuppressedReport >= StuckReportInterval)
            {
                _lastSuppressedReport = now;
                ReportSuppressed(path, suppressed, playerPos);
            }
            return;
        }
        _suppressedSince = null;
        _lastSuppressedReport = DateTime.MinValue;

        // (b) 正在等客戶端把角色搬過去(傳送台／宇宙快線／副本轉場)，或正在切區域／過場動畫：
        //     這些狀態下不動是正常的。等客戶端固定路徑的逾時另有自己的保險絲與診斷
        //     (WaitedTooLongForClientPath)，這裡不重複計時。
        if (CheckCondition(path[0].Type, out _) || InTransition)
        {
            _noProgressSince = null;
            _sameHeadSince = null;
            return;
        }

        // ① 位置停滯：離錨點超過 StuckMoveEpsilon 就重新錨定並重新計時。
        //    刻意用「離錨點的距離」而不是「每幀速度」—— 每幀速度對正常走路也會頻繁掉到 0
        //    (轉向、微調)，用它當判準會一直誤報。
        if (_noProgressSince == null || Vector3.Distance(playerPos, _noProgressAnchor) > StuckMoveEpsilon)
        {
            _noProgressSince = now;
            _noProgressAnchor = playerPos;
        }

        // ② 隊首停滯：隊首路徑點換了就重新計時。
        var head = path[0].Position;
        if (_sameHeadSince == null || _sameHeadWaypoint != head)
        {
            _sameHeadSince = now;
            _sameHeadWaypoint = head;
        }

        var noProgress = now - _noProgressSince.Value;
        var sameHead = now - _sameHeadSince.Value;
        if (noProgress < StuckReportThreshold && sameHead < StuckReportThreshold)
            return;
        if (now - _lastStuckReport < StuckReportInterval)
            return; // 節流：卡住是持續狀態，每幀印一行就是洗版
        _lastStuckReport = now;
        ReportStuck(path, noProgress, sameHead, playerPos, head, tolerance);
    }

    private void ReportStuck(PathSnapshot path, TimeSpan noProgress, TimeSpan sameHead, Vector3 playerPos, Vector3 head, float tolerance)
    {
        var dest = path.Last.Position;
        var progress = _manager.LoadTaskProgress;
        var meshStr = _manager.Navmesh != null ? "已載入" : "未載入";
        var progressStr = progress < 0 ? "未在建置" : $"建置中 {progress * 100:f0}%";
        Service.Log.Information(
            $"[vnav卡住診斷] 路徑在跑但沒有進展：位置停滯 {noProgress.TotalSeconds:f0} 秒、"
          + $"隊首路徑點停滯 {sameHead.TotalSeconds:f0} 秒。目前位置 {playerPos:f1}，"
          + $"下一個路徑點 {head:f1}(距離 {Vector3.Distance(playerPos, head):f1} 碼)，"
          + $"終點 {dest:f1}(距離 {Vector3.Distance(playerPos, dest):f1} 碼)，剩餘 {path.Count} 個路徑點。"
          + $"容許值={tolerance:f2}(使用者值 {Tolerance:f2})，移動開關={EffectiveMovementAllowed}(使用者值 {MovementAllowed})，"
          + $"飛行路徑={!IgnoreDeltaY}，移動覆寫已啟用={_movement.Enabled}，偵測到使用者輸入={_movement.UserInput}，"
          + $"導航網格={meshStr}({progressStr})；騎乘={Service.Condition[ConditionFlag.Mounted]}、"
          + $"飛行中={Service.Condition[ConditionFlag.InFlight]}、潛水={Service.Condition[ConditionFlag.Diving]}、"
          + $"詠唱中={Service.Condition[ConditionFlag.Casting]}、"
          + $"區域切換={Service.Condition[ConditionFlag.BetweenAreas]}/{Service.Condition[ConditionFlag.BetweenAreas51]}。"
          + $"⇒ 這一行只是診斷，vnavmesh 沒有因此中止、重試或改變任何路徑。");
    }

    private void ReportSuppressed(PathSnapshot path, TimeSpan suppressed, Vector3 playerPos)
    {
        var dest = path.Last.Position;
        var leases = MovementLeases.Snapshot();
        var who = leases.Length == 0
            ? "目前沒有任何租約 ⇒ 是使用者自己在「Navmesh manager」分頁取消了 Allow movement，或有外掛用舊端點 Path.SetMovementAllowed(false) 寫死了它"
            : string.Join("、", leases.Select(l => $"{l.Owner}(剩 {l.RemainingMs / 1000} 秒{(l.MovementAllowed == false ? "，要求不准動" : "")})"));
        Service.Log.Information(
            $"[vnav卡住診斷] 路徑已經排定 {suppressed.TotalSeconds:f0} 秒，但移動開關是關著的，所以角色不會動："
          + $"實際值={EffectiveMovementAllowed}，使用者值={MovementAllowed}；租約狀況：{who}。"
          + $"目前位置 {playerPos:f1}，終點 {dest:f1}，剩餘 {path.Count} 個路徑點。"
          + $"⇒ 這不是尋路壞掉：路徑算好了、Path.IsRunning 也回 true，純粹是移動被壓住。");
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
        ResetStuckWatch();

        // 🔑 Create 會把呼叫端的 List **抄成自己的陣列**，之後誰都不再碰那個 List：
        //    呼叫端事後繼續改它不影響我們，框架執行緒消耗路徑點也不會回頭動到它。
        //    (Move 會從 IPC 的 Path.MoveTo 進來，也就是呼叫端的執行緒。)
        // 🔑 一次參考指派就同時完成「換上新路徑」與「對外發佈」，中間沒有任何可觀察的中間態。
        Volatile.Write(ref _path, PathSnapshot.Create(waypoints));
        IgnoreDeltaY = ignoreDeltaY;
        DestinationTolerance = destTolerance;
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        UpdateSharedState(false);
        Volatile.Write(ref _path, PathSnapshot.Empty);
        ResetStuckWatch();
    }
}
