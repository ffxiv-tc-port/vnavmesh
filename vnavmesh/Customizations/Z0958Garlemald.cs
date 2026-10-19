using DotRecast.Detour;

namespace Navmesh.Customizations;

// 座標取自 AtmoOmen/ffxiv_navmesh-cn：高台邊緣往下跳回主網格的七段。
// 上游另有三段，在我方網格上起點吸附不到平台（XZ 超過連結半徑、建置時接不上）且現況本來就走得到，不收。
// 上游把本區 AgentRadius 改成 1；那會重算整區網格，沒有一起搬。
[CustomizationTerritory(958)]
internal class Z0958Garlemald : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnectionChecked(new(89.50866f, 14.650169f, -342.7801f), new(88.948364f, 10.800001f, -338.59482f));
        config.AddOffMeshConnectionChecked(new(85.01472f, 14.713364f, -344.4145f), new(85.06559f, 10.5f, -338.85672f));
        config.AddOffMeshConnectionChecked(new(75.4983f, 15.141907f, -350.81427f), new(74.07337f, 14.004005f, -348.2441f));
        config.AddOffMeshConnectionChecked(new(69.23156f, 14.011064f, -339.73407f), new(69.81415f, 10.799999f, -336.58148f));
        config.AddOffMeshConnectionChecked(new(78.68066f, 11.839239f, -339.81516f), new(78.3684f, 10.500001f, -337.14087f));
        config.AddOffMeshConnectionChecked(new(82.057556f, 12.0234f, -341.24762f), new(83.17428f, 10.5f, -339.57858f));
        config.AddOffMeshConnectionChecked(new(92.0544f, 14.591408f, -342.25626f), new(91.856544f, 10.799999f, -339.33905f));
    }
}
