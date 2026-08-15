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

    // 🔴 查表前把 key 的換行正規化成 LF。
    // C# 11 的原始字串字面值("""...""")逐字保留原始檔的換行序列,以 CRLF 儲存的 .cs
    // 因此會產生帶 CR 的執行期字串;而上面 Init() 是逐行讀 ini(ReadAllLines 吃掉行尾)
    // 再把字面 \n 換成真換行,所以表裡的 key 一律是 LF ⇒ 多行 key 兩邊永遠對不起來。
    // 失敗形式完全靜默:查不到就原樣回傳,不擲例外也不寫 log,那段文字就一直是英文。
    // 與 ECommons 本尊同款的修補(那邊 2026-08-15 實測 8 處中招)。
    // 📌 這個檔目前沒有任何多行 key,所以這是預防性加固,不是現貨 bug。
    // ⚠️ 只換 CRLF,不動落單的 CR —— 落單的 CR 不是換行風格差異,改了會動到字串內容。
    private static string NormalizeKey(string s) => s.Contains('\r') ? s.Replace("\r\n", "\n") : s;

    public static string Loc(this string s) => Translations.TryGetValue(NormalizeKey(s), out var t) ? t : s;

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
