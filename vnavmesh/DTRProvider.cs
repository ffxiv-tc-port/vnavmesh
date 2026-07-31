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

    public DTRProvider(NavmeshManager manager, AsyncMoveRequest asyncMove, FollowPath followPath)
    {
        _manager = manager;
        _asyncMove = asyncMove;
        _followPath = followPath;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
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
        }
    }
}
