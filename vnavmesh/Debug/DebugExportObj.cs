using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Navmesh.Debug;

public class DebugExportObj
{
    private class MegaMesh
    {
        public List<Vector3> Vertices = new();
        public List<(int v1, int v2, int v3)> Triangles = new();

        public unsafe void AddPCB(MeshPCB.FileNode* node, ref Matrix4x3 world)
        {
            if (node == null)
                return;
            int firstVertex = Vertices.Count;
            for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
                Vertices.Add(world.TransformCoordinate(node->Vertex(i)));
            foreach (ref var p in node->Primitives)
                Triangles.Add((p.V1 + firstVertex, p.V2 + firstVertex, p.V3 + firstVertex));
            AddPCB(node->Child1, ref world);
            AddPCB(node->Child2, ref world);
        }
    }

    // 🔴 與 DebugGameCollision 同一個形狀:Framework.Instance() 是
    //    [StaticAddress(..., isPointer: true)],會回 null;BGCollisionModule 與 SceneManager
    //    又各是一層裸指標欄位。裸解參考 null 原生指標是 AccessViolationException,
    //    在 .NET Core 屬 corrupted-state exception,try/catch 攔不到 ⇒ 只能事前逐層判空。
    private static unsafe BGCollisionModule* CollisionModuleOrNull()
    {
        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (framework == null)
                return null;
            var module = framework->BGCollisionModule;
            if (module == null || module->SceneManager == null)
                return null;
            return module;
        }
        catch
        {
            return null;
        }
    }

    public unsafe string BuildObjFromScene(bool includeStreamedMeshes, bool includeStandaloneMeshes)
    {
        var res = new MegaMesh();

        // 🔴 Framework.Instance() 宣告為 [StaticAddress(..., isPointer: true)]:產生器讀
        //    「指標的位址」再解參考一層,所以它會回 null(不帶 isPointer 的那種才保證非 null,
        //    失效時是擲 InvalidOperationException)。BGCollisionModule 與 SceneManager 又各是
        //    一層裸指標欄位。裸解參考 null 原生指標是 AccessViolationException,在 .NET Core
        //    屬 corrupted-state exception,try/catch 攔不到 ⇒ 只能事前逐層判空。
        //    fail-closed:取不到就回一份空的 .obj,而不是崩潰。
        var collisionModule = CollisionModuleOrNull();
        if (collisionModule == null)
        {
            Service.Log.Information("DebugExportObj: 取不到 BGCollisionModule / SceneManager,輸出的 .obj 會是空的。");
            return string.Empty;
        }

        // first pass - mark streamed meshes (so that we can ignore them on standalone mesh pass) and manually load & add full streamable meshes
        HashSet<nint> streamedMeshes = new();
        foreach (var s in collisionModule->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                if (coll->GetColliderType() != ColliderType.Streamed)
                    continue;
                var cast = (ColliderStreamed*)coll;
                if (cast->Header == null || cast->Elements == null)
                    continue;
                var basePath = cast->PathBaseString;
                var elements = new Span<ColliderStreamed.Element>(cast->Elements, cast->Header->NumMeshes);
                foreach (ref var e in elements)
                {
                    if (includeStandaloneMeshes && e.Mesh != null)
                    {
                        streamedMeshes.Add((nint)e.Mesh);
                    }
                    if (includeStreamedMeshes)
                    {
                        var f = Service.DataManager.GetFile($"{basePath}/tr{e.MeshId:d4}.pcb");
                        if (f != null)
                        {
                            var data = (MeshPCB.FileHeader*)Unsafe.AsPointer(ref f.Data[0]);
                            if (data->Version is 1 or 4)
                            {
                                res.AddPCB((MeshPCB.FileNode*)(data + 1), ref Matrix4x3.Identity);
                            }
                        }
                    }
                }
            }
        }

        // second pass - add standalone meshes
        if (includeStandaloneMeshes)
        {
            foreach (var s in collisionModule->SceneManager->Scenes)
            {
                foreach (var coll in s->Scene->Colliders)
                {
                    if (coll->GetColliderType() != ColliderType.Mesh || streamedMeshes.Contains((nint)coll))
                        continue;
                    var cast = (ColliderMesh*)coll;
                    if (cast->MeshIsSimple || cast->Mesh == null)
                        continue;
                    var mesh = (MeshPCB*)cast->Mesh;
                    res.AddPCB(mesh->RootNode, ref cast->World);
                }
            }
        }

        // print out to clipboard in .obj format
        var obj = new StringBuilder();
        foreach (var v in res.Vertices)
            obj.AppendLine($"v {v.X} {v.Y} {v.Z}");
        foreach (var tri in res.Triangles)
            obj.AppendLine($"f {tri.Item1 + 1} {tri.Item2 + 1} {tri.Item3 + 1}");
        return obj.ToString();
    }
}
