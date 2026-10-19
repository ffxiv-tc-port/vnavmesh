using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class DTRProvider : IDisposable
{
    private NavmeshManager _manager;
    private AsyncMoveRequest _asyncMove;
    private FollowPath _followPath;
    private IDtrBarEntry _dtrBarEntry;

    private Action? _toggleWindow;

    // 上一次寫進資訊列時的輸入；null＝還沒寫過。只在框架執行緒讀寫。
    // 建置進度以畫面上的百分比字串當鍵：進度每塊 tile 都在變，顯示只到整數百分比。
    private (bool Building, bool HasMesh, bool Moving, int Queued, bool Degraded)? _lastKey;
    private string _lastPercent = "";

    public DTRProvider(NavmeshManager manager, AsyncMoveRequest asyncMove, FollowPath followPath)
    {
        _manager = manager;
        _asyncMove = asyncMove;
        _followPath = followPath;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    /// <summary>
    /// 左右鍵共用的動作（開關主視窗；vnavmesh 沒有獨立設定視窗）。由 Plugin 在建立視窗之後注入。
    /// </summary>
    public void SetToggleWindowAction(Action toggleWindow)
    {
        _toggleWindow = toggleWindow;
        _dtrBarEntry.OnClick = _ => _toggleWindow?.Invoke();
    }

    public void Dispose()
    {
        _dtrBarEntry.Remove();
    }

    public void Update()
    {
        _dtrBarEntry.Shown = Service.Config.EnableDTR;
        if (_dtrBarEntry.Shown)
        {
            // DTR 是很擠的空間：狀態用圖示表達，不寫「Mesh: 」這種每次都一樣的前綴。
            //   Aethernet（網路節點）＝ 網格就緒
            //   FlyZone            ＝ 正在尋路或移動中
            //   Warning            ＝ 建置中（後面帶百分比，那是數字不是狀態，硬塞圖示會丟資訊）
            //   NoCircle           ＝ 沒有網格
            var loadProgress = _manager.LoadTaskProgress;
            var asyncMoveActive = _asyncMove.TaskInProgress;
            var isMoving = _followPath.WaypointCount > 0;
            // 文字、提示與快取鍵都用同一份快照：分開讀的話網格在中途換代會讓快取卡在舊畫面。
            var hasMesh = _manager.Navmesh != null;
            var numQueued = Service.Config.ShowQueryStatusInDTR ? _manager.NumQueuedPathfindRequests : 0;
            var degraded = FeatureHealth.Entries.Count > 0;

            // 值沒變就不重設；節點重建或重新顯示時 Dalamud 會用保存的 Text 自己重寫。
            Span<char> percent = stackalloc char[64];
            var percentLen = 0;
            if (loadProgress >= 0 && !(loadProgress * 100).TryFormat(percent, out percentLen, "f0"))
                percentLen = -1;
            var key = (loadProgress >= 0, hasMesh, asyncMoveActive || isMoving, numQueued, degraded);
            if (percentLen >= 0 && _lastKey == key && percent[..percentLen].SequenceEqual(_lastPercent))
                return;
            _lastKey = percentLen >= 0 ? key : null;
            _lastPercent = percentLen > 0 ? new string(percent[..percentLen]) : "";

            BitmapFontIcon icon;
            var detail = string.Empty;

            if (loadProgress >= 0)
            {
                icon = BitmapFontIcon.Warning;
                detail = $"{loadProgress * 100:f0}%";
            }
            else if (!hasMesh)
            {
                icon = BitmapFontIcon.NoCircle;
            }
            else if (asyncMoveActive || isMoving)
            {
                icon = BitmapFontIcon.FlyZone;
            }
            else
            {
                icon = BitmapFontIcon.Aethernet;
            }

            // 佇列深度是「有幾件事在排隊」，圖示表達不了，只在真的有排隊時才佔位。
            if (numQueued > 0)
                detail = detail.Length > 0 ? $"{detail} +{numQueued}" : $"+{numQueued}";

            _dtrBarEntry.Text = detail.Length > 0
                ? new SeString(new IconPayload(icon), new TextPayload(detail))
                : new SeString(new IconPayload(icon));

            // 圖示化之後光看畫面認不出是哪個外掛，所以提示要把「這是什麼、圖示代表什麼」講完整。
            var state = loadProgress >= 0 ? $"導航網格建置中 {loadProgress * 100:f0}%"
                      : !hasMesh ? "沒有導航網格"
                      : asyncMoveActive || isMoving ? "尋路／移動中"
                      : "導航網格就緒";
            _dtrBarEntry.Tooltip = new SeString(new TextPayload(
                $"vnavmesh — 導航網格\n目前：{state}\n\n" +
                "圖示：乙太網＝就緒／飛行區＝尋路中／警告＝建置中／禁止＝沒有網格\n" +
                "左鍵／右鍵：開啟／關閉 vnavmesh 主視窗" +
                (degraded ? "\n\n部分功能因遊戲改版後位址對不上而停用，詳見 vnavmesh 主視窗（/vnav）。" : "")));
        }
    }
}
