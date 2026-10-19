using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

using System.Collections.Generic;

namespace Navmesh.Customizations;

// 八個細長方塊加高、四個不可行走的方盒與一段往下跳，座標取自 AtmoOmen/ffxiv_navmesh-cn。
// 上游另有 6 條碼頭邊的雙向連結，我方網格上兩端本來就直線走得到，不收；
// 上游也改了本區的 AgentRadius／Filtering／Region 大小，那會重算整區網格，沒有一起搬。
[CustomizationTerritory(139)]
internal class Z0139UpperLaNoscea : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // 要先於 InsertAABoxCollider：後者插在 <box> 的最前面，會讓索引提示整批錯位（錯位時退回整表找鍵）。
        if (scene.Meshes.TryGetValue("<box>", out var box))
        {
            ReplaceInstanceTransform(box, 0x39F8F400000000ul, 45, new() { Row0 = new(1.2109348f, 0f, -0.57903975f), Row1 = new(0f, 3.678f, 0f), Row2 = new(0.08150698f, 0f, 0.17045398f), Row3 = new(406.5698f, 8.528983f, 22.765104f) });
            ReplaceInstanceTransform(box, 0x39F8F500000000ul, 46, new() { Row0 = new(1.2109348f, 0f, -0.57903975f), Row1 = new(0f, 3.778f, 0f), Row2 = new(0.08150698f, 0f, 0.17045398f), Row3 = new(409.1308f, 8.4353895f, 21.540106f) });
            ReplaceInstanceTransform(box, 0x39F8F600000000ul, 47, new() { Row0 = new(1.0185566f, 0f, -0.87418103f), Row1 = new(0f, 4.378f, 0f), Row2 = new(0.123051755f, 0f, 0.14337438f), Row3 = new(415.60382f, 8.512573f, 17.035158f) });
            ReplaceInstanceTransform(box, 0x39F8F700000000ul, 48, new() { Row0 = new(1.2012515f, 0f, -0.5988695f), Row1 = new(0f, 4.378f, 0f), Row2 = new(0.08429826f, 0f, 0.16909096f), Row3 = new(417.8848f, 8.528983f, 15.425109f) });
            ReplaceInstanceTransform(box, 0x39F95800000000ul, 83, new() { Row0 = new(3.0521717f, 0f, -4.080866f), Row1 = new(0f, 3.078f, 0f), Row2 = new(0.15091036f, 0f, 0.11286924f), Row3 = new(399.99274f, 8.529965f, 28.993406f) });
            ReplaceInstanceTransform(box, 0x39F95900000000ul, 84, new() { Row0 = new(1.804044f, 0f, -1.725368f), Row1 = new(0f, 3.778f, 0f), Row2 = new(0.13025147f, 0f, 0.13619088f), Row3 = new(403.3705f, 8.528974f, 25.155504f) });
            ReplaceInstanceTransform(box, 0x39F95A00000000ul, 85, new() { Row0 = new(2.0044036f, 0f, -1.4878964f), Row1 = new(0f, 3.878f, 0f), Row2 = new(0.11232427f, 0f, 0.15131642f), Row3 = new(412.47958f, 8.433727f, 19.522573f) });
            ReplaceInstanceTransform(box, 0x39F95B00000000ul, 86, new() { Row0 = new(2.3867178f, 0f, -0.73146963f), Row1 = new(0f, 4.178f, 0f), Row2 = new(0.055220105f, 0f, 0.18017808f), Row3 = new(421.58566f, 8.528974f, 14.065789f) });
        }

        scene.InsertAABoxCollider(new AABB { Min = new(401.63702f, 0.65079784f, 82.471825f), Max = new(402.63702f, 7.2507973f, 83.471825f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
        scene.InsertAABoxCollider(new AABB { Min = new(403.7864f, 1.1228976f, 77.04017f), Max = new(404.7864f, 6.922898f, 78.04017f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
        scene.InsertAABoxCollider(new AABB { Min = new(638.71655f, -3.6505f, 158.40239f), Max = new(647.1455f, -0.3935001f, 168.17238f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
        scene.InsertAABoxCollider(new AABB { Min = new(615.9527f, -3.6504998f, 156.4376f), Max = new(622.0457f, -0.5095f, 161.0796f) }, SceneExtractor.PrimitiveFlags.ForceUnwalkable);
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 跨 tile 的連結建置期加不進去，改在載入後接上；走路下去，用 None（不打折、不壓低全區成本倍率）。
        LinkPoints(mesh, new(389.47458f, 8.413181f, 36.191235f), new(381.6874f, -3.1000004f, 26.508116f), Navmesh.AreaId.None);
    }
}
