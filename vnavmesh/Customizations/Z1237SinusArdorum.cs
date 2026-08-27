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
    // 7：台服修正 —— CustomizeMesh 依 CosmicProgress.DevGrade（全服建設階段）替每條自訂
    //    捷徑加上開通門檻，未達門檻的路線直接略過，不必等端點預檢兜底，使用者也不必在
    //    「自訂捷徑」分頁一條一條手動取消勾選（見 NavmeshCustomization.LinkPoints 的
    //    minDevGrade/gateLabel 參數）。bump 版本讓舊快取失效重建。
    // 8：台服修正 —— 改用「當下 layout 裡有沒有這條路線的纜車碰撞模型」當主閘門，
    //    DevGrade 降為 fallback（掃不到任何模型時才用）。判斷來源從「推斷的 region↔期數
    //    對應」換成「直接讀遊戲載入的場景」，少一層猜測。bump 版本讓舊快取失效重建。
    public override int Version => 8;

    // 宇宙快線（Cosmoliner）的三個碰撞模型路徑。
    // 📌 2026-08-27 以 tools/sqpack/path_exists.py 實查台服 sqpack：三個路徑**都存在**
    //    （校準閘門通過：正樣本 3 全中、自造的負樣本不中）。所以這不是「照國際服寫死」。
    // ⚠️ 但「模型檔在 sqpack」不等於「這一期的場景實例裡有它」——那正是本檢查要問的問題。
    private static readonly HashSet<string> CosmolinerCollisionPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t100a.pcb",
        "bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t200a.pcb",
        "bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t300a.pcb",
    };

    // 纜車模型與捷徑座標之間允許的距離平方（2 公尺）。
    private const float CosmolinerMatchDistSq = 4f;

    // 「這個碰撞體現在是啟用的嗎」。0x400 是 vnavmesh SceneExtractor 本來就在用的
    // 「條件式失效碰撞體」旗標（見 SceneExtractor 對 Colliders 的處理與旁邊的上游註解），
    // 這裡只是把同一條規則也套到 BgParts 上，不是新發明的魔數。
    private static bool IsActive(ulong materialId) => (materialId & 0x410) != 0x400;

    private static bool IsCosmoliner(SceneDefinition scene, uint pathCrc)
        => scene.MeshPaths.TryGetValue(pathCrc, out var path) && CosmolinerCollisionPaths.Contains(path);

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

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // 台服的月面基地仍在建設階段（festival 層會隨全服進度推進），而下面的捷徑座標是
        // 上游照國際服「完工態」地形寫死的（上游漏套 Z1291Phaenna 的 festival 閘門手法）。
        // ⚠️ 刻意不硬編版本常數當閘門：TW 社群寫死 `== 0x09` 已因進度推進而過期（我們
        // 2026-08-01 實測 SubId=14），而且完工後 festival 層甚至可能整個消失 —— 任何常數
        // 都注定過期。改為三手：
        // 1. 把觀察到的 festival 層印進 log（Information，使用者預設記錄等級看得到），
        //    實機 log 直接告訴我們目前的階段值，日後要加正式閘門時有真值可抄；
        // 2. 依 CosmicProgress.DevGrade（全服建設階段，遊戲自己的權威數字）替每條捷徑
        //    先判斷所屬路線的門檻是否已達到，未達就直接略過（見下方 gate/link 區塊）——
        //    使用者因此不必在「自訂捷徑」分頁一條一條手動取消勾選；
        // 3. 全部照常嘗試建立，靠 LinkPoints 的三道端點預檢逐條把「地形還沒蓋到」的捷徑
        //    擋下來並記 Warning（閘門猜錯 region↔門檻對應時的最後防線）。寧可捷徑少也
        //    不要靜默錯。
        Service.Log.Information($"[Z1237SinusArdorum] festival 層狀態：{(festivalLayers.Count == 0 ? "（無）" : string.Join("、", festivalLayers.Select(l => $"id={l & 0xFFFF} subid={l >> 16}")))}；DevGrade={CosmicProgress.DevGrade}（第 {CosmicProgress.CurrentPhase} 期）；自訂捷徑依建設階段門檻與端點預檢逐條把關。");

        (Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation)
        {
            var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var adjD = Vector3.Transform(new(4.5f, 2.5f, 2.8f), q);
            var adjA = Vector3.Transform(new(-4.5f, 2.7f, 1.3f), q);
            return (adjD + worldPos, adjA + worldPos);
        }

        // 每條捷徑要求的全服建設階段門檻（見 CosmicProgress），由各 #region 開頭呼叫
        // gate(...) 設定、對此後的 addCosmoliner/link 呼叫持續生效直到下一次 gate(...)。
        // 門檻值取自 WKSPioneeringTrail 表（2026-08-02 以台服 7.20 EXD dump 核對）；
        // region 與期數的對應是從地理位置與期數名稱推斷的 —— 不是從遊戲資料直接讀出的
        // 關聯，佐證是「資料表裡剛好有 7 個開通路線的期數，程式碼裡剛好有 7 組路線
        // 群組，且順序一致」。⚠️ 就算推斷錯誤，最壞也只是少一條捷徑（IsBelow 對未知
        // 階段放行、端點預檢仍照跑），不會崩潰也不會走錯路。
        int gateGrade = 0; string gateLabel = "";
        void gate(int grade, string label) { gateGrade = grade; gateLabel = label; }
        void link(Vector3 a, Vector3 b) => LinkPoints(mesh, a, b, minReachablePolys: ReqReachablePolys, minDevGrade: gateGrade, gateLabel: gateLabel);

        // ── 場景偵測（主閘門）─────────────────────────────────────────────
        // 問的是「這條路線的兩端纜車模型，現在在不在載入的場景裡」——直接讀當下 layout，
        // 比 DevGrade 那條「region↔期數對應是從地理位置推斷的」少一層猜測。
        // 🔴 三層互補而非取代，順序固定（見 NavmeshCustomization.LinkPoints）：
        //    ① 使用者停用  ② 場景偵測（這裡）  ③ DevGrade fallback  ④ 端點預檢
        // ⚠️ CurrentScene 為 null（呼叫端沒設）或整張圖一個纜車模型都掃不到時，一律
        //    **退回 DevGrade fallback**，不是「全部放行」——後者會在模型路徑哪天過期時
        //    讓所有捷徑無條件建立，行為比今天更差。
        var scene = CurrentScene;
        var activeCosmoliners = scene == null ? [] : scene.BgParts
            .Where(part => !part.analytic && IsActive(part.matId) && IsCosmoliner(scene, part.crc))
            .Select(part => part.transform.Translation)
            .Concat(scene.Colliders
                .Where(collider => IsActive(collider.matId) && IsCosmoliner(scene, collider.crc))
                .Select(collider => collider.transform.Translation))
            .ToList();
        var sceneDetectionUsable = activeCosmoliners.Count > 0;
        Service.Log.Information($"[Z1237SinusArdorum] 場景偵測：找到 {activeCosmoliners.Count} 個啟用中的宇宙快線碰撞模型" +
            $"（場景定義 {(scene == null ? "未提供" : "已提供")}）。" +
            $"{(sceneDetectionUsable ? "以場景偵測為主閘門。" : "掃不到模型，本次退回 DevGrade 階段門檻判斷。")}");

        bool hasCosmoliner(Vector3 position)
            => activeCosmoliners.Any(active => Vector3.DistanceSquared(active, position) <= CosmolinerMatchDistSq);

        void addCosmoliner(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation)
        {
            // 場景偵測可用時，它說了算：兩端都要找得到啟用中的纜車模型。
            if (sceneDetectionUsable && (!hasCosmoliner(pointAPos) || !hasCosmoliner(pointBPos)))
            {
                var reason = $"場景裡找不到纜車模型（{pointAPos:f1} 端={hasCosmoliner(pointAPos)}，{pointBPos:f1} 端={hasCosmoliner(pointBPos)}）";
                Service.Log.Information($"[Z1237SinusArdorum] 該路線尚未開通，略過：{reason}");
                // 兩個方向各記一筆，「自訂捷徑」分頁才看得到它為什麼沒出現
                // ——被擋掉的捷徑如果完全不留紀錄，在列表上會像「從沒發生過」。
                var (dA, aA) = getPoints(pointAPos, pointARotation);
                var (dB, aB) = getPoints(pointBPos, pointBRotation);
                foreach (var (s, e) in new[] { (dA, aB), (dB, aA) })
                    CustomLinkTracker.Record(CustomLinkTracker.MakeKey(CurrentTerritory, s, e),
                        CurrentTerritory, s, e, CustomLinkResult.SkippedNoModel, reason);
                return;
            }

            var (depA, arrA) = getPoints(pointAPos, pointARotation);
            var (depB, arrB) = getPoints(pointBPos, pointBRotation);

            LinkPoints(mesh, depA, arrB, minReachablePolys: ReqReachablePolys, minDevGrade: gateGrade, gateLabel: gateLabel);
            LinkPoints(mesh, depB, arrA, minReachablePolys: ReqReachablePolys, minDevGrade: gateGrade, gateLabel: gateLabel);
        }

        #region base liners
        gate(8, "宇宙快線初次開通");
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
        gate(8, "宇宙快線初次開通");
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
        gate(55, "月底升降梯開通");
        // NE -> downstairs
        link(new(322.35117f, 43, -306.3f), new(404.5141f, -56.8f, -375.2349f));
        // downstairs -> NE
        link(new(390.42264f, -57, -394.7571f), new(308.2982f, 43.2f, -325.8215f));

        gate(58, "宇宙快線向月底延伸");
        // downstairs <-> NNEE
        addCosmoliner(new(433.426f, -59.5f, -415.135f), new(0, -0.873f, 0), new(624.088f, -74.5f, -556.224f), new(-pi, -0.908f, -pi));

        // NNEE <-> NNEEEE
        addCosmoliner(new(657.776f, -74.5f, -552.088f), new(-pi, 0.663f, pi), new(868, -58, -374), new(0, 0.873f, 0));

        // NNEE -> loop
        link(new(625.157f, -71.970f, -583.71f), new(366.911f, -117.3f, -834.9056f));
        link(new(388.186f, -117.470f, -848.963f), new(622.1744f, -108.3f, -944.7515f));
        link(new(646.472f, -108.480f, -924.356f), new(646.622f, -71.8f, -592.223f));
        #endregion

        #region SE
        gate(24, "宇宙快線向東南區域延伸");
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
        gate(30, "穿行威隧道開通");
        // SS -> tunnel
        link(new(-122.029f, 55, 740.012f), new(-316.2774f, 55.2f, 740));
        // tunnel -> SS
        link(new(-317.979f, 55, 759.97f), new(-123.75f, 55.2f, 760));

        gate(33, "宇宙快線向隧道內部區域延伸");
        // crater SE <-> N
        addCosmoliner(new(-340, 50.5f, 726), default, new(-596, 50, 390), new(0, -hpi, 0));

        // crater N <-> SW
        addCosmoliner(new(-644, 50, 390), new(0, hpi, 0), new(-734, 91, 740), default);

        // crater SW <-> SE
        addCosmoliner(new(-710, 91, 764), new(0, -hpi, 0), new(-364, 50.5f, 750), new(0, hpi, 0));
        #endregion

        #region NW
        gate(21, "宇宙快線向西北區域延伸");
        // W <-> WW
        addCosmoliner(new(-424, 36, 0), new(0, hpi, 0), new(-675, 60, 10), new(0, -hpi, 0));

        // NW <-> NNWW
        addCosmoliner(new(-296.971f, 34, -296.970f), new(0, 0.785f, 0), new(-523.029f, 59, -523.029f), new(-pi, 0.785f, pi));

        // WW <-> NNWW
        addCosmoliner(new(-699, 60, -14), default, new(-556.971f, 59, -523.029f), new(pi, -0.785f, pi));
        #endregion
    }
}
