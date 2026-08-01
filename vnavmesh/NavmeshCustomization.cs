using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Navmesh;

// base class for per-territory navmesh customizations
public class NavmeshCustomization
{
    // every time defaults change, we need to bump global navmesh version - this should be kept at zero
    // every time customization changes, we can bump the local version field, to avoid invalidating whole cache
    // each derived class should set it to non-zero value
    public virtual int Version => 0;

    public NavmeshSettings Settings = new();

    public virtual bool IsFlyingSupported(SceneDefinition definition) => Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(definition.TerritoryID)?.TerritoryIntendedUse.RowId is 1 or 49 or 47; // 1 is normal outdoor, 49 is island, 47 is Diadem

    // this is a customization point to add or remove colliders in the scene
    public virtual void CustomizeScene(SceneExtractor scene) { }

    public virtual void CustomizeSettings(DtNavMeshCreateParams config) { }

    public virtual void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers) { }

    // 目前正在建置的區域 ID —— 由呼叫 CustomizeMesh 的一方（NavmeshManager.BuildNavmesh／
    // 偵錯建置器）在呼叫前設定，供 LinkPoints 產生跨版本穩定的捷徑識別鍵（territory + 兩端
    // 座標，見 CustomLinkTracker.MakeKey）。註：customization 實例是每類單例；NavmeshManager
    // 同時間只跑一個建置任務、偵錯建置器也只針對目前區域，且目前沒有任何類別掛多個
    // territory 屬性，所以這裡不做執行緒防護。就算未來真的並行到同一實例，錯的也只是
    // 識別鍵的 territory 前綴（分組顯示錯地方），不影響網格本身。
    public uint CurrentTerritory;

    // 端點預檢的參數（啟發式；每次略過都會記 Warning 含實測數字，實機 log 若顯示誤殺／漏殺可據以調整）：
    // - LinkSnapMaxDistance：FindNearestPoly 的搜尋範圍是 (5,5,5)，吸附距離超過這個值代表
    //   座標附近根本沒有預期中的平台面（例如塔還沒蓋、吸附到遠處無關的面）。取 3.5：
    //   高於正常吸附誤差（<1m）與端點刻意抬離平台的高度（約 2.5~2.7m），低於搜尋上限 5m。
    // - LinkFloodRadius / minReachablePolys：從吸附到的多邊形以「行走成本」做 Dijkstra 洪泛，
    //   可達多邊形數低於門檻＝疑似孤島（見 TryResolveLinkEndpoint 的第三道預檢）。
    //   預設門檻刻意取低（4），避免誤殺既有區域（如 Z0132 通往小型室內的連結）；
    //   已知有地形分歧的區域（Z1237）自行傳更嚴的門檻。
    private const float LinkSnapMaxDistance = 3.5f;
    private const float LinkFloodRadius = 25f;
    protected const int LinkMinReachablePolysDefault = 4;

    protected void LinkPoints(DtNavMesh mesh, Vector3 startPos, Vector3 endPos, int minReachablePolys = LinkMinReachablePolysDefault)
    {
        // 🔴 台服實測（2026-07-31 / 2026-08-01）：Z1237SinusArdorum（宇宙探索月面基地）的
        // 自訂連結座標是照國際服「完工態」地形寫死的，台服仍在建設階段，兩種失敗都發生過：
        // 1. 端點附近找不到多邊形（如 <-104.5, 53.2, 727.3>）→ 舊版 InsertPointPoly 丟出
        //    ArgumentException，讓「整張圖」的網格建置中止 —— 該區域完全無法尋路。
        //    下游徵狀：ICE 的宇宙工具永遠卡在 HubReturn，因為它在等 Nav.IsReady。
        // 2. 端點吸附到「附近但不連通」的多邊形 → 捷徑建了但尋路永遠走不到它，
        //    症狀只是靜默繞遠路，log 完全沒有線索。
        // 所以插入前對兩個端點各做三道預檢（找得到多邊形／吸附距離／連通性），任何一道
        // 不過就記 Warning 並略過這一條連結 —— 絕不讓單一捷徑弄壞整張網格，也絕不靜默。
        // ⚠️ 必須「兩端都驗完才開始插入」：InsertPointPoly 會直接改動 tile（polyCount/
        // vertCount 遞增、陣列 resize），先插了 start 再發現 end 不行就會留下孤兒多邊形。
        // 每條捷徑的處置結果（成功／預檢略過／使用者停用）都記進 CustomLinkTracker，
        // 供「自訂捷徑」分頁顯示；使用者停用的記 Information（與預檢的 Warning 區分）。
        var key = CustomLinkTracker.MakeKey(CurrentTerritory, startPos, endPos);
        if (Service.Config.DisabledCustomLinks.Contains(key))
        {
            Service.Log.Information($"[NavmeshCustomization] 使用者已停用自訂連結，略過：{key}");
            CustomLinkTracker.Record(key, CurrentTerritory, startPos, endPos, CustomLinkResult.DisabledByUser, "使用者停用");
            return;
        }

        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        if (!TryResolveLinkEndpoint(query, filter, startPos, endPos, "起點", minReachablePolys, out var startRef, out var startPt, out var startFail))
        {
            CustomLinkTracker.Record(key, CurrentTerritory, startPos, endPos, CustomLinkResult.SkippedPrecheck, startFail);
            return;
        }
        if (!TryResolveLinkEndpoint(query, filter, endPos, startPos, "終點", minReachablePolys, out var endRef, out var endPt, out var endFail))
        {
            CustomLinkTracker.Record(key, CurrentTerritory, startPos, endPos, CustomLinkResult.SkippedPrecheck, endFail);
            return;
        }

        var refstart = InsertPointPoly(mesh, startRef, startPt);
        var refend = InsertPointPoly(mesh, endRef, endPt);

        mesh.GetTileAndPolyByRefUnsafe(refstart, out var startTile, out var startPoly);

        // start point -> end point link
        var idx = mesh.AllocLink(startTile);
        DtLink link = startTile.links[idx];
        link.refs = refend;
        link.edge = 0;
        link.side = 0;
        link.bmin = link.bmax = 0;
        link.next = startTile.polyLinks[startPoly.index];
        startTile.polyLinks[startPoly.index] = idx;

        CustomLinkTracker.Record(key, CurrentTerritory, startPos, endPos, CustomLinkResult.Linked, "通過");
    }

    // 只做查詢、不改動網格：給 LinkPoints 在插入前預檢單一端點用。三道預檢：
    // 1. 找得到多邊形（FindNearestPoly 成功且 ref != 0）；
    // 2. 吸附距離 <= LinkSnapMaxDistance（座標附近真的有預期中的面）；
    // 3. 連通性：從吸附到的多邊形沿可行走面做 Dijkstra 洪泛（FindPolysAroundCircle），
    //    可達多邊形太少＝疑似孤島（例如吸附到還沒蓋好的建物頂面）—— 這正是「捷徑建了
    //    但尋路永遠用不到、只會靜默繞遠路」的根因。但「少」不必然是壞：有些連結的
    //    目的端本來就是獨立的小區域（室內、洞窟），所以再給一次機會 —— 若這個端點與
    //    連結的另一端在網格上真的走得通（FindPath 完整成功、非 partial），照樣放行。
    //    注意方向：從「疑似孤島端」往另一端找，孤島元件小所以失敗得快。
    // 任何一道不過 → Warning（含兩端座標與 poly ref）+ false，並經 failReason 回傳一句
    // 簡短原因（zh-TW，直接存進 CustomLinkTracker 給「自訂捷徑」分頁顯示）。
    private static bool TryResolveLinkEndpoint(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 pos, Vector3 otherPos, string label, int minReachablePolys, out long polyRef, out RcVec3f snapped, out string failReason)
    {
        failReason = "";
        var status = query.FindNearestPoly(pos.SystemToRecast(), new(5, 5, 5), filter, out polyRef, out snapped, out _);
        if (status.Failed() || polyRef == 0)
        {
            Service.Log.Warning($"[NavmeshCustomization] 略過自訂連結 {pos} -> {otherPos}（{label}）：端點附近找不到多邊形。這通常代表該座標是照國際服／完工態地形寫死的，與目前客戶端不符。");
            failReason = $"{label}附近找不到多邊形（地形可能尚未建成）";
            return false;
        }

        var snapDist = (snapped.RecastToSystem() - pos).Length();
        if (snapDist > LinkSnapMaxDistance)
        {
            Service.Log.Warning($"[NavmeshCustomization] 略過自訂連結 {pos} -> {otherPos}（{label}）：端點只能吸附到 {snapDist:f1}m 外的多邊形 {polyRef:X}（上限 {LinkSnapMaxDistance:f1}m），附近沒有預期中的平台面。");
            failReason = $"{label}只能吸附到 {snapDist:f1}m 外的面（上限 {LinkSnapMaxDistance:f1}m）";
            return false;
        }

        List<long> floodRefs = [], floodParents = [];
        List<float> floodCosts = [];
        status = query.FindPolysAroundCircle(polyRef, snapped, LinkFloodRadius, filter, ref floodRefs, ref floodParents, ref floodCosts);
        if (!status.Failed() && floodRefs.Count >= minReachablePolys)
            return true;

        var otherStatus = query.FindNearestPoly(otherPos.SystemToRecast(), new(5, 5, 5), filter, out var otherRef, out var otherPt, out _);
        if (!otherStatus.Failed() && otherRef != 0)
        {
            List<long> path = [];
            var pathStatus = query.FindPath(polyRef, otherRef, snapped, otherPt, filter, ref path, new(DtDefaultQueryHeuristic.Default, 0, 0));
            if (pathStatus.Succeeded() && !pathStatus.IsPartial() && path.Count > 0)
                return true; // 跟另一端走得通，屬於可到達的區域，不是孤島
        }

        Service.Log.Warning($"[NavmeshCustomization] 略過自訂連結 {pos} -> {otherPos}（{label}）：端點吸附到的多邊形 {polyRef:X} 在 {LinkFloodRadius:f0}m 行走範圍內只連得到 {floodRefs.Count} 個多邊形（門檻 {minReachablePolys}），與另一端也走不通，視為不連通的孤島。");
        failReason = $"{label}吸附處疑似不連通的孤島（{LinkFloodRadius:f0}m 內僅 {floodRefs.Count} 個可達面，門檻 {minReachablePolys}）";
        return false;
    }

    // 呼叫端保證 startRef/startPolyPoint 已由 TryResolveLinkEndpoint 驗證過。
    private static long InsertPointPoly(DtNavMesh mesh, long startRef, RcVec3f startPolyPoint)
    {
        mesh.GetTileAndPolyByRefUnsafe(startRef, out var startTile, out var startPoly);
        var p = new DtPoly(startTile.data.header.polyCount, 1)
        {
            vertCount = 1,
            flags = 1
        };
        p.SetArea(Navmesh.OffMeshEndpoint);
        p.SetPolyType(DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION);
        p.verts[0] = startTile.data.header.vertCount;

        startTile.data.header.polyCount += 1;
        startTile.data.header.vertCount += 1;
        Array.Resize(ref startTile.data.polys, startTile.data.header.polyCount);
        Array.Resize(ref startTile.data.verts, startTile.data.header.vertCount * 3);

        // add new poly to mesh
        startTile.data.polys[^1] = p;
        startTile.data.verts[^3] = startPolyPoint.X;
        startTile.data.verts[^2] = startPolyPoint.Y;
        startTile.data.verts[^1] = startPolyPoint.Z;

        Array.Resize(ref startTile.polyLinks, startTile.polyLinks.Length + 1);
        startTile.polyLinks[^1] = DtNavMesh.DT_NULL_LINK;

        var salt = DtNavMesh.DecodePolyIdSalt(startRef);
        var pointRef = DtNavMesh.EncodePolyId(salt, startTile.index, p.index);

        // link point to the polygon it lies inside
        var idx = mesh.AllocLink(startTile);
        var link = startTile.links[idx];
        link.refs = startRef;
        link.edge = 0;
        link.side = 0xff;
        link.bmin = link.bmax = 0;
        startTile.polyLinks[p.index] = idx;

        // link owning polygon to point
        idx = mesh.AllocLink(startTile);
        link = startTile.links[idx];
        link.refs = pointRef;
        link.edge = 0xff;
        link.side = 0xff;
        link.bmin = link.bmax = 0;
        link.next = startTile.polyLinks[startPoly.index];
        startTile.polyLinks[startPoly.index] = idx;

        return pointRef;
    }
}

// attribute that defines which territories particular customization applies to
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class CustomizationTerritoryAttribute : Attribute
{
    public uint TerritoryID;

    public CustomizationTerritoryAttribute(uint territoryID) => TerritoryID = territoryID;
}

// registry containing all customizations
public static class NavmeshCustomizationRegistry
{
    public static NavmeshCustomization Default = new();
    public static Dictionary<uint, NavmeshCustomization> PerTerritory = new();

    static NavmeshCustomizationRegistry()
    {
        var baseType = typeof(NavmeshCustomization);
        foreach (var t in Assembly.GetExecutingAssembly().DefinedTypes.Where(t => t.IsSubclassOf(baseType)))
        {
            var instance = Activator.CreateInstance(t) as NavmeshCustomization;
            if (instance == null)
            {
                Service.Log.Error($"Failed to create instance of customization class {t}");
                continue;
            }

            foreach (var attr in t.GetCustomAttributes<CustomizationTerritoryAttribute>())
            {
                PerTerritory.Add(attr.TerritoryID, instance);
            }
        }
    }

    public static NavmeshCustomization ForTerritory(uint id) => PerTerritory.GetValueOrDefault(id, Default);
}

public static class SceneExtensions
{
    private static void InsertAxisAlignedCollider(this SceneExtractor scene, string meshKey, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M22 = scale.Y;
        transform.M33 = scale.Z;
        transform.Row3 = worldTransform;
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };
        var existingMesh = scene.Meshes[meshKey];
        var id = 0xbaadf00d00000001ul + (uint)existingMesh.Instances.Count;
        existingMesh.Instances.Insert(0, new(id, transform, aabb, forceSetFlags, forceClearFlags));
    }

    public static void InsertAABoxCollider(this SceneExtractor scene, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) => InsertAxisAlignedCollider(scene, "<box>", scale, worldTransform, forceSetFlags, forceClearFlags);

    public static void InsertAABoxCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertAABoxCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }

    public static void InsertCylinderCollider(this SceneExtractor scene, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) => InsertAxisAlignedCollider(scene, "<cylinder>", scale, worldTransform, forceSetFlags, forceClearFlags);
    public static void InsertCylinderCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertCylinderCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }
}

public static class CreateParamsExtensions
{
    public static void AddOffMeshConnection(this DtNavMeshCreateParams config, Vector3 ptA, Vector3 ptB, float radius = 0.5f, bool bidirectional = false, int userID = 0)
    {
        bool insideTile(Vector3 p) => p.X >= config.bmin.X && p.Y >= config.bmin.Y && p.Z >= config.bmin.Z && p.X <= config.bmax.X && p.Y <= config.bmax.Y && p.Z <= config.bmax.Z;

        var aInside = insideTile(ptA);
        var bInside = insideTile(ptB);

        if (aInside != bInside)
        {
            Service.Log.Error("This off-mesh connection would span two tiles, but Recast doesn't support these. Please adjust the endpoints or customize the mesh tile size so that both points are inside one tile.");
            Service.Log.Error($"Bounding box of matched tile: {config.bmin} <=> {config.bmax}");
            throw new ArgumentException("Invalid inter-tile off-mesh connection");
        }

        if (!aInside && !bInside)
            return;

        Extend(ref config.offMeshConVerts, 6);
        config.offMeshConVerts[^6] = ptA.X;
        config.offMeshConVerts[^5] = ptA.Y;
        config.offMeshConVerts[^4] = ptA.Z;
        config.offMeshConVerts[^3] = ptB.X;
        config.offMeshConVerts[^2] = ptB.Y;
        config.offMeshConVerts[^1] = ptB.Z;

        Extend(ref config.offMeshConDir, 1);
        config.offMeshConDir[^1] = bidirectional ? DtNavMesh.DT_OFFMESH_CON_BIDIR : 0;

        Extend(ref config.offMeshConFlags, 1);
        config.offMeshConFlags[^1] = 1;

        config.offMeshConCount++;

        Extend(ref config.offMeshConRad, 1);
        config.offMeshConRad[^1] = radius;

        Extend(ref config.offMeshConAreas, 1);
        config.offMeshConAreas[^1] = RcConstants.RC_WALKABLE_AREA;

        Extend(ref config.offMeshConUserID, 1);
        config.offMeshConUserID[^1] = userID;
    }

    private static void Extend<T>([NotNull] ref T[]? arr, int add)
    {
        arr ??= [];
        Array.Resize(ref arr, arr.Length + add);
    }
}
