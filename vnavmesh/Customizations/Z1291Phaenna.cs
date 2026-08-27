using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Customizations;

[CustomizationTerritory(1291)]
internal class Z1291Phaenna : NavmeshCustomization
{
    // 4：台服預防性修正 —— 台服目前沒有 1291 號區域（渴望灣/Phaenna，2026-08-02 確認過完全
    //    不存在），這段目前全部是死碼；比照 Z1237SinusArdorum 補上 festival 層／DevGrade
    //    記錄，並把版本號門檻略過的行為從「靜默 return」改成「記 Warning 再 return」，見
    //    CustomizeMesh 內的大段說明——查過 WKSPioneeringTrail／WKSDevGrade／
    //    WKSNextPlanetGuidance 三張表後，為什麼沒有比照 Z1237 換成 CosmicProgress.DevGrade
    //    閘門。bump 版本讓舊快取失效（雖然目前沒有台服快取檔會受影響）。
    public override int Version => 4;

    public override void CustomizeScene(SceneExtractor scene)
    {
        string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/c1w2/collision/c1w2_03_t200a.pcb"];

        foreach (var liner in doubleLiners)
        {
            if (scene.Meshes.TryGetValue(liner, out var cl))
            {
                // prevent agent from trying to climb the side of the ramp of a green cosmoliner - can cause issues if idiots set a very high path tolerance
                var departVerts = CollectionsMarshal.AsSpan(cl.Parts[29].Vertices);
                departVerts[129].Y += 1;
                departVerts[130].Y += 1;
                departVerts[132].Y += 1;
                departVerts[133].Y += 1;

                var box = SceneExtractor.BuildBoxMesh()[0];
                foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
                {
                    vert *= new Vector3(1.5f, 3.75f, 1.5f);
                    vert += new Vector3(4.5f, 6.25f, 0.5f);
                }
                cl.Parts.Add(box);
            }
        }

        if (scene.Meshes.TryGetValue("bg/ffxiv/cos_c1/hou/c1w2/collision/c1w2_t0_roc31.pcb", out var rock))
        {
            var p = SceneExtractor.BuildBoxMesh()[0];
            foreach (ref var vert in CollectionsMarshal.AsSpan(p.Vertices))
            {
                vert *= new Vector3(0.5f, 2, 0.5f);
                vert += new Vector3(-1, 0, -0.5f);
            }
            rock.Parts.Add(p);
        }
    }

    const float pi = MathF.PI;
    const float hpi = pi / 2;

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        (Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation)
        {
            var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var adjD = Vector3.Transform(new(4.5f, 2.5f, 2.3f), q);
            var adjA = Vector3.Transform(new(-4.5f, 2.7f, 1.8f), q);
            return (adjD + worldPos, adjA + worldPos);
        }

        void addCosmoliner(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation)
        {
            var (depA, arrA) = getPoints(pointAPos, pointARotation);
            var (depB, arrB) = getPoints(pointBPos, pointBRotation);

            LinkPoints(mesh, depA, arrB);
            LinkPoints(mesh, depB, arrA);
        }

        var festivalVersion = festivalLayers.FirstOrDefault() >> 16;

        // 本區（渴望灣/Phaenna，1291）沿用上游（xanunderscore）原本的寫法：把 festivalLayers
        // 的原始 subid 當版本號分級開關，跟 Z1237SinusArdorum 曾經壞掉的 `== 0x09` 是同一種
        // 手法家族——差別是這裡用「< 門檻」的遞增區間、不是「== 特定值」，理論上不會有
        // 「進度一旦跨過去就再也回不去」的問題，但仍然假設 TC 的 festival subid 進度節奏跟
        // 國際服一致，這個假設完全沒有驗證過（因為台服根本沒有 1291，測不出來）。
        //
        // ⚠️ 為什麼沒有比照 Z1237 換成 CosmicProgress.DevGrade 閘門：查過 WKSPioneeringTrail
        // 表（2026-08-02 以台服 7.20 EXD dump 核對）。這張表的結構是「外層 RowId＝星球、內層
        // 子列＝分期」——group 1（月面）的 16 筆子列數值＝0,4,8,14,18,21,24,30,33,37,43,49,
        // 55,58,62,62，剛好等於 CosmicProgress.PhaseThresholds 目前在用的那組數字，兩邊互相
        // 印證指的是同一份資料。但 group 2（星球序號緊接在月面之後，應該就是 Phaenna）**16
        // 筆子列全部是 0**；WKSDevGrade 表 64 號之後的列也整列歸零（不是文字空但數字在，是
        // 整列都沒有）；WKSNextPlanetGuidance 更是整張表只剩一筆全零的哨兵列。三張表一致
        // 指向「台服資料庫目前沒有任何 Phaenna 的建設階段資料」——不是「資料在但我猜錯對應
        // 關係」，是「連拿來猜的原始數字都不存在」。硬套 DevGrade 只會是憑空編造門檻值，比
        // 維持現狀更危險。等台服真的開放這個星球、上面三張表被填入實際資料後，應該重新走
        // 一次 Z1237 那套「用真值反推 region↔門檻」的推理，不要延用這則記錄裡的猜測。
        Service.Log.Information($"[Z1291Phaenna] festival 層狀態：{(festivalLayers.Count == 0 ? "（無）" : string.Join("、", festivalLayers.Select(l => $"id={l & 0xFFFF} subid={l >> 16}")))}；DevGrade={CosmicProgress.DevGrade}（第 {CosmicProgress.CurrentPhase} 期，僅供對照——本區目前門檻用的是 festival subid，不是這個值）。");

        if (festivalVersion < 0x06)
        {
            Service.Log.Warning($"[Z1291Phaenna] festival subid={festivalVersion:X} 未達門檻 0x06，略過 base/inner ring liners 全部自訂捷徑。若此區地形實際已建好，代表版本號門檻已過期，需要重新核對。");
            return;
        }

        #region base liners
        // base <-> N
        addCosmoliner(new(340, 52.5f, -486), default, new(300, 135, -756), new(pi, 0, -pi));

        // base <-> E
        addCosmoliner(new(406, 52.5f, -420), new(0, -hpi, 0), new(756, 52, -430), new(0, hpi, 0));

        // base <-> S
        addCosmoliner(new(340, 52.5f, -354), new(pi, 0, -pi), new(330, 52.5f, -152), default);

        // base <-> W
        addCosmoliner(new(274, 52.5f, -420), new(0, hpi, 0), new(-52, 25.5f, -402), new(0, -hpi, 0));
        #endregion

        #region inner ring liners
        // N <-> NE
        addCosmoliner(new(324, 135, -780), new(0, -hpi, 0), new(687.144f, 45, -730.321f), new(0, 1.047f, 0));

        // NE <-> E
        addCosmoliner(new(712.856f, 45, -699.679f), new(pi, 0.349f, pi), new(780, 52, -454), default);

        // E <-> SE
        addCosmoliner(new(780, 52, -406), new(-pi, 0, pi), new(730, 36, -61), default);

        // SE <-> S
        addCosmoliner(new(706, 36, -37), new(0, hpi, 0), new(354, 52.5f, -128), new(0, -hpi, 0));

        // S <-> SW
        addCosmoliner(new(306, 52.5f, -128), new(0, hpi, 0), new(26.971f, -10, -143.971f), new(0, -0.785f, 0));

        // SW <-> W
        addCosmoliner(new(-6.971f, -10, -143.971f), new(0, 0.785f, 0), new(-76, 25.5f, -378), new(-pi, 0, pi));

        // W <-> NW
        addCosmoliner(new(-76, 25.5f, -426), default, new(-130.908f, 62.5f, -731.091f), new(-pi, 0.087f, -pi));

        // NW <-> N
        addCosmoliner(new(-109.091f, 62.5f, -757.092f), new(0, -1.484f, 0), new(276, 135, -780), new(0, hpi, 0));

        // S <-> soda-lime float
        addCosmoliner(new(330, 52.5f, -104), new(-pi, 0, -pi), new(255, -9.5f, 108), default);

        // soda-lime float <-> SW
        addCosmoliner(new(231, -9.5f, 132), new(0, hpi, 0), new(26.971f, -10, -110.029f), new(pi, 0.785f, pi));
        #endregion

        if (festivalVersion < 0x0F)
        {
            Service.Log.Warning($"[Z1291Phaenna] festival subid={festivalVersion:X} 未達門檻 0x0F，略過 peninsula/scoresheen sands 自訂捷徑。");
            return;
        }

        #region peninsula
        // soda-lime float <-> peninsula E
        addCosmoliner(new(255, -9.5f, 156), new(pi, 0, pi), new(185, -5.5f, 406), default);

        // peninsula E <-> peninsula SW
        addCosmoliner(new(185, -5.5f, 454), new(pi, 0, pi), new(-64, 34, 660), new(0, -hpi, 0));

        // peninsula E <-> peninsula NW
        addCosmoliner(new(161, -5.5f, 430), new(0, hpi, 0), new(-136, 28.5f, 305), new(0, -hpi, 0));

        // peninsula SW <-> peninsula NW
        addCosmoliner(new(-88, 34, 636), default, new(-160, 28.5f, 329), new(pi, 0, pi));
        #endregion

        #region scoresheen sands
        // N sands <-> NW
        addCosmoliner(new(-623.029f, -2, -656.971f), new(0, -0.785f, 0), new(-156.909f, 62.5f, -752.908f), new(-pi, -1.484f, -pi));

        // N sands <-> E1 sands
        addCosmoliner(new(-623.029f, -2, -623.029f), new(pi, 0.785f, -pi), new(-422, -2, -430.785f), new(0, 0.524f, 0));

        // N sands <-> W sands
        addCosmoliner(new(-656.971f, -2, -623.029f), new(pi, -0.785f, pi), new(-768, 13.5f, -294), default);

        // E1 sands <-> W
        addCosmoliner(new(-389.215f, -2, -422), new(0, -1.047f, 0), new(-100, 25.5f, -402), new(0, hpi, 0));

        // E1 sands <-> W sands
        addCosmoliner(new(-430.785f, -2, -398), new(pi, -1.047f, pi), new(-744, 13.5f, -270), new(0, -hpi, 0));

        // E1 sands <-> E2 sands
        addCosmoliner(new(-398, -2, -389.215f), new(-pi, 0.524f, -pi), new(-326.971f, -5, -151.971f), new(0, 0.785f, 0));

        // E2 sands <-> SW
        addCosmoliner(new(-293.029f, -5, -151.971f), new(0, -0.785f, 0), new(-6.971f, -10, -110.029f), new(pi, -0.785f, pi));

        // E2 sands <-> peninsula NW
        addCosmoliner(new(-293.029f, -5, -118.029f), new(pi, 0.785f, pi), new(-160, 28.5f, 281), default);

        // E2 sands <-> S sands
        addCosmoliner(new(-326.971f, -5, -118.029f), new(pi, -0.785f, pi), new(-556, 24.5f, 50), new(0, -hpi, 0));

        // W sands <-> S sands
        addCosmoliner(new(-768, 13.5f, -246), new(pi, 0, pi), new(-604, 24.5f, 50), new(0, hpi, 0));
        #endregion

        if (festivalVersion < 0x14)
        {
            Service.Log.Warning($"[Z1291Phaenna] festival subid={festivalVersion:X} 未達門檻 0x14，略過 pools 自訂捷徑。");
            return;
        }

        #region pools
        // soda-lime float <-> pools E
        addCosmoliner(new(279, -9.5f, 132), new(0, -hpi, 0), new(830, -168, 415), new(0, 0.349f, 0));

        // pools E <-> pools S
        addCosmoliner(new(830, -168, 455), new(pi, -0.349f, pi), new(549.696f, -220, 748.473f), new(0, -1.396f, 0));

        // pools S <-> chasm
        addCosmoliner(new(510.304f, -220, 741.527f), new(0, 1.047f, 0), new(405.832f, -230, 253.635f), new(-pi, -0.175f, pi));

        // chasm <-> pools middle
        addCosmoliner(new(433.635f, -230, 234.168f), new(pi, 1.396f, pi), new(660, -242, 420), new(0, 0.785f, 0));
        #endregion

        if (festivalVersion < 0x25)
        {
            Service.Log.Warning($"[Z1291Phaenna] festival subid={festivalVersion:X} 未達門檻 0x25，略過西南區自訂捷徑。");
            return;
        }

        #region southwestern penis
        addCosmoliner(new(-580, 24.5f, 74), new(pi, 0, -pi), new(-363.473f, 11, 375.304f), new(0, 0.524f, 0));

        addCosmoliner(new(-356.527f, 11, 414.696f), new(pi, -0.175f, pi), new(-580, 28, 715), new(0, -hpi, 0));
        #endregion
    }
}
