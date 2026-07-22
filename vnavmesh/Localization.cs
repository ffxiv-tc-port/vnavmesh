using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Navmesh;

// Minimal self-contained localization helper mirroring ECommons.LanguageHelpers
// (adapted from visland's Helpers/Loc.cs): same ini format (English==translation,
// one entry per line, literal \n escapes, ?? positional placeholders) and the same
// .Loc() string extension name. vnavmesh never initializes ECommons, so we ship
// this tiny equivalent instead of pulling in the full library just for loc.
public static class Localization
{
    private static readonly Dictionary<string, string> Translations = [];

    public static void Init(string? directory)
    {
        Translations.Clear();
        if (directory == null)
            return;
        var path = Path.Combine(directory, "LanguageChineseTraditional.ini");
        try
        {
            if (!File.Exists(path))
                return;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var idx = line.IndexOf("==", StringComparison.Ordinal);
                if (idx <= 0)
                    continue;
                var key = line[..idx].Replace("\\n", "\n");
                var value = line[(idx + 2)..].TrimEnd('\r').Replace("\\n", "\n");
                Translations[key] = value;
            }
            Service.Log.Information($"Localization: loaded {Translations.Count} entries from {path}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"Localization: failed to load {path}");
        }
    }

    public static string Loc(this string s) => Translations.TryGetValue(s, out var t) ? t : s;

    public static string Loc(this string s, params object?[] args)
    {
        var result = s.Loc();
        foreach (var a in args)
        {
            var idx = result.IndexOf("??", StringComparison.Ordinal);
            if (idx < 0)
                break;
            result = result.Remove(idx, 2).Insert(idx, a?.ToString() ?? "");
        }
        return result;
    }
}
