using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Navmesh;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ExdZoneSharedGroup
{
    uint LGBSharedGroup;
    uint RequirementRow0;
    uint RequirementRow1;
    uint RequirementRow2;
    uint RequirementRow3;
    uint RequirementRow4;
    uint RequirementRow5;
    uint Unknown0;
    uint RequirementQuestSequence0;
    uint RequirementQuestSequence1;
    uint RequirementQuestSequence2;
    uint RequirementQuestSequence3;
    uint RequirementQuestSequence4;
    uint RequirementQuestSequence5;
    uint Unknown1;
    byte RequirementType0;
    byte RequirementType1;
    byte RequirementType2;
    byte RequirementType3;
    byte RequirementType4;
    byte RequirementType5;
    byte Unknown8;
    byte Unknown9;
    byte Unknown10;
    byte Unknown11;
    byte Unknown12;
    byte Unknown13;
    byte Unknown14;
    byte Unknown15;

    // 遊戲函式逐格檢查的 7 格條件（RequirementType[0..5] 與 Unknown8）有沒有任何一格非 0。
    // 刻意把任何非 0 類型都當成有條件、不只現行的 1..5：函式對不上時可能正是改版多了新類型。
    public readonly bool HasAnyRequirement
        => Unknown8 != 0 || RequirementType0 != 0 || RequirementType1 != 0 || RequirementType2 != 0
            || RequirementType3 != 0 || RequirementType4 != 0 || RequirementType5 != 0;

    public static implicit operator ExdZoneSharedGroup(Lumina.Excel.Sheets.ZoneSharedGroup sg) => new()
    {
        LGBSharedGroup = sg.LGBSharedGroup,
        RequirementRow0 = sg.RequirementRow[0].RowId,
        RequirementRow1 = sg.RequirementRow[1].RowId,
        RequirementRow2 = sg.RequirementRow[2].RowId,
        RequirementRow3 = sg.RequirementRow[3].RowId,
        RequirementRow4 = sg.RequirementRow[4].RowId,
        RequirementRow5 = sg.RequirementRow[5].RowId,
        Unknown0 = sg.Unknown0,
        RequirementQuestSequence0 = sg.RequirementQuestSequence[0],
        RequirementQuestSequence1 = sg.RequirementQuestSequence[1],
        RequirementQuestSequence2 = sg.RequirementQuestSequence[2],
        RequirementQuestSequence3 = sg.RequirementQuestSequence[3],
        RequirementQuestSequence4 = sg.RequirementQuestSequence[4],
        RequirementQuestSequence5 = sg.RequirementQuestSequence[5],
        Unknown1 = sg.Unknown1,
        RequirementType0 = sg.RequirementType[0],
        RequirementType1 = sg.RequirementType[1],
        RequirementType2 = sg.RequirementType[2],
        RequirementType3 = sg.RequirementType[3],
        RequirementType4 = sg.RequirementType[4],
        RequirementType5 = sg.RequirementType[5],
        Unknown8 = sg.Unknown8,
        Unknown9 = sg.Unknown9 ? (byte)1 : (byte)0,
        Unknown10 = sg.Unknown10 ? (byte)1 : (byte)0,
        Unknown11 = sg.Unknown11 ? (byte)1 : (byte)0,
        Unknown12 = sg.Unknown12 ? (byte)1 : (byte)0,
        Unknown13 = sg.Unknown13 ? (byte)1 : (byte)0,
        Unknown14 = sg.Unknown14 ? (byte)1 : (byte)0,
        Unknown15 = sg.Unknown15 ? (byte)1 : (byte)0,
    };
}

public unsafe static class LayoutUtils
{
    /// <summary>GetZoneSharedGroupsEnabled 對「無法得知」的群組填這個值；遊戲函式本身只回 0..7。</summary>
    public const uint SharedGroupStateUnknown = uint.MaxValue;

    // 刻意不放在靜態建構子裡解析：型別初始化一旦擲例外，整個 LayoutUtils（FindFilter 等）會永久不可用，
    // 而區域鍵每幀都經過這裡 ⇒ 一條特徵碼失效就讓所有導航停擺。
    private static readonly Lazy<nint> _getEnabledRequirementIndex = new(ResolveGetEnabledRequirementIndex);

    private static nint ResolveGetEnabledRequirementIndex()
    {
        if (Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 0F B6 53 6C", out var address))
            return address;
        FeatureHealth.Report("layout.zoneSharedGroup",
            "Quest-dependent terrain detection is off (game function signature not found): affected zones are cached by scene content and are not rebuilt automatically when quest progress changes terrain within the same zone; use /vnav rebuild if needed.",
            "[LayoutUtils] 找不到「區域共享群組啟用條件」函式的特徵碼（遊戲改版後常見），只停用這一項：導航網格照常建置；"
          + "有任務進度相關地形的區域改用場景內容當快取鍵，同一區域內任務進度改變地形時不會自動重建，必要時請用 /vnav rebuild。");
        return 0;
    }

    /// <summary>
    /// 目前區域每個共享群組（ZoneSharedGroup 子列）的啟用條件索引，只用來組區域鍵與快取鍵；網格內容取自當下的 layout。
    /// 契約：函式找不到時，沒有任何條件的子列回 0（與遊戲函式相同：類型 0 的條件略過、全部略過回 0），
    /// 其餘回 <see cref="SharedGroupStateUnknown"/>。
    /// </summary>
    public static uint[] GetZoneSharedGroupsEnabled(uint territoryType)
    {
        var rows = ZoneSharedGroupRows(territoryType);
        if (rows == null)
            return [];

        var indices = new uint[rows.Length];
        FillEnabled(rows, indices);
        return indices;
    }

    /// <summary>
    /// 與上面那支同一份結果，寫進呼叫端重用的緩衝（不夠長才換新的）並回傳群組數；給每幀呼叫的區域鍵比對用。
    /// 遊戲函式照樣每個群組問一次。
    /// </summary>
    public static int FillZoneSharedGroupsEnabled(uint territoryType, ref uint[]? buffer)
    {
        var rows = ZoneSharedGroupRows(territoryType);
        var count = rows?.Length ?? 0;
        if (buffer == null || buffer.Length < count)
            buffer = new uint[count];
        if (rows != null)
            FillEnabled(rows, buffer);
        return count;
    }

    private static void FillEnabled(ExdZoneSharedGroup[] rows, uint[] indices)
    {
        var fn = (delegate* unmanaged<ExdZoneSharedGroup*, uint>)_getEnabledRequirementIndex.Value;
        for (var i = 0; i < rows.Length; i++)
        {
            if (fn != null)
            {
                var exd = rows[i]; // 傳副本：遊戲函式拿到的一律是表裡的原值
                indices[i] = fn(&exd);
            }
            else
            {
                indices[i] = rows[i].HasAnyRequirement ? SharedGroupStateUnknown : 0;
            }
        }
    }

    // 最近一個區域的共享群組子列（已轉成遊戲函式吃的版面）。表的內容在執行期不變，所以轉一次就留著；
    // 整個物件一次換掉，哪條執行緒讀到的都是完整的一份。
    private sealed class ZoneRows(uint territory, ExdZoneSharedGroup[] rows)
    {
        public readonly uint Territory = territory;
        public readonly ExdZoneSharedGroup[] Rows = rows;
    }

    private static ZoneRows? _zoneRows;

    // null＝TerritoryType 沒有這一列。
    private static ExdZoneSharedGroup[]? ZoneSharedGroupRows(uint territoryType)
    {
        var cached = Volatile.Read(ref _zoneRows);
        if (cached != null && cached.Territory == territoryType)
            return cached.Rows;

        var tt = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territoryType);
        if (tt == null)
            return null;

        _ = _getEnabledRequirementIndex.Value; // 先解析函式位址再讀子列，失效時那一行 log 的時機與讀表失敗時的順序不變
        var source = tt.Value.ZoneSharedGroup.Value.ToList();
        var rows = new ExdZoneSharedGroup[source.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = source[i];
        Volatile.Write(ref _zoneRows, new ZoneRows(territoryType, rows));
        return rows;
    }

    /// <summary>區域鍵裡的共享群組段；正常時與原本的 string.Join 逐字相同，未知的群組印成「?」。</summary>
    public static string SharedGroupStatesString(uint[] states)
        => Array.IndexOf(states, SharedGroupStateUnknown) < 0
            ? string.Join('.', states)
            : string.Join('.', states.Select(s => s == SharedGroupStateUnknown ? "?" : s.ToString()));

    /// <summary>
    /// LayoutWorld.Instance() 的不擲版：特徵碼解不出來時回 null，否則與 Instance() 相同（仍可能合法回 null）。
    /// 解不出來時任何網格都建不了 ⇒ 這一項是大聲失敗（Error＋主視窗紅字），不是靜默退路。
    /// </summary>
    public static LayoutWorld* LayoutWorldOrNull()
    {
        if (LayoutWorld.Addresses.Instance.Value == 0)
        {
            FeatureHealth.Report("layout.world",
                "Navmesh cannot be built: the game's layout address no longer matches (usually right after a game update). Please wait for a plugin update.",
                "[LayoutUtils] LayoutWorld 的位址解不出來（遊戲改版後常見）：無法取得場景配置，導航網格一律不建置，"
              + "依賴 vnavmesh 導航的插件會看到「網格未就緒」。請等待插件更新，並連同這一行回報。",
                severe: true);
            return null;
        }
        return LayoutWorld.Instance();
    }

    public static string ReadString(byte* data) => data != null ? MemoryHelper.ReadStringNullTerminated((nint)data) : "";
    public static string ReadString(RefCountedString* data) => data != null ? data->DataString : "";

    public static V* FindPtr<K, V>(ref this StdMap<K, Pointer<V>> map, K key) where K : unmanaged, IComparable where V : unmanaged
    {
        return map.TryGetValuePointer(key, out var ptr) && ptr != null ? ptr->Value : null;
    }

    public static ILayoutInstance* FindInstance(LayoutManager* layout, ulong key)
    {
        foreach (var (ikt, ikv) in layout->InstancesByType)
        {
            var iter = ikv.Value->FindPtr(key);
            if (iter != null)
                return iter;
        }
        return null;
    }

    public static LayoutManager.Filter* FindFilter(LayoutManager* layout)
    {
        if (layout->CfcId != 0) // note: some code paths check cfcid match only if TerritoryTypeId != 0; don't think it actually matters
            foreach (var (k, v) in layout->Filters)
                if (v.Value->CfcId == layout->CfcId)
                    return v.Value;
        if (layout->TerritoryTypeId != 0)
            foreach (var (k, v) in layout->Filters)
                if (v.Value->TerritoryTypeId == layout->TerritoryTypeId)
                    return v.Value;
        return layout->TerritoryTypeId == 0 ? FindPtr(ref layout->Filters, layout->LayerFilterKey) : null;
    }

    public static bool LayerActiveFestival(FileLayerGroupLayer* layer, Span<GameMain.Festival> festivals)
    {
        if (layer->Festival.Id == 0)
            return true; // non-festival, always active

        if (layer->Festival.Phase != 0)
        {
            foreach (var f in festivals)
                if (f.Id == layer->Festival.Id && f.Phase == layer->Festival.Phase)
                    return true;
            return false;
        }
        else
        {
            foreach (var f in festivals)
                if (f.Id == layer->Festival.Id)
                    return true;
            return false;
        }
    }

    public static bool LayerActiveFilter(FileLayerGroupLayer* layer, uint filterId)
    {
        var filter = layer->Filter;
        return filter == null || filter->Operation switch
        {
            FileLayerGroupLayerFilter.Op.Match => filter->Entries.Contains(filterId),
            FileLayerGroupLayerFilter.Op.NoMatch => !filter->Entries.Contains(filterId),
            _ => true
        };
    }

    public static string FestivalString(GameMain.Festival f) => $"{(uint)(f.Phase << 16) | f.Id:X}";
    public static string FestivalsString(ReadOnlySpan<GameMain.Festival> f) => $"{FestivalString(f[0])}.{FestivalString(f[1])}.{FestivalString(f[2])}.{FestivalString(f[3])}";

    public static string Vec3ToSource(Vector3 v) => $"new Vector3({FloatLiteral(v.X)}, {FloatLiteral(v.Y)}, {FloatLiteral(v.Z)})";

    static string FloatLiteral(float f)
    {
        static bool almostEqual(float f1, float f2) => MathF.Abs(f2 - f1) < 0.1f;

        if (MathF.Abs(f) < 0.001f)
            return "0";

        if (MathF.Abs(f - MathF.Round(f)) < 0.001f)
            return MathF.Round(f).ToString();

        var abs = MathF.Abs(f);

        if (almostEqual(abs, MathF.PI))
            return f < 0 ? "-pi" : "pi";

        if (almostEqual(abs, MathF.PI * 0.5f))
            return f < 0 ? "-hpi" : "hpi";

        return f.ToString("0.###f");
    }
}
