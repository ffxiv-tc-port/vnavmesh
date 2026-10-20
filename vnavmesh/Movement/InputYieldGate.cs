using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement;

internal enum InputYieldEvent
{
    None,

    /// <summary>照舊：偵測到手動輸入就停止並清掉路徑（沒有任何租約要求讓位）。</summary>
    Cancel,

    /// <summary>開始讓位：路徑留著、暫停驅動角色。</summary>
    Started,

    /// <summary>讓位中停手滿 <see cref="InputYieldGate.ResumeQuietMs"/>：從目前位置接續。</summary>
    Resumed,

    /// <summary>讓位中選項被撤銷（租約放開／逾時／設成 false、使用者關掉輸入取消）而且當下沒在按：接續。</summary>
    Revoked,

    /// <summary>讓位滿 <see cref="InputYieldGate.MaxYieldMs"/>：照舊停止並清掉路徑。</summary>
    Expired,
}

/// <summary>
/// 「手動移動時讓位、停手後接續」的狀態機。純邏輯、不碰遊戲、不配置；只由框架執行緒呼叫。
/// 時間一律用呼叫端給的單調毫秒。<c>yieldOption=false</c> 時與舊規則逐拍相同（有輸入＝<see cref="InputYieldEvent.Cancel"/>）。
/// </summary>
internal sealed class InputYieldGate
{
    /// <summary>沒有輸入連續這麼久才接續（與 Lifestream 飛行讓位同值，同樣以按鍵判停手）。</summary>
    public const long ResumeQuietMs = 1000;

    /// <summary>一次讓位最長這麼久（與 BOCCHI 趕路讓位同值）；之後照舊結束。</summary>
    public const long MaxYieldMs = 30_000;

    public bool Yielding { get; private set; }
    public long StartedAt { get; private set; }

    private bool _quiet;
    private long _quietSince;

    public void Reset()
    {
        Yielding = false;
        StartedAt = 0;
        _quiet = false;
    }

    /// <param name="cancelOnInput">使用者設定「偵測到移動輸入就取消路徑」；關著時完全不看輸入。</param>
    /// <param name="yieldOption">目前有任何一把租約要求讓位。</param>
    /// <param name="userInput">這一幀偵測到移動輸入。</param>
    public InputYieldEvent Step(long now, bool cancelOnInput, bool yieldOption, bool userInput)
    {
        if (Yielding)
        {
            if (!cancelOnInput || !yieldOption)
            {
                Reset();
                return cancelOnInput && userInput ? InputYieldEvent.Cancel : InputYieldEvent.Revoked;
            }

            if (now - StartedAt >= MaxYieldMs)
            {
                Reset();
                return InputYieldEvent.Expired;
            }

            if (userInput)
            {
                _quiet = false;
                return InputYieldEvent.None;
            }

            if (!_quiet)
            {
                _quiet = true;
                _quietSince = now;
            }

            if (now - _quietSince < ResumeQuietMs)
                return InputYieldEvent.None;

            Reset();
            return InputYieldEvent.Resumed;
        }

        if (!cancelOnInput || !userInput)
            return InputYieldEvent.None;
        if (!yieldOption)
            return InputYieldEvent.Cancel;

        Yielding = true;
        StartedAt = now;
        _quiet = false;
        return InputYieldEvent.Started;
    }

    /// <summary>
    /// 接續時要接回哪一個路徑點：<c>0..barrier</c> 裡離角色最近的那個（同距離取後面的）。
    /// <paramref name="barrier"/>＝第一個客戶端固定路徑點的索引（不可越過），沒有就傳 <c>positions.Count - 1</c>。
    /// </summary>
    public static int ChooseRejoinIndex(IReadOnlyList<Vector3> positions, int barrier, Vector3 player, bool ignoreY)
    {
        var last = Math.Min(barrier, positions.Count - 1);
        var best = 0;
        var bestDist = float.MaxValue;
        for (var i = 0; i <= last; ++i)
        {
            var d = positions[i] - player;
            if (ignoreY)
                d.Y = 0;
            var dist = d.LengthSquared();
            if (dist <= bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best;
    }
}
