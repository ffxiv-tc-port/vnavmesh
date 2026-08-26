using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;

namespace Navmesh.Render;

// device + deferred context
public class RenderContext : IDisposable
{
    public SharpDX.Direct3D11.Device Device { get; private set; }
    public DeviceContext Context { get; private set; }

    public unsafe RenderContext()
    {
        // 🔴 Device.Instance() 是 [StaticAddress(..., isPointer: true)]：產生器讀「指標的位址」
        //    再解參考一層，遊戲尚未建立圖形裝置時回 null（非 isPointer 的那種才保證不回 null，
        //    是擲 InvalidOperationException）。裸解參考 null 原生指標是 AccessViolationException，
        //    在 .NET Core 屬 corrupted-state exception，try/catch 完全攔不到。
        //    這裡是建構子，失敗語意＝「渲染層無法初始化」，所以擲一個訊息明確的受管理例外：
        //    DebugDrawer 的建構失敗會被上層看見並記錄，而不是整個遊戲閃退且堆疊指不到這裡。
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (device == null)
            throw new InvalidOperationException("RenderContext: Device.Instance() 回 null，圖形裝置尚未就緒。");
        if (device->D3D11Forwarder == null)
            throw new InvalidOperationException("RenderContext: Device.D3D11Forwarder 為 null，圖形裝置尚未就緒。");

        Device = new((nint)device->D3D11Forwarder);
        Context = new(Device);
    }

    public void Dispose()
    {
        Context.Dispose();
    }

    public void Execute()
    {
        using var cmds = Context.FinishCommandList(true);
        Device.ImmediateContext.ExecuteCommandList(cmds, true);
        Context.ClearState();
    }
}
