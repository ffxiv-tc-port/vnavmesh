using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace Navmesh.Customizations;

// 兩個不可行走的方盒，座標取自 AtmoOmen/ffxiv_navmesh-cn。
// 上游另有 19 條短距離雙向連結（樓梯口、門檻），我方網格上兩端本來就直線走得到，不收。
[CustomizationTerritory(131)]
internal class Z0131UldahStepsofThal : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        scene.InsertAABoxCollider(new AABB { Min = new(-61.9686f, 38f, -12.888067f), Max = new(-57.172184f, 40.754723f, -10.959627f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
        scene.InsertAABoxCollider(new AABB { Min = new(2.8162823f, 12.217501f, 66.46936f), Max = new(10.950735f, 16.4005f, 69.52377f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
    }
}
