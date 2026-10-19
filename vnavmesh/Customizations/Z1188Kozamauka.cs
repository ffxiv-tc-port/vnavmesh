using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(1188)]
internal class Z1188Kozamauka : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // contender #3 in the most cursed mesh transformation finalists
        if (scene.Meshes.TryGetValue("bg/ex5/02_ykt_y6/fld/y6f2/collision/y6f2_x0_tst00.pcb", out var mesh))
            mesh.Instances[0].WorldTransform.M42 += 0.05f;
    }

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        // 奸臣的展示台；座標取自 AtmoOmen/ffxiv_navmesh-cn，單向照抄
        config.AddOffMeshConnectionChecked(new(379.16513f, 132.05937f, 686.66486f), new(378.89407f, 132.15149f, 693.27045f));
    }
}
