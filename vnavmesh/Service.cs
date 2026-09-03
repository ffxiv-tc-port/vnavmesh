using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Navmesh;

public class Service
{
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;

    public static Lumina.GameData LuminaGameData => DataManager.GameData;
    // 🔴 這裡**刻意不傳語言參數**：本 fork 的 Lumina（lib/Lumina/src/Lumina/Excel/ExcelModule.cs
    //    的 GetRawSheetCore 一開頭就是 language = Language;）把呼叫端傳進來的語言**整個覆蓋掉**，
    //    永遠回 GameData 自己的語言（台服＝繁中）。原本寫的 Lumina.Data.Language.English 是**死參數**：
    //    它看起來像「取英文表來做比對」，實際拿到的卻是繁中，留著只會誤導下一個人。
    //    ⚠️ 目前全部消費端都是 TerritoryType，取的是 Bg（資產路徑）、TerritoryIntendedUse（數字）、
    //    ZoneSharedGroup（列連結）、PlaceName（純顯示），**沒有任何拿英文字面值做比對的地方**，
    //    所以拿掉這個參數是零行為變更。日後要拿表裡的字串做比對，必須先知道拿到的是繁中。
    public static Lumina.Excel.ExcelSheet<T>? LuminaSheet<T>() where T : struct, Lumina.Excel.IExcelRow<T> => LuminaGameData?.GetExcelSheet<T>();
    public static T? LuminaRow<T>(uint row) where T : struct, Lumina.Excel.IExcelRow<T> => LuminaSheet<T>()?.GetRowOrDefault(row);

    public static Config Config = new();
}
