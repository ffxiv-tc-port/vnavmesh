namespace Navmesh.Customizations;

[CustomizationTerritory(146)]
internal class Z0146SouthernThanalan : NavmeshCustomization
{
    public override int Version => 4;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // ⚠️ 這一段在本 fork 是「有作用」的，不是從上游抄來的死碼：
        // SceneExtractor 對 collider 有「平時停用」過濾（(matId & 0x410) == 0x400 就跳過），
        // 但對 bgpart 沒有同樣的過濾。0x206406 & 0x410 == 0x400，正好落在那個類別 ——
        // 也就是說這批方盒實際遊戲中不生效，卻會被我們當成實體碰撞抽出來擋路。
        // 上游是在移除 bgpart 過濾之後才補這一段；我方分支從來沒有過該過濾，所以同樣需要。
        if (scene.Meshes.TryGetValue("<box>", out var mesh))
            mesh.Instances.RemoveAll(i => i.Material == 0x206406);

        // the ground directly in front of the bridge next to the amalj'aa camp has two triangles that cannot be landed on
        if (scene.Meshes.TryGetValue("bg/ffxiv/wil_w1/fld/w1f4/collision/tr1610.pcb", out var mesh2))
            foreach (var inst in mesh2.Instances)
                inst.ForceClearPrimFlags |= SceneExtractor.PrimitiveFlags.Unlandable;
    }
}
