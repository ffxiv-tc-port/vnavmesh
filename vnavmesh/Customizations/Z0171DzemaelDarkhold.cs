namespace Navmesh.Customizations;

[CustomizationTerritory(171)]
[CustomizationTerritory(1330)] // 澤梅爾要塞改版版本（上游 6fc80725eb8290472eee433fc4be7ee06ec79357）；台服 TerritoryType 1330 的 Bg 為空＝尚未上線，此註冊在台服上是死碼。
class Z0171DzemaelDarkhold : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
            if (key.StartsWith("bg/ffxiv/roc_r1/rad/r1r1/collision/r1r1_a1_dor"))
                mesh.Instances.Clear();
    }
}
