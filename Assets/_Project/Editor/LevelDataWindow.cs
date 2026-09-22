// ---------------------------------------------------------------------------
// LevelDataWindow.cs —— 关卡数据工具（只放在 Editor 文件夹里，不进游戏包）
//
// P0 阶段先提供三个菜单：
//   ⓪-1 导出当前场景 → LevelData JSON
//   ⓪-2 从 LevelData JSON 重建关卡
//   ⓪-3 往返一致性检查（导出 → 重建 → 再导出 → 逐行比对）
//
// 后续（P1/P3）这里会长成一个 EditorWindow：地形表 + 物件表 + Gizmo 预览 + 求解按钮。
//
// 本文件属于编辑器工具，允许做场景内的批量扫描（游戏运行时脚本依然遵守
// "禁止 FindObjectOfType" 的约定）。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LevelDataWindow
{
    public const string LevelsDir = "Assets/_Project/Levels";
    public const string SceneDir = "Assets/_Project/Scenes";
    public const string BuilderName = "LevelBuilder";

    /// <summary>1/4 格量化后误差超过这个值（世界单位）就报警告。</summary>
    public const float QuantizeWarn = 0.05f;

    public static readonly string[] LevelSceneNames = { "Level0", "Level1", "Level2" };

    // ======================= 菜单 =======================

    [MenuItem("Tools/呆呆史莱姆/⓪-1 导出当前场景 → LevelData JSON", false, 90)]
    public static void ExportMenu()
    {
        Scene scene = EditorSceneManager.GetActiveScene();
        ExportResult r = ExportScene(scene);
        if (r == null) { Debug.LogError("[关卡数据] 导出失败"); return; }
        WriteJson(scene.name, r.data);
        Report(scene.name, r);
    }

    [MenuItem("Tools/呆呆史莱姆/⓪-2 从 LevelData JSON 重建关卡", false, 91)]
    public static void RebuildMenu()
    {
        Scene scene = EditorSceneManager.GetActiveScene();
        bool ok = RebuildScene(scene, out string msg);
        Debug.Log("[关卡数据] " + msg);
        if (!ok) EditorUtility.DisplayDialog("重建关卡", msg, "好");
    }

    [MenuItem("Tools/呆呆史莱姆/⓪-3 往返一致性检查（导出→重建→再导出）", false, 92)]
    public static void RoundTripMenu()
    {
        Scene scene = EditorSceneManager.GetActiveScene();
        bool ok = RoundTrip(scene, out string msg);
        Debug.Log("[关卡数据] 往返检查：" + msg);
        if (!Application.isBatchMode) EditorUtility.DisplayDialog("往返一致性检查", msg, "好");
    }

    // ======================= 收益公式自检 =======================

    [MenuItem("Tools/呆呆史莱姆/⓪-5 收益公式自检（打出数值表）", false, 93)]
    public static void ScoreTableMenu()
    {
        Debug.Log(BuildScoreTable(EditorSceneManager.GetActiveScene()));
    }

    /// <summary>命令行入口：Unity.exe ... -executeMethod LevelDataWindow.BatchScoreTable</summary>
    public static void BatchScoreTable()
    {
        foreach (string name in LevelSceneNames)
        {
            string path = SceneDir + "/" + name + ".unity";
            if (!File.Exists(path)) { Debug.LogWarning("[收益自检] 场景不存在：" + path); continue; }
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Debug.Log(BuildScoreTable(EditorSceneManager.GetActiveScene()));
        }
    }

    /// <summary>
    /// 用【运行时同一套静态公式】打一张数值表。
    /// 目的：让「文档里算的数」和「代码里跑的数」永远是同一个，避免两边各算一套。
    /// </summary>
    static string BuildScoreTable(Scene scene)
    {
        ExportResult ex = ExportScene(scene);
        int coinTotal = 0;
        if (ex != null)
        {
            for (int i = 0; i < ex.data.objects.Length; i++)
            {
                LevelObject o = ex.data.objects[i];
                if (o != null && o.kind == LevelObjectKind.Coin)
                    coinTotal += Mathf.RoundToInt(o.Param(0, ex.data.meta.coinValue));
            }
        }

        LevelManager lm = FindInScene<LevelManager>(scene);
        float par = lm != null ? lm.parTime : 50f;
        float[] ratios = (lm != null && lm.starRatios != null && lm.starRatios.Length >= 3)
                       ? lm.starRatios : new float[] { 0.6f, 0.9f, 1.1f };

        int barMax = LevelManager.BarMaxFor(par);
        float drain = LevelManager.DrainRateFor(par);
        float drainPerSec = LevelManager.drainPerSecond;
        float parRemain = LevelManager.parRemain;
        float floorRatio = LevelManager.barFloor;

        // 血条掉到下限需要多久（之后时间就不再掉血了）
        float floorTime = drain > 0.0001f ? (1f - Mathf.Clamp01(floorRatio)) * 100f / drain : 9999f;
        int coinTarget = Mathf.RoundToInt(2f * par);            // = 20% × barMax
        int baseline = Mathf.RoundToInt(barMax * parRemain) + coinTotal;
        float bloodPerYuan = barMax > 0 ? 100f / barMax : 0f;   // 1 元 = 多少点血

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== 收益公式自检：" + scene.name + " ==========");
        sb.AppendLine(string.Format("【全局常数】每秒掉 {0:0.##} 元 ｜ 标准时间血条剩 {1:P0} ｜ 时间掉血下限 {2:P0}",
            drainPerSec, parRemain, floorRatio));
        sb.AppendLine(string.Format("【本关】标准时间 {0:0.#}s → 售价条满格 {1} 元 ｜ 掉血速率 {2:0.###} 血/秒",
            par, barMax, drain));
        sb.AppendLine(string.Format("【换算】1 秒 = {0:0.##} 元 ｜ 1 点血 = {1:0.##} 元 ｜ 1 枚金币({2}元) = {3:0.#} 秒绕路预算",
            drainPerSec, 1f / Mathf.Max(0.0001f, bloodPerYuan), 10, LevelManager.CoinDetourBudget(10)));
        sb.AppendLine(string.Format("【金币】本关 {0} 元，目标 ≈ {1} 元（= 2 × 标准时间）{2}",
            coinTotal, coinTarget,
            Mathf.Abs(coinTotal - coinTarget) <= coinTarget * 0.25f ? "  ✅" : "  ⚠ 偏离目标"));
        sb.AppendLine(string.Format("【关卡长度】按「跑图为主」估 ≈ {0} 格（4 × 标准时间；纯跑道 6.5 格/秒，有跳跃机关按 3~4 格/秒）。" +
            "操作密集的关卡（教程关）有效速度只有 1.5~2 格/秒，这个估计不适用",
            Mathf.RoundToInt(4f * par)));
        sb.AppendLine(string.Format("【星级基准分】{0} = 满格×{1:P0}({2}) + 金币({3})",
            baseline, parRemain, Mathf.RoundToInt(barMax * parRemain), coinTotal));
        sb.AppendLine(string.Format("  ⭐ >= {0}    ⭐⭐ >= {1}    ⭐⭐⭐ >= {2}    （理论上限 {3}）",
            Mathf.RoundToInt(baseline * ratios[0]),
            Mathf.RoundToInt(baseline * ratios[1]),
            Mathf.RoundToInt(baseline * ratios[2]),
            barMax + coinTotal));
        sb.AppendLine("  用时  | 血条 | 售价 | +全金币 | 备注");
        // 时间点必须【排序】，而且要把本关的标准时间插进去 —— 否则标准时间那一行会插在中间，
        // 表格读起来是乱的（之前就是这个 bug）。
        List<float> times = new List<float> { 20f, 30f, 40f, 50f, 60f, 70f, 80f, 90f, 100f, 120f };
        if (!times.Contains(par)) times.Add(par);
        times.Sort();

        bool floorMarked = false;
        for (int i = 0; i < times.Count; i++)
        {
            float t = times[i];
            float hpLeft = Mathf.Max(Mathf.Clamp01(floorRatio) * 100f, 100f - drain * t);
            int price = Mathf.RoundToInt(barMax * hpLeft / 100f);
            string note = "";
            if (Mathf.Abs(t - par) < 0.01f) note = "← 标准时间";
            else if (!floorMarked && t >= floorTime && floorTime < 9000f)
            {
                note = "← 从这行起已触到下限，时间不再掉血";
                floorMarked = true;
            }
            sb.AppendLine(string.Format("  {0,4:F0}s | {1,3:F0}% | {2,4} | {3,7} | {4}",
                t, hpLeft, price, price + coinTotal, note));
        }
        sb.AppendLine("==========================================");
        return sb.ToString();
    }

    // ======================= 批处理入口 =======================

    /// <summary>
    /// 命令行入口：
    /// Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelDataWindow.BatchExportAll
    /// </summary>
    public static void BatchExportAll()
    {
        EnsureDir(LevelsDir);
        int ok = 0;
        foreach (string name in LevelSceneNames)
        {
            string path = SceneDir + "/" + name + ".unity";
            if (!File.Exists(path)) { Debug.LogWarning("[关卡数据] 场景不存在，跳过：" + path); continue; }

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Scene scene = EditorSceneManager.GetActiveScene();
            ExportResult r = ExportScene(scene);
            if (r == null) { Debug.LogError("[关卡数据] 导出失败：" + name); continue; }

            WriteJson(name, r.data);
            Report(name, r);
            ok++;
        }
        AssetDatabase.Refresh();
        Debug.Log("[关卡数据] 批处理导出完成，共 " + ok + " 个场景。JSON 目录：" + LevelsDir);
    }

    /// <summary>
    /// 命令行入口：导出 → 重建 → 再导出 → 逐行比对。「一致才保存场景」，不一致就保持原文件不动。
    /// Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelDataWindow.BatchRoundTripAll
    /// </summary>
    public static void BatchRoundTripAll()
    {
        EnsureDir(LevelsDir);
        int pass = 0, fail = 0;

        foreach (string name in LevelSceneNames)
        {
            string path = SceneDir + "/" + name + ".unity";
            if (!File.Exists(path)) { Debug.LogWarning("[关卡数据] 场景不存在，跳过：" + path); continue; }

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Scene scene = EditorSceneManager.GetActiveScene();

            ExportResult first = ExportScene(scene);
            if (first == null) { Debug.LogError("[关卡数据] 往返检查：导出失败 " + name); fail++; continue; }

            // 【关键检查】磁盘上的 JSON 与"从场景导出的结果"必须一致。
            // 往返检查只能证明"重建前后自洽"，证明不了"导出器没有漏字段"——
            // 漏了字段它会一路悄悄收敛到代码默认值，看起来完全正常。
            // 这一步专门抓这种漏：只要不一致，就说明有数据在场景→JSON 的路上丢了。
            string beforePath = LevelsDir + "/" + name + ".json";
            if (File.Exists(beforePath))
            {
                LevelData onDisk = LevelData.FromJson(File.ReadAllText(beforePath, Encoding.UTF8));
                if (onDisk != null)
                {
                    List<string> exp = onDisk.CanonicalLines();
                    List<string> got = first.data.CanonicalLines();
                    List<string> lost = new List<string>();
                    int maxLines = Mathf.Max(exp.Count, got.Count);
                    for (int k = 0; k < maxLines && lost.Count < 12; k++)
                    {
                        string a2 = k < exp.Count ? exp[k] : "(JSON 里没有)";
                        string b2 = k < got.Count ? got[k] : "(场景里没有)";
                        if (a2 != b2) lost.Add("    JSON：" + a2 + "\n    场景：" + b2);
                    }
                    if (lost.Count > 0)
                    {
                        Debug.LogError("[关卡数据] ⚠ " + name + " 的 JSON 与场景不一致 —— 导出器可能漏读了字段，" +
                                       "继续导出会把下面这些差异【永久写进 JSON】：\n" + string.Join("\n", lost.ToArray()));
                    }
                }
            }

            WriteJson(name, first.data);
            Report(name, first);

            bool ok = RoundTrip(scene, out string msg);
            Debug.Log("========== 往返一致性检查：" + name + " → " + (ok ? "一致 ✅" : "不一致 ❌") + " ==========\n" + msg);

            if (ok)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[关卡数据] " + name + " 已保存重建结果。");
                pass++;
            }
            else
            {
                Debug.LogWarning("[关卡数据] " + name + " 往返不一致 →【没有保存】，场景文件保持原样。");
                fail++;
            }
        }

        AssetDatabase.Refresh();
        Debug.Log("[关卡数据] 往返检查批处理结束：通过 " + pass + " 个 / 失败 " + fail + " 个。");
    }

    /// <summary>
    /// 命令行入口：只用 JSON 重建（数据是真相源），重建完再跑一遍往返检查。
    /// Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelDataWindow.BatchRebuildAndVerifyAll
    /// </summary>
    public static void BatchRebuildAndVerifyAll()
    {
        BatchRebuildAll();
        BatchRoundTripAll();
    }

    /// <summary>只用 JSON 重建所有关卡场景并保存。</summary>
    public static void BatchRebuildAll()
    {
        EnsureDir(LevelsDir);
        foreach (string name in LevelSceneNames)
        {
            string path = SceneDir + "/" + name + ".unity";
            if (!File.Exists(path)) { Debug.LogWarning("[关卡数据] 场景不存在，跳过：" + path); continue; }

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Scene scene = EditorSceneManager.GetActiveScene();
            if (!RebuildScene(scene, out string m))
            {
                Debug.LogError("[关卡数据] 重建失败 " + name + "：" + m);
                continue;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[关卡数据] " + name + " 已按 JSON 重建并保存。\n" + m);
        }
        AssetDatabase.Refresh();
    }

    // ======================= 导出：场景 → LevelData =======================

    public class ExportResult
    {
        public LevelData data;
        public List<GameObject> sources = new List<GameObject>();
        public Dictionary<GameObject, string> idOf = new Dictionary<GameObject, string>();
        public List<string> skipped = new List<string>();
        public List<string> quantizeWarn = new List<string>();
    }

    public static ExportResult ExportScene(Scene scene)
    {
        if (!scene.IsValid()) return null;

        ExportResult r = new ExportResult();
        LevelData data = new LevelData();
        data.meta.levelId = scene.name;
        data.meta.displayName = scene.name;

        List<TerrainBlock> terrain = new List<TerrainBlock>();
        List<LevelObject> objects = new List<LevelObject>();
        List<Mural> murals = new List<Mural>();
        HashSet<Transform> consumed = new HashSet<Transform>();

        LevelBuilder builder = FindInScene<LevelBuilder>(scene);
        int coinN = 0, orbN = 0, wpN = 0, plateN = 0, platN = 0, gateN = 0, cpN = 0, enN = 0;

        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            foreach (Transform tr in rootGo.GetComponentsInChildren<Transform>(true))
            {
                GameObject go = tr.gameObject;
                if (consumed.Contains(tr)) continue;
                if (go.GetComponent<LevelBuilder>() != null) continue;   // 生成器自己不算关卡内容
                if (go.name.StartsWith("__")) continue;
                if (IsInsideConsumed(tr, consumed)) continue;

                // ---- 1) 壁画路牌（必须先判，否则它的 Post / Board 子物体会被当成未归类对象）----
                Mural mural;
                if (ClassifyMural(go, builder, out mural))
                {
                    murals.Add(mural);
                    r.sources.Add(go);
                    consumed.Add(tr);
                    continue;
                }

                // ---- 2) 物件类 ----
                LevelObjectKind okind;
                float[] op;
                if (ClassifyObject(go, out okind, out op))
                {
                    Bounds b;
                    bool hasBounds = TryWorldBounds(go, out b);
                    Vector2 center = hasBounds ? (Vector2)b.center : (Vector2)go.transform.position;

                    LevelObject lo = new LevelObject();
                    lo.kind = okind;
                    lo.name = go.name;
                    lo.p = op;
                    QuantizeCenter(center, r, go.name, out lo.qx, out lo.qy);

                    switch (okind)
                    {
                        case LevelObjectKind.Coin: lo.id = "coin_" + (coinN++); break;
                        case LevelObjectKind.SlimeOrb: lo.id = "orb_" + (orbN++); break;
                        case LevelObjectKind.Waypoint: lo.id = "stone_" + (wpN++); break;
                        case LevelObjectKind.PressurePlate: lo.id = "plate_" + (plateN++); break;
                        case LevelObjectKind.MovingPlatform: lo.id = "mplat_" + (platN++); break;
                        case LevelObjectKind.Gate: lo.id = "gate_" + (gateN++); break;
                        case LevelObjectKind.Checkpoint: lo.id = "cp_" + (cpN++); break;
                        case LevelObjectKind.Enemy: lo.id = "enemy_" + (enN++); break;
                        default: lo.id = "goal_0"; break;
                    }

                    // 尺寸用真实浮点（不吸附网格）：它由碰撞体量出来，是技术值不是设计值。
                    if (hasBounds) { lo.sizeX = b.size.x; lo.sizeY = b.size.y; }
                    if (okind == LevelObjectKind.MovingPlatform)
                    {
                        lo.pts = ExportPoints(go);
                    }

                    objects.Add(lo);
                    r.idOf[go] = lo.id;
                    r.sources.Add(go);
                    consumed.Add(tr);
                    continue;
                }

                // ---- 3) 地形类 ----
                TerrainKind tkind;
                float[] tp;
                if (ClassifyTerrain(go, out tkind, out tp))
                {
                    Bounds b;
                    if (!TryWorldBounds(go, out b))
                    {
                        r.skipped.Add(go.name + "（地形候选但没有碰撞体/精灵，跳过）");
                        continue;
                    }
                    TerrainBlock tb = new TerrainBlock();
                    tb.name = go.name;
                    tb.kind = tkind;
                    tb.p = tp;
                    QuantizeMin(b.min, r, go.name, out tb.qx, out tb.qy);
                    QuantizeSize(b.size, r, go.name, out tb.qw, out tb.qh);
                    terrain.Add(tb);
                    r.sources.Add(go);
                    consumed.Add(tr);
                    continue;
                }

                // ---- 4) 其余：有精灵或有碰撞体但没归类 → 记下来给人看 ----
                if (go.GetComponent<SpriteRenderer>() != null || go.GetComponent<Collider2D>() != null)
                {
                    if (!IsKnownTemplate(go))
                        r.skipped.Add(go.name + "  layer=" + LayerMask.LayerToName(go.layer));
                }
            }
        }

        // 出生点：不做 1/4 格量化（它是由碰撞体半高推出来的技术值，见 LevelMeta 注释）
        PlayerController pc = FindInScene<PlayerController>(scene);
        SlimeController sc = FindInScene<SlimeController>(scene);
        if (pc != null || sc != null)
        {
            data.meta.hasSpawn = true;
            if (pc != null) { data.meta.playerX = pc.transform.position.x; data.meta.playerY = pc.transform.position.y; }
            if (sc != null) { data.meta.slimeX = sc.transform.position.x; data.meta.slimeY = sc.transform.position.y; }
        }

        // 关卡编号与存档开关
        LevelManager lm = FindInScene<LevelManager>(scene);
        if (lm != null)
        {
            // ⚠ 凡是 LevelBuilder 写进组件的值，导出器都必须读回来。
            //   漏一个，下一次导出就会把它悄悄重置成代码默认值（踩过两次：mural、parTime）。
            data.meta.levelIndex = lm.levelIndex;
            data.meta.saveProgress = lm.saveProgress;
            data.meta.parTime = lm.parTime;
            data.meta.barMaxOverride = lm.barMaxOverride;
            if (lm.starRatios != null && lm.starRatios.Length > 0)
                data.meta.starRatios = (float[])lm.starRatios.Clone();
        }

        SlimePathFollow pf = FindInScene<SlimePathFollow>(scene);
        if (pf != null) data.meta.startStones = pf.stoneCount;

        LevelBuilder lb = FindInScene<LevelBuilder>(scene);
        if (lb != null) data.meta.coinValue = lb.defaultCoinValue;

        // 相机取景（也是关卡数据，必须能往返，否则重建会把取景改掉）
        CameraFollow cf = FindInScene<CameraFollow>(scene);
        if (cf != null)
        {
            data.meta.useCameraBounds = cf.useBounds;
            data.meta.camMinX = cf.boundsMin.x;
            data.meta.camMinY = cf.boundsMin.y;
            data.meta.camMaxX = cf.boundsMax.x;
            data.meta.camMaxY = cf.boundsMax.y;
            Camera cam = cf.GetComponent<Camera>();
            if (cam != null) data.meta.cameraSize = cam.orthographicSize;
        }

        // 联动关系
        List<LevelLink> links = new List<LevelLink>();
        foreach (GameObject src in r.sources)
        {
            PressurePlate plate = src.GetComponent<PressurePlate>();
            if (plate == null) continue;
            string sid;
            if (!r.idOf.TryGetValue(src, out sid)) continue;

            if (plate.platforms != null)
                foreach (MovingPlatform mp in plate.platforms)
                {
                    if (mp == null) continue;
                    string tid;
                    if (r.idOf.TryGetValue(mp.gameObject, out tid))
                        links.Add(new LevelLink(sid, tid, GuessLinkMode(plate), 0f));
                }
            if (plate.toggleObjects != null)
                foreach (GameObject tg in plate.toggleObjects)
                {
                    if (tg == null) continue;
                    string tid;
                    if (r.idOf.TryGetValue(tg, out tid))
                        links.Add(new LevelLink(sid, tid, GuessLinkMode(plate), 0f));
                }
        }

        data.terrain = terrain.ToArray();
        data.objects = objects.ToArray();
        data.links = links.ToArray();
        data.murals = murals.ToArray();
        data.EnsureNoNulls();
        r.data = data;
        return r;
    }

    static LinkMode GuessLinkMode(PressurePlate plate)
    {
        if (plate.oneShot) return plate.objectsActiveWhenPressed ? LinkMode.OpenOnce : LinkMode.CloseOnce;
        if (plate.objectsActiveWhenPressed) return LinkMode.Reverse;
        return LinkMode.Toggle;
    }

    static bool IsInsideConsumed(Transform tr, HashSet<Transform> consumed)
    {
        Transform p = tr.parent;
        while (p != null)
        {
            if (consumed.Contains(p)) return true;
            p = p.parent;
        }
        return false;
    }

    /// <summary>
    /// 认一块壁画路牌。生成器建的牌叫 `Mural_&lt;代号&gt;`，子物体固定是 Post / Board / (Icon 或 Label)，
    /// 所以可以从结构和名字反推回数据 —— 这点很重要：**导出器不认识的东西，下一次导出就会从数据里消失**。
    /// </summary>
    static bool ClassifyMural(GameObject go, LevelBuilder builder, out Mural mural)
    {
        mural = null;
        if (go == null || !go.name.StartsWith("Mural_", StringComparison.Ordinal)) return false;

        Mural m = new Mural();
        m.muralId = go.name.Substring("Mural_".Length);
        m.label = "";
        m.sprIndex = -1;
        m.qx = LevelUnits.ToQuarter(go.transform.position.x);
        m.qy = LevelUnits.ToQuarter(go.transform.position.y);

        Transform board = go.transform.Find("Board");
        if (board != null)
        {
            m.sizeX = board.localScale.x;
            m.sizeY = board.localScale.y;
        }

        Transform label = go.transform.Find("Label");
        if (label != null)
        {
            TextMeshPro tmp = label.GetComponent<TextMeshPro>();
            if (tmp != null) m.label = tmp.text;
        }

        // 正式版：从图标精灵反查它在 builder.muralSprites 里的下标
        Transform icon = go.transform.Find("Icon");
        if (icon != null && builder != null && builder.muralSprites != null)
        {
            SpriteRenderer isr = icon.GetComponent<SpriteRenderer>();
            if (isr != null && isr.sprite != null)
            {
                for (int i = 0; i < builder.muralSprites.Length; i++)
                {
                    if (builder.muralSprites[i] == isr.sprite) { m.sprIndex = i; break; }
                }
            }
        }

        mural = m;
        return true;
    }

    static bool ClassifyObject(GameObject go, out LevelObjectKind kind, out float[] p)
    {
        kind = LevelObjectKind.Coin;
        p = new float[0];

        Coin coin = go.GetComponent<Coin>();
        if (coin != null) { kind = LevelObjectKind.Coin; p = new float[] { coin.value }; return true; }

        SlimeOrb orb = go.GetComponent<SlimeOrb>();
        if (orb != null) { kind = LevelObjectKind.SlimeOrb; p = new float[] { orb.healAmount, orb.allowOverheal ? 1f : 0f }; return true; }

        if (go.GetComponent<WaypointMarker>() != null) { kind = LevelObjectKind.Waypoint; p = new float[0]; return true; }

        Goal goal = go.GetComponent<Goal>();
        if (goal != null)
        {
            kind = LevelObjectKind.Goal;
            p = new float[] { goal.onlySlime ? 1f : 0f, goal.requireSlimeNotCarried ? 1f : 0f, goal.cutsceneDuration };
            return true;
        }

        PressurePlate plate = go.GetComponent<PressurePlate>();
        if (plate != null)
        {
            kind = LevelObjectKind.PressurePlate;
            p = new float[] { plate.stayPressed ? 1f : 0f, plate.oneShot ? 1f : 0f,
                               plate.objectsActiveWhenPressed ? 1f : 0f, plate.pressDepth };
            return true;
        }

        MovingPlatform mp = go.GetComponent<MovingPlatform>();
        if (mp != null)
        {
            kind = LevelObjectKind.MovingPlatform;
            p = new float[] { mp.speed, mp.waitTime, mp.startActivated ? 1f : 0f,
                               mp.activatedByPlate ? 1f : 0f, mp.carryRider ? 1f : 0f };
            return true;
        }

        Checkpoint cp = go.GetComponent<Checkpoint>();
        if (cp != null)
        {
            kind = LevelObjectKind.Checkpoint;
            p = new float[] { cp.forPlayer ? 1f : 0f, cp.forSlime ? 1f : 0f };
            return true;
        }

        Enemy en = go.GetComponent<Enemy>();
        if (en != null)
        {
            kind = LevelObjectKind.Enemy;
            p = new float[] { en.moveSpeed, en.patrolDistance, en.waitAtTurnTime, en.startDirection,
                               en.contactDamage, en.killPlayer ? 1f : 0f };
            return true;
        }

        // Gate 没有专属脚本，靠 Layer 认：Gate 层 + 没有任何脚本 + 有碰撞体/精灵
        if (LayerMask.LayerToName(go.layer) == "Gate" && !HasAnyBehaviour(go) &&
            (go.GetComponent<Collider2D>() != null || go.GetComponent<SpriteRenderer>() != null))
        {
            kind = LevelObjectKind.Gate;
            p = new float[0];
            return true;
        }

        return false;
    }

    static bool ClassifyTerrain(GameObject go, out TerrainKind kind, out float[] p)
    {
        kind = TerrainKind.Ground;
        p = new float[0];

        Hazard h = go.GetComponent<Hazard>();
        if (h != null)
        {
            if (h.hazardType == HazardType.Water)
            {
                kind = TerrainKind.Water;
                p = new float[] { h.damage };
            }
            else
            {
                kind = TerrainKind.Hazard;
                // ⚠ 这里漏一个参数，重建时就会被重置成默认值（踩过好几次）
                p = new float[] { h.damage, h.killPlayer ? 1f : 0f, h.damageOnce ? 1f : 0f, h.fatalToPlayer ? 1f : 0f };
            }
            return true;
        }

        // 剩下的必须"没有任何脚本"，否则它属于场景模板（玩家/相机/管理器…）
        if (HasAnyBehaviour(go)) return false;

        string layer = LayerMask.LayerToName(go.layer);
        bool isGate = layer == "Gate";
        bool isSolid = layer == "Ground" || layer == "Platform";

        if (!isGate && !isSolid) return false;
        if (go.GetComponent<SpriteRenderer>() == null && go.GetComponent<Collider2D>() == null) return false;

        if (isGate)
        {
            // Gate 在数据里是物件，不是地形 —— 这里返回 false，交给调用方按物件处理
            return false;
        }

        if (go.GetComponent<Collider2D>() == null) { kind = TerrainKind.Decor; return true; }
        kind = (layer == "Platform") ? TerrainKind.OneWayPlatform : TerrainKind.Ground;
        return true;
    }

    static bool HasAnyBehaviour(GameObject go)
    {
        MonoBehaviour[] mbs = go.GetComponents<MonoBehaviour>();
        for (int i = 0; i < mbs.Length; i++)
            if (mbs[i] != null && !(mbs[i] is LevelBuilder)) return true;
        return false;
    }

    static readonly string[] TemplateTypes =
    {
        "PlayerController", "PlayerGrab", "PlayerInventory", "SlimeController", "SlimePathFollow",
        "CameraFollow", "LevelManager", "GameManager", "UIManager", "InventoryUI", "LevelResult",
        "PauseMenu", "MainMenu", "LevelSelectUI", "EventSystem", "Canvas", "Camera", "AudioListener"
    };

    static bool IsKnownTemplate(GameObject go)
    {
        if (go.GetComponent<Camera>() != null) return true;
        Component[] comps = go.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i] == null) continue;
            string n = comps[i].GetType().Name;
            for (int k = 0; k < TemplateTypes.Length; k++)
                if (n == TemplateTypes[k]) return true;
        }
        return false;
    }

    static int[] ExportPoints(GameObject go)
    {
        MovingPlatform mp = go.GetComponent<MovingPlatform>();
        if (mp == null || mp.points == null || mp.points.Length == 0) return new int[0];
        List<int> list = new List<int>();
        for (int i = 0; i < mp.points.Length; i++)
        {
            if (mp.points[i] == null) continue;
            Vector3 d = mp.points[i].position - go.transform.position;
            list.Add(LevelUnits.ToQuarter(d.x));
            list.Add(LevelUnits.ToQuarter(d.y));
        }
        return list.ToArray();
    }

    // ======================= 量化 =======================

    static void QuantizeCenter(Vector2 center, ExportResult r, string who, out int qx, out int qy)
    {
        Warn(center.x, r, who + ".x");
        Warn(center.y, r, who + ".y");
        qx = LevelUnits.ToQuarter(center.x);
        qy = LevelUnits.ToQuarter(center.y);
    }

    static void QuantizeMin(Vector2 min, ExportResult r, string who, out int qx, out int qy)
    {
        Warn(min.x, r, who + ".minX");
        Warn(min.y, r, who + ".minY");
        qx = LevelUnits.ToQuarter(min.x);
        qy = LevelUnits.ToQuarter(min.y);
    }

    static void QuantizeSize(Vector2 size, ExportResult r, string who, out int qw, out int qh)
    {
        Warn(size.x, r, who + ".w");
        Warn(size.y, r, who + ".h");
        qw = Mathf.Max(1, LevelUnits.ToQuarter(size.x));
        qh = Mathf.Max(1, LevelUnits.ToQuarter(size.y));
    }

    static void Warn(float world, ExportResult r, string who)
    {
        float e = LevelUnits.QuantizeError(world);
        if (e > QuantizeWarn)
            r.quantizeWarn.Add(string.Format("{0} = {1:F4} 量化到 {2:F4}（误差 {3:F4}）", who, world, LevelUnits.SnapWorld(world), e));
    }

    // ======================= 重建：LevelData → 场景 =======================

    public static bool RebuildScene(Scene scene, out string msg)
    {
        if (!scene.IsValid()) { msg = "没有打开的场景"; return false; }

        string jsonPath = LevelsDir + "/" + scene.name + ".json";
        if (!File.Exists(jsonPath))
        {
            msg = "找不到关卡数据：" + jsonPath + "\n先点 ⓪-1 导出。";
            return false;
        }

        LevelData data = LevelData.FromJson(File.ReadAllText(jsonPath, Encoding.UTF8));
        if (data == null) { msg = "JSON 解析失败：" + jsonPath; return false; }

        // 1) 先把场景里现有的关卡内容找出来（用于删除）
        ExportResult before = ExportScene(scene);

        LevelBuilder builder = EnsureBuilder(scene, before);

        // 2) 删掉旧的关卡内容（场景模板不动）
        int removed = 0;
        foreach (GameObject go in before.sources)
        {
            if (go == null) continue;
            UnityEngine.Object.DestroyImmediate(go);
            removed++;
        }

        // 3) 生成
        builder.ClearLevel();
        GameObject root = builder.Build(data);
        builder.PlaceSpawns(data);

        // 4) 重新连引用。重建后 LevelManager.goal 之类的旧引用已经变成"假 null"，
        //    必须补回来，否则关卡无法结算。AutoWire 只填空引用，不会覆盖已拖好的。
        //    ⚠ 它会"修"一些 int 字段，所以 IsRepairableInt 里不能放 levelIndex 这种
        //      "0 也是合法值"的字段，否则教程关的 levelIndex = 0 会被改成 1（踩过）。
        SlimeDemoSetup.AutoWireMenu();

        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.Refresh();

        msg = string.Format("重建完成：删掉 {0} 个旧对象，生成 {1} 个新对象（根节点 {2}）。\n数据来源：{3}",
            removed, builder.Spawned.Count, root != null ? root.name : "(失败)", jsonPath);
        return true;
    }

    static LevelBuilder EnsureBuilder(Scene scene, ExportResult existing)
    {
        LevelBuilder builder = FindInScene<LevelBuilder>(scene);
        if (builder == null)
        {
            GameObject go = new GameObject(BuilderName);
            SceneManager.MoveGameObjectToScene(go, scene);
            builder = go.AddComponent<LevelBuilder>();
            Debug.Log("[关卡数据] 场景里没有 " + BuilderName + "，已自动创建。");
        }

        if (builder.levelManager == null) builder.levelManager = FindInScene<LevelManager>(scene);
        if (builder.slime == null) builder.slime = FindInScene<SlimeController>(scene);
        if (builder.pathFollow == null) builder.pathFollow = FindInScene<SlimePathFollow>(scene);
        if (builder.cameraFollow == null) builder.cameraFollow = FindInScene<CameraFollow>(scene);
        if (builder.muralFont == null) builder.muralFont = FindMuralFont();
        if (builder.player == null)
        {
            PlayerController pc = FindInScene<PlayerController>(scene);
            if (pc != null) builder.player = pc.transform;
        }

        // 精灵：从现有场景里借（这样不用知道美术资源路径）
        if (builder.blockSprite == null && existing != null)
        {
            foreach (GameObject go in existing.sources)
            {
                if (go == null) continue;
                if (go.GetComponent<SpriteRenderer>() == null) continue;
                string layer = LayerMask.LayerToName(go.layer);
                if (layer == "Ground" || layer == "Platform") { builder.blockSprite = go.GetComponent<SpriteRenderer>().sprite; break; }
            }
        }
        if (builder.circleSprite == null && existing != null)
        {
            foreach (GameObject go in existing.sources)
            {
                if (go == null) continue;
                if (go.GetComponent<Coin>() == null) continue;
                SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) { builder.circleSprite = sr.sprite; break; }
            }
        }
        return builder;
    }

    // ======================= 往返一致性检查 =======================

    public static bool RoundTrip(Scene scene, out string msg)
    {
        ExportResult first = ExportScene(scene);
        if (first == null) { msg = "第一次导出失败"; return false; }

        WriteJsonTo(Path.Combine(LibraryDir(), scene.name + "_before.json"), first.data);
        if (!RebuildScene(scene, out msg)) return false;

        Scene again = EditorSceneManager.GetActiveScene();
        ExportResult second = ExportScene(again);
        if (second == null) { msg = "重建后的导出失败"; return false; }
        WriteJsonTo(Path.Combine(LibraryDir(), scene.name + "_after.json"), second.data);

        List<string> a = first.data.CanonicalLines();
        List<string> b = second.data.CanonicalLines();

        List<string> diffs = new List<string>();
        int max = Mathf.Max(a.Count, b.Count);
        for (int i = 0; i < max && diffs.Count < 20; i++)
        {
            string la = i < a.Count ? a[i] : "(缺)";
            string lb = i < b.Count ? b[i] : "(缺)";
            if (la != lb) diffs.Add("第 " + i + " 行\n    重建前：" + la + "\n    重建后：" + lb);
        }

        StringBuilder sb = new StringBuilder();
        if (diffs.Count == 0)
        {
            sb.AppendLine("往返一致 ✅  共 " + a.Count + " 条记录（地形 " + first.data.terrain.Length +
                          " / 物件 " + first.data.objects.Length + " / 联动 " + first.data.links.Length + "）");
        }
        else
        {
            sb.AppendLine("往返不一致 ❌  重建前 " + a.Count + " 条 / 重建后 " + b.Count + " 条，前 " + diffs.Count + " 处差异：");
            foreach (string d in diffs) sb.AppendLine("  " + d);
        }
        if (first.quantizeWarn.Count > 0)
        {
            sb.AppendLine("量化误差警告 " + first.quantizeWarn.Count + " 条：");
            for (int i = 0; i < first.quantizeWarn.Count && i < 10; i++) sb.AppendLine("  " + first.quantizeWarn[i]);
        }
        msg = sb.ToString();
        return diffs.Count == 0;
    }

    // ======================= 报告 =======================

    static void Report(string sceneName, ExportResult r)
    {
        LevelData d = r.data;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== 关卡数据导出：" + sceneName + " ==========");
        sb.AppendLine("地形 " + d.terrain.Length + " 块 / 物件 " + d.objects.Length + " 个 / 联动 " + d.links.Length + " 条");
        sb.AppendLine("  金币 " + d.CountOf(LevelObjectKind.Coin) +
                      " / 史莱姆球 " + d.CountOf(LevelObjectKind.SlimeOrb) +
                      " / 引导石 " + d.CountOf(LevelObjectKind.Waypoint) +
                      " / 终点 " + d.CountOf(LevelObjectKind.Goal));
        Vector2 min, max;
        if (d.GetBounds(out min, out max))
            sb.AppendLine(string.Format("  包围盒 x[{0:F2}, {1:F2}]  y[{2:F2}, {3:F2}]  宽 {4:F2} 高 {5:F2}",
                min.x, max.x, min.y, max.y, max.x - min.x, max.y - min.y));

        int ground = 0, plat = 0, water = 0, haz = 0, decor = 0;
        foreach (TerrainBlock t in d.terrain)
        {
            if (t.kind == TerrainKind.Ground) ground++;
            else if (t.kind == TerrainKind.OneWayPlatform) plat++;
            else if (t.kind == TerrainKind.Water) water++;
            else if (t.kind == TerrainKind.Hazard) haz++;
            else decor++;
        }
        sb.AppendLine("  地形分类：实心 " + ground + " / 单向平台 " + plat + " / 水 " + water + " / 伤害 " + haz + " / 装饰 " + decor);

        List<string> v = d.Validate();
        if (v.Count == 0) sb.AppendLine("  静态校验：通过");
        else { sb.AppendLine("  静态校验：" + v.Count + " 条问题"); foreach (string s in v) sb.AppendLine("    · " + s); }

        if (r.quantizeWarn.Count > 0)
        {
            sb.AppendLine("  ⚠ 量化误差 > " + QuantizeWarn + " 的字段 " + r.quantizeWarn.Count + " 条：");
            for (int i = 0; i < r.quantizeWarn.Count && i < 12; i++) sb.AppendLine("    · " + r.quantizeWarn[i]);
        }
        if (r.skipped.Count > 0)
        {
            sb.AppendLine("  未归类（有精灵/碰撞体但没被收进数据）" + r.skipped.Count + " 个：");
            for (int i = 0; i < r.skipped.Count && i < 12; i++) sb.AppendLine("    · " + r.skipped[i]);
        }
        sb.AppendLine("==========================================");
        Debug.Log(sb.ToString());
    }

    // ======================= 教程关 Level0 =======================

    /// <summary>
    /// 【一次性生成器】按《施工图_教程关Level0_坐标级.md》生成教程关数据。
    ///
    /// ⚠ 生成之后以 `Levels/Level0.json` 为真相源 ——
    ///   以后改教程关请改 JSON（等 P1 面板做出来就用面板），不要再回来改这个方法。
    ///   它存在的唯一理由是"第一次把施工图变成数据"，避免手写 60 多条坐标。
    ///
    /// 相对施工图的三处调整（见对话记录）：
    ///   1. Tunnel_1 净空 1.1 → 1.0（网格化：1/4 格里 (0.9, 1.2) 区间只有 1.0）
    ///   2. parTime = 80 秒（施工图的 3.5 分钟是"第一次玩"的时间，不是熟练标准时间）
    ///   3. 三枚金币 y 3.2 → 3.25、路牌 y 全部对齐到 1/4 格
    /// </summary>
    public static LevelData BuildTutorialData()
    {
        LevelData d = new LevelData();
        LevelMeta m = d.meta;
        m.levelId = "Level0";
        m.displayName = "教学关";
        m.parTime = 80f;              // 1 血 = 8 元；满格 800；金币目标 160 = 施工图里的 16 枚 ×10 ✓
        m.startStones = 3;
        m.coinValue = 10;
        m.levelIndex = 0;             // 教程关：通关解锁 LevelUnlocked_1
        m.saveProgress = false;       // 不计成绩、不计星级
        m.hasSpawn = true;
        m.playerX = 3f;  m.playerY = 0.6f;
        m.slimeX = 1f;   m.slimeY = 0.5f;
        m.useCameraBounds = true;
        m.camMinX = -2f; m.camMinY = -6f; m.camMaxX = 134f; m.camMaxY = 12f;
        m.cameraSize = 6.5f;

        List<TerrainBlock> t = new List<TerrainBlock>();
        // ---- 地面（实心）----
        AddT(t, "G01", 0f, -2f, 18f, 2f, TerrainKind.Ground);
        AddT(t, "G02", 18f, -2f, 2f, 2f, TerrainKind.Ground);
        AddT(t, "G_WaterBed1", 20f, -3f, 4f, 1.5f, TerrainKind.Ground);
        AddT(t, "G03", 24f, -2f, 10f, 2f, TerrainKind.Ground);
        AddT(t, "Plat_1", 28f, 0f, 4f, 2.5f, TerrainKind.Ground);       // 教"放下"：携带跳 1.8 上不去
        AddT(t, "G04", 34f, -2f, 6f, 2f, TerrainKind.Ground);
        AddT(t, "G_PitBed1", 40f, -3f, 4f, 1.5f, TerrainKind.Ground);
        AddT(t, "G05", 44f, -2f, 8f, 2f, TerrainKind.Ground);
        AddT(t, "G06a", 52f, -2f, 6f, 2f, TerrainKind.Ground);
        AddT(t, "G_WaterBed3", 58f, -3f, 4f, 1.5f, TerrainKind.Ground);
        AddT(t, "G06b", 62f, -2f, 12f, 2f, TerrainKind.Ground);
        AddT(t, "G07", 74f, -2f, 28f, 2f, TerrainKind.Ground);
        AddT(t, "Tunnel_1", 78f, 1.0f, 14f, 1.5f, TerrainKind.Ground);  // 净空 1.0，顶面 2.5
        AddT(t, "G08", 102f, -2f, 18f, 2f, TerrainKind.Ground);
        AddT(t, "G_WaterBed4", 105f, -3f, 3f, 1.5f, TerrainKind.Ground);
        AddT(t, "G_PitBed2", 112f, -3f, 4f, 1.5f, TerrainKind.Ground);
        AddT(t, "G09", 120f, -2f, 12f, 2f, TerrainKind.Ground);

        // ---- 边界墙（原来靠 EnsureBoundaryWalls 自动生成，现在进数据，避免重建后消失）----
        AddT(t, "Wall_Left", -2f, -13f, 2f, 60f, TerrainKind.Ground);
        AddT(t, "Wall_Right", 132f, -13f, 2f, 60f, TerrainKind.Ground);

        // ---- 水（深 1.5：抱着跳 1.8 出得来，掉进去不会困死）----
        AddT(t, "Water_1", 20f, -1.5f, 4f, 1.5f, TerrainKind.Water, 20f);
        AddT(t, "Water_2", 40f, -1.5f, 4f, 1.5f, TerrainKind.Water, 15f);
        AddT(t, "Water_3", 58f, -1.5f, 4f, 1.5f, TerrainKind.Water, 15f);
        AddT(t, "Water_4", 105f, -1.5f, 3f, 1.5f, TerrainKind.Water, 15f);
        AddT(t, "Water_5", 112f, -1.5f, 4f, 1.5f, TerrainKind.Water, 15f);

        List<LevelObject> o = new List<LevelObject>();
        // ---- 金币（16 枚 × 10 = 160 元 = 2 × parTime ✓ 正好是目标金币总额）----
        // 面值和尺寸都写显式值，不留给"用默认尺寸" —— 否则 JSON 与场景会不一致，
        // 往返检查会报"导出器漏读字段"（其实只是默认值被实体化了）。
        float[] cv = new float[] { 10f };
        AddO(o, "coin_0", "C1_1", LevelObjectKind.Coin, 7f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_1", "C1_2", LevelObjectKind.Coin, 13f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_2", "C2_1", LevelObjectKind.Coin, 27f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_3", "C2_2", LevelObjectKind.Coin, 30f, 3.25f, 0.6f, 0.6f, cv);   // 高台上（必须放下史莱姆才拿得到）
        AddO(o, "coin_4", "C3_1", LevelObjectKind.Coin, 46f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_5", "C3_2", LevelObjectKind.Coin, 49f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_6", "C4_1", LevelObjectKind.Coin, 66.5f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_7", "C4_2", LevelObjectKind.Coin, 70f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_8", "C5_1", LevelObjectKind.Coin, 81f, 0.5f, 0.6f, 0.6f, cv);    // 矮通道内（只有引导成功的玩家拿得到）
        AddO(o, "coin_9", "C5_2", LevelObjectKind.Coin, 85f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_10", "C5_3", LevelObjectKind.Coin, 89f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_11", "C5_4", LevelObjectKind.Coin, 82f, 3.25f, 0.6f, 0.6f, cv);  // 障碍顶（玩家路线）
        AddO(o, "coin_12", "C5_5", LevelObjectKind.Coin, 88f, 3.25f, 0.6f, 0.6f, cv);
        AddO(o, "coin_13", "C6_1", LevelObjectKind.Coin, 110f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_14", "C6_2", LevelObjectKind.Coin, 117.5f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_15", "C7_1", LevelObjectKind.Coin, 122f, 0.5f, 0.6f, 0.6f, cv);

        // ---- 回血球（过完水渠的补给，把掉的血补回来）----
        float[] orb = new float[] { 25f, 0f };
        AddO(o, "orb_0", "Orb_1", LevelObjectKind.SlimeOrb, 25.5f, 0.5f, 0.6f, 0.6f, orb);
        AddO(o, "orb_1", "Orb_2", LevelObjectKind.SlimeOrb, 38.5f, 0.5f, 0.6f, 0.6f, orb);
        AddO(o, "orb_2", "Orb_3", LevelObjectKind.SlimeOrb, 63f, 0.5f, 0.6f, 0.6f, orb);

        // ---- 检查点（4 个；这五类"机关"从未在任何关卡跑过，教程关正好第一次验证检查点）----
        float[] cp = new float[] { 1f, 1f };
        AddO(o, "cp_0", "CP_1", LevelObjectKind.Checkpoint, 34f, 0.5f, 0.8f, 0.8f, cp);
        AddO(o, "cp_1", "CP_2", LevelObjectKind.Checkpoint, 52.5f, 0.5f, 0.8f, 0.8f, cp);
        AddO(o, "cp_2", "CP_3", LevelObjectKind.Checkpoint, 64f, 0.5f, 0.8f, 0.8f, cp);
        AddO(o, "cp_3", "CP_4", LevelObjectKind.Checkpoint, 96f, 0.5f, 0.8f, 0.8f, cp);

        // ---- 收购站 ----
        AddO(o, "goal_0", "Goal", LevelObjectKind.Goal, 129f, 1f, 2f, 2f, new float[] { 1f, 0f, 1.2f });

        // ---- 15 块壁画路牌（占位阶段：牌面上直接写"以后该画什么"）----
        List<Mural> mu = new List<Mural>();
        AddM(mu, "H1", "跟随", 6f, 3.5f);
        AddM(mu, "H2", "举起E", 18.5f, 3.5f);
        AddM(mu, "H3", "水", 22f, 2.5f);
        AddM(mu, "H4", "放下E", 26.5f, 3.5f);
        AddM(mu, "H5", "跳", 30f, 3.75f);
        AddM(mu, "H6", "投掷Q", 38f, 3.5f);
        AddM(mu, "H7", "待命2E", 56f, 3.5f);
        AddM(mu, "H8", "召回Q", 64.5f, 3.5f);
        AddM(mu, "H9", "放石3E", 76f, 3.75f);
        AddM(mu, "H10", "撤石Q", 94f, 3.5f);
        AddM(mu, "H11", "金币", 7f, 1.75f);
        AddM(mu, "H12", "回血", 25.5f, 1.75f);
        AddM(mu, "H13", "收购站", 124f, 3.5f);
        AddM(mu, "R1", "举起E", 104.5f, 3.5f);   // 舱6 复习
        AddM(mu, "R2", "投掷Q", 111f, 3.5f);     // 舱6 复习

        d.terrain = t.ToArray();
        d.objects = o.ToArray();
        d.murals = mu.ToArray();
        d.links = new LevelLink[0];
        d.EnsureNoNulls();
        return d;
    }

    static void AddT(List<TerrainBlock> list, string name, float minX, float minY, float w, float h,
                     TerrainKind kind, params float[] p)
    {
        list.Add(new TerrainBlock(name,
            Mathf.RoundToInt(minX / LevelUnits.Q),
            Mathf.RoundToInt(minY / LevelUnits.Q),
            Mathf.RoundToInt(w / LevelUnits.Q),
            Mathf.RoundToInt(h / LevelUnits.Q), kind, p));
    }

    static void AddO(List<LevelObject> list, string id, string name, LevelObjectKind kind,
                     float x, float y, float sizeX = 0f, float sizeY = 0f, float[] p = null)
    {
        LevelObject o = new LevelObject();
        o.id = id; o.name = name; o.kind = kind;
        o.qx = Mathf.RoundToInt(x / LevelUnits.Q);
        o.qy = Mathf.RoundToInt(y / LevelUnits.Q);
        o.sizeX = sizeX; o.sizeY = sizeY;
        o.p = p ?? new float[0];
        list.Add(o);
    }

    static void AddM(List<Mural> list, string id, string label, float x, float y)
    {
        list.Add(new Mural(id, label,
            Mathf.RoundToInt(x / LevelUnits.Q),
            Mathf.RoundToInt(y / LevelUnits.Q), 3.2f, 1.6f));
    }

    // ======================= 正式关 Level1（规划向：收益最大化） =======================

    /// <summary>
    /// 【一次性生成器】重做 Level1。
    ///
    /// 设计意图：**规划向（收益最大化）+ 密集且各有特点的障碍 + 垂直结构（不是一直向右）**
    ///
    /// 六种障碍，每种要求不同的动作/决策，不重复：
    ///   A 断桥 ×3   x 16/29/42，各 **3 格宽**，**底下没有任何地形 → 即死深坑**
    ///               空手跳 5.87 过得去；携带跳 2.45 过不去 → 必须先放下史莱姆
    ///               ⚠ 坑宽为什么是 3 不是 4：史莱姆跟在玩家身后约 1.4 格，跳距只有 5.85，
    ///                 能跨过的最大坑宽 = 5.85 − 1.4 − 玩家提前起跳的距离。
    ///                 坑宽 4 时要求玩家在离边缘 0.45 格内起跳（几乎不可能）→ 史莱姆必掉坑。
    ///                 3 格时容差 1.45 格，正常玩不会掉。（掉了他也会被送回坑边，不会丢）
    ///   B 柱林 ×5   x 72~90，每根 2 格宽、底 1.5、顶面 2.75
    ///               从下面钻（净空 1.5）快；跳上柱顶拿金币慢 → 上下二选一
    ///   C 垂直井    x 92→118，地面挖空，井底 -6，**必须下去再爬上来**（不是一直向右）
    ///               井底 3 枚金币 + 三级阶梯（每级 2.0，携带跳 1.8 上不去 → 又一次强制放下）
    ///   D 天花板走廊 x 118→142，净空 1.5 → 玩家能走但**跳不起来**（撞顶），节奏从跳切成走
    ///   E 矮通道    x 146→162，净空 1.0 → 玩家钻不进、史莱姆钻得进
    ///               通道内 2 枚金币，想拿必须放下史莱姆 + 放引导石
    ///   F 之字高台  x 164→176，三层（顶面 2.5 / 5.0 / 7.5）**上-左-上**，方向反复
    ///
    /// 数值自洽（parTime 60）：满格 600 元 ｜ 掉血 0.833 血/秒 ｜ 1 血 6 元 ｜ 1 枚金币 = 2 秒
    ///   金币 12 枚 = 120 元 = 目标（2 × parTime）✅
    ///   关卡长 210 格；纯跑 32 秒 + 障碍操作 ≈ 47 秒
    ///   冲刺（40 秒不捡）= 400 元  vs  全收集（50 秒）= 470 元  → 差 17.5% ✅ 落在 15~35%
    ///
    /// 仍然【不放水】：水伤害 = 75 元 = 15 秒时间价值，而抱着走的代价只有 0.5 秒，
    /// 永远该抱着走，做不出取舍。这一关用"即死深坑"当惩罚，比"扣钱的水"更干脆。
    /// </summary>
    public static LevelData BuildLevel1Data()
    {
        LevelData d = new LevelData();
        LevelMeta m = d.meta;
        m.levelId = "Level1";
        m.displayName = "Level1";
        m.parTime = 60f;              // 1 血 = 6 元；满格 600；金币目标 120 = 12 枚 ✅
        m.startStones = 3;
        m.coinValue = 10;
        m.levelIndex = 1;
        m.saveProgress = true;
        m.hasSpawn = true;
        m.playerX = 3f;  m.playerY = 0.6f;
        m.slimeX = 1f;   m.slimeY = 0.5f;
        m.useCameraBounds = true;
        // ⚠ CameraFollow 的 bounds 语义是【相机视野必须待在这个矩形里】，不是"相机中心的范围"：
        //    相机中心会被夹到 [boundsMin + orthographicSize, boundsMax − orthographicSize]。
        //    所以要让相机跟到竖井底（玩家 −6 + offset 1.5 = −4.5），camMinY 必须 ≤ −11；
        //    而 −11 时画面最低正好到 −11，死亡判定块在 −13 仍在屏幕外 ✓
        //    （之前写成 −4，相机中心被夹到 2.5 以上，下井时完全看不见人 —— 踩过）
        m.camMinX = -2f; m.camMinY = -11f; m.camMaxX = 212f; m.camMaxY = 16f;
        m.cameraSize = 6.5f;

        List<TerrainBlock> t = new List<TerrainBlock>();

        // ---- 地面：被三处即死深坑和一口竖井切开 ----
        // 坑宽定 3 格，不是 4 —— 见下面 BuildLevel1Data 的注释：
        // 史莱姆跟在玩家身后 1.4 格，跳距只有 5.85，坑宽 4 时它必然跳不过去、掉坑里。
        AddT(t, "Ground_A", 0f, -2f, 16f, 2f, TerrainKind.Ground);
        // 坑：16→19（3 格，下面没东西 → 掉到 y=-20 即死）
        AddT(t, "Ground_B", 19f, -2f, 10f, 2f, TerrainKind.Ground);
        // 坑：29→32
        AddT(t, "Ground_C", 32f, -2f, 10f, 2f, TerrainKind.Ground);
        // 坑：42→45
        AddT(t, "Ground_D", 45f, -2f, 47f, 2f, TerrainKind.Ground);   // 45→92
        // 竖井：92→118
        AddT(t, "Ground_E", 118f, -2f, 24f, 2f, TerrainKind.Ground);  // 118→142 天花板走廊段
        AddT(t, "Ground_F", 142f, -2f, 20f, 2f, TerrainKind.Ground);  // 142→162 矮通道段
        AddT(t, "Ground_G", 162f, -2f, 48f, 2f, TerrainKind.Ground);  // 162→210 之字台 + 终点区

        // ---- B 柱林：底 1.5（玩家 1.2 钻得过），顶面 2.75（空手跳 3.0 上得去）----
        for (int i = 0; i < 5; i++)
            AddT(t, "Pillar_" + (i + 1), 72f + i * 4f, 1.5f, 2f, 1.25f, TerrainKind.Ground);

        // ---- C 垂直井：井底 -6，三级阶梯爬回主路（每级 2.0，携带跳 1.8 上不去）----
        AddT(t, "PitFloor", 92f, -8f, 22f, 2f, TerrainKind.Ground);   // 92→114，顶面 -6
        AddT(t, "Step_1", 104f, -5f, 4f, 1f, TerrainKind.Ground);     // 顶面 -4
        AddT(t, "Step_2", 108f, -3f, 4f, 1f, TerrainKind.Ground);     // 顶面 -2
        AddT(t, "Step_3", 112f, -2f, 4f, 1f, TerrainKind.Ground);     // 顶面 -1

        // ---- D 天花板走廊：净空 1.5，玩家能走但跳不起来 ----
        AddT(t, "Ceiling", 118f, 1.5f, 24f, 1.5f, TerrainKind.Ground);  // 118→142

        // ---- E 矮通道：净空 1.0，玩家钻不进、史莱姆钻得进 ----
        AddT(t, "Tunnel_1", 146f, 1.0f, 16f, 1.5f, TerrainKind.Ground); // 146→162，顶面 2.5

        // ---- F 之字高台：上-左-上，方向反复 ----
        AddT(t, "Zig_1", 170f, 1.0f, 6f, 1.5f, TerrainKind.Ground);     // 170→176，顶面 2.5
        AddT(t, "Zig_2", 164f, 3.5f, 6f, 1.5f, TerrainKind.Ground);     // 164→170，顶面 5.0
        AddT(t, "Zig_3", 170f, 6.0f, 6f, 1.5f, TerrainKind.Ground);     // 170→176，顶面 7.5

        // ---- 边界墙 ----
        AddT(t, "Wall_Left", -2f, -13f, 2f, 60f, TerrainKind.Ground);
        AddT(t, "Wall_Right", 210f, -13f, 2f, 60f, TerrainKind.Ground);

        // ---- 深坑底部的"死亡判定块"（屏幕看不见）----
        // 一整条横跨全关，所以无论从哪个坑掉下去都会碰到，不会无限往下掉。
        // 参数：伤害 999（史莱姆一下就死）、killPlayer、fatalToPlayer（玩家掉下去直接关卡失败，不给复活）。
        // 位置 y −16~−13：相机的 camMinY 已收到 −4，画面最低只到 −10.5，所以这块永远在屏幕外。
        AddT(t, "DeathPlane", -2f, -16f, 214f, 3f, TerrainKind.Hazard, 999f, 1f, 0f, 1f);

        // ---- 金币：12 枚 = 120 元（正好等于目标）----
        List<LevelObject> o = new List<LevelObject>();
        float[] cv = new float[] { 10f };
        AddO(o, "coin_0", "C_Start", LevelObjectKind.Coin, 8f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_1", "C_Pit1", LevelObjectKind.Coin, 24f, 0.5f, 0.6f, 0.6f, cv);   // 两个坑之间
        AddO(o, "coin_2", "C_Pit2", LevelObjectKind.Coin, 38f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_3", "C_Pillar1", LevelObjectKind.Coin, 72f, 3.25f, 0.6f, 0.6f, cv); // 柱顶
        AddO(o, "coin_4", "C_Pillar5", LevelObjectKind.Coin, 88f, 3.25f, 0.6f, 0.6f, cv);
        AddO(o, "coin_5", "C_Well1", LevelObjectKind.Coin, 98f, -5.5f, 0.6f, 0.6f, cv);   // 井底
        AddO(o, "coin_6", "C_Well2", LevelObjectKind.Coin, 104f, -5.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_7", "C_Well3", LevelObjectKind.Coin, 110f, -5.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_8", "C_Tun1", LevelObjectKind.Coin, 150f, 0.5f, 0.6f, 0.6f, cv);    // 通道内（只有史莱姆拿得到）
        AddO(o, "coin_9", "C_Tun2", LevelObjectKind.Coin, 158f, 0.5f, 0.6f, 0.6f, cv);
        AddO(o, "coin_10", "C_Zig2", LevelObjectKind.Coin, 166f, 5.5f, 0.6f, 0.6f, cv);   // 之字台中/顶层
        AddO(o, "coin_11", "C_Zig3", LevelObjectKind.Coin, 172f, 8.0f, 0.6f, 0.6f, cv);

        // ---- 检查点：**暂时不放** ----
        // 现在"掉坑"和"史莱姆死"都是直接关卡失败，而游戏里没有别的会杀死玩家的东西，
        // 所以检查点等于没用 —— 与其摆一个亮了却不起作用的物件误导玩家，不如先不摆。
        // 机制和脚本都留着（Checkpoint.cs），第 4 步放尖刺 / 敌人时再启用：
        // 那些用 killPlayer 但 fatalToPlayer = false，碰到只扣一条命、回检查点继续。
        // float[] cp = new float[] { 1f, 1f };
        // AddO(o, "cp_0", "CP_1", LevelObjectKind.Checkpoint, 50f, 0.5f, 0.8f, 0.8f, cp);
        // AddO(o, "cp_1", "CP_2", LevelObjectKind.Checkpoint, 120f, 0.5f, 0.8f, 0.8f, cp);
        // AddO(o, "cp_2", "CP_3", LevelObjectKind.Checkpoint, 165f, 0.5f, 0.8f, 0.8f, cp);

        AddO(o, "goal_0", "Goal", LevelObjectKind.Goal, 200f, 1f, 2f, 2f, new float[] { 1f, 0f, 1.2f });

        d.terrain = t.ToArray();
        d.objects = o.ToArray();
        d.murals = new Mural[0];
        d.links = new LevelLink[0];
        d.EnsureNoNulls();
        return d;
    }

    /// <summary>
    /// 菜单入口：重做 Level1 的关卡内容。
    /// 之所以做成菜单而不是只留命令行：Unity 开着的时候跑不了批处理，而做关卡要反复迭代。
    /// ⚠ 会覆盖 Level1 现有的关卡内容（地形 / 金币 / 检查点 / 终点），场景模板不动，且会先备份。
    /// </summary>
    [MenuItem("Tools/呆呆史莱姆/▷ 重做 Level1 关卡内容（覆盖，会先备份）", false, 87)]
    public static void RebuildLevel1Menu()
    {
        bool go = EditorUtility.DisplayDialog(
            "重做 Level1？",
            "会用「规划向」的新设计覆盖 Level1 的关卡内容：\n\n" +
            "  · 地形 / 金币 / 检查点 / 终点 全部换成新的\n" +
            "  · 玩家、史莱姆、相机、UI、管理器（场景模板）不动\n" +
            "  · 旧场景会先备份到 Library/SceneBackup_before_Level1Rework/\n\n" +
            "要继续吗？",
            "重做", "取消");
        if (!go) return;

        BatchRebuildLevel1();
        EditorUtility.DisplayDialog("重做完成",
            "Level1 已重建。\n\n打开 Assets/_Project/Scenes/Level1.unity 按 ▶ 就能试。\n" +
            "细节看 Console。", "好");
    }

    /// <summary>
    /// 命令行入口：重做 Level1 的关卡内容（会先备份场景）。
    /// Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelDataWindow.BatchRebuildLevel1
    /// </summary>
    public static void BatchRebuildLevel1()
    {
        EnsureDir(LevelsDir);

        // 0) 先备份：重建会删掉旧的关卡对象，出问题要能退回去
        string scenePath = SceneDir + "/Level1.unity";
        if (File.Exists(scenePath))
        {
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            string root = parent != null ? parent.FullName : Application.dataPath;
            string backupDir = Path.Combine(root, "Library", "SceneBackup_before_Level1Rework");
            Directory.CreateDirectory(backupDir);
            File.Copy(scenePath, Path.Combine(backupDir, "Level1.unity"), true);
            Debug.Log("[Level1 重做] 场景已备份到 " + backupDir);
        }

        // 1) 写数据（真相源）
        LevelData data = BuildLevel1Data();
        List<string> problems = data.Validate();
        foreach (string s in problems) Debug.LogWarning("[Level1 重做] 数据校验：" + s);
        WriteJson("Level1", data);
        Report("Level1", new ExportResult { data = data });

        // 2) 打开并重建（场景模板不动：玩家/史莱姆/相机/UI/管理器都保留）
        if (!File.Exists(scenePath)) { Debug.LogError("[Level1 重做] 找不到场景：" + scenePath); return; }
        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        Scene scene = EditorSceneManager.GetActiveScene();

        if (!RebuildScene(scene, out string msg)) { Debug.LogError("[Level1 重做] 重建失败：" + msg); return; }

        LevelResult lr = FindInScene<LevelResult>(scene);
        if (lr != null) lr.nextSceneName = "Level2";

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Level1 重做] 完成。\n" + msg);
    }

    /// <summary>
    /// 命令行入口：生成教程关数据 → 用 Level1 的场景模板复制出 Level0.unity → 重建 → 注册 Build Settings。
    /// Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelDataWindow.BatchCreateTutorial
    /// </summary>
    public static void BatchCreateTutorial()
    {
        EnsureDir(LevelsDir);

        // 1) 写数据（真相源）
        LevelData data = BuildTutorialData();
        List<string> problems = data.Validate();
        foreach (string s in problems) Debug.LogWarning("[教程关] 数据校验：" + s);
        WriteJson("Level0", data);
        Report("Level0", new ExportResult { data = data });

        // 2) 复制场景模板（玩家 / 史莱姆 / 相机 / Canvas / 各 Manager 都在模板里）
        string src = SceneDir + "/Level1.unity";
        string dst = SceneDir + "/Level0.unity";
        if (!File.Exists(src)) { Debug.LogError("[教程关] 找不到场景模板：" + src); return; }
        AssetDatabase.DeleteAsset(dst);
        if (!AssetDatabase.CopyAsset(src, dst)) { Debug.LogError("[教程关] 复制场景失败"); return; }
        AssetDatabase.Refresh();

        // 3) 打开并重建
        EditorSceneManager.OpenScene(dst, OpenSceneMode.Single);
        Scene scene = EditorSceneManager.GetActiveScene();
        if (!RebuildScene(scene, out string msg)) { Debug.LogError("[教程关] 重建失败：" + msg); return; }

        // 结算面板的"下一关"指向 Level1（模板是从 Level1 抄来的，它自己指向 Level2）
        LevelResult lr = FindInScene<LevelResult>(scene);
        if (lr != null) lr.nextSceneName = "Level1";

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[教程关] 已生成 " + dst + "\n" + msg);

        // 4) Build Settings：MainMenu → LevelSelect → Level0 → Level1 → Level2
        string[] order = { "MainMenu", "LevelSelect", "Level0", "Level1", "Level2" };
        List<EditorBuildSettingsScene> list = new List<EditorBuildSettingsScene>();
        foreach (string n in order)
        {
            string p = SceneDir + "/" + n + ".unity";
            if (File.Exists(p)) list.Add(new EditorBuildSettingsScene(p, true));
            else Debug.LogWarning("[教程关] Build Settings 跳过不存在的场景：" + p);
        }
        EditorBuildSettings.scenes = list.ToArray();

        AssetDatabase.Refresh();
        Debug.Log("[教程关] 完成：数据 " + LevelsDir + "/Level0.json，场景 " + dst +
                  "，Build Settings 已按 MainMenu → LevelSelect → Level0 → Level1 → Level2 排好。");
    }

    static TMP_FontAsset FindMuralFont()
    {
        string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
        TMP_FontAsset fallback = null;
        for (int i = 0; i < guids.Length; i++)
        {
            string p = AssetDatabase.GUIDToAssetPath(guids[i]);
            TMP_FontAsset f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(p);
            if (f == null) continue;
            if (p.IndexOf("SimHei", StringComparison.OrdinalIgnoreCase) >= 0) return f;
            if (fallback == null) fallback = f;
        }
        return fallback;
    }

    /// <summary>
    /// 无头冒烟测试：把关卡编辑器「读取 → 保存」这条链跑一遍，并验证【读进来再写出去不会丢数据】。
    ///
    /// 为什么专门测这个：面板编辑的是内存里的 LevelData，按保存就整份覆盖 JSON。
    /// 如果 JsonUtility 往返会丢字段，玩家（和设计师）的修改就会被无声抹掉 ——
    /// 和之前踩过的"导出器漏读字段"是同一类风险。
    ///
    /// GUI 本身画不画得出来只能在编辑器里看，这里测的是它背后的数据管线。
    /// </summary>
    public static void BatchPanelSmokeTest()
    {
        int ok = 0, bad = 0;

        foreach (string name in LevelSceneNames)
        {
            string json = LevelsDir + "/" + name + ".json";
            if (!File.Exists(json)) { Debug.LogWarning("[面板冒烟] 缺少 " + json + "，跳过"); continue; }

            LevelData d = LevelData.FromJson(File.ReadAllText(json, Encoding.UTF8));
            if (d == null) { Debug.LogError("[面板冒烟] " + name + " JSON 解析失败"); bad++; continue; }

            List<string> problems = d.Validate();
            List<string> before = d.CanonicalLines();

            // 模拟按「保存 JSON」
            File.WriteAllText(json, d.ToJson(), new UTF8Encoding(false));
            LevelData d2 = LevelData.FromJson(File.ReadAllText(json, Encoding.UTF8));
            List<string> after = d2.CanonicalLines();

            List<string> lost = new List<string>();
            int max = Mathf.Max(before.Count, after.Count);
            for (int i = 0; i < max && lost.Count < 8; i++)
            {
                string a = i < before.Count ? before[i] : "(写出去之前没有)";
                string b = i < after.Count ? after[i] : "(读回来之后没有)";
                if (a != b) lost.Add("    前：" + a + "\n    后：" + b);
            }

            if (lost.Count == 0)
            {
                Debug.Log(string.Format("[面板冒烟] {0} ✅ 读取→保存 往返无损；地形 {1} / 物件 {2} / 路牌 {3}；静态校验 {4} 条问题",
                    name, d.terrain.Length, d.objects.Length, d.murals.Length, problems.Count));
                ok++;
            }
            else
            {
                Debug.LogError("[面板冒烟] " + name + " ❌ 保存会丢数据：\n" + string.Join("\n", lost.ToArray()));
                bad++;
            }
        }

        AssetDatabase.Refresh();
        Debug.Log("[面板冒烟] 结束：通过 " + ok + " / 失败 " + bad);
    }

    // ======================= 小工具 =======================

    static void WriteJson(string name, LevelData data)
    {
        EnsureDir(LevelsDir);
        WriteJsonTo(LevelsDir + "/" + name + ".json", data);
    }

    static void WriteJsonTo(string path, LevelData data)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, data.ToJson(), new UTF8Encoding(false));
        Debug.Log("[关卡数据] 已写出 " + path + "（" + new FileInfo(path).Length + " 字节）");
    }

    /// <summary>工程 Library 目录：不参与资源导入，用于放临时产物。</summary>
    static string LibraryDir()
    {
        DirectoryInfo parent = Directory.GetParent(Application.dataPath);
        string root = parent != null ? parent.FullName : Application.dataPath;
        string dir = Path.Combine(root, "Library", "LevelRoundTrip");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    static void EnsureDir(string assetDir)
    {
        if (Directory.Exists(assetDir)) return;
        Directory.CreateDirectory(assetDir);
        AssetDatabase.Refresh();
    }

    static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T[] found = root.GetComponentsInChildren<T>(true);
            if (found != null && found.Length > 0) return found[0];
        }
        return null;
    }

    static bool TryWorldBounds(GameObject go, out Bounds b)
    {
        Collider2D col = go.GetComponent<Collider2D>();
        if (col != null) { b = col.bounds; return true; }
        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        if (sr != null) { b = sr.bounds; return true; }
        b = new Bounds(go.transform.position, Vector3.zero);
        return false;
    }
}

// ---------------------------------------------------------------------------
// LevelDataEditorWindow —— P1 关卡数据编辑面板
//
// 设计原则（为了"不把已经做好的东西改坏"）：
//   1. 纯新增：运行时脚本零改动，只多一个编辑器窗口
//   2. 内存里改，不碰场景 —— 除非你按「生成到当前场景」
//   3. 只有你按按钮才会写文件 / 动场景，没有任何自动保存
//   4. 生成走的还是跑通了三关往返校验的 RebuildScene，没有第二套生成逻辑
// ---------------------------------------------------------------------------
public class LevelDataEditorWindow : EditorWindow
{
    const float LabelW = 320f;
    const float NumW = 96f;

    int _levelPicker;
    LevelData _data;
    string _loadedName = "";
    bool _dirty;
    Vector2 _scroll;
    string _status = "";

    static readonly string[] TerrainKindNames = Enum.GetNames(typeof(TerrainKind));
    static readonly string[] ObjectKindNames = Enum.GetNames(typeof(LevelObjectKind));

    [MenuItem("Tools/呆呆史莱姆/◇ 关卡数据编辑器（表格改数值，不碰文本）", false, 89)]
    public static void Open()
    {
        LevelDataEditorWindow w = GetWindow<LevelDataEditorWindow>("关卡数据编辑器");
        w.minSize = new Vector2(880f, 600f);
        w.Show();
    }

    void OnGUI()
    {
        DrawToolbar();
        if (_data == null)
        {
            EditorGUILayout.HelpBox("选一个关卡 → 点「读取」。\n\n" +
                "所有修改都只存在这个窗口里，不会动场景也不会写文件；\n" +
                "要生效就按「生成到当前场景」，要存档就按「保存 JSON」。", MessageType.Info);
            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        DrawMeta();
        DrawSummary();
        DrawTerrain();
        DrawObjects();
        DrawMurals();
        EditorGUILayout.Space(8f);
        EditorGUILayout.EndScrollView();
    }

    // ======================= 顶部工具条 =======================

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        _levelPicker = EditorGUILayout.Popup(_levelPicker, LevelDataWindow.LevelSceneNames,
                                             EditorStyles.toolbarPopup, GUILayout.Width(110f));
        if (GUILayout.Button("读取", EditorStyles.toolbarButton, GUILayout.Width(60f))) LoadJson();
        if (GUILayout.Button("保存 JSON", EditorStyles.toolbarButton, GUILayout.Width(90f))) SaveJson();
        if (GUILayout.Button("生成到当前场景", EditorStyles.toolbarButton, GUILayout.Width(130f))) RebuildIntoScene();
        if (GUILayout.Button("从当前场景导出", EditorStyles.toolbarButton, GUILayout.Width(130f))) ExportFromScene();
        GUILayout.FlexibleSpace();
        GUILayout.Label(_dirty ? "● 有未保存的修改" : "已同步", EditorStyles.miniLabel, GUILayout.Width(120f));
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_status))
        {
            EditorGUILayout.HelpBox(_status, MessageType.None);
        }
        if (_dirty && !string.IsNullOrEmpty(_loadedName) &&
            _loadedName != LevelDataWindow.LevelSceneNames[_levelPicker])
        {
            EditorGUILayout.HelpBox("你正在编辑「" + _loadedName + "」，但下拉选的是「" +
                LevelDataWindow.LevelSceneNames[_levelPicker] + "」。先保存或重新读取。", MessageType.Warning);
        }
    }

    string CurrentName { get { return LevelDataWindow.LevelSceneNames[_levelPicker]; } }
    string CurrentJsonPath { get { return LevelDataWindow.LevelsDir + "/" + CurrentName + ".json"; } }

    void LoadJson()
    {
        string path = CurrentJsonPath;
        if (!File.Exists(path)) { _status = "找不到 " + path; return; }

        LevelData d = LevelData.FromJson(File.ReadAllText(path, Encoding.UTF8));
        if (d == null) { _status = "JSON 解析失败：" + path; return; }

        _data = d;
        _loadedName = CurrentName;
        _dirty = false;

        List<string> problems = _data.Validate();
        _status = problems.Count == 0
            ? "已读取 " + path + "（静态校验通过）"
            : "已读取 " + path + "，但有 " + problems.Count + " 条问题：" + string.Join("；", problems.ToArray());
    }

    void SaveJson()
    {
        if (_data == null) return;
        _data.EnsureNoNulls();
        Directory.CreateDirectory(LevelDataWindow.LevelsDir);
        File.WriteAllText(CurrentJsonPath, _data.ToJson(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        _loadedName = CurrentName;
        _dirty = false;
        _status = "已保存 " + CurrentJsonPath;
    }

    void RebuildIntoScene()
    {
        if (_data == null) return;

        string want = CurrentName;
        Scene active = EditorSceneManager.GetActiveScene();

        if (active.name != want)
        {
            bool go = EditorUtility.DisplayDialog("生成到哪个场景？",
                "现在打开的是「" + active.name + "」，而要生成的是「" + want + "」。\n\n" +
                "要不要先打开 " + want + " 再生成？",
                "打开并生成", "取消");
            if (!go) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string scenePath = LevelDataWindow.SceneDir + "/" + want + ".unity";
            if (!File.Exists(scenePath)) { _status = "场景不存在：" + scenePath; return; }
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            active = EditorSceneManager.GetActiveScene();
        }

        SaveJson();   // 先把内存里的改动落盘，再让 RebuildScene 从 JSON 建 —— 保持"JSON 是唯一真相源"

        if (!LevelDataWindow.RebuildScene(active, out string msg))
        {
            _status = "生成失败：" + msg;
            EditorUtility.DisplayDialog("生成失败", msg, "好");
            return;
        }

        EditorSceneManager.MarkSceneDirty(active);
        EditorSceneManager.SaveScene(active);
        _status = "已生成到 " + active.name + "，场景已保存。\n" + msg;
        Debug.Log("[关卡编辑器] " + _status);
    }

    void ExportFromScene()
    {
        Scene active = EditorSceneManager.GetActiveScene();
        LevelDataWindow.ExportResult r = LevelDataWindow.ExportScene(active);
        if (r == null) { _status = "导出失败"; return; }

        if (_dirty && !EditorUtility.DisplayDialog("覆盖未保存的修改？",
                "窗口里有未保存的修改，从场景导出会覆盖它。继续？", "覆盖", "取消"))
            return;

        _data = r.data;
        _loadedName = CurrentName;
        _dirty = true;     // 导出结果只在内存里，等用户确认后再保存
        _status = "已从场景「" + active.name + "」导出到窗口（还没写文件，确认无误后按「保存 JSON」）。";
    }

    // ======================= 关卡参数 =======================

    void DrawMeta()
    {
        EditorGUILayout.LabelField("关卡参数", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        EditorGUI.BeginChangeCheck();

        LevelMeta m = _data.meta;
        m.levelId = RowText("关卡 ID", m.levelId);
        m.parTime = RowF("标准时间 parTime（秒）—— 取 10 的倍数换算才是整数", m.parTime);
        m.barMaxOverride = RowI("售价条满格覆盖（0 = 自动 = 10 × parTime）", m.barMaxOverride);
        m.startStones = RowI("开局引导石颗数", m.startStones);
        m.coinValue = RowI("默认金币面值", m.coinValue);
        m.levelIndex = RowI("关卡编号 levelIndex（教程关 = 0）", m.levelIndex);
        m.saveProgress = RowBool("记录成绩 / 计星级（教程关取消勾选）", m.saveProgress);

        EditorGUILayout.Space(2f);
        m.hasSpawn = RowBool("写出生存点", m.hasSpawn);
        if (m.hasSpawn)
        {
            m.playerX = RowF("玩家出生 x", m.playerX);
            m.playerY = RowF("玩家出生 y", m.playerY);
            m.slimeX = RowF("史莱姆出生 x", m.slimeX);
            m.slimeY = RowF("史莱姆出生 y", m.slimeY);
        }

        EditorGUILayout.Space(2f);
        m.useCameraBounds = RowBool("相机限边界", m.useCameraBounds);
        m.cameraSize = RowF("相机 orthographicSize", m.cameraSize);
        if (m.useCameraBounds)
        {
            m.camMinX = RowF("相机边界 min x", m.camMinX);
            m.camMinY = RowF("相机边界 min y", m.camMinY);
            m.camMaxX = RowF("相机边界 max x", m.camMaxX);
            m.camMaxY = RowF("相机边界 max y", m.camMaxY);
        }

        if (EditorGUI.EndChangeCheck()) _dirty = true;
        EditorGUI.indentLevel--;
        EditorGUILayout.Space(6f);
    }

    // ======================= 数值汇总 =======================

    void DrawSummary()
    {
        LevelMeta m = _data.meta;
        int coinTotal = 0;
        for (int i = 0; i < _data.objects.Length; i++)
        {
            LevelObject o = _data.objects[i];
            if (o != null && o.kind == LevelObjectKind.Coin)
                coinTotal += Mathf.RoundToInt(o.Param(0, m.coinValue));
        }

        int barMax = m.barMaxOverride > 0 ? m.barMaxOverride : LevelManager.BarMaxFor(m.parTime);
        float drain = LevelManager.DrainRateFor(m.parTime);
        int coinTarget = Mathf.RoundToInt(2f * m.parTime);
        int baseline = Mathf.RoundToInt(barMax * LevelManager.parRemain) + coinTotal;
        bool coinOk = Mathf.Abs(coinTotal - coinTarget) <= coinTarget * 0.25f;

        EditorGUILayout.LabelField("数值汇总（与运行时同一套公式）", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(string.Format(
            "地形 {0} ｜ 物件 {1} ｜ 路牌 {2}", _data.terrain.Length, _data.objects.Length, _data.murals.Length));
        EditorGUILayout.LabelField(string.Format(
            "售价条满格 {0} 元 ｜ 掉血 {1:0.###} 血/秒 ｜ 1 点血 = {2:0.##} 元",
            barMax, drain, LevelManager.BarMaxFor(m.parTime) / 100f));
        EditorGUILayout.LabelField(string.Format(
            "金币总额 {0} 元 ｜ 目标 ≈ {1} 元（= 2 × parTime）  {2}",
            coinTotal, coinTarget, coinOk ? "✅" : "⚠ 偏离目标"));
        EditorGUILayout.LabelField(string.Format(
            "星级基准分 {0}  →  ⭐ {1} ｜ ⭐⭐ {2} ｜ ⭐⭐⭐ {3}",
            baseline,
            Mathf.RoundToInt(baseline * 0.6f),
            Mathf.RoundToInt(baseline * 0.9f),
            Mathf.RoundToInt(baseline * 1.1f)));
        EditorGUI.indentLevel--;
        EditorGUILayout.Space(6f);
    }

    // ======================= 地形表 =======================

    void DrawTerrain()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("地形（" + _data.terrain.Length + "）—— 坐标是世界单位，左下角 + 尺寸",
                                   EditorStyles.boldLabel);
        if (GUILayout.Button("+ 新增地形", GUILayout.Width(90f)))
        {
            List<TerrainBlock> list = new List<TerrainBlock>(_data.terrain);
            list.Add(new TerrainBlock("NewGround", 0, 0, 16, 8, TerrainKind.Ground));
            _data.terrain = list.ToArray();
            _dirty = true;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("名字", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
        GUILayout.Label("左 x", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("下 y", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("宽", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("高", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("类型", EditorStyles.miniBoldLabel, GUILayout.Width(130f));
        GUILayout.Label("参数（水/伤害）", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        int removeAt = -1;
        for (int i = 0; i < _data.terrain.Length; i++)
        {
            TerrainBlock t = _data.terrain[i];
            if (t == null) continue;

            EditorGUILayout.BeginHorizontal();
            t.name = EditorGUILayout.TextField(t.name, GUILayout.Width(120f));

            float minX = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(t.qx), GUILayout.Width(NumW));
            float minY = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(t.qy), GUILayout.Width(NumW));
            float w = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(t.qw), GUILayout.Width(NumW));
            float h = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(t.qh), GUILayout.Width(NumW));
            t.qx = LevelUnits.ToQuarter(minX);
            t.qy = LevelUnits.ToQuarter(minY);
            t.qw = Mathf.Max(1, LevelUnits.ToQuarter(w));
            t.qh = Mathf.Max(1, LevelUnits.ToQuarter(h));

            t.kind = (TerrainKind)EditorGUILayout.Popup((int)t.kind, TerrainKindNames, GUILayout.Width(130f));
            t.p = ParamsField(t.p, GUILayout.Width(120f));

            if (GUILayout.Button("×", GUILayout.Width(24f))) removeAt = i;
            EditorGUILayout.EndHorizontal();
        }
        if (EditorGUI.EndChangeCheck()) _dirty = true;

        if (removeAt >= 0)
        {
            List<TerrainBlock> list = new List<TerrainBlock>(_data.terrain);
            list.RemoveAt(removeAt);
            _data.terrain = list.ToArray();
            _dirty = true;
        }
        EditorGUI.indentLevel--;
        EditorGUILayout.Space(6f);
    }

    // ======================= 物件表 =======================

    void DrawObjects()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("物件（" + _data.objects.Length + "）—— 坐标是世界单位的中心点",
                                   EditorStyles.boldLabel);
        if (GUILayout.Button("+ 新增金币", GUILayout.Width(100f)))
        {
            List<LevelObject> list = new List<LevelObject>(_data.objects);
            LevelObject o = new LevelObject();
            o.id = "coin_" + list.Count;
            o.name = "NewCoin";
            o.kind = LevelObjectKind.Coin;
            o.sizeX = 0.6f; o.sizeY = 0.6f;
            o.p = new float[] { _data.meta.coinValue };
            list.Add(o);
            _data.objects = list.ToArray();
            _dirty = true;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("id", EditorStyles.miniBoldLabel, GUILayout.Width(80f));
        GUILayout.Label("名字", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
        GUILayout.Label("种类", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
        GUILayout.Label("中心 x", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("中心 y", EditorStyles.miniBoldLabel, GUILayout.Width(NumW));
        GUILayout.Label("宽", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
        GUILayout.Label("高", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
        GUILayout.Label("参数（面值/伤害…）", EditorStyles.miniBoldLabel, GUILayout.Width(140f));
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        int removeAt = -1;
        for (int i = 0; i < _data.objects.Length; i++)
        {
            LevelObject o = _data.objects[i];
            if (o == null) continue;

            EditorGUILayout.BeginHorizontal();
            o.id = EditorGUILayout.TextField(o.id, GUILayout.Width(80f));
            o.name = EditorGUILayout.TextField(o.name, GUILayout.Width(90f));
            o.kind = (LevelObjectKind)EditorGUILayout.Popup((int)o.kind, ObjectKindNames, GUILayout.Width(120f));

            float x = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(o.qx), GUILayout.Width(NumW));
            float y = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(o.qy), GUILayout.Width(NumW));
            o.qx = LevelUnits.ToQuarter(x);
            o.qy = LevelUnits.ToQuarter(y);

            o.sizeX = EditorGUILayout.DelayedFloatField(o.sizeX, GUILayout.Width(70f));
            o.sizeY = EditorGUILayout.DelayedFloatField(o.sizeY, GUILayout.Width(70f));
            o.p = ParamsField(o.p, GUILayout.Width(140f));

            if (GUILayout.Button("×", GUILayout.Width(24f))) removeAt = i;
            EditorGUILayout.EndHorizontal();
        }
        if (EditorGUI.EndChangeCheck()) _dirty = true;

        if (removeAt >= 0)
        {
            List<LevelObject> list = new List<LevelObject>(_data.objects);
            list.RemoveAt(removeAt);
            _data.objects = list.ToArray();
            _dirty = true;
        }
        EditorGUI.indentLevel--;
        EditorGUILayout.Space(6f);
    }

    // ======================= 路牌表 =======================

    void DrawMurals()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("壁画路牌（" + _data.murals.Length + "）—— 占位阶段牌面上写的就是「标签」这列",
                                   EditorStyles.boldLabel);
        if (GUILayout.Button("+ 新增路牌", GUILayout.Width(90f)))
        {
            List<Mural> list = new List<Mural>(_data.murals);
            list.Add(new Mural("H?", "标签", 0, 0, 3.2f, 1.6f));
            _data.murals = list.ToArray();
            _dirty = true;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel++;
        EditorGUI.BeginChangeCheck();
        int removeAt = -1;
        for (int i = 0; i < _data.murals.Length; i++)
        {
            Mural mu = _data.murals[i];
            if (mu == null) continue;

            EditorGUILayout.BeginHorizontal();
            mu.muralId = EditorGUILayout.TextField(mu.muralId, GUILayout.Width(50f));
            mu.label = EditorGUILayout.TextField(mu.label, GUILayout.Width(120f));

            float x = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(mu.qx), GUILayout.Width(NumW));
            float y = EditorGUILayout.DelayedFloatField(LevelUnits.ToWorld(mu.qy), GUILayout.Width(NumW));
            mu.qx = LevelUnits.ToQuarter(x);
            mu.qy = LevelUnits.ToQuarter(y);

            mu.sizeX = EditorGUILayout.DelayedFloatField(mu.sizeX, GUILayout.Width(70f));
            mu.sizeY = EditorGUILayout.DelayedFloatField(mu.sizeY, GUILayout.Width(70f));

            if (GUILayout.Button("×", GUILayout.Width(24f))) removeAt = i;
            EditorGUILayout.EndHorizontal();
        }
        if (EditorGUI.EndChangeCheck()) _dirty = true;

        if (removeAt >= 0)
        {
            List<Mural> list = new List<Mural>(_data.murals);
            list.RemoveAt(removeAt);
            _data.murals = list.ToArray();
            _dirty = true;
        }
        EditorGUI.indentLevel--;
    }

    // ======================= 小工具 =======================

    static string RowText(string label, string v)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(LabelW));
        v = EditorGUILayout.TextField(v);
        EditorGUILayout.EndHorizontal();
        return v;
    }

    static float RowF(string label, float v)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(LabelW));
        v = EditorGUILayout.DelayedFloatField(v, GUILayout.Width(NumW));
        EditorGUILayout.EndHorizontal();
        return v;
    }

    static int RowI(string label, int v)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(LabelW));
        v = EditorGUILayout.DelayedIntField(v, GUILayout.Width(NumW));
        EditorGUILayout.EndHorizontal();
        return v;
    }

    static bool RowBool(string label, bool v)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(LabelW));
        v = EditorGUILayout.Toggle(v, GUILayout.Width(20f));
        EditorGUILayout.EndHorizontal();
        return v;
    }

    /// <summary>参数用逗号分隔的文本框编辑：不同种类的参数个数不一样，这样最省事也最透明。</summary>
    static float[] ParamsField(float[] p, params GUILayoutOption[] options)
    {
        string s = JoinFloats(p);
        string ns = EditorGUILayout.DelayedTextField(s, options);
        return ns == s ? p : ParseFloats(ns);
    }

    static string JoinFloats(float[] a)
    {
        if (a == null || a.Length == 0) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(a[i].ToString("0.###"));
        }
        return sb.ToString();
    }

    static float[] ParseFloats(string s)
    {
        if (string.IsNullOrEmpty(s)) return new float[0];
        string[] parts = s.Split(new char[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        List<float> list = new List<float>();
        for (int i = 0; i < parts.Length; i++)
        {
            float v;
            if (float.TryParse(parts[i], out v)) list.Add(v);
        }
        return list.ToArray();
    }
}
