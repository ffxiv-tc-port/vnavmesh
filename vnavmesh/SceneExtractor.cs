using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// extract geometry from scene definition; does not interact with game state, so safe to run in background
public class SceneExtractor
{
    [Flags]
    public enum MeshType
    {
        None = 0,
        Terrain = 1 << 0,
        FileMesh = 1 << 1,
        CylinderMesh = 1 << 2,
        AnalyticShape = 1 << 3,
        AnalyticPlane = 1 << 4,

        All = (1 << 5) - 1
    }

    [Flags]
    public enum PrimitiveFlags
    {
        None = 0,
        ForceUnwalkable = 1 << 0, // this primitive can't be walked on, even if normal is fine
        FlyThrough = 1 << 1, // this primitive should not be present in voxel map
        Unlandable = 1 << 2, // this primitive can't be landed on (fly->walk transition)
        ForceWalkable = 1 << 3, // this primitive can be walked on, even though it isn't landable
        Fishable = 1 << 4, // player can fish if they have line of sight on this primitive
    }

    public record struct Primitive(int V1, int V2, int V3, PrimitiveFlags Flags, ulong Material = 0);

    public class MeshPart
    {
        public List<Vector3> Vertices = [];
        public List<Primitive> Primitives = [];
    }

    // Material 是這個實例的原始 matId（未經 matMask 篩選、未轉成 PrimitiveFlags）。
    // 之所以要留原始值：ExtractMaterialFlags 是有損轉換，轉完就分不出「哪個材質」，
    // 而部分區域的自訂化需要按材質整批移除實例（見 Z0146SouthernThanalan）。
    // ⚠️ 本欄位只被 Customizations 讀取，網格建置流程完全不看它——加這個欄位不改變任何既有行為。
    public class MeshInstance(ulong id, Matrix4x3 worldTransform, AABB worldBounds, ulong material, PrimitiveFlags forceSetPrimFlags, PrimitiveFlags forceClearPrimFlags)
    {
        public ulong Id = id;
        public ulong Material = material;
        public Matrix4x3 WorldTransform = worldTransform;
        public AABB WorldBounds = worldBounds;
        public PrimitiveFlags ForceSetPrimFlags = forceSetPrimFlags;
        public PrimitiveFlags ForceClearPrimFlags = forceClearPrimFlags;
    }

    public class Mesh
    {
        public List<MeshPart> Parts = [];
        public List<MeshInstance> Instances = [];
        public MeshType MeshType;
    }

    public Dictionary<string, Mesh> Meshes { get; private set; } = [];

    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyAnalyticPlaneSingle = "<plane one-sided>";
    private const string _keyAnalyticPlaneDouble = "<plane two-sided>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    private static List<MeshPart> _meshBox;
    private static List<MeshPart> _meshSphere;
    private static List<MeshPart> _meshCylinder;
    private static List<MeshPart> _meshPlane;

    static SceneExtractor()
    {
        _meshBox = BuildBoxMesh();
        _meshSphere = BuildSphereMesh(16);
        _meshCylinder = BuildCylinderMesh(16);
        _meshPlane = BuildPlaneMesh();
    }

    public unsafe SceneExtractor(SceneDefinition scene)
    {
        Meshes[_keyAnalyticBox] = new() { Parts = _meshBox, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticSphere] = new() { Parts = _meshSphere, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticCylinder] = new() { Parts = _meshCylinder, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticPlaneSingle] = new() { Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyAnalyticPlaneDouble] = new() { Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyMeshCylinder] = new() { Parts = _meshCylinder, MeshType = MeshType.CylinderMesh };
        foreach (var path in scene.MeshPaths.Values)
            AddMesh(path, MeshType.FileMesh);

        foreach (var terr in scene.Terrains)
        {
            var list = Service.DataManager.GetFile(terr + "/list.pcb");
            if (list != null)
            {
                fixed (byte* data = &list.Data[0])
                {
                    var header = (ColliderStreamed.FileHeader*)data;
                    foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                    {
                        var mesh = AddMesh($"{terr}/tr{entry.MeshId:d4}.pcb", MeshType.Terrain);
                        AddInstance(mesh, 0, ref Matrix4x3.Identity, ref entry.Bounds, 0, 0);
                    }
                }
            }
        }

        // 非等比縮放的球體碰撞體彙總: 每顆一行 ERR 會洗版, 改成整次擷取結束後印一行 Information.
        List<(ulong key, Vector3 semiAxes)> nonUniformSpheres = [];

        foreach (var part in scene.BgParts)
        {
            var info = ExtractBgPartInfo(scene, part.key, part.transform, part.crc, part.analytic, nonUniformSpheres);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], part.key, ref info.transform, ref info.bounds, part.matId, part.matMask);
        }

        foreach (var coll in scene.Colliders)
        {
            // try to filter out all colliders that become inactive under normal conditions (0x400)
            // excluding the invisible walls surrounding overworld zones, which additionally have bit 0x10 set
            if ((coll.matId & 0x410) == 0x400)
                continue;

            var info = ExtractColliderInfo(scene, coll.key, coll.transform, coll.crc, coll.type, nonUniformSpheres);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], coll.key, ref info.transform, ref info.bounds, coll.matId, coll.matMask);
        }

        // add fake colliders on overworld zone transitions to prevent fly pathfind from trying to go OOB there
        foreach (var ex in scene.ExitRanges)
        {
            var transform = new Matrix4x3(ex.transform.Compose());
            var bounds = CalculateBoxBounds(ref transform);
            AddInstance(Meshes[_keyAnalyticBox], ex.key, ref transform, ref bounds, 0x202411, 0x7FFFFFFFF);
        }

        ReportNonUniformSpheres(scene, nonUniformSpheres);
    }

    public (string path, Matrix4x3 transform, AABB bounds) ExtractBgPartInfo(SceneDefinition scene, ulong key, Transform instanceTransform, uint crc, bool analytic, List<(ulong key, Vector3 semiAxes)>? nonUniformSpheres = null)
    {
        if (analytic)
        {
            if (scene.AnalyticShapes.TryGetValue(crc, out var shape))
            {
                // see Client::LayoutEngine::Layer::BgPartsLayoutInstance_calculateSRT
                // S1*T1 * S*R*T = (S1*S*R,    0
                //                  T1*SR + T, 1)
                var scaleVector = (shape.bbMax - shape.bbMin) * 0.5f;
                if (shape.transform.Type == (int)FileLayerGroupAnalyticCollider.Type.Cylinder)
                    // z component is ignored for cylinder meshes and possibly others
                    scaleVector.Z = scaleVector.X;

                var mtxBounds = Matrix4x4.CreateScale(scaleVector);
                mtxBounds.Translation = (shape.bbMin + shape.bbMax) * 0.5f;
                var fullTransform = mtxBounds * shape.transform.Compose() * instanceTransform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);
                var (path, bounds) = (FileLayerGroupAnalyticCollider.Type)shape.transform.Type switch
                {
                    FileLayerGroupAnalyticCollider.Type.Box => (_keyAnalyticBox, CalculateBoxBounds(ref resultingTransform)),
                    FileLayerGroupAnalyticCollider.Type.Sphere => (_keyAnalyticSphere, CalculateSphereBounds(key, ref resultingTransform, nonUniformSpheres)),
                    FileLayerGroupAnalyticCollider.Type.Cylinder => (_keyMeshCylinder, CalculateBoxBounds(ref resultingTransform)), // TODO: we can probably do a tighter fit for cylinders...
                    FileLayerGroupAnalyticCollider.Type.Plane => (_keyAnalyticPlaneSingle, CalculatePlaneBounds(ref resultingTransform)),
                    _ => ("", default)
                };
                return (path, resultingTransform, bounds);
            }
            return ("", Matrix4x3.Identity, default);
        }
        else
        {
            var path = scene.MeshPaths[crc];
            var transform = new Matrix4x3(instanceTransform.Compose());
            var bounds = CalculateMeshBounds(Meshes[path], ref transform);
            return (path, transform, bounds);
        }
    }

    public (string path, Matrix4x3 transform, AABB bounds) ExtractColliderInfo(SceneDefinition scene, ulong key, Transform instanceTransform, uint crc, FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType type, List<(ulong key, Vector3 semiAxes)>? nonUniformSpheres = null)
    {
        var transform = new Matrix4x3(instanceTransform.Compose());
        var (path, bounds) = type switch
        {
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box => (_keyAnalyticBox, CalculateBoxBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere => (_keyAnalyticSphere, CalculateSphereBounds(key, ref transform, nonUniformSpheres)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder => (_keyAnalyticCylinder, CalculateBoxBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane => (_keyAnalyticPlaneSingle, CalculatePlaneBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh => (scene.MeshPaths[crc], CalculateMeshBounds(Meshes[scene.MeshPaths[crc]], ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided => (_keyAnalyticPlaneDouble, CalculatePlaneBounds(ref transform)),
            _ => ("", default)
        };
        return (path, transform, bounds);
    }

    private unsafe Mesh AddMesh(string path, MeshType type)
    {
        var mesh = new Mesh();
        var f = Service.DataManager.GetFile(path);
        if (f != null)
        {
            fixed (byte* rawData = &f.Data[0])
            {
                var data = (MeshPCB.FileHeader*)rawData;
                if (data->Version is 1 or 4)
                {
                    FillFromFileNode(mesh.Parts, (MeshPCB.FileNode*)(data + 1));
                }
            }
        }
        mesh.MeshType = type;
        Meshes[path] = mesh;
        return mesh;
    }

    private void AddInstance(Mesh mesh, ulong id, ref Matrix4x3 worldTransform, ref AABB worldBounds, ulong matId, ulong matMask)
    {
        var instance = new MeshInstance(id, worldTransform, worldBounds, matId, ExtractMaterialFlags(matMask & matId), ExtractMaterialFlags(matMask & ~matId));
        mesh.Instances.Add(instance);
    }

    private static AABB CalculateBoxBounds(ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        for (int i = 0; i < 8; ++i)
        {
            var p = ((i & 1) != 0 ? world.Row0 : -world.Row0) + ((i & 2) != 0 ? world.Row1 : -world.Row1) + ((i & 4) != 0 ? world.Row2 : -world.Row2) + world.Row3;
            res.Min = Vector3.Min(res.Min, p);
            res.Max = Vector3.Max(res.Max, p);
        }
        return res;
    }

    // 球體實例的世界包圍盒。
    // _meshSphere 是「單位球」(BuildSphereMesh 的頂點全部落在 |v| = 1 上), 所以套上 world 之後的實體是橢球,
    // 三個半軸長分別等於 Row0/Row1/Row2 的長度; 沿世界軸 k 的半長 = 3x3 部分第 k 個「行(column)」的長度
    // (TransformCoordinate 是列向量慣例: worldX = dot((M11,M21,M31), local), 對 |local| <= 1 取極大值就是該行的長度)。
    //
    // 🔴 原本的寫法拿 Row0.Length() 當三個軸共用的半徑。等比縮放(含旋轉)時三個行長都等於它, 結果不變;
    //    但非等比縮放時只要別的軸比 Row0 長, 包圍盒就會「低估」, 而 WorldBounds 有兩個吃低估虧的用途:
    //      1. NavmeshRasterizer.RasterizeMesh 開頭的整塊剔除 (Max <= bmin || Min >= bmax 就整個實例跳過)
    //         => 低估會把真的伸進本 tile 的橢球誤判成完全在外, 該實例的碰撞面完全不進網格 = 導航破洞。
    //      2. NavmeshRasterizer.Rasterize 的 perMeshInteriors 內部填實範圍 (NavmeshBuilder.cs 對 AnalyticShape 這一路是開的)
    //         => 低估會讓橢球內部有一段沒被填實, 尋路可能從實心物體內部穿過去。
    //    反過來「高估」是安全的: 剔除只是少剔一點、逐三角形裁切照樣正確, 而 FillInterior 對 cnt == 0 的格子直接跳過。
    // => 一律改算精確的橢球 AABB, 它永遠不小於真實幾何。
    private static AABB CalculateSphereBounds(ulong id, ref Matrix4x3 world, List<(ulong key, Vector3 semiAxes)>? nonUniformSpheres = null)
    {
        var semiAxes = new Vector3(world.Row0.Length(), world.Row1.Length(), world.Row2.Length());
        if (nonUniformSpheres != null && (Math.Abs(semiAxes.X - semiAxes.Y) > 0.1 || Math.Abs(semiAxes.X - semiAxes.Z) > 0.1))
            nonUniformSpheres.Add((id, semiAxes));
        var halfExtents = new Vector3(
            new Vector3(world.M11, world.M21, world.M31).Length(),
            new Vector3(world.M12, world.M22, world.M32).Length(),
            new Vector3(world.M13, world.M23, world.M33).Length());
        return new AABB() { Min = world.Row3 - halfExtents, Max = world.Row3 + halfExtents };
    }

    // 非等比縮放的球體本身不是錯誤(上面已經按精確橢球處理), 但它是「這個區域的碰撞資料長得不太一樣」的線索,
    // 使用者回報時帶得出區域與實例編號才有用 => 整批彙總成一行 Information (使用者跑 LogLevel 2, Debug 收不到)。
    // key 的形狀見 FFXIVClientStructs 的 LayoutManager.InstancesByType 註解: InstanceId << 32 | SubId。
    private static void ReportNonUniformSpheres(SceneDefinition scene, List<(ulong key, Vector3 semiAxes)> nonUniformSpheres)
    {
        if (nonUniformSpheres.Count == 0)
            return;

        var worst = nonUniformSpheres[0];
        var worstSpread = SemiAxisSpread(worst.semiAxes);
        for (int i = 1; i < nonUniformSpheres.Count; ++i)
        {
            var spread = SemiAxisSpread(nonUniformSpheres[i].semiAxes);
            if (spread > worstSpread)
            {
                worst = nonUniformSpheres[i];
                worstSpread = spread;
            }
        }

        var worstInstance = worst.key >> 32;
        Service.Log.Information($"[SceneExtractor] 區域 {scene.TerritoryID}: 有 {nonUniformSpheres.Count} 個球體碰撞體是非等比縮放, 已按精確的橢球包圍盒處理(不影響導航正確性)。差距最大的是實例 {worstInstance:X} (key {worst.key:X}), 半軸 {worst.semiAxes:f3}");
    }

    private static float SemiAxisSpread(Vector3 semiAxes)
        => Math.Max(semiAxes.X, Math.Max(semiAxes.Y, semiAxes.Z)) - Math.Min(semiAxes.X, Math.Min(semiAxes.Y, semiAxes.Z));

    private static AABB CalculateMeshBounds(Mesh mesh, ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        foreach (var part in mesh.Parts)
        {
            foreach (var v in part.Vertices)
            {
                var p = world.TransformCoordinate(v);
                res.Min = Vector3.Min(res.Min, p);
                res.Max = Vector3.Max(res.Max, p);
            }
        }
        return res;
    }

    private static AABB CalculatePlaneBounds(ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        for (int i = 0; i < 4; ++i)
        {
            var p = ((i & 1) != 0 ? world.Row0 : -world.Row0) + ((i & 2) != 0 ? world.Row1 : -world.Row1) + world.Row3;
            res.Min = Vector3.Min(res.Min, p);
            res.Max = Vector3.Max(res.Max, p);
        }
        return res;
    }

    private unsafe void FillFromFileNode(List<MeshPart> parts, MeshPCB.FileNode* node)
    {
        if (node == null)
            return;
        parts.Add(BuildMeshFromNode(node));
        FillFromFileNode(parts, node->Child1);
        FillFromFileNode(parts, node->Child2);
    }

    private unsafe MeshPart BuildMeshFromNode(MeshPCB.FileNode* node)
    {
        var part = new MeshPart();
        for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
            part.Vertices.Add(node->Vertex(i));
        foreach (ref var p in node->Primitives)
            part.Primitives.Add(new(p.V1, p.V2, p.V3, ExtractMaterialFlags(p.Material), p.Material));
        return part;
    }

    private static ulong[] _materialsFlyThrough = [
        0x100000, // generally set on the invisible walls surrounding walkable areas that can be flown from
        0x1000000, // if this bit is set, flying upwards into the surface will trigger dive -> fly (or swim) transition
        0x800000, // not really sure what this is, appears on invisible roof of divable zones

        // 0xBC00 can be dived into, but 0xB800 cannot. both allow fishing
        // 0x400 is some kind of generic "this collider is conditionally active" flag, but it's set on zone walls, so we can't skip it
        0xB400,
    ];

    private PrimitiveFlags ExtractMaterialFlags(ulong mat)
    {
        var res = PrimitiveFlags.None;
        foreach (var fly in _materialsFlyThrough)
            if ((mat & fly) == fly)
                res |= PrimitiveFlags.FlyThrough;

        if ((mat & 0x200000) != 0)
            res |= PrimitiveFlags.Unlandable;

        // i've only seen this on holes in arenas
        // ⚠️ 上游用這一條取代了 Z1242Yuweyawata 的手動圓柱碰撞體,並把那個檔刪掉。
        //    我方**保留**那個檔:這個材質位在台服的資料裡有沒有被設起來無法離線證明,
        //    而兩邊設的都是同一個 ForceUnwalkable,重複標記是冪等的。
        //    假設不成立(台服沒設這個位)時,保留下來的手動圓柱仍然擋得住那個洞;
        //    刪掉的話就是靜默的尋路退步(走進最終王場地的洞裡),不會有任何錯誤訊息。
        if ((mat & 0x2000000) != 0)
            res |= PrimitiveFlags.ForceUnwalkable;

        // 0x11 is set on all the invisible walls surrounding every zone; some are not marked as unlandable so we can't just use that
        // some regular terrain materials have 0x10 set as well (see flowers in il mheg) which is why we check for both bits here
        if ((mat & 0x1F) == 0x11)
            res |= PrimitiveFlags.Unlandable | PrimitiveFlags.ForceUnwalkable;

        if ((mat & 0x8000) != 0)
            res |= PrimitiveFlags.Fishable;

        return res;
    }

    public static List<MeshPart> BuildBoxMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new(-1, -1, -1));
        mesh.Vertices.Add(new(-1, -1, +1));
        mesh.Vertices.Add(new(+1, -1, -1));
        mesh.Vertices.Add(new(+1, -1, +1));
        mesh.Vertices.Add(new(-1, +1, -1));
        mesh.Vertices.Add(new(-1, +1, +1));
        mesh.Vertices.Add(new(+1, +1, -1));
        mesh.Vertices.Add(new(+1, +1, +1));
        // bottom (y=-1)
        mesh.Primitives.Add(new(0, 2, 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(1, 2, 3, PrimitiveFlags.None));
        // top (y=+1)
        mesh.Primitives.Add(new(5, 7, 4, PrimitiveFlags.None));
        mesh.Primitives.Add(new(4, 7, 6, PrimitiveFlags.None));
        // left (x=-1)
        mesh.Primitives.Add(new(0, 1, 4, PrimitiveFlags.None));
        mesh.Primitives.Add(new(4, 1, 5, PrimitiveFlags.None));
        // right (x=1)
        mesh.Primitives.Add(new(2, 6, 3, PrimitiveFlags.None));
        mesh.Primitives.Add(new(3, 6, 7, PrimitiveFlags.None));
        // front (z=-1)
        mesh.Primitives.Add(new(0, 4, 2, PrimitiveFlags.None));
        mesh.Primitives.Add(new(2, 4, 6, PrimitiveFlags.None));
        // back (z=1)
        mesh.Primitives.Add(new(1, 3, 5, PrimitiveFlags.None));
        mesh.Primitives.Add(new(5, 3, 7, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildSphereMesh(int numSegments)
    {
        var mesh = new MeshPart();
        var angle = 360.Degrees() / numSegments;
        var maxParallel = numSegments / 4 - 1;
        for (int p = -maxParallel; p <= maxParallel; ++p)
        {
            var r = (p * angle).ToDirection();
            for (int i = 0; i < numSegments; ++i)
            {
                var v = (i * angle).ToDirection() * r.Y;
                mesh.Vertices.Add(new(v.X, r.X, v.Y));
            }
        }
        var icap = mesh.Vertices.Count;
        mesh.Vertices.Add(new(0, -1, 0));
        mesh.Vertices.Add(new(0, +1, 0));
        // sides
        for (int p = 0; p < maxParallel * 2; ++p)
        {
            var ip = p * numSegments;
            for (int i = 0; i < numSegments - 1; ++i)
            {
                var iv = ip + i;
                mesh.Primitives.Add(new(iv, iv + 1, iv + numSegments, PrimitiveFlags.None));
                mesh.Primitives.Add(new(iv + numSegments, iv + 1, iv + numSegments + 1, PrimitiveFlags.None));
            }
            mesh.Primitives.Add(new(ip + numSegments - 1, ip, ip + numSegments * 2 - 1, PrimitiveFlags.None));
            mesh.Primitives.Add(new(ip + numSegments * 2 - 1, ip, ip + numSegments, PrimitiveFlags.None));
        }
        // bottom
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add(new(i + 1, i, icap, PrimitiveFlags.None));
        mesh.Primitives.Add(new(0, numSegments - 1, icap, PrimitiveFlags.None));
        // top
        var itop = icap - numSegments;
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add(new(itop + i, itop + i + 1, icap + 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(itop + numSegments - 1, itop, icap + 1, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildCylinderMesh(int numSegments)
    {
        var mesh = new MeshPart();
        var angle = 360.Degrees() / numSegments;
        for (int i = 0; i < numSegments; ++i)
        {
            // note: we try to emulate hardcoded pcb mesh, that's why we do an extra +5 here...
            var p = ((i + 5) * angle).ToDirection();
            mesh.Vertices.Add(new(p.X, -1, p.Y));
            mesh.Vertices.Add(new(p.X, +1, p.Y));
        }
        mesh.Vertices.Add(new(0, -1, 0));
        mesh.Vertices.Add(new(0, +1, 0));
        // sides
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2;
            mesh.Primitives.Add(new(iv, iv + 2, iv + 1, PrimitiveFlags.None));
            mesh.Primitives.Add(new(iv + 1, iv + 2, iv + 3, PrimitiveFlags.None));
        }
        var ivn = (numSegments - 1) * 2;
        mesh.Primitives.Add(new(ivn, 0, ivn + 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(ivn + 1, 0, 1, PrimitiveFlags.None));
        // bottom
        var bcenter = numSegments * 2;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2;
            mesh.Primitives.Add(new(iv + 2, iv, bcenter, PrimitiveFlags.None));
        }
        mesh.Primitives.Add(new(0, ivn, bcenter, PrimitiveFlags.None));
        // top
        var tcenter = bcenter + 1;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2 + 1;
            mesh.Primitives.Add(new(iv, iv + 2, tcenter, PrimitiveFlags.None));
        }
        mesh.Primitives.Add(new(ivn + 1, 1, tcenter, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildPlaneMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new(-1, +1, 0));
        mesh.Vertices.Add(new(-1, -1, 0));
        mesh.Vertices.Add(new(+1, -1, 0));
        mesh.Vertices.Add(new(+1, +1, 0));
        mesh.Primitives.Add(new(0, 1, 2, PrimitiveFlags.None));
        mesh.Primitives.Add(new(0, 2, 3, PrimitiveFlags.None));
        return [mesh];
    }
}
