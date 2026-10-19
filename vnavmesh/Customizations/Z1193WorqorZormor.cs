using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(1193)]
class Z1193WorqorZormor : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // remove large crystal blocking initial path, which is destroyed after first pack dies
        scene.Meshes.Remove("bg/ex5/02_ykt_y6/dun/y6d2/collision/y6d2_a1_cry03.pcb");
    }

    // 以下座標取自 AtmoOmen/ffxiv_navmesh-cn：岩階一路往下跳的短連結與四段客戶端固定路徑（副本轉場）。
    // 上游另有 6 條建置期連結與 1 條 LinkPoints，我方網格上兩端本來就直線走得到，不收。
    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnectionChecked(new(-84.984985f, 83.43838f, 16.815525f), new(-87.23949f, 81.304985f, 17.476467f));
        config.AddOffMeshConnectionChecked(new(-92.886f, 80.07646f, 21.805302f), new(-93.76564f, 78.168976f, 23.270887f));
        config.AddOffMeshConnectionChecked(new(-95.49261f, 77.89406f, 26.56182f), new(-96.23459f, 75.66163f, 27.87869f));
        config.AddOffMeshConnectionChecked(new(-96.299484f, 74.923706f, 29.690712f), new(-96.711395f, 71.75397f, 31.935032f));
        config.AddOffMeshConnectionChecked(new(-95.62928f, 71.56621f, 34.3437f), new(-97.0606f, 70.000046f, 35.750156f));
        config.AddOffMeshConnectionChecked(new(-101.45528f, 71.65432f, 33.624153f), new(-102.373276f, 70.000015f, 35.08779f));
        config.AddOffMeshConnectionChecked(new(-93.90274f, 71.61809f, 35.02472f), new(-93.40851f, 70.00004f, 37.18663f));
        config.AddOffMeshConnectionChecked(new(-98.566376f, 75.169525f, 29.490572f), new(-99.71539f, 73.12116f, 30.449541f));
        config.AddOffMeshConnectionChecked(new(-94.2036f, 74.68306f, 30.736431f), new(-94.37086f, 71.95897f, 32.22297f));
        config.AddOffMeshConnectionChecked(new(-96.92457f, 78.09169f, 26.331053f), new(-97.84229f, 76.02344f, 27.226051f));
        config.AddOffMeshConnectionChecked(new(-94.37889f, 77.62675f, 27.035778f), new(-94.630646f, 75.495056f, 28.086235f));
        config.AddOffMeshConnectionChecked(new(-94.47819f, 80.21791f, 21.210327f), new(-95.51148f, 78.58182f, 22.707567f));
        config.AddOffMeshConnectionChecked(new(-84.36124f, 83.42916f, 17.949215f), new(-85.31665f, 81.34958f, 18.760738f));
        config.AddOffMeshConnectionChecked(new(-86.4144f, 83.42893f, 16.008148f), new(-87.37458f, 81.41614f, 16.410416f));
        config.AddOffMeshConnectionChecked(new(-96.01609f, 77.96924f, 26.361475f), new(-96.6175f, 75.70476f, 27.728762f));
        config.AddOffMeshConnectionChecked(new(-97.099884f, 74.99227f, 29.68504f), new(-97.36233f, 71.70395f, 31.629616f));
        config.AddOffMeshConnectionChecked(new(-97.75854f, 71.45653f, 32.651985f), new(-98.02482f, 70.15888f, 34.33348f));
        // 往最後一段客戶端固定路徑的起點；那一點附近的平台若不在網格上，這條與下面最後一條 LinkPoints 都會被略過。
        config.AddOffMeshConnectionChecked(new(-57.41605f, 104.38435f, -20.145775f), new(-52.023705f, 99.79068f, -22.82613f));
    }

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        LinkPoints(mesh, new(-60.596786f, 41.184914f, 52.18263f), new(-49.127636f, 53.765175f, 38.118057f));
        LinkPoints(mesh, new(-49.69194f, 55.609795f, 16.1616f), new(-66.190384f, 85.04915f, 13.934385f));
        LinkPoints(mesh, new(-106.27985f, 71.03789f, 43.525322f), new(-145.49803f, 80.13396f, 41.328983f));
        LinkPoints(mesh, new(-52.031303f, 100.02917f, -22.83974f), new(-62.87685f, 319.26578f, -20.215199f));
    }
}
