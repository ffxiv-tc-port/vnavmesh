using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Navmesh;

// compact scene definition as extracted from the game's layout
// the goal is to fill it quickly on main thread, and then extract real data in background thread
public class SceneDefinition
{
    public uint TerritoryID;
    public uint CFCID;
    public SortedSet<uint> FestivalLayers = new();
    public List<uint> ZoneSGs = new();
    public List<string> Terrains = new();
    public Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = new(); // key = crc; used by bgparts
    public Dictionary<uint, string> MeshPaths = new(); // key = crc, value = pcb path; used by all colliders
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, bool analytic)> BgParts = new();
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, ColliderType type)> Colliders = new();
    public List<(ulong key, Transform transform)> ExitRanges = new();

    public unsafe void FillFromActiveLayout()
    {
        // LayoutWorld.Instance() is a [StaticAddress(..., isPointer: true)]: it loads a global pointer
        // that the game itself null-checks (the signature it is anchored to is mov rcx,[rip+x] / test
        // rcx,rcx / jz), so it legitimately returns null - title screen, between zones, early startup.
        // Fail closed by leaving the scene definition empty, which every caller already treats as
        // "nothing is loaded". FillFromLayout() already tolerates a null LayoutManager.
        var world = LayoutUtils.LayoutWorldOrNull();
        if (world == null)
            return;

        FillFromLayout(world->GlobalLayout);
        FillFromLayout(world->ActiveLayout);
    }

    public unsafe void FillFromLayout(LayoutManager* layout)
    {
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return;

        var filter = LayoutUtils.FindFilter(layout);
        TerritoryID = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
        CFCID = filter != null ? filter->CfcId : layout->CfcId;
        ZoneSGs.AddRange(LayoutUtils.GetZoneSharedGroupsEnabled(TerritoryID));

        foreach (var (k, v) in layout->Layers)
        {
            if (v.Value->FestivalId != 0)
            {
                FestivalLayers.Add(((uint)v.Value->FestivalSubId << 16) | v.Value->FestivalId);
            }
        }

        foreach (var (k, v) in layout->CrcToAnalyticShapeData)
        {
            AnalyticShapes[k.Key] = (v.Transform, v.BoundsMin, v.BoundsMax);
        }

        foreach (var (k, v) in layout->Terrains)
        {
            Terrains.Add($"{v.Value->PathString}/collision");
        }

        var bgParts = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.BgPart);
        if (bgParts != null)
        {
            BgParts.EnsureCapacity(BgParts.Count + CountBgPartsWithCollision(bgParts));
            foreach (var (k, v) in *bgParts)
            {
                var cast = (BgPartsLayoutInstance*)v.Value;
                if ((cast->Flags3 & 0x10) == 0)
                    continue;

                if (cast->AnalyticShapeDataCrc != 0)
                {
                    BgParts.Add((k, *v.Value->GetTransformImpl(), cast->AnalyticShapeDataCrc, ((ulong)cast->CollisionMaterialIdHigh << 32) | cast->CollisionMaterialIdLow, ((ulong)cast->CollisionMaterialMaskHigh << 32) | cast->CollisionMaterialMaskLow, true));
                }
                else if (cast->CollisionMeshPathCrc != 0)
                {
                    if (!MeshPaths.ContainsKey(cast->CollisionMeshPathCrc))
                        MeshPaths[cast->CollisionMeshPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, cast->CollisionMeshPathCrc));
                    BgParts.Add((k, *v.Value->GetTransformImpl(), cast->CollisionMeshPathCrc, ((ulong)cast->CollisionMaterialIdHigh << 32) | cast->CollisionMaterialIdLow, ((ulong)cast->CollisionMaterialMaskHigh << 32) | cast->CollisionMaterialMaskLow, false));
                }
            }
        }

        var colliders = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.CollisionBox);
        if (colliders != null)
        {
            Colliders.EnsureCapacity(Colliders.Count + CountCollidersWithCollision(colliders));
            foreach (var (k, v) in *colliders)
            {
                var cast = (CollisionBoxLayoutInstance*)v.Value;
                if ((cast->Flags3 & 0x10) == 0)
                    continue;

                if (cast->PcbPathCrc != 0 && !MeshPaths.ContainsKey(cast->PcbPathCrc))
                    MeshPaths[cast->PcbPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, cast->PcbPathCrc));
                Colliders.Add((k, cast->Transform, cast->PcbPathCrc, ((ulong)cast->MaterialIdHigh << 32) | cast->MaterialIdLow, ((ulong)cast->MaterialMaskHigh << 32) | cast->MaterialMaskLow, cast->TriggerBoxLayoutInstance.Type));
            }
        }

        var exitRanges = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.ExitRange);
        if (exitRanges != null)
        {
            ExitRanges.EnsureCapacity(ExitRanges.Count + exitRanges->Count);
            foreach (var (k, v) in *exitRanges)
            {
                ExitRanges.Add((k, *v.Value->GetTransformImpl()));
            }
        }
    }

    // Exact counts of what the loops in FillFromLayout add (same predicates, same frame), so each
    // list is sized once instead of regrowing; capacity only, the contents and order are unchanged.
    private static unsafe int CountBgPartsWithCollision(StdMap<ulong, Pointer<ILayoutInstance>>* map)
    {
        var count = 0;
        foreach (var (_, v) in *map)
        {
            var cast = (BgPartsLayoutInstance*)v.Value;
            if ((cast->Flags3 & 0x10) != 0 && (cast->AnalyticShapeDataCrc != 0 || cast->CollisionMeshPathCrc != 0))
                ++count;
        }
        return count;
    }

    private static unsafe int CountCollidersWithCollision(StdMap<ulong, Pointer<ILayoutInstance>>* map)
    {
        var count = 0;
        foreach (var (_, v) in *map)
        {
            if ((((CollisionBoxLayoutInstance*)v.Value)->Flags3 & 0x10) != 0)
                ++count;
        }
        return count;
    }

    /// <summary>
    /// 場景內容的雜湊（16 個大寫十六進位字元），共享群組狀態未知時當快取鍵用：內容相同 ⇒ 建出的網格相同。
    /// 刻意不含 ZoneSGs（正是未知的那部分）與實例 Transform 的 Type（對實例是填充欄位、建置不讀）；
    /// 字典依鍵排序，雜湊與插入順序無關。
    /// </summary>
    public string ContentFingerprint()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(TerritoryID);
            w.Write(CFCID);
            w.Write(FestivalLayers.Count);
            foreach (var f in FestivalLayers)
                w.Write(f);
            w.Write(Terrains.Count);
            foreach (var t in Terrains)
                w.Write(t);
            w.Write(AnalyticShapes.Count);
            foreach (var (crc, shape) in AnalyticShapes.OrderBy(kv => kv.Key))
            {
                w.Write(crc);
                WriteTransform(w, shape.transform, includeType: true); // 分析形狀的 Type 是形狀種類，建置會讀
                WriteVector(w, shape.bbMin);
                WriteVector(w, shape.bbMax);
            }
            w.Write(MeshPaths.Count);
            foreach (var (crc, path) in MeshPaths.OrderBy(kv => kv.Key))
            {
                w.Write(crc);
                w.Write(path);
            }
            w.Write(BgParts.Count);
            foreach (var p in BgParts)
            {
                w.Write(p.key);
                WriteTransform(w, p.transform, includeType: false);
                w.Write(p.crc);
                w.Write(p.matId);
                w.Write(p.matMask);
                w.Write(p.analytic);
            }
            w.Write(Colliders.Count);
            foreach (var c in Colliders)
            {
                w.Write(c.key);
                WriteTransform(w, c.transform, includeType: false);
                w.Write(c.crc);
                w.Write(c.matId);
                w.Write(c.matMask);
                w.Write((int)c.type);
            }
            w.Write(ExitRanges.Count);
            foreach (var e in ExitRanges)
            {
                w.Write(e.key);
                WriteTransform(w, e.transform, includeType: false);
            }
        }
        var hash = SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static void WriteTransform(BinaryWriter w, Transform t, bool includeType)
    {
        WriteVector(w, t.Translation);
        if (includeType)
            w.Write(t.Type);
        w.Write(t.Rotation.X);
        w.Write(t.Rotation.Y);
        w.Write(t.Rotation.Z);
        w.Write(t.Rotation.W);
        WriteVector(w, t.Scale);
    }
}
