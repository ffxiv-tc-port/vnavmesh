using System;
using System.Numerics;

namespace Navmesh.Movement;

/// <summary>
/// 「路徑在跑卻沒有進展」的計時器與觸發判準。純邏輯、不碰遊戲狀態；只由框架執行緒呼叫。
/// </summary>
/// <remarks>
/// ① 位置停滯：離錨點超過 <see cref="MoveEpsilon"/> 就重新錨定並重新計時。
/// ② 隊首停滯：隊首路徑點換了就重新計時。長路段正常走時它本來就會很長，所以只當回報內容、不單獨觸發。
/// 觸發＝① 達 <see cref="ReportThreshold"/>，且距上一次回報已過 <see cref="ReportInterval"/>。
/// </remarks>
internal sealed class ProgressStallWatch
{
    public static readonly TimeSpan ReportThreshold = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
    public const float MoveEpsilon = 1.0f; // 碼

    private DateTime? _noProgressSince;
    private Vector3 _anchor;
    private DateTime? _sameHeadSince;
    private Vector3 _head;
    private DateTime _lastReport = DateTime.MinValue;

    /// <summary>角色本來就不該動的狀態：停止累計，但保留回報節流。</summary>
    public void Pause()
    {
        _noProgressSince = null;
        _sameHeadSince = null;
    }

    /// <summary>全部歸零，含回報節流。</summary>
    public void Reset()
    {
        Pause();
        _lastReport = DateTime.MinValue;
    }

    /// <summary>推進一幀；回 true＝這一幀要寫一行回報。</summary>
    public bool Tick(DateTime now, Vector3 playerPos, Vector3 head, out TimeSpan noProgress, out TimeSpan sameHead)
    {
        // 刻意用「離錨點的距離」而不是每幀速度：正常走路的每幀速度也常掉到 0（轉向、微調）。
        if (_noProgressSince == null || Vector3.Distance(playerPos, _anchor) > MoveEpsilon)
        {
            _noProgressSince = now;
            _anchor = playerPos;
        }

        if (_sameHeadSince == null || _head != head)
        {
            _sameHeadSince = now;
            _head = head;
        }

        noProgress = now - _noProgressSince.Value;
        sameHead = now - _sameHeadSince.Value;
        if (noProgress < ReportThreshold)
            return false;
        if (now - _lastReport < ReportInterval)
            return false; // 卡住是持續狀態，每幀印一行就是洗版
        _lastReport = now;
        return true;
    }
}
