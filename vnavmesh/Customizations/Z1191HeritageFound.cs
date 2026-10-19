namespace Navmesh.Customizations;

// 把兩處 FATE 場地的矮石頭／矮牆加高（網格不再從上面跨過去）；變換取自 AtmoOmen/ffxiv_navmesh-cn。
// 上游另把本區 AgentRadius 改成 1；那會重算整區網格，沒有一起搬。
[CustomizationTerritory(1191)]
internal class Z1191HeritageFound : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // 地圖左下角 FATE 的小石頭
        if (scene.Meshes.TryGetValue("bg/ex5/01_xkt_x6/fld/x6f2/collision/x6f2_d1_grk1c.pcb", out var grk1c))
        {
            ReplaceInstanceTransform(grk1c, 0x99943900000000ul, 9, new() { Row0 = new(-0.8939932f, -7.815536E-08f, 0.29047602f), Row1 = new(-4.1655397E-07f, 3.64f, -3.0264414E-07f), Row2 = new(-0.29047602f, -1.0757164E-07f, -0.8939932f), Row3 = new(-610.4479f, -14.00001f, 673.8964f) });
            ReplaceInstanceTransform(grk1c, 0x9E809700000000ul, 41, new() { Row0 = new(1f, 0f, 0f), Row1 = new(0f, 3.9f, 0f), Row2 = new(0f, 0f, 1f), Row3 = new(-577.0337f, -13.89243f, 674.726f) });
            ReplaceInstanceTransform(grk1c, 0x9F079300000000ul, 114, new() { Row0 = new(0.9969984f, 0.077421784f, 0f), Row1 = new(-0.27097633f, 3.4894946f, 0f), Row2 = new(0f, 0f, 1f), Row3 = new(-458.461f, -10.9592f, 511.9943f) });
        }
        if (scene.Meshes.TryGetValue("bg/ex5/01_xkt_x6/fld/x6f2/collision/x6f2_d1_grk2b.pcb", out var grk2b))
            ReplaceInstanceTransform(grk2b, 0x99AF3700000000ul, 4, new() { Row0 = new(0.99465585f, 0f, -0.1753846f), Row1 = new(0f, 2.51f, 0f), Row2 = new(0.1753846f, 0f, 0.99465585f), Row3 = new(-576.5275f, -14.00001f, 671.7339f) });

        // 蛇 FATE 的矮牆
        if (scene.Meshes.TryGetValue("bg/ex5/01_xkt_x6/fld/x6f2/collision/x6f2_d0_gr2m2.pcb", out var gr2m2))
            ReplaceInstanceTransform(gr2m2, 0x99B66100000000ul, 2, new() { Row0 = new(-0.14698899f, 2.2f, -0.9891381f), Row1 = new(-1.6142769E-09f, 2.1f, -2.184532E-08f), Row2 = new(0.9891381f, 0.20000002f, -0.14698899f), Row3 = new(-459.8482f, -6.892f, 511.1482f) });
    }
}
