using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Navmesh.Movement;

/// <summary>
/// 停止診斷的 Dalamud 端：共用節流、從呼叫堆疊找呼叫端插件、依節流結果寫 Information 或 Debug。
/// 純邏輯（節流、格式、挑呼叫端）在 <see cref="StopDiagnostics"/>。
/// </summary>
internal static class StopLog
{
    public const long IntervalMs = 10_000;

    /// <summary>鍵＝「原因｜呼叫端插件」。IPC 端點跑在呼叫端執行緒，所以用自帶鎖的節流。</summary>
    public static readonly KeyedIntervalLogGate CallerGate = new(IntervalMs);

    public static long NowMs => Environment.TickCount64;

    /// <summary>
    /// 走訪整個呼叫堆疊，只在真的要寫停止診斷時呼叫。插件歸屬看組件所在的 AssemblyLoadContext，
    /// 所以經由 ECommons 等共用庫呼叫進來的也算到那個插件頭上。任何失敗都回 ("?", "?")。
    /// </summary>
    public static (string Plugin, string Assembly) IdentifyCaller()
    {
        try
        {
            var self = typeof(StopLog).Assembly;
            var selfName = Service.PluginInterface.InternalName;
            var owners = new List<FrameOwner>();
            var nonPluginContexts = new List<AssemblyLoadContext>();
            foreach (var frame in new StackTrace(1, false).GetFrames())
            {
                var asm = frame.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null)
                    continue;
                if (asm == self)
                {
                    owners.Add(new(asm.GetName().Name, null, true));
                    continue;
                }
                var plugin = PluginOf(asm, nonPluginContexts);
                owners.Add(new(asm.GetName().Name, plugin, plugin == selfName));
                if (plugin != null && plugin != selfName)
                    break;
            }
            return StopDiagnostics.PickCaller(owners);
        }
        catch (Exception)
        {
            return ("?", "?");
        }
    }

    private static string? PluginOf(Assembly asm, List<AssemblyLoadContext> nonPluginContexts)
    {
        var context = AssemblyLoadContext.GetLoadContext(asm);
        if (context == null || context == AssemblyLoadContext.Default || nonPluginContexts.Contains(context))
            return null;
        var plugin = Service.PluginInterface.GetPlugin(asm)?.InternalName;
        if (plugin == null)
            nonPluginContexts.Add(context);
        return plugin;
    }

    /// <summary>同一個 <paramref name="gateKey"/> 每 10 秒一行 Information，其餘寫 Debug；節流欄位接在 <paramref name="body"/> 後面。</summary>
    public static void Emit(string gateKey, string body)
    {
        var information = CallerGate.TryPass(gateKey, NowMs, out var suppressed);
        var line = body + StopDiagnostics.SuppressedField(information, suppressed);
        if (information)
            Service.Log.Information(line);
        else
            Service.Log.Debug(line);
    }
}
