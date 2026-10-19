using System.Collections.Generic;
using System.Threading;

namespace Navmesh;

/// <summary>
/// 遊戲改版後特徵碼／靜態位址解不出來、改成「只停用那一項」的登記處：每項只寫一次 log，
/// 主視窗與 DTR 提示列出目前停用中的項目。
/// 契約：Report 可從任何執行緒呼叫，同一個 id 只記第一次；Entries 是不可變快照，UI 每幀讀。
/// </summary>
internal static class FeatureHealth
{
    public readonly record struct Entry(string Id, string UiText, bool Severe);

    private static readonly object _lock = new();
    private static Entry[] _entries = [];

    public static IReadOnlyList<Entry> Entries => Volatile.Read(ref _entries);

    public static void Report(string id, string uiText, string logText, bool severe = false)
    {
        // 失效期間每幀都會進來：先無鎖掃一次，陣列只有幾格。
        if (Contains(Volatile.Read(ref _entries), id))
            return;

        var entry = new Entry(id, uiText.Loc(), severe);
        lock (_lock)
        {
            if (Contains(_entries, id))
                return;
            Volatile.Write(ref _entries, [.. _entries, entry]);
        }

        if (severe)
            Service.Log.Error(logText);
        else
            Service.Log.Information(logText);
    }

    private static bool Contains(Entry[] entries, string id)
    {
        foreach (var e in entries)
            if (e.Id == id)
                return true;
        return false;
    }
}
