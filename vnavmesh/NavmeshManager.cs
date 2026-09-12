using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public sealed class NavmeshManager : IDisposable
{
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;

    public string CurrentKey { get; private set; } = ""; // unique string representing currently loaded navmesh

    /// <summary>
    /// 同一代的導航網格與查詢物件，<b>永遠成對</b>。
    /// <para>
    /// 🔴 為什麼要成對：<c>Navmesh</c> 與 <c>Query</c> 原本是兩個獨立的自動屬性，寫入端是框架
    ///    執行緒（載入完成 / <see cref="ClearState"/>），而讀取端有好幾個跑在<b>別條執行緒</b>上
    ///    （IPC 端點跑在呼叫端的執行緒、尋路的 body 跑在執行緒池）。分兩次讀的程式碼因此會踩到
    ///    兩種形狀：
    ///    <list type="number">
    ///    <item><b>檢查與使用之間被清掉</b>：<c>if (Query == null) throw ...;</c> 之後那句
    ///          <c>Query.PathfindMesh(...)</c> 是<b>第二次</b>讀 —— 中間被 ClearState 清成 null
    ///          就會擲 NullReferenceException，而不是原本要給呼叫端的那句說明。</item>
    ///    <item><b>混到兩代</b>：<c>BuildBitmap</c> 讀 <c>Navmesh</c> 與 <c>Query</c> 各數次，
    ///          切區域時可能拿到「舊網格 + 新查詢物件」。那時 Query 算出來的 poly ref 會被拿去
    ///          索引舊網格的 <c>m_tiles</c>（<c>GetTileAndPolyByRefUnsafe</c> 不驗 salt、不驗界線），
    ///          結果是 IndexOutOfRange / NullReference，而不是一個看得懂的錯誤。</item>
    ///    </list>
    /// </para>
    /// <para>
    /// 🔑 參考型別的指派是原子的 ⇒ 讀到的永遠是完整的一代，不會是半舊半新。
    ///    <c>Volatile</c> 只是讓「寫入端已經寫了、讀取端還看得到舊值」這件事在記憶體模型上也被排除；
    ///    x64 上它不產生任何指令，<b>零執行期成本、零每幀新增工作</b>。
    /// </para>
    /// </summary>
    public sealed class MeshGeneration(Navmesh? mesh, NavmeshQuery? query)
    {
        public readonly Navmesh? Mesh = mesh;
        public readonly NavmeshQuery? Query = query;
    }

    private static readonly MeshGeneration NoMesh = new(null, null);
    private MeshGeneration _generation = NoMesh;

    /// <summary>一次取得同一代的網格與查詢物件。<b>同一個流程裡要用到兩者時一律走這支</b>，不要分兩次讀下面兩個屬性。</summary>
    public MeshGeneration Current => Volatile.Read(ref _generation);

    public Navmesh? Navmesh => Current.Mesh;
    public NavmeshQuery? Query => Current.Query;
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;

    /// <summary>
    /// 發佈新的一代，然後才通知訂閱者。
    /// ⚠️ 順序刻意是「先發佈再通知」：載入路徑本來就是這個順序，ClearState 以前是反過來的
    ///    （先 Invoke 再把欄位清成 null）。兩邊統一之後，訂閱者在回呼裡回頭問 manager
    ///    看到的一定是通知所描述的那一代。目前唯一的訂閱者 FollowPath.OnNavmeshChanged
    ///    只清路徑點、不回頭讀 manager，所以這次統一對現有行為零影響。
    /// </summary>
    private void PublishMesh(Navmesh? mesh, NavmeshQuery? query)
    {
        Volatile.Write(ref _generation, mesh == null && query == null ? NoMesh : new MeshGeneration(mesh, query));
        OnNavmeshChanged?.Invoke(mesh, query);
    }

    // 建置進度被「N 條建置執行緒」寫、被框架／繪製／IPC 呼叫端讀。
    // 舊宣告是 volatile float，那擋得住可見性問題，但擋不住下面這個：
    // NavmeshBuilder.BuildTiles 的 onTileFinished 回呼是在**平行的 Task.Run 裡**呼叫的
    // (同時最多 Service.Config.BuildMaxCores 條，預設＝ProcessorCount)，而收到回呼時做的
    // `_loadTaskProgress += deltaProgress` 是**讀-改-寫**。volatile 不會讓 += 變成原子操作
    // ⇒ 兩條執行緒同時遞增時其中一次會被整個覆蓋掉(lost update)。
    // 失敗形式是靜默的**少算**：進度條走到某個百分比就不再前進、永遠到不了 99%，
    // 而網格其實建好了。使用者看到的是「卡住」，log 裡什麼都沒有。
    // 一起受影響的是 Nav.BuildProgress 這個 IPC 端點與 DTR 上的百分比。
    // float 沒有 Interlocked.Increment/Add，但有 Interlocked.CompareExchange(ref float,...)
    // ⇒ 用 CAS 迴圈累加(見 AddLoadProgress)。
    // 刻意**不**標 volatile：CompareExchange 吃的是 ref，對 volatile 欄位取 ref 會觸發 CS0420
    // (「對 volatile 欄位的參考不會被視為 volatile」)。整個類別統一採「欄位是普通的、
    // 每個存取點自己講清楚」這一種形狀，與上面的 _generation 一致。
    private float _loadTaskProgress = -1;

    // negative if load task is not running, otherwise in [0, 1] range
    public float LoadTaskProgress => Volatile.Read(ref _loadTaskProgress);

    /// <summary>
    /// 把建置進度往上加。<b>會從多條建置執行緒同時被呼叫</b>，所以走 CAS 迴圈而不是 +=。
    /// </summary>
    private void AddLoadProgress(float delta)
    {
        // 讀到的舊值是別條執行緒剛寫的也沒關係：CompareExchange 回傳「比較當時的實際值」，
        // 與預期不符就代表有人插隊，重跑一次即可。競爭者最多 BuildMaxCores 條，而且
        // **每塊 tile 才呼叫一次(不是每幀)**，所以這個迴圈實際上幾乎不會轉第二圈。
        float old, updated;
        do
        {
            old = Volatile.Read(ref _loadTaskProgress);
            updated = old + delta;
        }
        while (Interlocked.CompareExchange(ref _loadTaskProgress, updated, old) != old);
    }

    private CancellationTokenSource? _currentCTS; // this is signalled when mesh is unloaded, all pathfinding tasks that use it are then cancelled

    // 兩個 CTS 分工，不可合併成一個：
    //   _currentCTS  ＝「網格生命週期」。ClearState/Reload 用它，**載入工作本身也綁在它上面**。
    //   _pathfindCTS ＝「尋路批次」。CancelAllPathfinds 只取消這一個。
    // 🔴 為什麼一定要分開：載入工作與尋路工作原本共用同一個 token，所以「取消全部尋路」若
    //    直接取消 _currentCTS，會**連進行中的網格載入一起殺掉**，而且沒有任何東西會把它
    //    重新啟動 ⇒ 網格永遠載不起來。分成兩個之後，取消尋路對載入零影響。
    // 尋路工作同時連結兩者，所以「網格被卸掉時尋路也要一起取消」的原有語意完全保留。
    private CancellationTokenSource _pathfindCTS = new();
    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    // _numActivePathfinds 被三種執行緒碰，所以讀寫兩側都要明確同步：
    //   遞增 ＝ QueryPath 裡**同步**做(見該處說明) ⇒ 跑在**呼叫端的執行緒**上：
    //          IPC 的 Nav.Pathfind / PathfindWithTolerance / PathfindAvoid /
    //          PathfindCancelable 都跑在對方的執行緒上(Dalamud 的 IPC 不會替你切到
    //          框架執行緒)，而 FollowPath 那條路徑走的是框架執行緒。
    //   遞減 ＝ 掛在工作的完成回呼上(ExecuteSynchronously + TaskScheduler.Default)
    //          ⇒ 跑在**讓那個工作完成的那條執行緒**上，一般是執行緒池。
    //   讀取 ＝ IPC 的 Nav.PathfindInProgress / Nav.PathfindNumQueued(呼叫端執行緒)、
    //          DTRProvider 與 ReportPathfindStall(框架執行緒)、除錯視窗(繪製執行緒)。
    // 寫入端本來就用 Interlocked，缺的是**讀取端**：裸欄位讀取允許 JIT 把值提到迴圈外
    // 或重用暫存器，於是「尋路早就結束了，輪詢的呼叫端還一直看到 true」在記憶體模型上
    // 是合法的。int 的讀取本身是原子的，所以正解是 Volatile.Read —— 它在 x64 不產生任何
    // 額外指令，**零執行期成本、零每幀新增工作**，只是把重排序排除掉。
    // 刻意**不**把欄位標成 volatile，理由與 _loadTaskProgress 同一條(CS0420)。
    // 讀取點跑在呼叫端執行緒上也**不套任何閘門**：這是純量原子讀，而 Nav.PathfindInProgress
    // 是會被輪詢的布林端點 —— 對它套會阻塞的閘門本身就是紅線。
    private int _numActivePathfinds;

    public bool PathfindInProgress => Volatile.Read(ref _numActivePathfinds) > 0;

    /// <summary>
    /// 進行中以外、還在排隊的尋路筆數。
    /// <para>
    /// 這裡**只讀一次**欄位。舊碼是
    /// <c>_numActivePathfinds &gt; 0 ? _numActivePathfinds - 1 : 0</c>，那是**兩次**讀取：
    /// 第一次讀到 1(通過 &gt; 0 的判斷)、遞減在兩次讀取之間發生、第二次讀到 0
    /// ⇒ 這個屬性會回傳 <b>-1</b>。它會被直接串進 DTR 的文字(變成 "+-1")與 IPC 的
    /// Nav.PathfindNumQueued，而呼叫端普遍拿它跟 0 比大小。失敗形式是靜默的怪數字，
    /// 不是例外。讀進區域變數之後這個窗口就不存在了。
    /// </para>
    /// </summary>
    public int NumQueuedPathfindRequests
    {
        get
        {
            var active = Volatile.Read(ref _numActivePathfinds);
            return active > 0 ? active - 1 : 0;
        }
    }

    private DirectoryInfo _cacheDir;

    // 🔴 IPC 全量重建的節流狀態。詳見 RebuildFromIPC 的說明。
    public static readonly TimeSpan IPCRebuildMinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IPCRebuildHardCap = TimeSpan.FromMinutes(5);
    private DateTime _lastIPCRebuild = DateTime.MinValue;
    private DateTime _lastIPCRebuildSkipLog = DateTime.MinValue;
    private int _ipcRebuildSkipCount;

    // 只節流「說明訊息」，不節流取消動作本身。詳見 CancelAllPathfinds。
    private static readonly TimeSpan CancelAllLogMinInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastCancelAllLog = DateTime.MinValue;

    // 🔴🔴 _lastLoadQueryTask 的「讀舊值 → 串上去 → 寫回新值」是**讀-改-寫**，而三個
    //    ExecuteWhenIdle 多載全部會從**呼叫端的執行緒**被踩到：IPC 的 Nav.Pathfind /
    //    PathfindWithTolerance / PathfindAvoid / PathfindCancelable / PathfindCancelAll /
    //    Nav.Rebuild / Nav.Reload / Nav.BuildBitmap* 的實作都跑在呼叫端那條執行緒上
    //    (Dalamud 的 IPC 不會替你切到框架執行緒)，而 Update() / Reload() / ClearState()
    //    跑在框架執行緒。零同步時兩條執行緒可以讀到**同一個 prev**、各自把自己的工作串在
    //    它後面 ⇒ 兩個工作並行。
    // 🔑 這個欄位自己的註解寫著 we limit the concurrency to max 1 running task ——
    //    那個不變式在 IPC 路徑上其實從來沒有被強制過。
    // 🔴 失敗形式**不是** AVE，所以它可以長期存在而沒被發現：兩筆尋路同時用同一個
    //    NavmeshQuery(內部的 DtNavMeshQuery 帶節點池等可變狀態，不是執行緒安全的)，
    //    表現是「算出來的路徑是錯的」或 DotRecast 內部擲例外，不是遊戲崩潰。
    // 🔑 鎖只蓋「取舊值＋排程＋寫回」三件事。Service.Framework.Run 在本 pin 一律是
    //    FrameworkThreadTaskFactory.StartNew(...)(Dalamud/Game/Framework.cs:135-163)，
    //    **永遠只把工作排進佇列、不會就地執行 delegate**，所以持鎖期間不碰 ImGui、
    //    不做檔案 I/O、也不會重入這把鎖。
    private readonly object _taskChainLock = new();

    // -- 「尋路送出後遲遲沒有結果」診斷（見 ReportPathfindStall）-------------------
    // 🔴 純觀測：不取消、不重試、不動網格。
    private static readonly TimeSpan PathfindStallThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PathfindStallReportInterval = TimeSpan.FromSeconds(30);
    private DateTime? _pathfindBusySince;
    private DateTime _lastPathfindStallReport = DateTime.MinValue;

    public unsafe NavmeshManager(DirectoryInfo cacheDir)
    {
        _cacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists

        // prepare a task with correct task scheduler that other tasks can be chained off
        _lastLoadQueryTask = Service.Framework.Run(() => Log("Tasks kicked off"));
    }

    public void Dispose()
    {
        Log("Disposing");
        ClearState();
    }

    public void Update()
    {
        CosmicProgress.Update(); // 主執行緒；見 CosmicProgress 的執行緒約定
        ReportPathfindStall();

        var curKey = GetCurrentKey();
        if (curKey != CurrentKey)
        {
            // navmesh needs to be reloaded
            if (!Service.Config.AutoLoadNavmesh)
            {
                if (CurrentKey.Length == 0)
                    return; // nothing is loaded, and auto-load is forbidden
                curKey = ""; // just unload existing mesh
            }
            Log($"Starting transition from '{CurrentKey}' to '{curKey}'");
            CurrentKey = curKey;
            Reload(true);
            // mesh load is now in progress
        }
    }

    public bool Reload(bool allowLoadFromCache)
    {
        ClearState();
        if (CurrentKey.Length > 0)
        {
            var cts = _currentCTS = new();
            ExecuteWhenIdle(async cancel =>
            {
                Volatile.Write(ref _loadTaskProgress, 0f);

                using var resetLoadProgress = new OnDispose(() => Volatile.Write(ref _loadTaskProgress, -1f));

                var waitStart = DateTime.Now;

                while (InCutscene)
                {
                    if ((DateTime.Now - waitStart).TotalSeconds >= 5)
                    {
                        waitStart = DateTime.Now;
                        Log("waiting for cutscene");
                    }
                    await Service.Framework.DelayTicks(1, cancel);
                }

                var (cacheKey, scene) = await Service.Framework.Run(() =>
                {
                    var scene = new SceneDefinition();
                    scene.FillFromActiveLayout();
                    var cacheKey = GetCacheKey(scene);
                    return (cacheKey, scene);
                }, cancel);

                if (cacheKey.Length == 0)
                {
                    // GetCacheKey() only returns an empty string when the layout was unavailable, i.e. it
                    // vanished while this build was queued behind another task or waiting out a cutscene.
                    // Abort rather than build an empty mesh and persist it under a junk cache name, and
                    // re-arm CurrentKey so Update() kicks off a fresh transition once a layout is back.
                    Service.Log.Information($"[NavmeshManager] Layout unavailable when starting build for '{CurrentKey}'; aborting build, will retry once a layout is loaded");
                    CurrentKey = "";
                    return;
                }

                Log($"Kicking off build for '{cacheKey}' (reload={allowLoadFromCache})");
                var navmesh = await Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache, cancel), cancel);
                Log($"Mesh loaded: '{cacheKey}'");
                PublishMesh(navmesh, new(navmesh));
            }, cts.Token);
        }
        return true;
    }

    /// <summary>
    /// 🔴 給 IPC 的 Nav.Rebuild 專用入口：帶最小間隔節流的全量重建（不吃快取）。
    ///
    /// 為什麼要節流：Nav.Rebuild 走的是 Reload(allowLoadFromCache: false)，也就是
    /// BuildNavmesh 裡「跳過 cache.Exists 分支、整個區域逐 tile 重建」的路徑（一個 256 tile
    /// 的區域實測約 1.2 秒，大區更久）。重建期間 Navmesh/Query 會被 ClearState 清成 null，
    /// 玩家因此不會移動 —— 而呼叫端普遍是「偵測到卡住就重建」的形狀，於是：
    ///   卡住 → 要求重建 → 重建期間動不了 → 還是卡住 → 再要求重建 …… 自我維持。
    /// AutoDuty 的實機 log（2026-08-31 20:50 前後）就出現過連續 128 次全量重建，
    /// 每次都印一輪 Queueing state clear / Kicking off build。IPCProvider 上列了 7 個
    /// 呼叫端，所以節流放在 vnavmesh 這端一次保護全部，比逐一去修呼叫端可靠。
    ///
    /// 🔴 刻意只擋 IPC 這條路：使用者自己按 UI 的「Rebuild」或打 /vnav rebuild 走的是
    ///    Reload(false)，語意就是「我現在就要重建」，不受此限、也不更新這裡的時間戳。
    /// 🔴 IPCRebuildHardCap 是安全閥：萬一建置進度旗標因為任何理由卡住不歸位，
    ///    超過這個時間一律放行，免得這個節流本身變成「永遠不能重建」的新故障。
    /// </summary>
    /// <returns>true 表示這次真的送出重建；false 表示被節流略過。</returns>
    public bool RebuildFromIPC()
    {
        var now = DateTime.Now;
        var since = now - _lastIPCRebuild;
        var buildInProgress = Volatile.Read(ref _loadTaskProgress) >= 0;

        if ((since < IPCRebuildMinInterval || buildInProgress) && since < IPCRebuildHardCap)
        {
            // 這支跑在 IPC 呼叫端的執行緒上(Nav.Rebuild)，兩個外掛同時打就是兩條執行緒。
            // ++ 是讀-改-寫 ⇒ 裸寫會少算。這只是診斷用的次數，但少算沒有任何好處。
            var skipped = Interlocked.Increment(ref _ipcRebuildSkipCount);
            // 診斷寫 Information（使用者跑 LogLevel 1），但節流到最多每 5 秒一行，
            // 免得呼叫端每秒打一次就把 log 洗掉。
            if ((now - _lastIPCRebuildSkipLog).TotalSeconds >= 5)
            {
                _lastIPCRebuildSkipLog = now;
                Service.Log.Information(
                    $"[NavmeshManager] 已略過外掛透過 IPC 要求的全量重建 {skipped} 次："
                  + $"距上次重建 {since.TotalSeconds:f1} 秒，未達 {IPCRebuildMinInterval.TotalSeconds:f0} 秒的最小間隔"
                  + (buildInProgress ? "，且目前仍在建置中" : "")
                  + "。全量重建期間玩家不會移動，呼叫端若以「卡住」當觸發條件會自我維持。"
                  + "使用者自己按 UI 的 Rebuild 或 /vnav rebuild 不受此限。");
                Interlocked.Exchange(ref _ipcRebuildSkipCount, 0);
            }
            return false;
        }

        _lastIPCRebuild = now;
        _lastIPCRebuildSkipLog = DateTime.MinValue; // 讓下一次被略過時立刻有一行說明，不必等 5 秒
        Interlocked.Exchange(ref _ipcRebuildSkipCount, 0);
        return Reload(false);
    }

    internal void ReplaceMesh(Navmesh mesh)
    {
        Log($"Mesh replaced");
        PublishMesh(mesh, new(mesh));
    }

    /// <summary>
    /// IPC 的 Nav.PathfindCancelAll 專用入口：**只取消進行中/排隊中的尋路，不動導航網格**。
    ///
    /// 為什麼不再是 Reload(true)（本函式取代的舊實作）：Reload 會先 ClearState() 把
    /// Navmesh/Query 清成 null，再從快取非同步重新載入。取消的效果確實有達到，但代價是
    /// 整張網格被卸掉再載入 —— 這段期間 Nav.IsReady 回 false、Nav.Pathfind 直接擲例外。
    /// 而呼叫端幾乎清一色是「取消 → 立刻重新規劃路徑」的形狀，於是重試必定先失敗一次，
    /// 要等載入完成才會成功。改成純取消之後，Nav.IsReady 全程維持 true。
    ///
    /// 🔴 這裡**刻意沒有任何節流**。對一個「取消」動作加節流，會讓取消靜默地不發生，
    ///    那比多做幾次工作糟得多。（有節流的是 RebuildFromIPC，那是全量重建，兩者不要混。）
    ///    下面的 early-return **不是節流**：_numActivePathfinds 是在 QueryPath 裡同步遞增的，
    ///    所以它等於 0 就代表真的沒有尋路可取消，跳過是精確的 no-op，順便讓工作佇列不會
    ///    因為呼叫端輪詢式地連打取消而堆積一長串 Dispose 動作。
    /// </summary>
    public void CancelAllPathfinds()
    {
        // 只讀一次：這支跑在 IPC 呼叫端的執行緒上，而計數是別的執行緒在遞減。
        // 舊碼讀兩次(判斷一次、印訊息時又一次)，中間遞減完就會印出「已取消 0 筆」。
        var cancelled = Volatile.Read(ref _numActivePathfinds);
        if (cancelled <= 0)
            return; // 沒有進行中或排隊中的尋路（見上面說明：這不是節流）

        // 換 CTS 是**讀-改-寫**，而這支可以被兩條 IPC 呼叫端執行緒同時踩到。
        // 零同步時兩邊會讀到**同一個** cts、各自 new 一個新的寫回去 ⇒ 其中一個新 CTS
        // 變成孤兒(沒有任何東西握著它、也永遠不會被 Cancel 或 Dispose)，而在那個瞬間
        // 進來的尋路若剛好綁到孤兒身上，**之後每一次 Nav.PathfindCancelAll 都取消不了它**。
        // 這正是本函式說明裡講的「取消靜默地不發生」，只是成因在同步而不在節流。
        // Interlocked.Exchange 讓「取舊值」與「寫新值」變成一步 ⇒ 每個舊 CTS 只會有
        // 一條執行緒拿到，Cancel/Dispose 也就只會各做一次。
        var cts = Interlocked.Exchange(ref _pathfindCTS, new CancellationTokenSource());
        cts.Cancel();

        // 舊 CTS 的 Dispose 排到工作佇列尾端 —— 與 ClearState 對 _currentCTS 的處理同一個
        // 理由：QueryPath 建立的 linked CTS 還握著對它的註冊，等佇列排空才釋放最安全。
        ExecuteWhenIdle(cts.Dispose, default);

        Log($"Cancelled {cancelled} pathfind(s); navmesh left loaded");
        var now = DateTime.Now;
        if (now - _lastCancelAllLog >= CancelAllLogMinInterval)
        {
            _lastCancelAllLog = now;
            Service.Log.Information($"[NavmeshManager] Nav.PathfindCancelAll: 已取消 {cancelled} 筆尋路，導航網格保持載入(Nav.IsReady 不會變 false)");
        }
    }

    private static bool InCutscene => Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    // ⚠️ 參數順序刻意與上游一致(range 在 externalCancel 之前),讓日後追上游不必再改一次。
    //    順序換了但型別不相容(float vs CancellationToken),舊的位置引數呼叫會編譯失敗而不是靜默錯位。
    public Task<List<Waypoint>> QueryPath(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        // 只讀一次 —— 與下面 body 裡對 Query 的處理同一個理由(見 MeshGeneration 說明的
        // 「檢查與使用之間被清掉」)：這支跑在呼叫端的執行緒上，而 ClearState 是在框架
        // 執行緒上把 _currentCTS 設成 null 的。舊碼判斷時讀一次、取 .Token 時又讀一次，
        // 中間被清掉就會擲 NullReferenceException，把下面這句寫給呼叫端看的說明蓋掉。
        var meshCTS = Volatile.Read(ref _currentCTS);
        if (meshCTS == null)
            throw new Exception($"Can't initiate query - navmesh is not loaded");

        // 工作可以被三種來源取消：網格被卸掉(_currentCTS)、外掛端要求取消全部尋路
        // (_pathfindCTS，走 CancelAllPathfinds)、呼叫端自己的 token(externalCancel)。
        // _pathfindCTS 同樣只讀一次：CancelAllPathfinds 會用 Interlocked.Exchange 換掉它，
        // 讀兩次有可能一次拿到舊的、一次拿到新的。
        var pathfindCTS = Volatile.Read(ref _pathfindCTS);
        var combined = CancellationTokenSource.CreateLinkedTokenSource(meshCTS.Token, pathfindCTS.Token, externalCancel);
        Interlocked.Increment(ref _numActivePathfinds);
        var task = ExecuteWhenIdle(async cancel =>
        {
            Log($"Kicking off pathfind from {from} to {to}");
            var path = await Task.Run(() =>
            {
                combined.Token.ThrowIfCancellationRequested();
                // 🔴 這一段跑在執行緒池上，而 ClearState 跑在框架執行緒 —— 所以只讀一次。
                //    分兩次讀（檢查一次、使用一次）時，中間被清掉就會擲 NullReferenceException，
                //    把下面那句寫給呼叫端看的說明蓋掉。取到之後那個物件是不可變的一代，繼續用它安全。
                var q = Query;
                if (q == null)
                    throw new Exception($"Can't pathfind, navmesh did not build successfully");
                Log($"Executing pathfind from {from} to {to}");
                // ⚠️ 迴避圓目前只支援地面路徑。飛行路徑要繞圓得改 VoxelPathfind,那是另一個階段的事,
                //    所以這裡**明講**它沒生效,而不是安靜地忽略參數。
                if (flying && avoidCenter != null && avoidRadius > 0)
                    Service.Log.Information($"[NavmeshManager] 飛行路徑尚未支援迴避圓(中心 {avoidCenter} 半徑 {avoidRadius:f1}),本次忽略該參數。");
                var meshFilter = !flying && avoidCenter != null && avoidRadius > 0
                    ? new NavmeshQuery.AvoidRadiusFilter(avoidCenter.Value, avoidRadius)
                    : null;
                return flying ? q.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token) : q.PathfindMesh(from, to, UseRaycasts, UseStringPulling, combined.Token, range, meshFilter);
            }, combined.Token);
            Log($"Pathfinding done: {path.Count} waypoints");
            return path;
        }, combined.Token);

        // 🔴 遞減與 linked CTS 的釋放**必須掛在工作的完成回呼上**，不能只放在 body 裡：
        //    ExecuteWhenIdle 底層是 Service.Framework.Run(...) ＝ TaskFactory.StartNew(delegate, token)，
        //    而 TPL 在工作真正開始執行前發現 token 已取消時，會把工作直接標成 Canceled 而
        //    **完全不執行 delegate** ⇒ 原本寫在 body 裡的 OnDispose 永遠不會跑。
        //    舊碼靠 ClearState 裡的 _numActivePathfinds = 0 幫忙收尾，那在「取消＝順便重載網格」
        //    的年代還過得去；但 CancelAllPathfinds 改成只取消尋路之後，呼叫端的典型形狀是
        //    「取消 → 立刻重新規劃路徑」，硬重設會把**新工作**的計數一起歸零，
        //    讓 Nav.PathfindInProgress 在尋路確實進行中的時候謊報 false。
        //    改成一增一減嚴格配對（無論 body 有沒有跑）就沒有這個問題。
        _ = task.ContinueWith(_ =>
        {
            Interlocked.Decrement(ref _numActivePathfinds);
            combined.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    // 只要座標、不要 area id 的版本。🔴 IPC 的 Nav.Pathfind / PathfindWithTolerance /
    // PathfindCancelable 一直回 List<Vector3>,全艦隊消費端都照這個型別寫,**不可以改**。
    public async Task<List<Vector3>> QueryPathBasic(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
    {
        var result = await QueryPath(from, to, flying, range, externalCancel, avoidCenter, avoidRadius);
        return [.. result.Select(w => w.Position)];
    }

    // note: pixelSize should be power-of-2
    // 🔴 IPC 的 Nav.BuildBitmap / Nav.BuildBitmapBounded 會從**呼叫端的執行緒**進來，而這支要
    //    同時用到網格與查詢物件 ⇒ 一開始就把整代抓進區域變數，之後全程用那一份。
    //    舊碼對 Navmesh / Query 讀了五次：切區域時可能拿到「舊網格 + 新查詢物件」，
    //    那時 poly ref 會被拿去索引另一張網格的 m_tiles（GetTileAndPolyByRefUnsafe 不驗 salt
    //    也不驗界線）；而且 null 檢查之後的每一次讀取都可能已經被 ClearState 清成 null。
    public (Vector3 min, Vector3 max) BuildBitmap(Vector3 startingPos, string filename, float pixelSize, AABB? mapBounds = null)
    {
        var gen = Current;
        if (gen.Mesh is not { } navmesh || gen.Query is not { } query)
            throw new InvalidOperationException($"Can't build bitmap - navmesh creation is in progress");

        bool inBounds(Vector3 vert) => mapBounds is not AABB aabb || vert.X >= aabb.Min.X && vert.Y >= aabb.Min.Y && vert.Z >= aabb.Min.Z && vert.X <= aabb.Max.X && vert.Y <= aabb.Max.Y && vert.Z <= aabb.Max.Z;

        var startPoly = query.FindNearestMeshPoly(startingPos);
        var reachablePolys = query.FindReachableMeshPolys(startPoly);

        HashSet<long> polysInbounds = [];

        Vector3 min = new(1024), max = new(-1024);
        foreach (var p in reachablePolys)
        {
            navmesh.Mesh.GetTileAndPolyByRefUnsafe(p, out var tile, out var poly);
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var v = NavmeshBitmap.GetVertex(tile, poly.verts[i]);
                if (!inBounds(v))
                    goto cont;

                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                //Service.Log.Debug($"{p:X}.{i}= {v}");
            }

            polysInbounds.Add(p);

        cont:;
        }
        //Service.Log.Debug($"bounds: {min}-{max}");

        var bitmap = new NavmeshBitmap(min, max, pixelSize);
        foreach (var p in polysInbounds)
        {
            bitmap.RasterizePolygon(navmesh.Mesh, p);
        }
        bitmap.Save(filename);
        Service.Log.Debug($"Generated nav bitmap '{filename}' @ {startingPos}: {bitmap.MinBounds}-{bitmap.MaxBounds}");
        return (bitmap.MinBounds, bitmap.MaxBounds);
    }

    // if non-empty string is returned, active layout is ready
    private unsafe string GetCurrentKey()
    {
        // LayoutWorld.Instance() is [StaticAddress(..., isPointer: true)] and legitimately returns null
        // (title screen / between zones). An empty key is the established "nothing loaded" value here.
        // This runs every frame from Update(), so it deliberately does not log.
        var world = LayoutWorld.Instance();
        if (world == null)
            return ""; // layout world not available

        var layout = world->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return ""; // layout not ready

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;

        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        // CE always has a festival layer (i hope). the non-festival layout is briefly loaded when entering the zone, which triggers a useless mesh build (which is also expensive because the zone is large)
        if (terrRow?.TerritoryIntendedUse.RowId == 60)
        {
            var fest = layout->ActiveFestivals[0];
            if (fest.Id == 0 && fest.Phase == 0)
                return "";
        }

        var sgs = LayoutUtils.GetZoneSharedGroupsEnabled(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

        return $"{terrRow?.Bg}//{filterKey:X}//{LayoutUtils.FestivalsString(layout->ActiveFestivals)}//{string.Join('.', sgs)}";
    }

    internal static unsafe string GetCacheKey(SceneDefinition scene)
    {
        // note: festivals are active globally, but majority of zones don't have festival-specific layers, so we only want real ones in the cache key
        // LayoutWorld.Instance() is [StaticAddress(..., isPointer: true)] and can legitimately be null.
        // The layout can also be torn down between the reload being queued and the build actually
        // starting (zone change, logout, the cutscene wait above). LayoutUtils.FindFilter() dereferences
        // its argument unconditionally, so both have to be checked here. An empty string is an
        // unambiguous sentinel: a real key always contains the "__" separators below.
        var world = LayoutWorld.Instance();
        var layout = world != null ? world->ActiveLayout : null;
        if (layout == null)
            return "";

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        var terrId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
        var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(terrId);

        static string numbers<T>(IEnumerable<T> nums) where T : INumber<T> => string.Join('.', nums.Select(n => n.ToString("X", CultureInfo.InvariantCulture)));

        return $"{terrRow?.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{numbers(scene.FestivalLayers)}__{numbers(scene.ZoneSGs)}";
    }

    private void ClearState()
    {
        if (_currentCTS == null)
            return; // already cleared

        var cts = _currentCTS;
        _currentCTS = null;
        cts.Cancel();
        Log("Queueing state clear");
        ExecuteWhenIdle(() =>
        {
            Log("Clearing state");
            // 🔑 這裡原本有 _numActivePathfinds = 0;（用來補救「工作被取消所以 body 沒跑、
            //    計數沒遞減」）。QueryPath 改成用完成回呼遞減之後，一增一減已嚴格配對，
            //    這行變成多餘 —— 而且是有害的：Reload/CancelAllPathfinds 之後呼叫端會馬上
            //    送出新的尋路，這行會把那筆新工作的計數一起歸零，之後它完成時再遞減就變負數，
            //    Nav.PathfindInProgress 於是在尋路進行中謊報 false。所以刻意移除，不要加回來。
            cts.Dispose();
            PublishMesh(null, null);
        }, default);
    }

    private Navmesh BuildNavmesh(SceneDefinition scene, string cacheKey, bool allowLoadFromCache, CancellationToken cancel)
    {
        Log($"Build task started: '{cacheKey}'");
        var customization = NavmeshCustomizationRegistry.ForTerritory(scene.TerritoryID);
        Log($"Customization for '{scene.TerritoryID}': {customization.GetType()}");
        customization.CurrentTerritory = scene.TerritoryID; // 供 LinkPoints 產生「territory + 座標」的捷徑識別鍵
        customization.CurrentScene = scene; // 供自訂化以「當下 layout 有沒有這個碰撞模型」判斷路線是否開通

        var layers = scene.FestivalLayers.ToList();

        // try reading from cache
        var cache = new FileInfo($"{_cacheDir.FullName}/{cacheKey}.navmesh");
        if (allowLoadFromCache && cache.Exists)
        {
            try
            {
                Log($"Loading cache: {cache.FullName}");
                using var stream = cache.OpenRead();
                using var reader = new BinaryReader(stream);
                var mesh = Navmesh.Deserialize(reader, customization.Version);
                customization.CustomizeMesh(mesh, layers);
                return mesh;
            }
            catch (Exception ex)
            {
                Log($"Failed to load cache: {ex}");
            }
        }
        cancel.ThrowIfCancellationRequested();

        // cache doesn't exist or can't be used for whatever reason - build navmesh from scratch
        var builder = new NavmeshBuilder(scene, customization);
        var deltaProgress = 0.99f / (builder.NumTilesX * builder.NumTilesZ);
        builder.BuildTiles(() =>
        {
            AddLoadProgress(deltaProgress);
            cancel.ThrowIfCancellationRequested();
        });

        // write results to cache
        {
            Service.Log.Debug($"Writing cache: {cache.FullName}");
            using var stream = cache.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream);
            builder.Navmesh.Serialize(writer);
        }
        customization.CustomizeMesh(builder.Navmesh, layers);
        deltaProgress += 0.01f;
        return builder.Navmesh;
    }

    // 見 _taskChainLock 的說明：三個多載的讀-改-寫都必須在同一把鎖裡。
    private void ExecuteWhenIdle(Action task, CancellationToken token)
    {
        lock (_taskChainLock)
        {
            var prev = _lastLoadQueryTask;
            _lastLoadQueryTask = Service.Framework.Run(async () =>
            {
                await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                _ = prev.Exception;
                task();
            }, token);
        }
    }

    private void ExecuteWhenIdle(Func<CancellationToken, Task> task, CancellationToken token)
    {
        lock (_taskChainLock)
        {
            var prev = _lastLoadQueryTask;
            _lastLoadQueryTask = Service.Framework.Run(async () =>
            {
                await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                _ = prev.Exception;
                var t = task(token);
                await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                LogTaskError(t);
            }, token);
        }
    }

    private Task<T> ExecuteWhenIdle<T>(Func<CancellationToken, Task<T>> task, CancellationToken token)
    {
        lock (_taskChainLock)
        {
            var prev = _lastLoadQueryTask;
            var res = Service.Framework.Run(async () =>
            {
                await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                _ = prev.Exception;
                var t = task(token);
                await ((Task)t).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                LogTaskError(t);
                return t.Result;
            }, token);
            _lastLoadQueryTask = res;
            return res;
        }
    }

    /// <summary>
    /// 「尋路送出後遲遲沒有結果」的診斷。每幀由框架執行緒呼叫(見 Update)。
    /// <para>
    /// 🔴 <b>純觀測</b>：不取消、不重試、不動網格。這一梯只要證據。
    /// </para>
    /// <para>
    /// 判準＝<c>_numActivePathfinds</c> 連續大於 0 超過門檻。它在 QueryPath 裡同步遞增、
    /// 在工作的完成回呼裡遞減，所以「進行中」與「排隊中」都算 —— <b>排隊中卡住正是最常見的
    /// 形狀</b>：ExecuteWhenIdle 是單一串接鏈，一筆網格建置就會把後面所有尋路一起擋住。
    /// </para>
    /// <para>
    /// ⚠️ 因此這一行<b>一定要印建置進度</b>：正在建置網格時排隊等上幾十秒是正常的，
    /// 看那個欄位才分得出「在等建置」與「尋路工作本身沒有完成」。
    /// </para>
    /// <para>
    /// ⚠️ 已知的合理誤報：①飛行路徑(PathfindVolume)在大區域本來就可能跑很久；
    /// ②呼叫端自己連續送出大量尋路時佇列本來就長。兩者都會在同一行裡露出來(筆數、建置進度)。
    /// </para>
    /// </summary>
    private void ReportPathfindStall()
    {
        var active = Volatile.Read(ref _numActivePathfinds);
        if (active <= 0)
        {
            _pathfindBusySince = null;
            _lastPathfindStallReport = DateTime.MinValue; // 下一次卡住立刻有一行，不必等節流窗
            return;
        }

        var now = DateTime.Now;
        _pathfindBusySince ??= now;
        var stalled = now - _pathfindBusySince.Value;
        if (stalled < PathfindStallThreshold || now - _lastPathfindStallReport < PathfindStallReportInterval)
            return;
        _lastPathfindStallReport = now;

        var progress = Volatile.Read(ref _loadTaskProgress);
        var progressStr = progress < 0 ? "未在建置" : $"建置中 {progress * 100:f0}%";
        var meshStr = Navmesh != null ? "已載入" : "未載入";
        Service.Log.Information(
            $"[vnav卡住診斷] 尋路佇列已經 {stalled.TotalSeconds:f0} 秒沒有排空：進行中＋排隊中共 {active} 筆"
          + $"(其中排隊 {active - 1} 筆)。導航網格={meshStr}，區域鍵「{CurrentKey}」，{progressStr}，"
          + $"過場動畫中={InCutscene}。"
          + $"⇒ 顯示「建置中」時尋路是排在建置後面等，屬正常；顯示「未在建置」而這一行持續出現，"
          + $"代表尋路工作本身沒有完成(飛行路徑在大區域可能真的要跑很久)。");
    }

    private static void Log(string message) => Service.Log.Debug($"[NavmeshManager] [{Thread.CurrentThread.ManagedThreadId}] {message}");
    private static void LogTaskError(Task task)
    {
        if (task.IsFaulted)
            Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
    }
}
