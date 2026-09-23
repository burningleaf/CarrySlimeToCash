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

    /// <summary>导入用的 Pixels Per Unit。⚠ 磁盘上现有 20 张 mural 的 .meta 实测就是 40
    /// （历史遗留：这一栏曾写成 32，与 .meta 不符）。重新生成会把导入设置按本值强写回去，
    /// 所以这里必须是 40 —— 否则重画一次就会把已经画好的 15 张放大 1.25 倍并上下溢出牌面。</summary>
    public static float muralPixelsPerUnit = 40f;

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
        // ⚠ 说明串必须与画法一致（它是生成日志里唯一给人看的证据）：
        //   动作键按 PlayerGrab.cs:97-159 的实际分工写 —— 空手 E 举/放、Q 投；哨子 E 召回、Q 冲刺；
        //   引导石 E 放路点、Q 撤路点。物品键帽 = 新增的 1/2/3 角标。
        MuralAdd(list, "H2", "举起：小孩把史莱姆举过头顶 + 1 键帽（空手）+ E 键帽", PaintCarryOverhead);
        MuralAdd(list, "H3", "水：三条波浪 + 水滴 + 感叹号", PaintWater);
        MuralAdd(list, "H4", "放下：小孩把史莱姆放到地上 + 1 键帽（空手）+ E 键帽", PaintPlaceDown);
        MuralAdd(list, "H5", "跳：向上粗箭头 + 起跳的小孩 + 地面线 + 空格键帽", PaintJump);
        MuralAdd(list, "H6", "投掷：抛物动作 + 抛物线虚线 + 史莱姆 + 1 键帽（空手）+ Q 键帽", PaintThrow);
        MuralAdd(list, "H7", "待命：史莱姆 + 钉住符号 + 2 键帽（哨子）+ 两个 E 键帽串联（第二个加圈）", PaintStay);
        MuralAdd(list, "H8", "召回：史莱姆 + 一串箭头指向小孩 + 2 键帽（哨子）+ E 键帽", PaintRecall);
        MuralAdd(list, "H9", "放引导石：菱形石头 + 虚线路径 + 三个点 + 3 键帽（引导石）+ E 键帽", PaintWaypointSet);
        MuralAdd(list, "H10", "撤石：虚线路径被斜杠划掉 + 3 键帽（引导石）+ Q 键帽", PaintWaypointClear);
        MuralAdd(list, "H11", "金币：史莱姆 + 向下箭头 → 金币", PaintCoin);
        MuralAdd(list, "H12", "回血：心 + 加号", PaintHeal);
        MuralAdd(list, "H13", "收购站：摊位 + 屋顶 + 金币", PaintShop);
        MuralAdd(list, "R1", "复习·举起（H2 简化版：1 键帽 + E 键帽）", PaintCarrySimple);
        MuralAdd(list, "R2", "复习·投掷（H6 简化版：1 键帽 + Q 键帽）", PaintThrowSimple);

        // ⚠ 下面 5 条必须【追加在末尾】：Level0.json 的 murals 数组顺序 = 本列表顺序（下标即 sprIndex）。
        MuralAdd(list, "N1", "物品栏 1：空手（手掌 + 1 键帽 + 底槽）", PaintItemBar1);
        MuralAdd(list, "N2", "物品栏 2：哨子（哨子 + 声波 + 2 键帽 + 底槽）", PaintItemBar2);
        MuralAdd(list, "N3", "物品栏 3：引导石（菱形 + 3 键帽 + 底槽）", PaintItemBar3);
        MuralAdd(list, "N4", "物品栏 4：预留（方框 + ? + 4 键帽 + 底槽，方案 4-A）", PaintItemBar4);
        MuralAdd(list, "N5", "换物品前先放下（1 键帽 + 开锁 + 空手小孩 + 落地史莱姆 + E 键帽）", PaintItemBar5);

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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 物品：1 空手
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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 物品：1 空手（与 H2 一致，复习才成立）
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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 物品：1 空手
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

        MuralKeyCapSpace(c, 47f, 11f, k);                                 // 动作：空格（跳跃）—— 本关唯一玩家无从得知的键
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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 物品：1 空手
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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 物品：1 空手（与 H6 一致，复习才成立）
    }

    /// <summary>H7 待命（哨子 2 + 连按 2 次 E）：史莱姆 + 脚下"钉住"符号 + 上方两个 E 键帽串联（第二个加圈）。</summary>
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

        // ×2：两个 E 键帽用箭头串起来 + 第二个加圈 = "同一件事做两次"
        // （原来的"两个叠放小方块"读不出"按两次"，见壁画规格文档 §2.4-1）
        c.KeyBadge(27f, 44f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
        c.Arrow(32f, 60f, 44f, 60f, muralLineWidth, 5f, k);
        c.KeyBadge(49f, 44f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);
        c.Ring(49f, 44f, muralBadgeRadius + 2f, muralLineWidth, k);       // 圈 = 第二次

        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph2, muralBadgeGlyphPixel, k);   // 物品：2 哨子
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);   // 动作：E
    }

    /// <summary>H8 召回（**2 哨子 + E**，不是 Q）：史莱姆 + 一串箭头指向小孩 + 物品/动作键帽。
    /// ⚠ 这里原来标的是 `Q` —— 那是历史错误：`PlayerGrab.cs:133-138` 写明 `Q` + 哨子 = **冲刺**，
    /// 而"召回" = `2` + `E`（`PlayerGrab.cs:106-109` → `ToggleMode`）。按错的键玩会得到"史莱姆冲刺"，与牌面意思相反。</summary>
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

        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);   // 动作：E（召回）
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph2, muralBadgeGlyphPixel, k);   // 物品：2 哨子
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
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph3, muralBadgeGlyphPixel, k);   // 物品：3 引导石
    }

    /// <summary>H10 撤石（按 Q）：虚线路径被一条斜杠划掉 + 圆形 Q 徽章。</summary>
    static void PaintWaypointClear(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.DashedStroke(4f, 36f, 39f, 36f, muralLineWidth, 3.2f, 2.2f, k);
        c.Stroke(8f, 22f, 36f, 50f, 4f, k);                               // 大斜杠
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphQ, muralBadgeGlyphPixel, k);
        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph3, muralBadgeGlyphPixel, k);   // 物品：3 引导石
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
    //  五之二、物品栏 5 张（N1~N5）：四张单格拼成一条 + 一张"换物品前先放下"
    //  出处：壁画规格文档 §3（方案 B）。⚠ 顺序必须与 Level0.json 的
    //  murals 数组逐条一致（H1..H13, R1, R2, N1..N5 = 20 条）。
    // =================================================================================

    /// <summary>N1 物品栏第 1 格 = 空手：张开的手掌 + 左下 `1` 键帽 + 底槽。</summary>
    static void PaintItemBar1(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(0, 2, 63, 4, k);                                            // 底槽（四张画在同一 y，拼起来连成一条）
        c.KeyBadge(16f, 16f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);

        c.Disc(44f, 26f, 11f, k);                                         // 掌根
        c.Stroke(34f, 30f, 32f, 46f, muralLineWidth, k);                  // 四指
        c.Stroke(40f, 30f, 39f, 48f, muralLineWidth, k);
        c.Stroke(46f, 30f, 47f, 48f, muralLineWidth, k);
        c.Stroke(52f, 30f, 55f, 45f, muralLineWidth, k);
        c.Stroke(35f, 23f, 24f, 29f, muralLineWidth, k);                  // 拇指
    }

    /// <summary>N2 物品栏第 2 格 = 哨子：圆头 + 管身 + 声波 + 左下 `2` 键帽 + 底槽。</summary>
    static void PaintItemBar2(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(0, 2, 63, 4, k);                                            // 底槽
        c.KeyBadge(16f, 16f, muralBadgeRadius, MuralGlyph2, muralBadgeGlyphPixel, k);

        c.Ring(32f, 36f, 12f, muralLineWidth, k);                         // 挂绳（被主体挡掉一半，可接受）
        c.Disc(36f, 32f, 11f, k);                                         // 圆头
        c.Box(36, 26, 58, 36, k);                                         // 管身
        c.ClearBox(54, 28, 58, 34);                                       // 抠出吹嘴
        c.ClearDisc(36f, 44f, 3f);                                        // 气孔

        c.Stroke(50f, 44f, 58f, 50f, muralLineWidth, k);                  // 声波两条
        c.Stroke(50f, 38f, 58f, 36f, muralLineWidth, k);
    }

    /// <summary>N3 物品栏第 3 格 = 引导石：菱形石头（与 H9 同一形状）+ 十字光纹 + 左下 `3` 键帽 + 底槽。</summary>
    static void PaintItemBar3(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(0, 2, 63, 4, k);                                            // 底槽
        c.KeyBadge(16f, 16f, muralBadgeRadius, MuralGlyph3, muralBadgeGlyphPixel, k);

        c.Diamond(40f, 32f, 9f, 11f, k);                                  // 同一物品必须同一形状：H9 用的就是菱形
        c.ClearBox(39, 29, 41, 35);                                       // 十字光纹（抠出）
        c.ClearBox(36, 31, 44, 33);
    }

    /// <summary>N4 物品栏第 4 格 = 预留（方案 4-A）：空心方框 + 抠出的 `?`（与 HUD 的 Icon_Slot4 同形）+ 左下 `4` 键帽 + 底槽。
    /// ⚠ 第 4 格内容一旦定了（例如"跳跃云朵瓶"），**只换这一个函数体**，坐标/JSON/接线都不用动。</summary>
    static void PaintItemBar4(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.Box(0, 2, 63, 4, k);                                            // 底槽
        c.KeyBadge(16f, 16f, muralBadgeRadius, MuralGlyph4, muralBadgeGlyphPixel, k);

        c.BoxOutline(30, 20, 58, 48, muralLineWidth, k);                  // 空心方框
        MuralCarveGlyph(c, 44f, 34f, MuralGlyphQuestion, muralBadgeGlyphPixel);   // 抠出 `?`
    }

    /// <summary>N5 换物品前先放下（补上现在完全没人教的强制前置：`allowSwitchWhileCarrying = false`）。
    /// 与 H4 的区别：这里**没有地面线**、**有 `1` 键帽**、中间多一个"开锁"符号 ⇒ 讲的是"为了换物品而放下"。</summary>
    static void PaintItemBar5(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.KeyBadge(14f, 14f, muralBadgeRadius, MuralGlyph1, muralBadgeGlyphPixel, k);   // 先切 `1` 空手
        c.KeyBadge(52f, 11f, muralBadgeRadius, MuralGlyphE, muralBadgeGlyphPixel, k);   // 再按 `E` 放下

        c.Slime(20f, 12f, 8f, k);                                         // 中央左：落地的史莱姆

        c.Disc(42f, 34f, 4.5f, k);                                        // 中央右：空手小孩
        c.Stroke(42f, 29f, 42f, 18f, muralLineWidth, k);                  // 脊柱
        c.Stroke(42f, 18f, 39f, 10f, muralLineWidth, k);                  // 两条腿
        c.Stroke(42f, 18f, 45f, 10f, muralLineWidth, k);
        c.Stroke(42f, 30f, 38f, 20f, muralLineWidth, k);                  // 两臂自然下垂（不是"举着"）
        c.Stroke(42f, 30f, 46f, 20f, muralLineWidth, k);

        c.Ring(31f, 26f, 7f, muralLineWidth, k);                          // 开锁 = 解除限制
        c.ClearBox(31, 32, 33, 34);                                       // 缺口
        c.Stroke(25f, 26f, 37f, 26f, muralLineWidth, k);                  // 锁梁
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

    /// <summary>数字 1（5×7 点阵）：物品栏第 1 格 = 空手。</summary>
    static readonly string[] MuralGlyph1 =
    {
        "..#..",
        ".##..",
        "..#..",
        "..#..",
        "..#..",
        "..#..",
        ".###.",
    };

    /// <summary>数字 2：物品栏第 2 格 = 哨子。</summary>
    static readonly string[] MuralGlyph2 =
    {
        ".###.",
        "#...#",
        "....#",
        "..##.",
        ".#...",
        "#....",
        "#####",
    };

    /// <summary>数字 3：物品栏第 3 格 = 引导石。</summary>
    static readonly string[] MuralGlyph3 =
    {
        ".###.",
        "#...#",
        "....#",
        "..##.",
        "....#",
        "#...#",
        ".###.",
    };

    /// <summary>数字 4：物品栏第 4 格（预留位）。</summary>
    static readonly string[] MuralGlyph4 =
    {
        "...#.",
        "..##.",
        ".#.#.",
        "#..#.",
        "#####",
        "...#.",
        "...#.",
    };

    /// <summary>问号：第 4 格"预留"的符号（与 HUD 的 Icon_Slot4 同形）。</summary>
    static readonly string[] MuralGlyphQuestion =
    {
        ".###.",
        "#...#",
        "....#",
        "..##.",
        "..#..",
        ".....",
        "..#..",
    };

    /// <summary>宽键帽的半宽/半高（px）。放 (47,11) 时 x ∈ [33,61]、y ∈ [4,18]，都在 64 画布内。</summary>
    public static int muralSpaceCapHalfW = 14;
    public static int muralSpaceCapHalfH = 7;

    /// <summary>
    /// 宽键帽（空格这类没有字母的键）：实心长条 + 削两个上角 + 抠出一个"⌣"。
    /// 画法与 <see cref="MuralCanvas.KeyBadge"/> 同一套（实心 + 透明抠出），只是形状是长条而不是圆。
    /// </summary>
    static void MuralKeyCapSpace(MuralCanvas c, float cx, float cy, Color32 k)
    {
        int hw = Mathf.Max(1, muralSpaceCapHalfW);
        int hh = Mathf.Max(1, muralSpaceCapHalfH);
        int ix = Mathf.RoundToInt(cx), iy = Mathf.RoundToInt(cy);

        c.Box(ix - hw, iy - hh, ix + hw, iy + hh, k);
        c.Clear(ix - hw, iy + hh);                       // 削两个上角，看起来像圆角键帽
        c.Clear(ix + hw, iy + hh);

        for (int i = -9; i <= 9; i++)                    // "⌣"：中间低、两端高
        {
            int dy = -(i * i) / 18;
            c.ClearBox(ix + i, iy + dy - 1, ix + i, iy + dy + 1);
        }
    }

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

    /// <summary>
    /// 把一个 5×7 点阵**抠**在 (cx,cy) 居中的位置（排版算法与 <see cref="MuralCanvas.KeyBadge"/> 逐字一致，
    /// 只是没有圆底、也不写墨色 ⇒ 用在"方框里抠出一个 `?`"这种场合）。
    /// </summary>
    static void MuralCarveGlyph(MuralCanvas c, float cx, float cy, string[] glyph, int pixel)
    {
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
                int py0 = Mathf.RoundToInt(bottom + (gh - 1 - row) * pixel);   // 第 0 行在顶部
                c.ClearBox(px0, py0, px0 + pixel - 1, py0 + pixel - 1);
            }
        }
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
