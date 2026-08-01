using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Customizations;

[CustomizationTerritory(1237)]
internal class Z1237SinusArdorum : NavmeshCustomization
{
    // 5：台服修正 —— LinkPoints 改為「端點落不到網格上就略過該連結」，而不是丟例外讓
    //    整張圖的網格建置中止（見 NavmeshCustomization.LinkPoints）。bump 版本讓既有的
    //    壞快取失效，使用者才不必手動按「Rebuild scene extract only」。
    // 6：台服修正 —— LinkPoints 預檢加上吸附距離與連通性驗證（擋掉「端點吸附到附近但
    //    不連通的多邊形 → 捷徑靜默失效只會繞遠路」），並在 CustomizeMesh 記錄觀察到的
    //    festival 層。bump 版本讓舊快取失效重建。
    public override int Version => 6;

    // 本區的連通性門檻取比預設（4）嚴：月面是開闊的連續地形，合法的塔平台在 25m 行走
    // 範圍內必然外溢到大量地表多邊形；建設中的殘缺結構才會只連到少數幾個。就算誤殺，
    // 最壞結果也只是該條捷徑消失、改走基礎地形＋Warning log，不會靜默。
    private const int ReqReachablePolys = 16;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // annoying rock
        scene.InsertCylinderCollider(new Vector3(2, 10, 2), new Vector3(-206.5f, 29, 301.5f));

        // add box collider blocking entry path for green (depart) cosmoliner to discourage vnav from going through green cosmoliners on short paths
        // later we add off-mesh connections between liners, so those will be used for long paths
        string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t300a.pcb", "bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t200a.pcb"];

        foreach (var liner in doubleLiners)
        {
            if (scene.Meshes.TryGetValue(liner, out var cl))
            {
                var box = SceneExtractor.BuildBoxMesh()[0];
                foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
                {
                    vert *= new Vector3(1.5f, 3.75f, 1.5f);
                    vert += new Vector3(4.5f, 6.25f, 0.5f);
                }
                cl.Parts.Add(box);
            }
        }

        if (scene.Meshes.TryGetValue("bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t100a.pcb", out var mesh))
        {
            var box = SceneExtractor.BuildBoxMesh()[0];
            foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
            {
                vert *= new Vector3(1.5f, 3.75f, 1.5f);
                vert += new Vector3(0, 6.25f, 0.5f);
            }
            mesh.Parts.Add(box);
        }

        if (scene.Meshes.TryGetValue("bg/ffxiv/cos_c1/hou/common/collision/c1w0_00_bx00d.pcb", out var mesh2))
        {
            foreach (var inst in mesh2.Instances)
                inst.WorldTransform.M22 *= 2;
        }
    }

    const float pi = MathF.PI;
    const float hpi = pi / 2;

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        // 台服的月面基地仍在建設階段（festival 層會隨全服進度推進），而下面的捷徑座標是
        // 上游照國際服「完工態」地形寫死的（上游漏套 Z1291Phaenna 的 festival 閘門手法）。
        // ⚠️ 刻意不硬編版本常數當閘門：TW 社群寫死 `== 0x09` 已因進度推進而過期（我們
        // 2026-08-01 實測 SubId=14），而且完工後 festival 層甚至可能整個消失 —— 任何常數
        // 都注定過期。改為兩手：
        // 1. 把觀察到的 festival 層印進 log（Information，使用者預設記錄等級看得到），
        //    實機 log 直接告訴我們目前的階段值，日後要加正式閘門時有真值可抄；
        // 2. 全部照常嘗試建立，靠 LinkPoints 的三道端點預檢逐條把「地形還沒蓋到」的捷徑
        //    擋下來並記 Warning。寧可捷徑少也不要靜默錯。
        Service.Log.Information($"[Z1237SinusArdorum] festival 層狀態：{(festivalLayers.Count == 0 ? "（無）" : string.Join("、", festivalLayers.Select(l => $"id={l & 0xFFFF} subid={l >> 16}")))}；自訂捷徑座標為完工態，建設未及之處由端點預檢逐條略過（見 Warning）。");

        (Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation)
        {
            var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var adjD = Vector3.Transform(new(4.5f, 2.5f, 2.8f), q);
            var adjA = Vector3.Transform(new(-4.5f, 2.7f, 1.3f), q);
            return (adjD + worldPos, adjA + worldPos);
        }

        void addCosmoliner(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation)
        {
            var (depA, arrA) = getPoints(pointAPos, pointARotation);
            var (depB, arrB) = getPoints(pointBPos, pointBRotation);

            LinkPoints(mesh, depA, arrB, ReqReachablePolys);
            LinkPoints(mesh, depB, arrA, ReqReachablePolys);
        }

        #region base liners
        // base <-> N
        addCosmoliner(new(0, 1, -65), default, new(0, 38, -376), new(pi, 0, -pi));

        // base <-> E
        addCosmoliner(new(65, 1, 0), new(0, -hpi, 0), new(376, 40, 0), new(0, hpi, 0));

        // base <-> S
        addCosmoliner(new(0, 1, 65), new(pi, 0, -pi), new(0, 35, 376), default);

        // base <-> W
        addCosmoliner(new(-65, 1, 0), new(0, hpi, 0), new(-376, 36, 0), new(0, -hpi, 0));
        #endregion

        #region inner ring liners
        // N <-> NE
        addCosmoliner(new(24, 38, -400), new(0, -hpi, 0), new(272, 40.5f, -320), new(0, 0.698f, 0));

        // NE <-> E
        // note that when the X and Z rotations are + or -pi (which is the only nonzero value they get), the Y rotation sign is flipped from what would give the "correct" transformation - this is some math thing that i'm too dumb to understand most likely
        addCosmoliner(new(311.65f, 40.5f, -272.75f), new(pi, 0.698f, -pi), new(400, 40, -24), default);

        // E <-> SE
        addCosmoliner(new(400, 40, 24), new(pi, 0, pi), new(296.971f, 25, 263.029f), new(0, -0.785f, 0));

        // SE <-> S
        addCosmoliner(new(263.029f, 25, 296.971f), new(-pi, -0.785f, -pi), new(24, 35, 400), new(0, -pi / 2, 0));

        // S <-> SW
        addCosmoliner(new(-24, 35, 400), new(0, hpi, 0), new(-263.029f, 29, 296.971f), new(-pi, 0.785f, pi));

        // SW <-> W
        addCosmoliner(new(-296.971f, 29, 263.029f), new(0, 0.785f, 0), new(-400, 36, 24), new(-pi, 0, pi));

        // W <-> NW
        addCosmoliner(new(-400, 36, -24), default, new(-296.971f, 34, -263.028f), new(pi, -0.785f, pi));

        // NW <-> N
        addCosmoliner(new(-263.029f, 34, -296.97f), new(0, -0.785f, 0), new(-24, 38, -400), new(0, hpi, 0));
        #endregion

        #region NE caves
        // NE -> downstairs
        LinkPoints(mesh, new(322.35117f, 43, -306.3f), new(404.5141f, -56.8f, -375.2349f), ReqReachablePolys);
        // downstairs -> NE
        LinkPoints(mesh, new(390.42264f, -57, -394.7571f), new(308.2982f, 43.2f, -325.8215f), ReqReachablePolys);

        // downstairs <-> NNEE
        addCosmoliner(new(433.426f, -59.5f, -415.135f), new(0, -0.873f, 0), new(624.088f, -74.5f, -556.224f), new(-pi, -0.908f, -pi));

        // NNEE <-> NNEEEE
        addCosmoliner(new(657.776f, -74.5f, -552.088f), new(-pi, 0.663f, pi), new(868, -58, -374), new(0, 0.873f, 0));

        // NNEE -> loop
        LinkPoints(mesh, new(625.157f, -71.970f, -583.71f), new(366.911f, -117.3f, -834.9056f), ReqReachablePolys);
        LinkPoints(mesh, new(388.186f, -117.470f, -848.963f), new(622.1744f, -108.3f, -944.7515f), ReqReachablePolys);
        LinkPoints(mesh, new(646.472f, -108.480f, -924.356f), new(646.622f, -71.8f, -592.223f), ReqReachablePolys);
        #endregion

        #region SE
        // E <-> EE
        addCosmoliner(new(424, 40, 0), new(0, -hpi, 0), new(720, 59, -4), new(0, hpi, 0));

        // EE <-> SSEE
        addCosmoliner(new(744, 59, 20), new(-pi, 0, pi), new(444.069f, 45, 493.713f), new(0, -0.785f, 0));

        // SE <-> SSEE
        addCosmoliner(new(296.971f, 25, 296.971f), new(-pi, 0.785f, pi), new(410.127f, 45, 493.713f), new(0, 0.785f, 0));

        // SSEE <-> SS
        addCosmoliner(new(410.127f, 45, 527.654f), new(pi, -0.785f, pi), new(-76, 50.5f, 750), new(0, -hpi, 0));

        // S <-> SS
        addCosmoliner(new(0, 35, 424), new(pi, 0, -pi), new(-100, 50.5f, 726), default);
        #endregion

        #region SW crater
        // SS -> tunnel
        LinkPoints(mesh, new(-122.029f, 55, 740.012f), new(-316.2774f, 55.2f, 740), ReqReachablePolys);
        // tunnel -> SS
        LinkPoints(mesh, new(-317.979f, 55, 759.97f), new(-123.75f, 55.2f, 760), ReqReachablePolys);

        // crater SE <-> N
        addCosmoliner(new(-340, 50.5f, 726), default, new(-596, 50, 390), new(0, -hpi, 0));

        // crater N <-> SW
        addCosmoliner(new(-644, 50, 390), new(0, hpi, 0), new(-734, 91, 740), default);

        // crater SW <-> SE
        addCosmoliner(new(-710, 91, 764), new(0, -hpi, 0), new(-364, 50.5f, 750), new(0, hpi, 0));
        #endregion

        #region NW
        // W <-> WW
        addCosmoliner(new(-424, 36, 0), new(0, hpi, 0), new(-675, 60, 10), new(0, -hpi, 0));

        // NW <-> NNWW
        addCosmoliner(new(-296.971f, 34, -296.970f), new(0, 0.785f, 0), new(-523.029f, 59, -523.029f), new(-pi, 0.785f, pi));

        // WW <-> NNWW
        addCosmoliner(new(-699, 60, -14), default, new(-556.971f, 59, -523.029f), new(pi, -0.785f, pi));
        #endregion
    }
}
