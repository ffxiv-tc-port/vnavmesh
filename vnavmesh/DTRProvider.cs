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

    private Action? _openConfig;

    public DTRProvider(NavmeshManager manager, AsyncMoveRequest asyncMove, FollowPath followPath)
    {
        _manager = manager;
        _asyncMove = asyncMove;
        _followPath = followPath;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    /// <summary>右鍵開啟主視窗。由 Plugin 在建立視窗之後注入（視窗比 DTRProvider 晚建立）。</summary>
    public void SetOpenConfigAction(Action openConfig)
    {
        _openConfig = openConfig;
        _dtrBarEntry.OnClick = ev =>
        {
            if (ev.ClickType == MouseClickType.Right)
                _openConfig?.Invoke();
        };
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
            // DTR 是很擠的空間：狀態改用圖示表達，不再寫「Mesh: 」這種每次都一樣的前綴。
            //   Aethernet（網路節點）＝ 網格就緒
            //   FlyZone            ＝ 正在尋路或移動中
            //   Warning            ＝ 建置中（後面帶百分比，那是數字不是狀態，硬塞圖示會丟資訊）
            //   NoCircle           ＝ 沒有網格
            var loadProgress = _manager.LoadTaskProgress;
            var asyncMoveActive = _asyncMove.TaskInProgress;
            var isMoving = _followPath.Waypoints.Count > 0;

            BitmapFontIcon icon;
            var detail = string.Empty;

            if (loadProgress >= 0)
            {
                icon = BitmapFontIcon.Warning;
                detail = $"{loadProgress * 100:f0}%";
            }
            else if (_manager.Navmesh == null)
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
            if (Service.Config.ShowQueryStatusInDTR)
            {
                var numQueued = _manager.NumQueuedPathfindRequests;
                if (numQueued > 0)
                    detail = detail.Length > 0 ? $"{detail} +{numQueued}" : $"+{numQueued}";
            }

            _dtrBarEntry.Text = detail.Length > 0
                ? new SeString(new IconPayload(icon), new TextPayload(detail))
                : new SeString(new IconPayload(icon));

            // 圖示化之後光看畫面認不出是哪個外掛，所以提示要把「這是什麼、圖示代表什麼」講完整。
            var state = loadProgress >= 0 ? $"導航網格建置中 {loadProgress * 100:f0}%"
                      : _manager.Navmesh == null ? "沒有導航網格"
                      : asyncMoveActive || isMoving ? "尋路／移動中"
                      : "導航網格就緒";
            _dtrBarEntry.Tooltip = new SeString(new TextPayload(
                $"vnavmesh — 導航網格\n目前：{state}\n\n" +
                "圖示：以太網＝就緒／飛行區＝尋路中／警告＝建置中／禁止＝沒有網格\n" +
                "右鍵：開啟主視窗"));
        }
    }
}
