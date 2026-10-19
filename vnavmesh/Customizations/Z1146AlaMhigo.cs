using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace Navmesh.Customizations;

// 拿掉一塊碰撞網格裡的兩個三角形並加一個不可行走的方盒，資料取自 AtmoOmen/ffxiv_navmesh-cn。
// 三角形以「第幾個 part／第幾個 primitive」指名：與上游的 .pcb 解析順序相同才對得上，超出範圍就不動。
[CustomizationTerritory(1146)]
internal class Z1146AlaMhigo : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("bg/ex2/01_gyr_g3/dun/g3d2/collision/tr1117.pcb", out var mesh) && mesh.Parts.Count > 29)
        {
            var prims = mesh.Parts[29].Primitives;
            foreach (var i in (int[])[7, 9])
                if (i < prims.Count)
                    prims[i] = new(0, 0, 0, SceneExtractor.PrimitiveFlags.None, 0x4000ul);
        }

        scene.InsertAABoxCollider(new AABB { Min = new(-260.90683f, 26.5f, 94.2588f), Max = new(-255.9119f, 29.5f, 96.54003f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
    }
}
