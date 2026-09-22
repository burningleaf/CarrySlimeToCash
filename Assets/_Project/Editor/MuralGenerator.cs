// =====================================================================================
//  MuralGenerator.cs —— 《带呆呆史莱姆越障换钱》象形图路牌生成器（流5：美术与音频）
//
//  干什么：
//    程序化画出教学关 Level0 路牌上的 15 张"象形图"（64×64 PNG，透明底 + 近白图案），
//    并顺手把每张图的 TextureImporter 设好
//    （Sprite / Single / Point / 无 Mipmap / 无压缩 / Clamp / PPU 32 / FullRect / Center）。
//
//  为什么：
//    LevelBuilder.BuildMural 现在挂的是占位文字（showMuralLabels = true）。
//    这批图出来之后，把 sprite 接进 LevelData 的 murals[].sprIndex 就能关掉文字。
//    ⚠ 接线不在这一个文件里做 —— 那是另一轮的事，本文件只产图。
//
//  怎么用：
//    · 菜单：Tools/呆呆史莱姆/▣ 生成象形图路牌（15 张 Mural PNG）
//    · 命令行：Unity.exe -batchmode -quit -projectPath <项目路径>
//              -executeMethod MuralGenerator.BatchGenerateMurals
//
//  画风约定：
//    · 图案是"单色近白"，进游戏由 SpriteRenderer.color 染色（路牌底板是深色 0.16/0.14/0.25，正好反衬）
//    · 圆形按键徽章 = 实心浅色圆 + 用透明"抠出"字母（字母透出底板颜色，不是画上去的）
//    · 线宽最小 3px（64 的画布上再细就看不清了）
//    · 像素 (ix, iy) 就代表坐标点 (ix, iy)，iy 向上，画布左下角 = (0,0)
//      （Texture2D.SetPixels32 的 index 0 = 左下角，所以本文件的 y 方向 = PNG 里的上方）
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MuralGenerator
{
    // =================================================================================
    //  一、可调数值（工程约定：数值放 public static 字段）
    // =================================================================================

    /// <summary>输出目录（Unity 相对路径）。</summary>
    public static string muralOutputDir = "Assets/_Project/Art/UI/Murals";

    /// <summary>画布边长（正方形）。</summary>
    public static int muralSize = 64;

    /// <summary>导入用的 Pixels Per Unit。</summary>
    public static float muralPixelsPerUnit = 32f;

    /// <summary>基础线宽（px）。规范要求 ≥3。</summary>
    public static int muralLineWidth = 3;

    /// <summary>圆形按键徽章半径（px）。</summary>
    public static float muralBadgeRadius = 11f;

    /// <summary>徽章里字母的像素格大小（5×7 点阵 × 2 = 10×14，塞得进半径 11 的圆）。</summary>
    public static int muralBadgeGlyphPixel = 2;

    /// <summary>图案颜色：接近纯白（进游戏再染色）。</summary>
    public static Color32 muralInkColor = new Color32(242, 245, 252, 255);

    /// <summary>完全透明（用来"抠"形状）。</summary>
    public static Color32 muralClearColor = new Color32(0, 0, 0, 0);

    /// <summary>自检下限：不透明像素占比低于它 = 等于没画出来。</summary>
    public static float muralMinCoverage = 0.05f;

    /// <summary>自检上限：不透明像素占比高于它 = 糊成一片了。</summary>
    public static float muralMaxCoverage = 0.85f;

    // =================================================================================
    //  二、入口
    // =================================================================================

    [MenuItem("Tools/呆呆史莱姆/▣ 生成象形图路牌（15 张 Mural PNG）", false, 82)]
    public static void GenerateMuralsMenu()
    {
        MuralGenerateAll();
    }

    /// <summary>命令行入口（无参数、无返回值）。</summary>
    public static void BatchGenerateMurals()
    {
        MuralGenerateAll();
    }

    // =================================================================================
    //  三、主流程：画 → 写盘 → 导入设置 → 自检
    // =================================================================================

    static void MuralGenerateAll()
    {
        List<MuralDef> defs = MuralBuildDefs();
        if (defs.Count == 0)
        {
            Debug.LogWarning("[MuralGenerator] 图定义为空，什么都没生成。");
            return;
        }

        string absDir = MuralToAbsolutePath(muralOutputDir);
        if (!Directory.Exists(absDir)) Directory.CreateDirectory(absDir);

        List<string> lines = new List<string>();
        int okCount = 0;
        int warnCount = 0;
        int failCount = 0;
        float minSeen = 1f;
        float maxSeen = 0f;

        for (int i = 0; i < defs.Count; i++)
        {
            MuralDef def = defs[i];
            string fileName = "Mural_" + def.id + ".png";
            string unityPath = muralOutputDir + "/" + fileName;
            string absFile = Path.Combine(absDir, fileName);

            try
            {
                // ---- 画 ----
                MuralCanvas canvas = new MuralCanvas(muralSize, muralSize);
                def.paint(canvas);
                float coverage = canvas.OpaqueRatio();
                if (coverage < minSeen) minSeen = coverage;
                if (coverage > maxSeen) maxSeen = coverage;

                // ---- 写 PNG ----
                Texture2D tex = canvas.ToTexture();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                if (png == null || png.Length == 0) throw new Exception("EncodeToPNG 返回了空数据");
                File.WriteAllBytes(absFile, png);

                // ---- 导入设置 ----
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);
                MuralApplyImportSettings(unityPath);

                okCount++;

                // ---- 自检 ----
                bool tooFew = coverage < muralMinCoverage;
                bool tooMany = coverage > muralMaxCoverage;
                string flag = "";
                if (tooFew || tooMany)
                {
                    warnCount++;
                    flag = tooFew ? "  ⚠ 太少" : "  ⚠ 太多";
                    Debug.LogWarning(string.Format(
                        "[MuralGenerator] 不透明像素占比异常：{0} = {1:F1}%（{2}）",
                        fileName, coverage * 100f,
                        tooFew
                            ? "少于 5% —— 说明等于没画出来，检查画法"
                            : "多于 85% —— 说明糊成一片了，检查画法"));
                }

                lines.Add(string.Format("  {0,-16} {1}×{1}  不透明 {2,5:F1}%{3}   {4}",
                    fileName, muralSize, coverage * 100f, flag, def.note));
            }
            catch (Exception ex)
            {
                failCount++;
                Debug.LogError("[MuralGenerator] 生成失败：" + unityPath + " → " + ex);
            }
        }

        // ---- 汇总表 ----
        Debug.Log("[MuralGenerator] ================= 象形图路牌汇总（" + okCount + "/" + defs.Count + " 张成功） =================");
        for (int i = 0; i < lines.Count; i++) Debug.Log("[MuralGenerator] " + lines[i]);
        Debug.Log(string.Format(
            "[MuralGenerator] 成功 {0} / 占比告警 {1} / 失败 {2}；占比区间 {3:F1}%~{4:F1}%（自检区间 {5:F0}%~{6:F0}%）",
            okCount, warnCount, failCount, minSeen * 100f, maxSeen * 100f,
            muralMinCoverage * 100f, muralMaxCoverage * 100f));
        Debug.Log(string.Format(
            "[MuralGenerator] 输出目录：{0}（{1}）；导入设置：Sprite/Single/Point/无Mipmap/无压缩/Clamp/PPU {2}/FullRect/Center",
            muralOutputDir, absDir, muralPixelsPerUnit));
    }

    /// <summary>统一导入设置。顺序有讲究：必须先 ImportAsset 才能取到 importer；
    /// pivot/对齐必须走 ReadTextureSettings / SetTextureSettings（直接赋 spritePivot 会被 spriteAlignment 冲掉）。</summary>
    static void MuralApplyImportSettings(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            Debug.LogWarning("[MuralGenerator] 取不到 TextureImporter：" + path + "（图写出来了，但导入设置没设上，重跑一次即可）");
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = true;
        importer.spritePixelsPerUnit = muralPixelsPerUnit;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = 2048;

        TextureImporterSettings s = new TextureImporterSettings();
        importer.ReadTextureSettings(s);
        s.textureType = TextureImporterType.Sprite;
        s.spriteMode = (int)SpriteImportMode.Single;
        s.filterMode = FilterMode.Point;
        s.mipmapEnabled = false;
        s.wrapMode = TextureWrapMode.Clamp;
        s.wrapModeU = TextureWrapMode.Clamp;
        s.wrapModeV = TextureWrapMode.Clamp;
        s.alphaIsTransparency = true;
        s.alphaSource = TextureImporterAlphaSource.FromInput;
        s.spritePixelsPerUnit = muralPixelsPerUnit;
        s.spriteMeshType = SpriteMeshType.FullRect;   // 路牌会被拉伸，FullRect 比 Tight 稳
        s.npotScale = TextureImporterNPOTScale.None;
        s.sRGBTexture = true;
        s.spriteAlignment = (int)SpriteAlignment.Center;
        importer.SetTextureSettings(s);

        EditorUtility.SetDirty(importer);
        importer.SaveAndReimport();
    }

    /// <summary>"Assets/xxx/yyy" → "&lt;项目&gt;/Assets/xxx/yyy"（绝对路径）。</summary>
    static string MuralToAbsolutePath(string unityPath)
    {
        string p = (unityPath ?? "").Replace('\\', '/').TrimEnd('/');
        if (p == "Assets") return Application.dataPath;
        if (p.StartsWith("Assets/", StringComparison.Ordinal))
        {
            string sub = p.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(Application.dataPath, sub);
        }
        return p.Replace('/', Path.DirectorySeparatorChar);
    }

    // =================================================================================
    //  四、15 张图的清单
    // =================================================================================

    /// <summary>一张象形图的定义。</summary>
    public class MuralDef
    {
        public string id;
        public string note;
        public Action<MuralCanvas> paint;
    }

    static void MuralAdd(List<MuralDef> list, string id, string note, Action<MuralCanvas> paint)
    {
        MuralDef d = new MuralDef();
        d.id = id;
        d.note = note;
        d.paint = paint;
        list.Add(d);
    }

    static List<MuralDef> MuralBuildDefs()
    {
        List<MuralDef> list = new List<MuralDef>();

        MuralAdd(list, "H1", "跟随：史莱姆 + 箭头 → 小孩", PaintFollow);
        MuralAdd(list, "H2", "举起：小孩把史莱姆举过头顶 + E 徽章", PaintCarryOverhead);
        MuralAdd(list, "H3", "水：三条波浪 + 水滴 + 感叹号", PaintWater);
        MuralAdd(list, "H4", "放下：小孩把史莱姆放到地上 + E 徽章", PaintPlaceDown);
        MuralAdd(list, "H5", "跳：向上粗箭头 + 起跳的小孩 + 地面线", PaintJump);
        MuralAdd(list, "H6", "投掷：抛物动作 + 抛物线虚线 + 史莱姆 + Q 徽章", PaintThrow);
        MuralAdd(list, "H7", "待命：史莱姆 + 钉住符号 + ×2（两个小方块）", PaintStay);
        MuralAdd(list, "H8", "召回：史莱姆 + 一串箭头指向小孩 + Q 徽章", PaintRecall);
        MuralAdd(list, "H9", "放引导石：菱形石头 + 虚线路径 + 三个点 + E 徽章", PaintWaypointSet);
        MuralAdd(list, "H10", "撤石：虚线路径被斜杠划掉 + Q 徽章", PaintWaypointClear);
        MuralAdd(list, "H11", "金币：史莱姆 + 向下箭头 → 金币", PaintCoin);
        MuralAdd(list, "H12", "回血：心 + 加号", PaintHeal);
        MuralAdd(list, "H13", "收购站：摊位 + 屋顶 + 金币", PaintShop);
        MuralAdd(list, "R1", "复习·举起（H2 简化版）", PaintCarrySimple);
        MuralAdd(list, "R2", "复习·投掷（H6 简化版）", PaintThrowSimple);

        return list;
    }

    // =================================================================================
    //  五、逐个画（参数都是 64×64 画布上的像素坐标，y 向上）
    // =================================================================================

    /// <summary>H1 跟随：史莱姆 + 一个箭头指向小孩剪影。</summary>
    static void PaintFollow(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Slime(14f, 17f, 10f, k);                                        // 史莱姆（左）
        c.Arrow(27f, 26f, 39f, 26f, muralLineWidth, 8f, k);               // 箭头 → 小孩
        c.Figure(new Vector2(48f, 42f), 5f,                                // 头
                 new Vector2(48f, 36f), new Vector2(48f, 24f),             // 脖子 / 胯
                 new Vector2(43f, 30f), new Vector2(53f, 30f),             // 两只手
                 new Vector2(44f, 16f), new Vector2(52f, 16f),             // 两只脚
                 muralLineWidth, k);
    }

    /// <summary>H2 举起（按 E）：小孩双手把史莱姆举过头顶 + 圆形 E 徽章。
    /// 手臂故意做成两节（肩→肘→手），让肘往外拐 —— 否则手臂会糊在头上，整张图变成一个高脚杯。</summary>
    static void PaintCarryOverhead(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(6, 5, 34, 7, k);                                            // 地面线
        c.Disc(20f, 37f, 5f, k);                                          // 头
        c.Stroke(20f, 31f, 20f, 17f, muralLineWidth, k);                  // 脊柱
        c.Stroke(20f, 17f, 14f, 8f, muralLineWidth, k);                   // 两条腿
        c.Stroke(20f, 17f, 26f, 8f, muralLineWidth, k);
        c.Limb(new Vector2(20f, 32f), new Vector2(11f, 33f), new Vector2(10f, 46f), muralLineWidth, k);
        c.Limb(new Vector2(20f, 32f), new Vector2(29f, 33f), new Vector2(30f, 46f), muralLineWidth, k);
        c.Slime(20f, 47f, 9f, k);                                         // 举在头顶的史莱姆
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
    }

    /// <summary>R1 复习·举起：和 H2 同图，略简（无地面线、史莱姆不带眼睛），功能徽章保留。</summary>
    static void PaintCarrySimple(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Disc(20f, 37f, 5f, k);                                          // 头
        c.Stroke(20f, 31f, 20f, 17f, muralLineWidth, k);                  // 脊柱
        c.Stroke(20f, 17f, 14f, 8f, muralLineWidth, k);                   // 两条腿
        c.Stroke(20f, 17f, 26f, 8f, muralLineWidth, k);
        c.Limb(new Vector2(20f, 32f), new Vector2(11f, 33f), new Vector2(10f, 46f), muralLineWidth, k);
        c.Limb(new Vector2(20f, 32f), new Vector2(29f, 33f), new Vector2(30f, 46f), muralLineWidth, k);
        c.Slime(20f, 47f, 9f, k, false);
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
    }

    /// <summary>H3 水：三条波浪线 + 一个水滴（水滴里抠出感叹号）。</summary>
    static void PaintWater(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Wave(3f, 36f, 12f, 2.4f, 9f, muralLineWidth, k);
        c.Wave(3f, 36f, 22f, 2.4f, 9f, muralLineWidth, k);
        c.Wave(3f, 36f, 32f, 2.4f, 9f, muralLineWidth, k);

        c.Disc(48f, 40f, 9f, k);                                          // 水滴的肚子
        c.Triangle(new Vector2(48f, 59f),                                 // 水滴的尖
                   new Vector2(39.5f, 42f), new Vector2(56.5f, 42f), k);

        c.ClearBox(47, 46, 49, 52);                                       // 感叹号：竖杠
        c.ClearBox(47, 35, 49, 37);                                       // 感叹号：点
    }

    /// <summary>H4 放下（按 E）：小孩把史莱姆放到地上 + 一个向下的小箭头 + 圆形 E 徽章。
    /// 向下的小箭头是必要的：没有它，H4 和 H2 只看姿势容易看混。</summary>
    static void PaintPlaceDown(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(2, 6, 36, 8, k);                                            // 地面
        c.Disc(11f, 36f, 4.5f, k);                                        // 头
        c.Stroke(11f, 31f, 11f, 20f, muralLineWidth, k);                  // 脊柱（站直）
        c.Stroke(11f, 20f, 8f, 9f, muralLineWidth, k);                    // 两条腿
        c.Stroke(11f, 20f, 15f, 9f, muralLineWidth, k);
        c.Limb(new Vector2(11f, 31f), new Vector2(14f, 25f), new Vector2(18f, 19f), muralLineWidth, k);
        c.Limb(new Vector2(11f, 31f), new Vector2(15f, 24f), new Vector2(21f, 20f), muralLineWidth, k);
        c.Arrow(31f, 30f, 31f, 21f, muralLineWidth, 6f, k);               // 向下的小箭头 = 放到地上
        c.Slime(31f, 9f, 8f, k);                                          // 放到地上的史莱姆
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
    }

    /// <summary>H5 跳：向上的粗箭头 + 正在起跳的小孩 + 地面横线。</summary>
    static void PaintJump(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(2, 5, 61, 7, k);                                            // 地面横线
        c.Arrow(14f, 12f, 14f, 50f, 4f, 11f, k);                          // 向上的粗箭头

        c.Disc(40f, 42f, 5.5f, k);                                        // 悬空的小孩：头
        c.Stroke(40f, 36f, 40f, 26f, muralLineWidth, k);                  // 脊柱
        c.Limb(new Vector2(40f, 26f), new Vector2(35f, 23f), new Vector2(34f, 16f), muralLineWidth, k);
        c.Limb(new Vector2(40f, 26f), new Vector2(45f, 24f), new Vector2(46f, 17f), muralLineWidth, k);
        c.Limb(new Vector2(40f, 37f), new Vector2(33f, 40f), new Vector2(31f, 48f), muralLineWidth, k);
        c.Limb(new Vector2(40f, 37f), new Vector2(47f, 40f), new Vector2(49f, 48f), muralLineWidth, k);
    }

    /// <summary>H6 投掷（按 Q）：小孩抛掷 + 抛物线虚线 + 末端一个史莱姆 + 圆形 Q 徽章。</summary>
    static void PaintThrow(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Figure(new Vector2(11f, 34f), 4.5f,                             // 前抛姿势
                 new Vector2(11f, 28f), new Vector2(11f, 16f),
                 new Vector2(4f, 30f), new Vector2(21f, 32f),
                 new Vector2(8f, 8f), new Vector2(15f, 8f),
                 muralLineWidth, k);
        c.DashedCurve(MuralParabola(new Vector2(24f, 30f), new Vector2(55f, 26f), 14f, 22),
                      muralLineWidth, 3.2f, 2.2f, k);
        c.Slime(56f, 26f, 6f, k);                                         // 抛物线末端的史莱姆
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphQ, muralBadgeGlyphPixel, k);
    }

    /// <summary>R2 复习·投掷：和 H6 同图，略简（史莱姆不带眼睛），功能徽章保留。</summary>
    static void PaintThrowSimple(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Figure(new Vector2(11f, 34f), 4.5f,
                 new Vector2(11f, 28f), new Vector2(11f, 16f),
                 new Vector2(4f, 30f), new Vector2(21f, 32f),
                 new Vector2(8f, 8f), new Vector2(15f, 8f),
                 muralLineWidth, k);
        c.DashedCurve(MuralParabola(new Vector2(24f, 30f), new Vector2(55f, 26f), 14f, 16),
                      muralLineWidth, 3.6f, 2.6f, k);
        c.Slime(56f, 26f, 6f, k, false);
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphQ, muralBadgeGlyphPixel, k);
    }

    /// <summary>H7 待命（连按 2 次 E）：史莱姆 + 脚下"钉住"符号 + 上方 ×2（两个叠在一起的小方块）。</summary>
    static void PaintStay(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Slime(18f, 26f, 11f, k);                                        // 史莱姆

        c.Stroke(18f, 26f, 18f, 15f, muralLineWidth, k);                  // 固定符号：钉杆
        c.Box(9, 12, 27, 15, k);                                          // 固定符号：横杠
        c.Box(10, 4, 11, 9, k);                                           // 固定符号：地面剖面线
        c.Box(15, 4, 16, 9, k);
        c.Box(20, 4, 21, 9, k);
        c.Box(25, 4, 26, 9, k);

        c.BoxOutline(34, 36, 46, 48, muralLineWidth, k);                  // ×2：两个叠在一起的小方块
        c.BoxOutline(41, 45, 53, 57, muralLineWidth, k);
    }

    /// <summary>H8 召回（按 Q）：史莱姆 + 一串箭头指向小孩 + 圆形 Q 徽章。</summary>
    static void PaintRecall(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Slime(14f, 28f, 9f, k);                                         // 史莱姆
        c.Arrow(26f, 30f, 38f, 30f, muralLineWidth, 7f, k);               // 一串箭头（被拉回去）
        c.Arrow(26f, 39f, 38f, 39f, muralLineWidth, 7f, k);
        c.Arrow(26f, 48f, 38f, 48f, muralLineWidth, 7f, k);

        c.Figure(new Vector2(50f, 50f), 4.5f,                             // 小孩（右上，避开徽章）
                 new Vector2(50f, 44f), new Vector2(50f, 32f),
                 new Vector2(45f, 38f), new Vector2(55f, 38f),
                 new Vector2(46f, 24f), new Vector2(54f, 24f),
                 muralLineWidth, k);

        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphQ, muralBadgeGlyphPixel, k);
    }

    /// <summary>H9 放引导石（3 选石，E 放置）：菱形石头 + 虚线路径 + 三个小点 + 圆形 E 徽章。</summary>
    static void PaintWaypointSet(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Disc(10f, 54f, 2.5f, k);                                        // 三个小点 = 最多 3 个路径石
        c.Disc(19f, 54f, 2.5f, k);
        c.Disc(28f, 54f, 2.5f, k);

        c.DashedStroke(3f, 32f, 10f, 32f, muralLineWidth, 3.2f, 2.2f, k); // 虚线路径（石头左边）
        c.Diamond(20f, 32f, 9f, 11f, k);                                  // 菱形引导石
        c.DashedStroke(30f, 32f, 39f, 32f, muralLineWidth, 3.2f, 2.2f, k);// 虚线路径（石头右边）

        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
    }

    /// <summary>H10 撤石（按 Q）：虚线路径被一条斜杠划掉 + 圆形 Q 徽章。</summary>
    static void PaintWaypointClear(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.DashedStroke(4f, 36f, 39f, 36f, muralLineWidth, 3.2f, 2.2f, k);
        c.Stroke(8f, 22f, 36f, 50f, 4f, k);                               // 大斜杠
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphQ, muralBadgeGlyphPixel, k);
    }

    /// <summary>H11 金币：史莱姆 + 向下的箭头指着金币。</summary>
    static void PaintCoin(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Slime(32f, 47f, 10f, k);                                        // 箭头起点的史莱姆
        c.Arrow(32f, 44f, 32f, 31f, muralLineWidth, 8f, k);               // 向下箭头
        c.Disc(32f, 15f, 10f, k);                                         // 金币
        c.ClearRing(32f, 15f, 7f, 1.6f);                                  // 币面内圈（抠出）
    }

    /// <summary>H12 回血：一颗心 + 一个加号。</summary>
    static void PaintHeal(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        MuralHeart(c, 20f, 34f, 15f, k);                                  // 心
        c.Box(39, 30, 59, 36, k);                                         // 加号：横
        c.Box(46, 22, 52, 44, k);                                         // 加号：竖
    }

    /// <summary>H13 收购站：一个带屋顶的摊位 + 一枚金币。</summary>
    static void PaintShop(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BoxOutline(10, 20, 38, 42, muralLineWidth, k);                  // 摊位主体
        c.Triangle(new Vector2(24f, 60f),                                  // 屋顶
                   new Vector2(4f, 42f), new Vector2(44f, 42f), k);
        c.ClearBox(16, 28, 32, 36);                                       // 柜台窗口
        c.Disc(50f, 31f, 8f, k);                                          // 金币
        c.ClearRing(50f, 31f, 5.5f, 1.5f);
    }

    // =================================================================================
    //  六、徽章点阵（'#' = 字母本体 → 用透明抠掉）
    // =================================================================================

    /// <summary>字母 E（5×7 点阵）。</summary>
    static readonly string[] MuralGlyphE =
    {
        "#####",
        "#....",
        "#....",
        "####.",
        "#....",
        "#....",
        "#####",
    };

    /// <summary>字母 Q（5×7 点阵）。</summary>
    static readonly string[] MuralGlyphQ =
    {
        ".###.",
        "#...#",
        "#...#",
        "#...#",
        "#.#.#",
        "#..#.",
        ".##.#",
    };

    // =================================================================================
    //  七、小工具
    // =================================================================================

    /// <summary>抛物线采样点（p0 → p1，hump = 中间最高抬升多少像素）。</summary>
    static Vector2[] MuralParabola(Vector2 p0, Vector2 p1, float hump, int samples)
    {
        if (samples < 2) samples = 2;
        Vector2[] pts = new Vector2[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            Vector2 p = Vector2.Lerp(p0, p1, t);
            p.y += hump * 4f * t * (1f - t);   // t(1-t) 最大 0.25 → 乘 4 让中点正好抬高 hump
            pts[i] = p;
        }
        return pts;
    }

    /// <summary>心形（两个圆 + 一个倒三角）。</summary>
    static void MuralHeart(MuralCanvas c, float cx, float cy, float s, Color32 k)
    {
        float r = s * 0.34f;
        c.Disc(cx - s * 0.28f, cy + s * 0.18f, r, k);
        c.Disc(cx + s * 0.28f, cy + s * 0.18f, r, k);
        c.Triangle(new Vector2(cx - s * 0.60f, cy + s * 0.22f),
                   new Vector2(cx + s * 0.60f, cy + s * 0.22f),
                   new Vector2(cx, cy - s * 0.62f), k);
    }
}

// =====================================================================================
//  八、画布：一块 64×64 的 RGBA32 像素缓冲 + 一点绘图基元
//     —— 只用 Texture2D / Color32 / EncodeToPNG，不碰 System.Drawing
// =====================================================================================

public class MuralCanvas
{
    public readonly int w;
    public readonly int h;

    readonly Color32[] _px;

    public MuralCanvas(int width, int height)
    {
        w = Mathf.Max(1, width);
        h = Mathf.Max(1, height);
        _px = new Color32[w * h];   // Color32 默认值 = (0,0,0,0) = 全透明
    }

    public bool Inside(int x, int y)
    {
        return x >= 0 && x < w && y >= 0 && y < h;
    }

    public Color32 Get(int x, int y)
    {
        if (!Inside(x, y)) return new Color32(0, 0, 0, 0);
        return _px[y * w + x];
    }

    /// <summary>画一个像素（越界自动忽略）。</summary>
    public void Px(int x, int y, Color32 c)
    {
        if (!Inside(x, y)) return;
        _px[y * w + x] = c;
    }

    /// <summary>抠掉一个像素（= 写透明，让底板颜色透出来）。</summary>
    public void Clear(int x, int y)
    {
        Px(x, y, MuralGenerator.muralClearColor);
    }

    // ------------------------------ 基元 ------------------------------

    /// <summary>实心矩形（含端点）。</summary>
    public void Box(int x0, int y0, int x1, int y1, Color32 c)
    {
        if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
        if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) Px(x, y, c);
    }

    /// <summary>抠空的矩形（含端点）。</summary>
    public void ClearBox(int x0, int y0, int x1, int y1)
    {
        Box(x0, y0, x1, y1, MuralGenerator.muralClearColor);
    }

    /// <summary>矩形描边（线宽 thickness，向外不扩）。</summary>
    public void BoxOutline(int x0, int y0, int x1, int y1, int thickness, Color32 c)
    {
        int t = Mathf.Max(1, thickness);
        Box(x0, y0, x1, y0 + t - 1, c);
        Box(x0, y1 - t + 1, x1, y1, c);
        Box(x0, y0, x0 + t - 1, y1, c);
        Box(x1 - t + 1, y0, x1, y1, c);
    }

    /// <summary>实心圆。</summary>
    public void Disc(float cx, float cy, float r, Color32 c)
    {
        MuralEllipse(cx, cy, r, r, c);
    }

    public void ClearDisc(float cx, float cy, float r)
    {
        MuralEllipse(cx, cy, r, r, MuralGenerator.muralClearColor);
    }

    /// <summary>实心椭圆（rx / ry）。</summary>
    void MuralEllipse(float cx, float cy, float rx, float ry, Color32 c)
    {
        if (rx <= 0f || ry <= 0f) return;
        int x0 = Mathf.FloorToInt(cx - rx), x1 = Mathf.CeilToInt(cx + rx);
        int y0 = Mathf.FloorToInt(cy - ry), y1 = Mathf.CeilToInt(cy + ry);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x - cx) / rx;
                float dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1f) Px(x, y, c);
            }
        }
    }

    /// <summary>圆环（描边圆）。</summary>
    public void Ring(float cx, float cy, float r, float thickness, Color32 c)
    {
        if (r <= 0f) return;
        float half = Mathf.Max(0.5f, thickness * 0.5f);
        int x0 = Mathf.FloorToInt(cx - r - half), x1 = Mathf.CeilToInt(cx + r + half);
        int y0 = Mathf.FloorToInt(cy - r - half), y1 = Mathf.CeilToInt(cy + r + half);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (Mathf.Abs(d - r) <= half) Px(x, y, c);
            }
        }
    }

    /// <summary>抠出圆环（金币内圈就用它）。</summary>
    public void ClearRing(float cx, float cy, float r, float thickness)
    {
        Ring(cx, cy, r, thickness, MuralGenerator.muralClearColor);
    }

    /// <summary>实心菱形（|dx|/rx + |dy|/ry ≤ 1）。</summary>
    public void Diamond(float cx, float cy, float rx, float ry, Color32 c)
    {
        if (rx <= 0f || ry <= 0f) return;
        int x0 = Mathf.FloorToInt(cx - rx), x1 = Mathf.CeilToInt(cx + rx);
        int y0 = Mathf.FloorToInt(cy - ry), y1 = Mathf.CeilToInt(cy + ry);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = Mathf.Abs(x - cx) / rx;
                float dy = Mathf.Abs(y - cy) / ry;
                if (dx + dy <= 1f) Px(x, y, c);
            }
        }
    }

    /// <summary>实心三角形（重心坐标法）。</summary>
    public void Triangle(Vector2 a, Vector2 b, Vector2 cc, Color32 c)
    {
        float d = (b.y - cc.y) * (a.x - cc.x) + (cc.x - b.x) * (a.y - cc.y);
        if (Mathf.Abs(d) < 0.0001f) return;

        int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, cc.x)));
        int x1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, cc.x)));
        int y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, cc.y)));
        int y1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, cc.y)));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float w0 = ((b.y - cc.y) * (x - cc.x) + (cc.x - b.x) * (y - cc.y)) / d;
                float w1 = ((cc.y - a.y) * (x - cc.x) + (a.x - cc.x) * (y - cc.y)) / d;
                float w2 = 1f - w0 - w1;
                if (w0 >= -0.001f && w1 >= -0.001f && w2 >= -0.001f) Px(x, y, c);
            }
        }
    }

    /// <summary>粗线段（圆头）：到线段的距离 ≤ thickness/2。</summary>
    public void Stroke(float x0, float y0, float x1, float y1, float thickness, Color32 c)
    {
        float half = Mathf.Max(1f, thickness) * 0.5f;
        float dx = x1 - x0, dy = y1 - y0;
        float lenSq = dx * dx + dy * dy;

        int minX = Mathf.FloorToInt(Mathf.Min(x0, x1) - half) - 1;
        int maxX = Mathf.CeilToInt(Mathf.Max(x0, x1) + half) + 1;
        int minY = Mathf.FloorToInt(Mathf.Min(y0, y1) - half) - 1;
        int maxY = Mathf.CeilToInt(Mathf.Max(y0, y1) + half) + 1;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float t = 0f;
                if (lenSq > 0.0001f) t = Mathf.Clamp01(((x - x0) * dx + (y - y0) * dy) / lenSq);
                float px = x0 + dx * t, py = y0 + dy * t;
                float ddx = x - px, ddy = y - py;
                if (ddx * ddx + ddy * ddy <= half * half) Px(x, y, c);
            }
        }
    }

    /// <summary>箭头：杆 + 实心三角箭头（(x1,y1) = 箭头尖）。</summary>
    public void Arrow(float x0, float y0, float x1, float y1, float thickness, float head, Color32 c)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float ux = dx / len, uy = dy / len;

        if (head < 1f) head = 1f;
        Stroke(x0, y0, x1 - ux * head * 0.6f, y1 - uy * head * 0.6f, thickness, c);

        float nx = -uy, ny = ux;
        float bx = x1 - ux * head, by = y1 - uy * head;   // 底边中点
        float hw = head * 0.5f;
        Triangle(new Vector2(x1, y1),
                 new Vector2(bx + nx * hw, by + ny * hw),
                 new Vector2(bx - nx * hw, by - ny * hw), c);
    }

    /// <summary>正弦波线（水波纹）。</summary>
    public void Wave(float x0, float x1, float y, float amp, float period, float thickness, Color32 c)
    {
        if (period < 0.5f) period = 0.5f;
        int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(x1 - x0) / 1.5f));
        float prevX = x0;
        float prevY = y + Mathf.Sin(x0 / period * Mathf.PI * 2f) * amp;
        for (int i = 1; i <= steps; i++)
        {
            float x = Mathf.Lerp(x0, x1, (float)i / steps);
            float yy = y + Mathf.Sin(x / period * Mathf.PI * 2f) * amp;
            Stroke(prevX, prevY, x, yy, thickness, c);
            prevX = x;
            prevY = yy;
        }
    }

    /// <summary>直线虚线（dash = 每段实线长，gap = 间隔长）。</summary>
    public void DashedStroke(float x0, float y0, float x1, float y1, float thickness, float dash, float gap, Color32 c)
    {
        DashedCurve(new Vector2[] { new Vector2(x0, y0), new Vector2(x1, y1) }, thickness, dash, gap, c);
    }

    /// <summary>折线 / 曲线虚线（按累计弧长切段）。</summary>
    public void DashedCurve(Vector2[] pts, float thickness, float dash, float gap, Color32 c)
    {
        if (pts == null || pts.Length < 2) return;
        if (dash < 0.5f) dash = 0.5f;
        if (gap < 0.5f) gap = 0.5f;
        float period = dash + gap;
        float acc = 0f;

        for (int i = 0; i + 1 < pts.Length; i++)
        {
            Vector2 a = pts[i];
            Vector2 b = pts[i + 1];
            float segLen = Vector2.Distance(a, b);
            if (segLen < 0.0001f) continue;

            int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / 0.5f));
            for (int s = 0; s < steps; s++)
            {
                float t0 = (float)s / steps;
                float t1 = (float)(s + 1) / steps;
                float mid = acc + segLen * (t0 + t1) * 0.5f;
                if (Mathf.Repeat(mid, period) > dash) continue;   // 落在间隔里
                Vector2 p0 = Vector2.Lerp(a, b, t0);
                Vector2 p1 = Vector2.Lerp(a, b, t1);
                Stroke(p0.x, p0.y, p1.x, p1.y, thickness, c);
            }
            acc += segLen;
        }
    }

    // ------------------------------ 角色剪影 ------------------------------

    /// <summary>火柴人式的小孩剪影：一个头 + 一条脊柱 + 两条手臂 + 两条腿。</summary>
    public void Figure(Vector2 head, float headR, Vector2 neck, Vector2 hip,
                       Vector2 handA, Vector2 handB, Vector2 footA, Vector2 footB,
                       float thickness, Color32 c)
    {
        Disc(head.x, head.y, headR, c);
        Stroke(neck.x, neck.y, hip.x, hip.y, thickness, c);
        Stroke(neck.x, neck.y, handA.x, handA.y, thickness, c);
        Stroke(neck.x, neck.y, handB.x, handB.y, thickness, c);
        Stroke(hip.x, hip.y, footA.x, footA.y, thickness, c);
        Stroke(hip.x, hip.y, footB.x, footB.y, thickness, c);
    }

    /// <summary>两节肢体（肩→肘→手 / 胯→膝→脚）：拐一下关节，姿势才不像晾衣架。</summary>
    public void Limb(Vector2 root, Vector2 joint, Vector2 end, float thickness, Color32 c)
    {
        Stroke(root.x, root.y, joint.x, joint.y, thickness, c);
        Stroke(joint.x, joint.y, end.x, end.y, thickness, c);
    }

    /// <summary>史莱姆象形：半圆穹顶（平底）+ 两只"抠出来"的眼睛。</summary>
    public void Slime(float cx, float bottomY, float r, Color32 c)
    {
        Slime(cx, bottomY, r, c, true);
    }

    public void Slime(float cx, float bottomY, float r, Color32 c, bool eyes)
    {
        if (r <= 0f) return;
        int x0 = Mathf.FloorToInt(cx - r), x1 = Mathf.CeilToInt(cx + r);
        int y0 = Mathf.FloorToInt(bottomY), y1 = Mathf.CeilToInt(bottomY + r);
        float rr = r * r;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - bottomY;
                if (dx * dx + dy * dy <= rr) Px(x, y, c);
            }
        }
        if (!eyes) return;
        float er = Mathf.Max(1.2f, r * 0.17f);
        ClearDisc(cx - r * 0.36f, bottomY + r * 0.42f, er);
        ClearDisc(cx + r * 0.36f, bottomY + r * 0.42f, er);
    }

    // ------------------------------ 按键徽章 ------------------------------

    /// <summary>
    /// 圆形按键徽章：实心浅色圆 + 用透明"抠出"字母（字母是空心的，透出底板颜色）。
    /// glyph = 5×7 的 '#'/'.' 点阵；pixel = 一个点阵格子画几像素。
    /// </summary>
    public void KeyBadge(float cx, float cy, float r, string[] glyph, int pixel, Color32 ink)
    {
        Disc(cx, cy, r, ink);
        if (glyph == null || glyph.Length == 0) return;

        int gh = glyph.Length;
        int gw = 0;
        for (int i = 0; i < gh; i++)
        {
            int len = glyph[i] == null ? 0 : glyph[i].Length;
            if (len > gw) gw = len;
        }
        if (gw == 0) return;
        if (pixel < 1) pixel = 1;

        float left = cx - (gw * pixel - 1) * 0.5f;
        float bottom = cy - (gh * pixel - 1) * 0.5f;

        for (int row = 0; row < gh; row++)
        {
            string line = glyph[row];
            if (line == null) continue;
            for (int col = 0; col < line.Length; col++)
            {
                if (line[col] != '#') continue;
                int px0 = Mathf.RoundToInt(left + col * pixel);
                int py0 = Mathf.RoundToInt(bottom + (gh - 1 - row) * pixel);   // 第 0 行画在顶部
                ClearBox(px0, py0, px0 + pixel - 1, py0 + pixel - 1);
            }
        }
    }

    // ------------------------------ 输出 ------------------------------

    /// <summary>不透明像素占比（0~1），自检用。</summary>
    public float OpaqueRatio()
    {
        int n = 0;
        for (int i = 0; i < _px.Length; i++)
        {
            if (_px[i].a > 8) n++;
        }
        return _px.Length == 0 ? 0f : (float)n / _px.Length;
    }

    /// <summary>变成 Texture2D（调用方负责 EncodeToPNG 之后 DestroyImmediate）。</summary>
    public Texture2D ToTexture()
    {
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels32(_px);
        tex.Apply(false, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }
}
