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

    // ⚠️ 第一參數是 Navmesh(外層容器)不是 DtNavMesh:LinkPoints 需要 nmesh.Links 來記錄
    //    連結兩端座標供偵錯視覺化。實作裡要用底層網格時取 mesh.Mesh。
    public virtual void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers) { }

    // 目前正在建置的區域 ID —— 由呼叫 CustomizeMesh 的一方（NavmeshManager.BuildNavmesh／
    // 偵錯建置器）在呼叫前設定，供 LinkPoints 產生跨版本穩定的捷徑識別鍵（territory + 兩端
    // 座標，見 CustomLinkTracker.MakeKey）。
    public uint CurrentTerritory;

    // 目前正在建置的場景定義。與 CurrentTerritory 同一個形狀:由呼叫 CustomizeMesh 的一方
    // 在呼叫前設定,供自訂化用「當下 layout 裡有沒有這個碰撞模型」判斷路線開通與否
    // (見 Z1237SinusArdorum)。
    // 🔴 **兩個寫入點都要設**:NavmeshManager.BuildNavmesh 與 Debug/DebugNavmeshCustom。
    //    只設一個的話偵錯建置器會靜默半失效(掃不到模型 ⇒ 全部退回 DevGrade fallback)。
    public SceneDefinition? CurrentScene;

    // - LinkSnapMaxDistance：FindNearestPoly 的搜尋範圍是 (5,5,5)，吸附距離超過這個值代表
    //   座標附近根本沒有預期中的平台面（例如塔還沒蓋、吸附到遠處無關的面）。取 3.5：
    //   高於正常吸附誤差（<1m）與端點刻意抬離平台的高度（約 2.5~2.7m），低於搜尋上限 5m。
    // - LinkFloodRadius / minReachablePolys：從吸附到的多邊形以「行走成本」做 Dijkstra 洪泛，
    //   可達多邊形數低於門檻＝疑似孤島（見 TryResolveLinkEndpoint 的第三道預檢）。
    private const float LinkSnapMaxDistance = 3.5f;
    private const float LinkFloodRadius = 25f;
    protected const int LinkMinReachablePolysDefault = 4;

    // ⚠️ 參數順序刻意與上游一致到第 4 個(areaId),讓上游的 Customizations 可以逐字沿用。
    //    我方獨有的三個尾參數(minReachablePolys/minDevGrade/gateLabel)排在 areaId 之後,
    //    🔴 **呼叫端一律用具名引數傳它們** —— 位置引數在上游未來又插一個參數時會靜默錯位,
    //    而型別剛好相容的話連編譯錯誤都不會有。
    protected void LinkPoints(Navmesh nmesh, Vector3 startPos, Vector3 endPos, Navmesh.AreaId areaId = Navmesh.AreaId.ClientPath, int minReachablePolys = LinkMinReachablePolysDefault, int minDevGrade = 0, string gateLabel = "")
    {
        var mesh = nmesh.Mesh;
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

        // 使用者的明確意圖（上面的停用）優先於自動閘門；閘門檢查要在使用者停用之後、
        // 端點預檢之前——猜錯 region↔門檻對應最壞只是少一條捷徑（見上方大段說明）。
        if (minDevGrade > 0 && Service.Config.GateCustomLinksByDevGrade && CosmicProgress.IsBelow(minDevGrade, out var curGrade))
        {
            var phase = CosmicProgress.PhaseForThreshold(minDevGrade);
            var reason = $"建設階段未達（需階段 {minDevGrade}{(phase > 0 ? $"／第 {phase} 期" : "")}：{gateLabel}；目前階段 {curGrade}）";
            Service.Log.Information($"[NavmeshCustomization] 該路線尚未開通，略過自訂連結：{key}（{reason}）");
            CustomLinkTracker.Record(key, CurrentTerritory, startPos, endPos, CustomLinkResult.SkippedDevGrade, reason);
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

        var refstart = InsertPointPoly(mesh, startRef, startPt, areaId);
        // 終點側額外標 Endpoint 位:FollowPath 靠它判斷「走到這裡要停下來等客戶端把路徑播完」,
        // NavmeshQuery 的成本函式也靠 (cur ^ next) == Endpoint 認出「這一步是在跨越連結本身」。
        var refend = InsertPointPoly(mesh, endRef, endPt, areaId | Navmesh.AreaId.Endpoint);

        // 供偵錯視覺化用;不序列化。要在兩端都插入成功之後才記,免得預檢失敗的連結留下鬼影。
        nmesh.Links.Add((mesh.GetPolyCenter(refstart).RecastToSystem(), mesh.GetPolyCenter(refend).RecastToSystem()));

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
    // 3. 連通性：從吸附到的多邊形沿可行走面做 Dijkstra 洪泛（FindPolysAroundCircle），可達多邊形太少＝疑似孤島。
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
    private static long InsertPointPoly(DtNavMesh mesh, long startRef, RcVec3f startPolyPoint, Navmesh.AreaId areaId)
    {
        mesh.GetTileAndPolyByRefUnsafe(startRef, out var startTile, out var startPoly);
        var p = new DtPoly(startTile.data.header.polyCount, 1)
        {
            vertCount = 1,
            flags = 1
        };
        p.SetArea((int)areaId);
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
        // Material 傳 0：這是自訂化「憑空插入」的合成碰撞體，不對應遊戲場景裡任何 bgpart／
        // collider，沒有真實 matId 可帶。按材質批次移除的自訂化（如 Z0146）比對的是真實
        // 材質值，0 不會誤中。
        existingMesh.Instances.Insert(0, new(id, transform, aabb, 0, forceSetFlags, forceClearFlags));
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
    // 與 AddOffMeshConnection 相同，唯一差別是「連結跨越 tile 邊界」時記 Warning 並略過，
    // 而不是擲 ArgumentException。
    // 🔴 為什麼需要這個版本：CustomizeSettings 是在「每一塊 tile」的建置任務裡呼叫的，從那裡擲出的例外會讓整張圖的建置中止。
    // 回傳值：true = 已加入（或兩端都不在本 tile、屬正常略過）；false = 跨 tile 被略過。
    public static bool AddOffMeshConnectionChecked(this DtNavMeshCreateParams config, Vector3 ptA, Vector3 ptB, float radius = 0.5f, bool bidirectional = false, int userID = 0)
    {
        bool insideTile(Vector3 p) => p.X >= config.bmin.X && p.Y >= config.bmin.Y && p.Z >= config.bmin.Z && p.X <= config.bmax.X && p.Y <= config.bmax.Y && p.Z <= config.bmax.Z;

        if (insideTile(ptA) != insideTile(ptB))
        {
            // Information 級：使用者跑 LogLevel 1，這是要請他回報的線索。
            Service.Log.Information($"[NavmeshCustomization] 略過跨 tile 的自訂 off-mesh 連結 {ptA} -> {ptB}：Recast 不支援跨 tile 連結。本塊 tile 範圍 {config.bmin} <=> {config.bmax}。這通常代表座標是照國際服地形寫死的，與目前客戶端的 tile 網格對不上。");
            return false;
        }

        config.AddOffMeshConnection(ptA, ptB, radius, bidirectional, userID);
        return true;
    }

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
