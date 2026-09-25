// =====================================================================================
//  MuralGenerator.cs —— 《带呆呆史莱姆越障换钱》象形图路牌生成器（流5：美术与音频）
//
//  干什么：
//    程序化画出教学关 Level0 路牌上的 20 张"象形图"（64×64 PNG，透明底 + 近白图案），
//    并顺手把每张图的 TextureImporter 设好
//    （Sprite / Single / Point / 无 Mipmap / 无压缩 / Clamp / PPU 40 / FullRect / Center）。
//    ⚠ PPU 是 40（不是 32）：磁盘上现有 mural 的 .meta 实测就是 40，64px = 1.6 世界单位 = 牌面高。
//      写回 32 会把已经画好的图放大 1.25 倍并上下溢出牌面。生成完 VisualFix.BatchFixImportPpu()
//      也会按「Murals → PPU 40」把它掰回来（见 VisualFix.WantedPpu）。
//
//  为什么：
//    LevelBuilder.BuildMural 现在挂的是占位文字（showMuralLabels = true）。
//    这批图出来之后，把 sprite 接进 LevelData 的 murals[].sprIndex 就能关掉文字。
//    ⚠ 接线不在这一个文件里做 —— 那是另一轮的事，本文件只产图。
//
//  怎么用：
//    · 菜单：Tools/呆呆史莱姆/4 生成素材/象形图路牌
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

    /// <summary>圆形按键徽章半径（px）。**r=8 ⇒ 直径 17px**（旧值 11 ⇒ 直径 22px = 画布 34%，
    /// 一放下去就压住人物下半身 —— 壁画规格里实测的根因）。</summary>
    public static float muralBadgeRadius = 8f;

    /// <summary>徽章里字母的像素格大小（5×7 点阵 × 1 = **5×7px**，塞得进半径 8 的圆）。</summary>
    public static int muralBadgeGlyphPixel = 1;

    /// <summary>圆键帽圆心 y（固定值：所有带键帽的牌同一条水平线）。</summary>
    public static float muralBadgeCy = 12f;

    /// <summary>物品键帽圆心 x（固定值；圆键帽 bbox = x[3,19] × y[4,20]）。</summary>
    public static float muralBadgeLeftX = 11f;

    /// <summary>动作键帽圆心 x（固定值；圆键帽 bbox = x[44,60] × y[4,20]）。</summary>
    public static float muralBadgeRightX = 52f;

    /// <summary>图案颜色：接近纯白（进游戏再染色）。</summary>
    public static Color32 muralInkColor = new Color32(242, 245, 252, 255);

    /// <summary>完全透明（用来"抠"形状）。</summary>
    public static Color32 muralClearColor = new Color32(0, 0, 0, 0);

    /// <summary>自检下限：不透明像素占比低于它 = 等于没画出来。</summary>
    public static float muralMinCoverage = 0.05f;

    /// <summary>自检上限：不透明像素占比高于它 = 糊成一片了。</summary>
    public static float muralMaxCoverage = 0.85f;

    // =================================================================================
    //  一之二、布局分区与防重叠硬规则（壁画规格；数值全部放 public 字段）
    //
    //  为什么要这些：用户反馈「H2/H4~H7/N5/R1/R2 元素相互重叠导致看不清」。根因是
    //  **键帽过大（r=11 ⇒ 直径 22px = 画布 34%）+ 元素跨区**（地面线 / 斜杠 / 钉住横杠横穿画面）。
    //  所以本轮先立"三带 + 一条竖缝"，再逐张把元素画进格子里。
    //
    //      y=61 ┌──────────────────────────────┐  M 主图带 y∈[24,61]
    //           │  M-L (x2..30)  ║  M-R (33..61)│  ║ = 竖缝 x∈[31,32]（必须全透明）
    //      y=24 ├──────────────────────────────┤
    //           │      缝 y∈[21,23]（必须全透明）│
    //      y=20 ├──────────────────────────────┤  K 键帽带 y∈[4,20]
    //           │  L槽(11,12) r=8   R槽(52,12)  │  空格长条 x∈[21,54]
    //      y=3  ├──────────────────────────────┤
    //           │  L 底槽带 y∈[2,3]（仅 N1~N4） │
    //      y=2  └──────────────────────────────┘
    // =================================================================================

    /// <summary>外留白规则：所有不透明像素必须落在 [margin, size-1-margin]。⚠ 规格约定的是 2px。</summary>
    public static int muralSafeMargin = 2;

    /// <summary>横缝规则：这条带子必须**全透明** —— 它把键帽带与主图带物理分开。</summary>
    public static int muralSeamY0 = 21;
    public static int muralSeamY1 = 23;

    /// <summary>竖缝规则：M 带里 x∈[seamX0,seamX1]、y∈[mainY0,mainY1] 必须**全透明** —— 它把主图分成左右两格。</summary>
    public static int muralSeamX0 = 31;
    public static int muralSeamX1 = 32;

    /// <summary>M 主图带的 y 范围。</summary>
    public static int muralMainY0 = 24;
    public static int muralMainY1 = 61;

    /// <summary>M-L / M-R 两格的 x 范围（中间隔着竖缝）。</summary>
    public static int muralMainLeftX0 = 2;
    public static int muralMainLeftX1 = 30;
    public static int muralMainRightX0 = 33;
    public static int muralMainRightX1 = 61;

    /// <summary>K 键帽带的 y 范围。</summary>
    public static int muralKeyBandY0 = 4;
    public static int muralKeyBandY1 = 20;

    /// <summary>K-L / K-R 两个键帽槽的 x 范围（圆心见 muralBadgeLeftX / muralBadgeRightX）。</summary>
    public static int muralKeyLeftX0 = 3;
    public static int muralKeyLeftX1 = 19;
    public static int muralKeyRightX0 = 44;
    public static int muralKeyRightX1 = 60;

    /// <summary>L 底槽带（仅 N1~N4 用；与键帽隔 1px ⇒ 满足包围盒分离规则）。</summary>
    public static int muralSlotY0 = 2;
    public static int muralSlotY1 = 3;

    /// <summary>空格长条键帽的 bbox（规格）：**34×13**，中心 (37.5,12)。</summary>
    public static int muralSpaceCapX0 = 21;
    public static int muralSpaceCapX1 = 54;
    public static int muralSpaceCapY0 = 6;
    public static int muralSpaceCapY1 = 18;

    /// <summary>空格长条中间那条"横杠"（⎵ 的通识写法）的尺寸：宽 10 × 高 2，高度方向居中。</summary>
    public static int muralSpaceBarW = 10;
    public static int muralSpaceBarH = 2;

    /// <summary>包围盒分离规则：任意两个元素的包围盒必须"x 向或 y 向分离 ≥ 本值"。</summary>
    public static int muralMinElemGap = 1;

    /// <summary>同格间距规则：**同一格内**两个元素必须分离 ≥ 本值（2px ≈ 屏幕 3~4px，缩到 1/2 仍看得见缝）。</summary>
    public static int muralMinSameZoneGap = 2;

    /// <summary>最小尺寸规则：任何元素的 min(宽,高) ≥ 本值（≈12 屏幕像素 @1080p）。</summary>
    public static int muralMinElemSize = 6;

    /// <summary>每格元素数上限：M-L / M-R **每格**最多几个元素。</summary>
    public static int muralMaxElemsPerSide = 2;

    /// <summary>键帽数上限：K 键帽带最多几个键帽（空格长条算 1 个且独占）。</summary>
    public static int muralMaxKeyCaps = 2;

    /// <summary>全张元素数上限。</summary>
    public static int muralMaxElemsTotal = 6;

    /// <summary>最小尺寸规则的**例外**：L 底槽带只有 2px 高（规格定的），它天然过不了"min(宽,高) ≥ 6"。
    /// 规格又把底槽当作"一条同款记号"保留 ⇒ 这里显式把 L 带排除在最小尺寸规则之外（默认 true）。
    /// 这是规格自身的一处口径冲突，已在报告里写明；置 false 可让它变成硬失败。</summary>
    public static bool muralSlotExemptFromMinSize = true;

    // =================================================================================
    //  二、入口
    // =================================================================================

    [MenuItem("Tools/呆呆史莱姆/4 生成素材/象形图路牌", false, 401)]
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
        int layoutFailCount = 0;
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

                // ---- 防重叠自检（规格的防重叠硬规则 + 验收项）----
                //   放在写盘前：日志与图一一对应，出问题能立刻定位到是哪张牌、哪个元素。
                List<string> layoutLines = new List<string>();
                List<string> layoutBad = new List<string>();
                bool layoutOk = MuralCheckLayout(def.id, canvas, layoutLines, layoutBad);
                if (!layoutOk)
                {
                    layoutFailCount++;
                    for (int b = 0; b < layoutBad.Count; b++)
                        Debug.LogWarning(string.Format(
                            "[MuralGenerator] 防重叠自检不过：{0} → {1}（只能挪坐标 / 改分区，⛔ 不许把元素缩小硬塞）",
                            fileName, layoutBad[b]));
                }

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
                if (!layoutOk) flag += "  ⚠ 防重叠不过";

                lines.Add(string.Format("  {0,-16} {1}×{1}  不透明 {2,5:F1}%{3}   {4}",
                    fileName, muralSize, coverage * 100f, flag, def.note));
                for (int l = 0; l < layoutLines.Count; l++)
                    lines.Add("       [壁画自检] " + layoutLines[l]);
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
            "[MuralGenerator] 成功 {0} / 占比告警 {1} / 防重叠不过 {2} / 失败 {3}；占比区间 {4:F1}%~{5:F1}%（自检区间 {6:F0}%~{7:F0}%）",
            okCount, warnCount, layoutFailCount, failCount, minSeen * 100f, maxSeen * 100f,
            muralMinCoverage * 100f, muralMaxCoverage * 100f));
        Debug.Log(string.Format(
            "[MuralGenerator] 布局硬规则：留白 {0}px｜横缝 y[{1},{2}] 必须空｜键帽 α17px 圆心 ({3:0},{4:0})/({5:0},{6:0})｜"
            + "M-L x[{7},{8}] / 竖缝 x[{9},{10}] / M-R x[{11},{12}]｜元素间 ≥{13}px、同格 ≥{14}px、单元素 min≥{15}px",
            muralSafeMargin, muralSeamY0, muralSeamY1, muralBadgeLeftX, muralBadgeCy, muralBadgeRightX, muralBadgeCy,
            muralMainLeftX0, muralMainLeftX1, muralSeamX0, muralSeamX1, muralMainRightX0, muralMainRightX1,
            muralMinElemGap, muralMinSameZoneGap, muralMinElemSize));
        Debug.Log(string.Format(
            "[MuralGenerator] 输出目录：{0}（{1}）；导入设置：Sprite/Single/Point/无Mipmap/无压缩/Clamp/PPU {2}/FullRect/Center",
            muralOutputDir, absDir, muralPixelsPerUnit));

        // ⚠ 生成即自愈（生成器自愈，第二道防线）：上面写盘用的是 muralPixelsPerUnit（当前 40，是对的），
        //   但不能再假设它永远对 —— 一旦被改错，壁画画完就带着错的 PPU 上架（64px 图会缩放 1.25 倍并上下溢出牌面）。
        //   这里统一按 VisualFix.WantedPpu（Murals/** → 40）把导入设置掰回来。
        //   放在 MuralGenerateAll() 里（而不是只放在 BatchGenerateMurals 里）：菜单入口 GenerateMuralsMenu()
        //   与批处理入口 BatchGenerateMurals() 走的是同一个方法，两条路径都能兜住 ——
        //   与 PixelArtGenerator.GenerateAll() 的收口方式保持一致。
        VisualFix.BatchFixImportPpu();
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

        MuralAdd(list, "H1", "跟随：小孩（M-L）+ 史莱姆（M-R）+ 指向小孩的箭头（M-R；镜像后不再跨竖缝）", PaintFollow);
        // ⚠ 说明串必须与画法一致（它是生成日志里唯一给人看的证据）：
        //   动作键按 PlayerGrab.cs:97-159 的实际分工写 —— 空手 E 举/放、Q 投；哨子 E 召回、Q 冲刺；
        //   引导石 E 放路点、Q 撤路点。物品键帽 = 新增的 1/2/3 角标。
        //   第二轮重画：20 张全部按壁画规格的"三带 + 竖缝"重排（键帽 r 11→8、字形 2→1），
        //   每张都在生成末尾跑 MuralCheckLayout() 防重叠自检 —— 日志里带 `[壁画自检]` 的那些行就是证据。
        MuralAdd(list, "H2", "举起：小孩把史莱姆举过头顶（M-L）+ 1 键帽（空手）+ E 键帽", PaintCarryOverhead);
        MuralAdd(list, "H3", "水：三条波浪（M-L）+ 水滴（M-R，抠出感叹号）", PaintWater);
        MuralAdd(list, "H4", "放下：小孩双手向下（M-L）+ 向下箭头 + 落地史莱姆（M-R）+ 1/E 键帽", PaintPlaceDown);
        MuralAdd(list, "H5", "跳：起跳的小孩 + 地面短线（M-L）+ 向上箭头（M-R，轴心对齐）+ 空格长条键帽", PaintJump);
        MuralAdd(list, "H6", "投掷：前抛小孩（M-L）+ 抛物线虚线 + 落地史莱姆（M-R）+ 1/Q 键帽", PaintThrow);
        MuralAdd(list, "H7", "静止↔跟随：左态（史莱姆 + 停记号横杠）/ 右态（史莱姆 + 尖朝左箭头）+ 2/E 键帽", PaintStay);
        MuralAdd(list, "H8", "召回：小孩（M-L）+ 三连箭头指向他 + 史莱姆（M-R）+ 2 键帽（哨子）+ E 键帽", PaintRecall);
        MuralAdd(list, "H9", "放引导石：左虚线 + 菱形石头（M-L）+ 三个小点 + 右虚线（M-R）+ 3/E 键帽", PaintWaypointSet);
        MuralAdd(list, "H10", "撤石（候选 A）：菱形石头（M-L）+ 向左的回收箭头（M-R）+ 3/Q 键帽", PaintWaypointClear);
        MuralAdd(list, "H11", "金币：史莱姆 + 斜向下箭头（M-L）+ 金币（M-R）", PaintCoin);
        MuralAdd(list, "H12", "回血：心（M-L）+ 加号（M-R）", PaintHeal);
        MuralAdd(list, "H13", "收购站：屋檐 + 摊位（M-L，算一个元素）+ 金币（M-R）", PaintShop);
        MuralAdd(list, "R1", "复习·举起（H2 同构图，只把史莱姆眼睛去掉）+ 1/E 键帽", PaintCarrySimple);
        MuralAdd(list, "R2", "复习·投掷（H6 同构图，史莱姆不画眼睛 + 虚线更疏）+ 1/Q 键帽", PaintThrowSimple);

        // ⚠ 下面 5 条必须【追加在末尾】：Level0.json 的 murals 数组顺序 = 本列表顺序（下标即 sprIndex）。
        MuralAdd(list, "N1", "物品栏 1：空手（手掌在 M-R + 1 键帽 + 底槽）", PaintItemBar1);
        MuralAdd(list, "N2", "物品栏 2：哨子（哨子在 M-R + 声波 + 2 键帽 + 底槽；挂绳环已删）", PaintItemBar2);
        MuralAdd(list, "N3", "物品栏 3：引导石（菱形在 M-R + 十字光纹 + 3 键帽 + 底槽）", PaintItemBar3);
        MuralAdd(list, "N4", "物品栏 4：预留（方框 + ? 在 M-R + 4 键帽 + 底槽，方案 4-A）", PaintItemBar4);
        MuralAdd(list, "N5", "哨子：按 2 切到哨子 → 按 E 让史莱姆站在原地（暂停记号 ⏸ = 定住；小孩 M-L + 暂停记号/落地史莱姆 M-R + 2/E 键帽）", PaintItemBar5);

        return list;
    }

    // =================================================================================
    //  五、逐个画（参数都是 64×64 画布上的像素坐标，y 向上）
    // =================================================================================

    // ---- 键帽统一入口（规格：圆键帽 r=8、圆心固定 (11,12)/(52,12)、字形 5×7）----
    //  每次画键帽都顺手把它登记成一个元素（BeginElem/EndElem），防重叠自检才知道它占了哪块地。
    static void MuralKeyCap(MuralCanvas c, float cx, float cy, string[] glyph, MuralZone zone, string label)
    {
        c.BeginElem(label, zone);
        c.KeyBadge(cx, cy, muralBadgeRadius, glyph, muralBadgeGlyphPixel, muralInkColor);
        c.EndElem(glyph);
    }

    /// <summary>物品键帽（K-L 槽，圆心 (11,12)）。</summary>
    static void MuralKeyCapLeft(MuralCanvas c, string[] glyph, string label)
    {
        MuralKeyCap(c, muralBadgeLeftX, muralBadgeCy, glyph, MuralZone.KeyLeft, label);
    }

    /// <summary>动作键帽（K-R 槽，圆心 (52,12)）。</summary>
    static void MuralKeyCapRight(MuralCanvas c, string[] glyph, string label)
    {
        MuralKeyCap(c, muralBadgeRightX, muralBadgeCy, glyph, MuralZone.KeyRight, label);
    }

    /// <summary>H1 跟随：小孩（M-L）+ 史莱姆（M-R）+ 一条指向小孩的箭头（M-R）。
    /// ⚠ 与旧版相比是**镜像**过来的：旧版史莱姆在左、箭头 x[26,39] 横穿竖缝 x[31,32] ⇒ 违反"横缝/竖缝必须全透明"与分区约束（规格当时漏检了这一条）。
    ///   现在史莱姆与箭头同住 M-R、箭头朝左指向 M-L 的小孩 —— 语义不变（史莱姆朝小孩移动），缝里一个像素都没有。</summary>
    static void PaintFollow(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩", MuralZone.MainLeft);
        c.Figure(new Vector2(14f, 45f), 5f,                                // 头
                 new Vector2(14f, 40f), new Vector2(14f, 30f),             // 脖子 / 胯
                 new Vector2(9f, 36f), new Vector2(19f, 36f),              // 两只手
                 new Vector2(10f, 26f), new Vector2(18f, 26f),             // 两只脚
                 muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("箭头（史莱姆→小孩）", MuralZone.MainRight);
        c.Arrow(58f, 38f, 46f, 38f, muralLineWidth, 8f, k);                // 朝左 = 朝小孩
        c.EndElem(null);

        c.BeginElem("史莱姆", MuralZone.MainRight);
        c.Slime(52f, 48f, 9f, k);                                          // 史莱姆（右）
        c.EndElem(null);
    }

    /// <summary>H2 举起（按 E）：小孩（M-L）双手把史莱姆举过头顶 + `1` / `E` 两个圆键帽（K 带）。
    /// 旧版的病灶：`1` 键帽 (14,14) r=11 压住双腿 / 脊柱，地面线 Box(6,5,34,7) 又横穿到键帽里（4 处相交）。
    /// 新版：键帽缩小并移出 M 带；**删掉整条地面线**；人物与史莱姆严格留 2px 以上（同格间距规则）。</summary>
    static void PaintCarryOverhead(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（双手上举）", MuralZone.MainLeft);
        c.Disc(15f, 42f, 5f, k);                                          // 头
        c.Stroke(15f, 37f, 15f, 29f, muralLineWidth, k);                  // 脊柱
        c.Stroke(15f, 29f, 11f, 25f, muralLineWidth, k);                  // 两条腿
        c.Stroke(15f, 29f, 19f, 25f, muralLineWidth, k);
        c.Limb(new Vector2(15f, 38f), new Vector2(7f, 40f), new Vector2(9f, 49f), muralLineWidth, k);
        c.Limb(new Vector2(15f, 38f), new Vector2(23f, 40f), new Vector2(21f, 49f), muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("史莱姆（举过头顶）", MuralZone.MainLeft);
        c.Slime(15f, 53f, 7f, k);                                         // 举在头顶的史莱姆
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");                 // 物品：1 空手
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（举起）");
    }

    /// <summary>R1 复习·举起：与 H2 **同构图**（复习牌的意义就在这里），只把史莱姆的**眼睛去掉**当简化。
    /// ⚠ 旧版用"少画地面线"当简化，结果 R1 和 H2 长得不一样 —— 那不是复习牌该有的样子。</summary>
    static void PaintCarrySimple(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（双手上举）", MuralZone.MainLeft);
        c.Disc(15f, 42f, 5f, k);                                          // 头
        c.Stroke(15f, 37f, 15f, 29f, muralLineWidth, k);                  // 脊柱
        c.Stroke(15f, 29f, 11f, 25f, muralLineWidth, k);                  // 两条腿
        c.Stroke(15f, 29f, 19f, 25f, muralLineWidth, k);
        c.Limb(new Vector2(15f, 38f), new Vector2(7f, 40f), new Vector2(9f, 49f), muralLineWidth, k);
        c.Limb(new Vector2(15f, 38f), new Vector2(23f, 40f), new Vector2(21f, 49f), muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("史莱姆（不画眼睛 = 简化）", MuralZone.MainLeft);
        c.Slime(15f, 53f, 7f, k, false);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");                 // 与 H2 一致，复习才成立
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（举起）");
    }

    /// <summary>H3 水：三条波浪线（M-L）+ 一个水滴（M-R，水滴里抠出感叹号）。
    /// ⚠ 旧版波纹一直画到 x=36，**横穿竖缝 x[31,32]** ⇒ 违反"竖缝必须全透明"（规格当时漏检了这一条）。新版缩到 M-L 内。</summary>
    static void PaintWater(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("三条波浪（水面）", MuralZone.MainLeft);
        // ⚠ y 必须 ≥24：中间那条原来画在 y=22 —— 正好落在**横缝 y[21,23]** 里（规格当时漏检了这一条）。
        c.Wave(3f, 28f, 28f, 2.4f, 9f, muralLineWidth, k);
        c.Wave(3f, 28f, 37f, 2.4f, 9f, muralLineWidth, k);
        c.Wave(3f, 28f, 46f, 2.4f, 9f, muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("水滴（危险地形）", MuralZone.MainRight);
        c.Disc(48f, 40f, 9f, k);                                          // 水滴的肚子
        c.Triangle(new Vector2(48f, 59f),                                 // 水滴的尖
                   new Vector2(39.5f, 42f), new Vector2(56.5f, 42f), k);
        c.ClearBox(47, 46, 49, 52);                                       // 感叹号：竖杠
        c.ClearBox(47, 35, 49, 37);                                       // 感叹号：点
        c.EndElem(null);
    }

    /// <summary>H4 放下（按 E）：小孩（M-L，双手向下）+ 向下箭头（M-R）+ 落地史莱姆（M-R）+ 两个键帽。
    /// 旧版四条地面线横穿 34px，把键帽 / 双腿 / 脊柱 / 落地史莱姆四处压在一起（包围盒分离规则 ×4）。
    /// 新版：**删掉整条地面线**；箭头移到 M-R、正对史莱姆上方；人物与键帽彻底分开。</summary>
    static void PaintPlaceDown(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（双手向下放）", MuralZone.MainLeft);
        c.Disc(12f, 45f, 4.5f, k);                                        // 头
        c.Stroke(12f, 40f, 12f, 31f, muralLineWidth, k);                  // 脊柱（站直）
        c.Stroke(12f, 31f, 9f, 27f, muralLineWidth, k);                   // 两条腿
        c.Stroke(12f, 31f, 15f, 27f, muralLineWidth, k);
        c.Limb(new Vector2(12f, 39f), new Vector2(17f, 35f), new Vector2(19f, 29f), muralLineWidth, k);
        c.Limb(new Vector2(12f, 39f), new Vector2(7f, 35f), new Vector2(6f, 29f), muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("向下箭头（放到地上）", MuralZone.MainRight);
        c.Arrow(40f, 38f, 40f, 50f, muralLineWidth, 8f, k);
        c.EndElem(null);

        c.BeginElem("落地史莱姆", MuralZone.MainRight);
        c.Slime(40f, 26f, 7f, k);                                         // 放到地上的史莱姆
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");                 // 物品：1 空手
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（放下）");
    }

    /// <summary>H5 跳：起跳的小孩 + 脚下 20px 地面短线（M-L）+ 向上的粗箭头（M-R）+ **空格长条键帽**。
    /// 旧版三个病灶：① 地面线 Box(2,5,61,7) 整条穿进空格键帽；② 键帽里画的是一道 `"⌣"` 弧线（像嘴/山丘，认不出是空格）；
    /// ③ 箭头在左 (x=14)、人物在右 (x=40)、键帽在右下 (47,11) ⇒ 三者互不对齐。
    /// 新版：人物与地面短线住 M-L；箭头住 M-R 且**轴心 x=38 = 空格键帽中心** ⇒ "按空格 = 向上"一条直线读完。</summary>
    static void PaintJump(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("起跳的小孩 + 地面短线", MuralZone.MainLeft);
        c.Box(6, 24, 25, 26, k);                                          // 脚下 20px 地面短线（当作人物元素的一部分，不跨格）
        c.Disc(16f, 44f, 4.5f, k);                                        // 悬空的小孩：头
        c.Stroke(16f, 39f, 16f, 31f, muralLineWidth, k);                  // 脊柱
        c.Limb(new Vector2(16f, 31f), new Vector2(11f, 28f), new Vector2(10f, 31f), muralLineWidth, k);
        c.Limb(new Vector2(16f, 31f), new Vector2(21f, 28f), new Vector2(22f, 31f), muralLineWidth, k);
        c.Limb(new Vector2(16f, 40f), new Vector2(11f, 44f), new Vector2(12f, 50f), muralLineWidth, k);
        c.Limb(new Vector2(16f, 40f), new Vector2(21f, 44f), new Vector2(20f, 50f), muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("向上箭头", MuralZone.MainRight);
        c.Arrow(38f, 26f, 38f, 56f, muralLineWidth, 9f, k);               // 箭头轴 x = 38 ⇒ 正对空格键帽中心
        c.EndElem(null);

        MuralKeyCapSpace(c);                                              // 动作：空格（跳跃）—— 本关唯一玩家无从得知的键
    }

    /// <summary>H6 投掷（按 Q）：小孩前抛姿势（M-L）+ 抛物线虚线（M-R）+ 落地史莱姆（M-R）+ 键帽。
    /// 旧版：`1` 键帽压住双脚/脊柱/胯（包围盒分离规则 ×3）；抛物线从 x=24 起步、**横穿竖缝**且末端与史莱姆贴在一起。
    /// 新版：抛物线整体进 M-R（起点 x=35）、上抬到史莱姆上方、两者留 3px。</summary>
    static void PaintThrow(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（前抛姿势）", MuralZone.MainLeft);
        c.Figure(new Vector2(13f, 45f), 4.5f,                             // 前抛姿势
                 new Vector2(13f, 40f), new Vector2(13f, 30f),
                 new Vector2(6f, 38f), new Vector2(19f, 34f),
                 new Vector2(10f, 26f), new Vector2(16f, 26f),
                 muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("抛物线（虚线）", MuralZone.MainRight);
        c.DashedCurve(MuralParabola(new Vector2(35f, 38f), new Vector2(56f, 36f), 14f, 22),
                      muralLineWidth, 3.2f, 2.2f, k);
        c.EndElem(null);

        c.BeginElem("落地的史莱姆", MuralZone.MainRight);
        c.Slime(50f, 25f, 6f, k);                                         // 抛物线末端的史莱姆
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");                 // 物品：1 空手
        MuralKeyCapRight(c, MuralGlyphQ, "Q 键帽（投掷）");
    }

    /// <summary>R2 复习·投掷：与 H6 **同坐标同构图**（复习才成立），简化 = 史莱姆不画眼睛 + 虚线更疏。</summary>
    static void PaintThrowSimple(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（前抛姿势）", MuralZone.MainLeft);
        c.Figure(new Vector2(13f, 45f), 4.5f,
                 new Vector2(13f, 40f), new Vector2(13f, 30f),
                 new Vector2(6f, 38f), new Vector2(19f, 34f),
                 new Vector2(10f, 26f), new Vector2(16f, 26f),
                 muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("抛物线（虚线更疏）", MuralZone.MainRight);
        c.DashedCurve(MuralParabola(new Vector2(35f, 38f), new Vector2(56f, 36f), 14f, 16),
                      muralLineWidth, 3.6f, 2.6f, k);
        c.EndElem(null);

        c.BeginElem("落地的史莱姆（不画眼睛）", MuralZone.MainRight);
        c.Slime(50f, 25f, 6f, k, false);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");                 // 与 H6 一致，复习才成立
        MuralKeyCapRight(c, MuralGlyphQ, "Q 键帽（投掷）");
    }

    /// <summary>H7 静止（Stay）↔ 跟随（Follow）：**左右两态对比**（左 = 停 / 右 = 跟），只保留 `2` + `E` 两个键帽。
    /// ⚠ 旧版 9 个元素、5 处 bbox 相交，读不出两态；而且用"两个 E + 第二个加圈"表达"按两次"是**语义错误**：
    ///   `PlayerGrab.cs:106-109`（哨子 → ToggleMode）是**按一次切一次**。本轮借"减元素"把它删掉。
    /// 判据（规格）：缩到 16×16 后能数出左右各 2 个团块，且左右记号**形状不同**（左 = 实心横杠 / 右 = 尖朝左的箭头）。</summary>
    static void PaintStay(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("左态：史莱姆（停住）", MuralZone.MainLeft);
        c.Slime(16f, 28f, 8f, k);
        c.EndElem(null);

        c.BeginElem("左态：停记号（实心横杠）", MuralZone.MainLeft);
        c.Box(12, 41, 20, 46, k);                                         // 9×6（规格写 9×5，那过不了最小尺寸规则的 min≥6）
        c.EndElem(null);

        c.BeginElem("右态：史莱姆（跟随）", MuralZone.MainRight);
        c.Slime(48f, 28f, 8f, k);
        c.EndElem(null);

        c.BeginElem("右态：跟记号（尖朝左的箭头）", MuralZone.MainRight);
        c.Arrow(56f, 45f, 44f, 45f, muralLineWidth, 7f, k);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph2, "2 键帽（哨子）");
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（切换）");
    }

    /// <summary>H8 召回（`2` 哨子 + `E`）：小孩（M-L）+ 三连箭头（M-R，指向小孩）+ 史莱姆（M-R）+ 键帽。
    /// ⚠ 这里标的**不是** `Q` —— `PlayerGrab.cs:133-138` 写明 `Q` + 哨子 = **冲刺**；"召回" = `2` + `E`（`ToggleMode`）。
    /// 旧版三条箭头 x[26,38] 横穿竖缝、`E` 键帽 x 贴到 63（留白/分区越界）⇒ 本轮整体镜像：小孩在左、箭头在右指向他。</summary>
    static void PaintRecall(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（召回目标）", MuralZone.MainLeft);
        c.Figure(new Vector2(14f, 45f), 4.5f,
                 new Vector2(14f, 40f), new Vector2(14f, 30f),
                 new Vector2(10f, 36f), new Vector2(18f, 36f),
                 new Vector2(11f, 26f), new Vector2(17f, 26f),
                 muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("三连箭头（被拉回去）", MuralZone.MainRight);
        c.Arrow(58f, 30f, 46f, 30f, muralLineWidth, 7f, k);
        c.Arrow(58f, 37f, 46f, 37f, muralLineWidth, 7f, k);
        c.Arrow(58f, 44f, 46f, 44f, muralLineWidth, 7f, k);
        c.EndElem(null);

        c.BeginElem("史莱姆", MuralZone.MainRight);
        c.Slime(52f, 52f, 8f, k);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph2, "2 键帽（哨子）");
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（召回）");
    }

    /// <summary>H9 放引导石（`3` 选石，`E` 放置）：菱形石头 + 左右两段虚线路径（M-L）+ 三个小点（M-R）+ 键帽。
    /// 旧版：菱形 x[11,29] 与 `3` 键帽 (r=11) 实际像素重叠，右虚线 x[30,39] 跨竖缝。新版：菱形下移进 M 带、
    /// 左虚线缩到石头左侧（间隔 ≥2px）、右虚线整体进 M-R、三个小点搬到 M-R 上方（元素数仍为 6）。</summary>
    static void PaintWaypointSet(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("虚线路径（石头左侧）", MuralZone.MainLeft);
        c.DashedStroke(3f, 36f, 7f, 36f, muralLineWidth, 3.2f, 2.2f, k);
        c.EndElem(null);

        c.BeginElem("菱形引导石", MuralZone.MainLeft);
        c.Diamond(20f, 36f, 9f, 11f, k);
        c.EndElem(null);

        c.BeginElem("三个小点（最多 3 颗石头）", MuralZone.MainRight);
        c.Disc(38f, 54f, 2.5f, k);
        c.Disc(45f, 54f, 2.5f, k);
        c.Disc(52f, 54f, 2.5f, k);
        c.EndElem(null);

        c.BeginElem("虚线路径（石头右侧）", MuralZone.MainRight);
        c.DashedStroke(35f, 36f, 43f, 36f, muralLineWidth, 3.2f, 2.2f, k);
        c.EndElem(null);

        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（放路点）");
        MuralKeyCapLeft(c, MuralGlyph3, "3 键帽（引导石）");               // 物品：3 引导石
    }

    /// <summary>H10 撤石（按 Q）：**规格里的候选 A 方案** —— 菱形引导石 + 一条**向左**的回收箭头。
    /// ⛔ 旧版画的是"虚线路径 + 对角大斜杠 + 横杠"（通用读感 = ❌ 禁止 / 取消），与"撤销一次"不是一回事
    ///   （用户原话："H10 很难表达出我们想要的意思"）。
    ///   语义（`PlayerGrab.cs:141-145`）：切 `3` 引导石、按 `Q` = **只撤销最后一个路点并返还 1 颗石头**，不是清空。
    ///   新版用**箭头方向**表达"收回"：石头在左、箭头从右往左指向它，与 H9（放石头）形成一放一收的对偶。</summary>
    static void PaintWaypointClear(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("菱形引导石", MuralZone.MainLeft);
        c.Diamond(16f, 38f, 8f, 10f, k);
        c.EndElem(null);

        c.BeginElem("向左的回收箭头", MuralZone.MainRight);
        c.Arrow(56f, 36f, 34f, 36f, muralLineWidth, 8f, k);
        c.EndElem(null);

        MuralKeyCapRight(c, MuralGlyphQ, "Q 键帽（收回最后一颗石头）");
        MuralKeyCapLeft(c, MuralGlyph3, "3 键帽（引导石）");               // 物品：3 引导石
    }

    /// <summary>H11 金币：史莱姆（M-L）+ 斜向下的箭头（M-L）+ 金币（M-R）。
    /// ⚠ 旧版是"史莱姆 → 竖直箭头 → 金币"三层竖直排，三样都在同一格 ⇒ 要么堆到元素数上限、要么越界。
    ///   新版把金币挪到 M-R、箭头改成指向它（语义不变：史莱姆换金币），缝里全空。</summary>
    static void PaintCoin(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("史莱姆", MuralZone.MainLeft);
        c.Slime(18f, 52f, 9f, k);                                         // 箭头起点的史莱姆
        c.EndElem(null);

        c.BeginElem("向下箭头", MuralZone.MainLeft);
        c.Arrow(24f, 46f, 26f, 32f, muralLineWidth, 8f, k);               // 指向右下角的金币
        c.EndElem(null);

        c.BeginElem("金币", MuralZone.MainRight);
        c.Disc(41f, 32f, 8f, k);                                          // 金币
        c.ClearRing(41f, 32f, 5f, 1.6f);                                  // 币面内圈（抠出）
        c.EndElem(null);
    }

    /// <summary>H12 回血：一颗心（M-L）+ 一个加号（M-R）。</summary>
    static void PaintHeal(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("心（回血）", MuralZone.MainLeft);
        MuralHeart(c, 16f, 38f, 15f, k);                                  // 心
        c.EndElem(null);

        c.BeginElem("加号", MuralZone.MainRight);
        c.Box(39, 34, 59, 40, k);                                         // 加号：横
        c.Box(46, 26, 52, 48, k);                                         // 加号：竖
        c.EndElem(null);
    }

    /// <summary>H13 收购站：一栋房子（屋檐 + 摊位算**一个**元素 —— 它们本来就相接，拆成两个过不了同格间距规则）+ 一枚金币。
    /// ⚠ 旧版房子 x[4,44] 横跨竖缝、金币 x[42,58] 与房子 x 向重叠 2px（包围盒分离违规）⇒ 房子收进 M-L、金币挪到 M-R。</summary>
    static void PaintShop(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("收购站（屋檐 + 摊位）", MuralZone.MainLeft);
        c.BoxOutline(4, 24, 30, 44, muralLineWidth, k);                   // 摊位主体
        c.Triangle(new Vector2(17f, 58f),                                  // 屋顶
                   new Vector2(3f, 44f), new Vector2(30f, 44f), k);
        c.ClearBox(10, 30, 24, 38);                                       // 柜台窗口
        c.EndElem(null);

        c.BeginElem("金币", MuralZone.MainRight);
        c.Disc(48f, 34f, 8f, k);                                          // 金币
        c.ClearRing(48f, 34f, 5.5f, 1.5f);
        c.EndElem(null);
    }

    // =================================================================================
    //  五之二、物品栏 5 张（N1~N5）：四张单格拼成一条 + 一张"哨子：按 2 切到哨子、按 E 让史莱姆站住"
    //  出处：壁画规格（方案 B）。⚠ 顺序必须与 Level0.json 的
    //  murals 数组逐条一致（H1..H13, R1, R2, N1..N5 = 20 条）。
    // =================================================================================

    /// <summary>N 底槽：L 底槽带（四张单格拼成一条）。
    /// ⚠ 两个坑：① 旧版 x[0,63] 贴画布两边（留白规则）⇒ 退到留白内；
    ///   ② 规格把 L 带写成 y[2,3]、K-L 槽写成 y[4,20]，那两者**紧挨着（0px）**，过不了防重叠规则的"分离 ≥1px"
    ///   ⇒ 这里只画 **y=2 那一行**（仍在 L 带内）留出 1px 缝；语义不变（还是"四张拼成同一条物品栏"的记号）。</summary>
    static void PaintItemBarSlot(MuralCanvas c)
    {
        c.BeginElem("底槽（四张拼成一条物品栏）", MuralZone.BottomSlot);
        c.Box(muralSafeMargin, muralSlotY0, muralSize - 1 - muralSafeMargin, muralSlotY0, muralInkColor);
        c.EndElem(null);
    }

    /// <summary>N1 物品栏第 1 格 = 空手：张开的手掌（M-R）+ `1` 键帽（K-L）+ 底槽（L 带）。
    /// ⚠ 旧版手掌 x[22.5,57] 横跨竖缝、拇指压住 `1` 键帽（实际像素重叠）⇒ 本轮整只手收进 M-R。</summary>
    static void PaintItemBar1(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        PaintItemBarSlot(c);

        c.BeginElem("手掌（空手）", MuralZone.MainRight);
        c.Disc(50f, 35f, 10f, k);                                         // 掌根
        c.Stroke(41f, 36f, 39f, 54f, muralLineWidth, k);                  // 四指
        c.Stroke(47f, 36f, 46f, 58f, muralLineWidth, k);
        c.Stroke(53f, 36f, 54f, 57f, muralLineWidth, k);
        c.Stroke(58f, 36f, 60f, 52f, muralLineWidth, k);
        c.Stroke(45f, 32f, 36f, 36f, muralLineWidth, k);                  // 拇指
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph1, "1 键帽（空手）");
    }

    /// <summary>N2 物品栏第 2 格 = 哨子：圆头 + 管身 + 声波（M-R）+ `2` 键帽（K-L）+ 底槽。
    /// ⚠ 旧版圆头 x[25,47] 与挂绳一起横跨竖缝 ⇒ 本轮**删掉挂绳环**（元素 −1，符合"宁可少画"）、整只哨子收进 M-R。</summary>
    static void PaintItemBar2(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        PaintItemBarSlot(c);

        c.BeginElem("哨子", MuralZone.MainRight);
        c.Disc(46f, 38f, 11f, k);                                         // 圆头
        c.Box(46, 31, 58, 45, k);                                         // 管身
        c.ClearBox(54, 34, 58, 42);                                       // 抠出吹嘴
        c.ClearDisc(46f, 49f, 3f);                                        // 气孔
        c.Stroke(51f, 52f, 58f, 57f, muralLineWidth, k);                  // 声波两条
        c.Stroke(51f, 46f, 58f, 44f, muralLineWidth, k);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph2, "2 键帽（哨子）");
    }

    /// <summary>N3 物品栏第 3 格 = 引导石：菱形石头（与 H9 / H10 同一形状）+ 十字光纹（M-R）+ `3` 键帽 + 底槽。
    /// ⚠ 旧版菱形 x[31,49] 跨竖缝 ⇒ 本轮右移进 M-R（十字光纹同步右移）。</summary>
    static void PaintItemBar3(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        PaintItemBarSlot(c);

        c.BeginElem("引导石（菱形）", MuralZone.MainRight);
        c.Diamond(46f, 38f, 9f, 11f, k);                                  // 同一物品必须同一形状：H9/H10 用的就是菱形
        c.ClearBox(45, 35, 47, 41);                                       // 十字光纹（抠出）
        c.ClearBox(42, 37, 50, 39);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph3, "3 键帽（引导石）");
    }

    /// <summary>N4 物品栏第 4 格 = 预留（方案 4-A）：空心方框 + 抠出的 `?`（与 HUD 的 Icon_Slot4 同形）+ `4` 键帽 + 底槽。
    /// ⚠ 旧版方框 x[30,58] 跨竖缝、下沿 y=20 侵入缝 y[21,23] ⇒ 本轮方框整体进 M-R。
    /// ⚠ 第 4 格内容一旦定了（例如"跳跃云朵瓶"），**只换这一个函数体**，坐标/JSON/接线都不用动。</summary>
    static void PaintItemBar4(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        PaintItemBarSlot(c);

        c.BeginElem("预留框（?）", MuralZone.MainRight);
        c.BoxOutline(36, 26, 58, 50, muralLineWidth, k);                  // 空心方框
        MuralCarveGlyph(c, 47f, 38f, MuralGlyphQuestion, muralBadgeGlyphPixel);   // 抠出 `?`
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph4, "4 键帽（预留）");
    }

    /// <summary>N5 哨子：按 `2` 切到哨子（物品栏第 2 格 = 哨子）→ 按 `E` 让史莱姆**站在原地**（跟随 ↔ 待命）。
    /// 画面语言：**暂停记号（两条竖杠：左 `Box(41,38,44,53)` + 右 `Box(50,38,53,53)`）= 定住/别动** —— 直读的"⏸"；
    ///   两条杆 + 中间 5px 空隙**登记成同一个元素**（所以"元素最小尺寸 ≥6"判的是整体 13×16，不是单根杆宽 4）。
    /// 与 H7 的区别：H7 是**状态记号的左右两态对照**（左"停"实心横杠 / 右"跟"箭头）；N5 是**一个直读的暂停符**，讲"按 E = 定住"。布局：人物住 M-L，暂停记号与落地史莱姆住 M-R（同格间隔 6px）。旧版违规已修（`1` 键帽 r=11 压住落地史莱姆、开口环与 `E` 键帽相切）。</summary>
    static void PaintItemBar5(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("小孩（空手、双臂下垂）", MuralZone.MainLeft);
        c.Disc(14f, 44f, 4.5f, k);                                        // 头
        c.Stroke(14f, 39f, 14f, 30f, muralLineWidth, k);                  // 脊柱
        c.Stroke(14f, 30f, 11f, 26f, muralLineWidth, k);                  // 两条腿
        c.Stroke(14f, 30f, 17f, 26f, muralLineWidth, k);
        c.Stroke(14f, 38f, 10f, 32f, muralLineWidth, k);                  // 两臂自然下垂（不是"举着"）
        c.Stroke(14f, 38f, 18f, 32f, muralLineWidth, k);
        c.EndElem(null);

        c.BeginElem("暂停记号（两条竖杠）", MuralZone.MainRight);
        c.Box(41, 38, 44, 53, k);                                         // 左杆 4×16
        c.Box(50, 38, 53, 53, k);                                         // 右杆 4×16（等宽等高、关于 x=47 对称；中间 x[45,49] 留 5px 空隙）
        c.EndElem(null);

        c.BeginElem("落地史莱姆", MuralZone.MainRight);
        c.Slime(46f, 25f, 6f, k);
        c.EndElem(null);

        MuralKeyCapLeft(c, MuralGlyph2, "2 键帽（切到哨子）");              // 先切到哨子那一格
        MuralKeyCapRight(c, MuralGlyphE, "E 键帽（让史莱姆站住）");         // 再按 E 让它站住
    }

    // =================================================================================
    //  五之三、防重叠自检（规格的防重叠硬规则 + 验收项）
    //
    //  为什么要有它：这一轮的毛病（用户原话"画面中的各个元素之间相互重叠，导致图片不清晰"）
    //  肉眼要盯着 20 张图才看得出来，而且"改一张坏一张"特别容易。
    //  所以把规格里的硬规则做成**代码能自证**的东西：每个元素在画的时候就登记了自己的
    //  **真实不透明像素包围盒**（不是"我以为画到哪"），生成末尾两两算间隔、逐条对判据。
    //  日志形如：`[壁画自检] H7 元素=6（K 2 / M-L 2 / M-R 2） 最小间隔=3px（…）✅`
    // =================================================================================

    /// <summary>分区 → 该格的像素矩形（含端点）。</summary>
    static void MuralZoneRect(MuralZone z, out int x0, out int y0, out int x1, out int y1)
    {
        switch (z)
        {
            case MuralZone.KeyLeft:
                x0 = muralKeyLeftX0; y0 = muralKeyBandY0; x1 = muralKeyLeftX1; y1 = muralKeyBandY1; break;
            case MuralZone.KeyRight:
                x0 = muralKeyRightX0; y0 = muralKeyBandY0; x1 = muralKeyRightX1; y1 = muralKeyBandY1; break;
            case MuralZone.SpaceCap:
                x0 = muralSpaceCapX0; y0 = muralSpaceCapY0; x1 = muralSpaceCapX1; y1 = muralSpaceCapY1; break;
            case MuralZone.BottomSlot:
                x0 = muralSafeMargin; y0 = muralSlotY0;
                x1 = muralSize - 1 - muralSafeMargin; y1 = muralSlotY1; break;
            case MuralZone.MainLeft:
                x0 = muralMainLeftX0; y0 = muralMainY0; x1 = muralMainLeftX1; y1 = muralMainY1; break;
            default:
                x0 = muralMainRightX0; y0 = muralMainY0; x1 = muralMainRightX1; y1 = muralMainY1; break;
        }
    }

    /// <summary>"格"的编号：同格间距规则（同格 ≥2px）只在同一格里要求。K 带算一格（键帽之间本来就不挨着）。</summary>
    static int MuralZoneGrid(MuralZone z)
    {
        if (z == MuralZone.KeyLeft || z == MuralZone.KeyRight || z == MuralZone.SpaceCap) return 0;   // K 带
        if (z == MuralZone.BottomSlot) return 1;                                                     // L 带
        return 2 + (int)z;                                                                           // M-L(6) / M-R(7) 各自一格
    }

    /// <summary>两个元素的分离度 = max(x 向空隙, y 向空隙)；**负数 = 相交**（-1 = 紧挨着）。</summary>
    static int MuralGap(MuralElement a, MuralElement b)
    {
        int gx = Mathf.Max(a.minX, b.minX) - Mathf.Min(a.maxX, b.maxX) - 1;
        int gy = Mathf.Max(a.minY, b.minY) - Mathf.Min(a.maxY, b.maxY) - 1;
        return Mathf.Max(gx, gy);
    }

    static string MuralRectText(MuralElement e)
    {
        return e.IsEmpty ? "(空)" : string.Format("x[{0},{1}] y[{2},{3}]", e.minX, e.maxX, e.minY, e.maxY);
    }

    /// <summary>
    /// 跑一张图的防重叠自检。<paramref name="lines"/> 收"给人看的证据行"，<paramref name="bad"/> 收失败项。
    /// 返回 true = 全部硬规则通过。
    /// </summary>
    public static bool MuralCheckLayout(string id, MuralCanvas c, List<string> lines, List<string> bad)
    {
        int elemCount = c.elements.Count;
        int keyCount = 0, mainLeft = 0, mainRight = 0, spaceCount = 0;
        int minGap = int.MaxValue;
        string minGapWho = "-";

        // ---- 留白 ----
        int bx0, by0, bx1, by1;
        if (!c.OpaqueBBox(out bx0, out by0, out bx1, out by1))
        {
            bad.Add("留白规则：整张图一个不透明像素都没有");
        }
        else
        {
            int lo = muralSafeMargin, hi = muralSize - 1 - muralSafeMargin;
            if (bx0 < lo || bx1 > hi || by0 < lo || by1 > hi)
                bad.Add(string.Format("留白规则 越出留白：全图 bbox x[{0},{1}] y[{2},{3}]，要求 ⊂ [{4},{5}]",
                    bx0, bx1, by0, by1, lo, hi));
        }

        // ---- 两条缝必须全透明 ----
        int seamH = c.OpaqueCountIn(0, muralSeamY0, c.w - 1, muralSeamY1);
        if (seamH > 0)
            bad.Add(string.Format("横缝规则 y[{0},{1}] 里有 {2} 个不透明像素（要求 0）", muralSeamY0, muralSeamY1, seamH));
        int seamV = c.OpaqueCountIn(muralSeamX0, muralMainY0, muralSeamX1, muralMainY1);
        if (seamV > 0)
            bad.Add(string.Format("竖缝规则 x[{0},{1}] × y[{2},{3}] 里有 {4} 个不透明像素（要求 0）",
                muralSeamX0, muralSeamX1, muralMainY0, muralMainY1, seamV));

        // ---- 逐元素：分区 / 计数 / 尺寸 / 键帽 / 空格长条 ----
        for (int i = 0; i < elemCount; i++)
        {
            MuralElement e = c.elements[i];
            if (e.IsEmpty)
            {
                bad.Add("分区规则 元素是空的（没画出一个不透明像素）：" + e.name);
                continue;
            }

            int zx0, zy0, zx1, zy1;
            MuralZoneRect(e.zone, out zx0, out zy0, out zx1, out zy1);
            if (e.minX < zx0 || e.maxX > zx1 || e.minY < zy0 || e.maxY > zy1)
                bad.Add(string.Format("分区规则 跨格：{0} {1} 超出它声明的分区 x[{2},{3}] y[{4},{5}]",
                    e.name, MuralRectText(e), zx0, zx1, zy0, zy1));

            switch (e.zone)
            {
                case MuralZone.KeyLeft: keyCount++; break;
                case MuralZone.KeyRight: keyCount++; break;
                case MuralZone.SpaceCap: keyCount++; spaceCount++; break;
                case MuralZone.MainLeft: mainLeft++; break;
                case MuralZone.MainRight: mainRight++; break;
            }

            // 最小尺寸：min(宽,高) ≥ 6；**线状元素**（虚线路径）按规格第二句放行 —— 薄的那一维就是标准线宽。
            int mn = Mathf.Min(e.Width, e.Height);
            int mx = Mathf.Max(e.Width, e.Height);
            bool slotExempt = e.zone == MuralZone.BottomSlot && muralSlotExemptFromMinSize;
            bool lineOk = mn >= muralLineWidth && mx >= muralMinElemSize;
            if (!slotExempt && mn < muralMinElemSize && !lineOk)
                bad.Add(string.Format("最小尺寸规则 元素太小：{0} 是 {1}×{2}（要求 min≥{3}，或线宽≥{4} 且长边≥{3}）",
                    e.name, e.Width, e.Height, muralMinElemSize, muralLineWidth));

            if (e.zone == MuralZone.KeyLeft || e.zone == MuralZone.KeyRight)
            {
                int rr = Mathf.RoundToInt(muralBadgeRadius);
                int side = 2 * rr + 1;
                int ccx = e.zone == MuralZone.KeyLeft ? Mathf.RoundToInt(muralBadgeLeftX) : Mathf.RoundToInt(muralBadgeRightX);
                int ccy = Mathf.RoundToInt(muralBadgeCy);
                if (!e.isKeyCap)
                    bad.Add("键帽规则 键帽槽里的元素没走 MuralKeyCap 登记：" + e.name);
                if (e.Width != side || e.Height != side)
                    bad.Add(string.Format("键帽规则 键帽直径不是 {0}px：{1} 是 {2}×{3}", side, e.name, e.Width, e.Height));
                if (e.minX + e.maxX != 2 * ccx || e.minY + e.maxY != 2 * ccy)
                    bad.Add(string.Format("键帽规则 键帽圆心不是 ({0},{1})：{2} 在 ({3:0.#},{4:0.#})",
                        ccx, ccy, e.name, (e.minX + e.maxX) * 0.5f, (e.minY + e.maxY) * 0.5f));

                int discN = 0;
                for (int dy = -rr; dy <= rr; dy++)
                    for (int dx = -rr; dx <= rr; dx++)
                        if (dx * dx + dy * dy <= rr * rr) discN++;
                int dots = e.glyphDots < 0 ? 0 : e.glyphDots;
                int got = c.OpaqueCountIn(e.minX, e.minY, e.maxX, e.maxY);
                if (got != discN - dots)
                    bad.Add(string.Format("键帽像素区间 键帽像素数不对：{0} 实际 {1}，期望 {2}（圆 {3} − 字形 {4}）",
                        e.name, got, discN - dots, discN, dots));
            }

            if (e.zone == MuralZone.SpaceCap)
            {
                int wantW = muralSpaceCapX1 - muralSpaceCapX0 + 1;
                int wantH = muralSpaceCapY1 - muralSpaceCapY0 + 1;
                if (Mathf.Abs(e.Width - wantW) > 1 || Mathf.Abs(e.Height - wantH) > 1)
                    bad.Add(string.Format("空格长条规则 不是 {0}×{1}：{2} 是 {3}×{4}", wantW, wantH, e.name, e.Width, e.Height));
                if (e.minX + e.maxX != muralSpaceCapX0 + muralSpaceCapX1)
                    bad.Add(string.Format("空格长条规则 没居中：{0} x[{1},{2}]，期望 x[{3},{4}]",
                        e.name, e.minX, e.maxX, muralSpaceCapX0, muralSpaceCapX1));
                int barX0 = muralSpaceCapX0 + (wantW - muralSpaceBarW) / 2;
                int barY0 = muralSpaceCapY0 + (wantH - muralSpaceBarH) / 2;
                int holes = c.OpaqueCountIn(barX0, barY0, barX0 + muralSpaceBarW - 1, barY0 + muralSpaceBarH - 1);
                if (holes != 0)
                    bad.Add(string.Format("空格长条规则 中间的横杠没抠出来：{0}×{1} 那块里有 {2} 个不透明像素",
                        muralSpaceBarW, muralSpaceBarH, holes));
            }
        }

        // ---- 元素数上限 ----
        if (keyCount > muralMaxKeyCaps)
            bad.Add(string.Format("元素数上限 K 带键帽 {0} 个（上限 {1}）", keyCount, muralMaxKeyCaps));
        if (spaceCount > 1)
            bad.Add(string.Format("元素数上限 空格长条应独占 K 带，现在有 {0} 条", spaceCount));
        if (mainLeft > muralMaxElemsPerSide)
            bad.Add(string.Format("元素数上限 M-L 有 {0} 个元素（上限 {1}）", mainLeft, muralMaxElemsPerSide));
        if (mainRight > muralMaxElemsPerSide)
            bad.Add(string.Format("元素数上限 M-R 有 {0} 个元素（上限 {1}）", mainRight, muralMaxElemsPerSide));
        if (elemCount > muralMaxElemsTotal)
            bad.Add(string.Format("元素数上限 全张 {0} 个元素（上限 {1}）", elemCount, muralMaxElemsTotal));

        // ---- 两两包围盒间隔 ----
        for (int i = 0; i < elemCount; i++)
        {
            MuralElement a = c.elements[i];
            if (a.IsEmpty) continue;
            for (int j = i + 1; j < elemCount; j++)
            {
                MuralElement b = c.elements[j];
                if (b.IsEmpty) continue;
                int gap = MuralGap(a, b);
                if (gap < minGap) { minGap = gap; minGapWho = a.name + " ↔ " + b.name; }
                if (gap < muralMinElemGap)
                    bad.Add(string.Format("包围盒分离规则 包围盒相交：{0} {1} ↔ {2} {3}（分离 {4}px，要求 ≥{5}）",
                        a.name, MuralRectText(a), b.name, MuralRectText(b), gap, muralMinElemGap));
                else if (MuralZoneGrid(a.zone) == MuralZoneGrid(b.zone) && gap < muralMinSameZoneGap)
                    bad.Add(string.Format("同格间距规则 只隔 {0}px：{1} ↔ {2}（要求 ≥{3}）",
                        gap, a.name, b.name, muralMinSameZoneGap));
            }
        }

        // ---- 覆盖率（沿用既有的 muralMinCoverage / muralMaxCoverage）----
        float cov = c.OpaqueRatio();
        if (cov < muralMinCoverage || cov > muralMaxCoverage)
            bad.Add(string.Format("覆盖率规则 {0:F1}% 超出 [{1:F0}%,{2:F0}%]", cov * 100f, muralMinCoverage * 100f, muralMaxCoverage * 100f));

        lines.Add(string.Format("{0} 元素={1}（键帽 {2} / M-L {3} / 空格 {4} / M-R {5}） 最小间隔={6}px（{7}） 覆盖率={8:F1}% {9}",
            id, elemCount, keyCount, mainLeft, spaceCount, mainRight,
            minGap == int.MaxValue ? -1 : minGap, minGapWho, cov * 100f, bad.Count == 0 ? "✅" : "❌"));
        return bad.Count == 0;
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

    /// <summary>空格键帽的半宽/半高（px）与中间"横杠"的尺寸 —— ⚠ 已废弃，改由
    /// <see cref="muralSpaceCapX0"/> 等 bbox 字段与 <see cref="muralSpaceBarW"/> 定义（规格）。
    /// 保留这两个字段只为兼容可能还在读它的旧调用（当前文件内已无引用）。</summary>
    public static int muralSpaceCapHalfW = 17;
    public static int muralSpaceCapHalfH = 6;

    /// <summary>
    /// 空格键帽（规格）：**圆角长条 34×13 + 中间一条 10×2 水平横杠**。
    /// 为什么这样一眼能认出是空格（三点，缺一不可）：
    ///   ① 形状唯一 —— 全 20 张里只有它是"长条"，其余键帽都是直径 17px 的圆；
    ///   ② 符号正确 —— 长条中间一道横杠是"空白/空格"（⎵）的通识写法；
    ///      ⛔ 旧版那条 `"⌣"` 弧线必须删掉：实测读感是"嘴 / 山丘"，反而认不出是空格键；
    ///   ③ 位置直读 —— 长条在底部中轴、正上方就是向上箭头（H5 的箭头轴 = 键帽中心 x）。
    /// </summary>
    static void MuralKeyCapSpace(MuralCanvas c)
    {
        Color32 k = muralInkColor;

        c.BeginElem("空格长条键帽", MuralZone.SpaceCap);

        c.Box(muralSpaceCapX0, muralSpaceCapY0, muralSpaceCapX1, muralSpaceCapY1, k);
        c.Clear(muralSpaceCapX0, muralSpaceCapY0);                 // 削四个角 ⇒ 圆角长条
        c.Clear(muralSpaceCapX1, muralSpaceCapY0);
        c.Clear(muralSpaceCapX0, muralSpaceCapY1);
        c.Clear(muralSpaceCapX1, muralSpaceCapY1);

        int w = muralSpaceCapX1 - muralSpaceCapX0 + 1;             // 34
        int h = muralSpaceCapY1 - muralSpaceCapY0 + 1;             // 13
        int bw = Mathf.Max(1, muralSpaceBarW), bh = Mathf.Max(1, muralSpaceBarH);
        int bx0 = muralSpaceCapX0 + (w - bw) / 2;                  // 33 ⇒ 横杠 x[33,42]
        int by0 = muralSpaceCapY0 + (h - bh) / 2;                  // 11 ⇒ 横杠 y[11,12]
        c.ClearBox(bx0, by0, bx0 + bw - 1, by0 + bh - 1);          // 中间 10×2 横杠

        c.EndElem(null);
        c.MarkLastElemAsSpaceCap();
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

/// <summary>一张壁画里"元素"允许待的分区（壁画规格的三带 + 一条竖缝）。
/// 骨架布局（像素区间全部由 MuralGenerator 的 public 字段给出）：
///   L 底槽带 y[2,3]（仅 N1~N4）｜K 键帽带 y[4,20]（K-L / K-R / 空格长条）｜缝 y[21,23] 必须空｜
///   M 主图带 y[24,61]，被竖缝 x[31,32] 分成 M-L 与 M-R。</summary>
public enum MuralZone
{
    BottomSlot = 0,   // L 底槽带
    KeyLeft = 1,      // K-L 槽（物品键帽，圆心 (11,12)）
    KeyRight = 2,     // K-R 槽（动作键帽，圆心 (52,12)）
    SpaceCap = 3,     // 空格长条键帽（整条都在 K 带里，允许横跨竖缝 x[31,32]）
    MainLeft = 4,     // M-L 主图左格 x[2,30]
    MainRight = 5,    // M-R 主图右格 x[33,61]
}

/// <summary>一个"元素"的实际不透明像素包围盒（含端点）。防重叠自检按它算各项规则与验收项。</summary>
public class MuralElement
{
    public string name = "";
    public MuralZone zone = MuralZone.MainLeft;

    /// <summary>是不是圆键帽（验收时要按规格核对 bbox = 17×17 与圆心）。</summary>
    public bool isKeyCap;

    /// <summary>键帽字形点阵里 '#' 的个数（-1 = 没有字形）。验收项用它核对"抠掉几个像素"。</summary>
    public int glyphDots = -1;

    /// <summary>是不是空格长条键帽（单独核对 34×13 + 中间横杠）。</summary>
    public bool isSpaceCap;

    public int minX, minY, maxX, maxY;

    public int Width { get { return maxX - minX + 1; } }
    public int Height { get { return maxY - minY + 1; } }

    /// <summary>空元素（一个不透明像素都没有）—— 说明画法函数写错了。</summary>
    public bool IsEmpty { get { return maxX < minX || maxY < minY; } }
}

public class MuralCanvas
{
    public readonly int w;
    public readonly int h;

    readonly Color32[] _px;

    // ------------------------------ 元素登记（防重叠自检，规格） ------------------------------
    //
    // "元素" = 一个能数出来的团块（键帽 / 人物 / 史莱姆 / 一条箭头链 / 网状记号…）。
    // 画法函数在每个元素前后各调一次 BeginElem / EndElem，EndElem 时把**真实不透明像素**收紧成包围盒。

    /// <summary>本张图登记过的所有元素（按登记顺序）。</summary>
    public readonly List<MuralElement> elements = new List<MuralElement>();

    MuralElement _cur;
    int _tx0, _ty0, _tx1, _ty1;

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
        // 元素登记的"碰过的范围"：只记**不透明**像素（抠洞用的是透明色，不算进包围盒）
        if (_cur != null && c.a > 8)
        {
            if (x < _tx0) _tx0 = x;
            if (y < _ty0) _ty0 = y;
            if (x > _tx1) _tx1 = x;
            if (y > _ty1) _ty1 = y;
        }
    }

    /// <summary>抠掉一个像素（= 写透明，让底板颜色透出来）。</summary>
    public void Clear(int x, int y)
    {
        Px(x, y, MuralGenerator.muralClearColor);
    }

    /// <summary>开始登记一个元素：从这一刻到 EndElem 之间画的**不透明**像素都算它的。</summary>
    public void BeginElem(string name, MuralZone zone)
    {
        _cur = new MuralElement();
        _cur.name = name == null ? "" : name;
        _cur.zone = zone;
        _tx0 = int.MaxValue; _ty0 = int.MaxValue;
        _tx1 = int.MinValue; _ty1 = int.MinValue;
    }

    /// <summary>结束登记：把"碰过的范围"收紧成**真实不透明像素**的包围盒（字形抠的洞不影响外壳）。
    /// glyph = 该元素的键帽字形（null ⇒ 不是键帽）。</summary>
    public void EndElem(string[] glyph)
    {
        if (_cur == null) return;

        _cur.minX = 1; _cur.minY = 1; _cur.maxX = 0; _cur.maxY = 0;      // 默认 = 空
        if (_tx0 <= _tx1 && _ty0 <= _ty1)
        {
            int mx0 = int.MaxValue, my0 = int.MaxValue, mx1 = int.MinValue, my1 = int.MinValue;
            for (int y = _ty0; y <= _ty1; y++)
            {
                for (int x = _tx0; x <= _tx1; x++)
                {
                    if (!Inside(x, y)) continue;
                    if (_px[y * w + x].a <= 8) continue;
                    if (x < mx0) mx0 = x;
                    if (y < my0) my0 = y;
                    if (x > mx1) mx1 = x;
                    if (y > my1) my1 = y;
                }
            }
            if (mx0 <= mx1 && my0 <= my1)
            {
                _cur.minX = mx0; _cur.minY = my0; _cur.maxX = mx1; _cur.maxY = my1;
            }
        }

        if (glyph != null)
        {
            _cur.isKeyCap = true;
            int dots = 0;
            for (int i = 0; i < glyph.Length; i++)
            {
                string row = glyph[i];
                if (row == null) continue;
                for (int j = 0; j < row.Length; j++) if (row[j] == '#') dots++;
            }
            _cur.glyphDots = dots;
        }

        elements.Add(_cur);
        _cur = null;
    }

    /// <summary>把刚登记的那个元素标成"空格长条键帽"（单独核对 34×13 + 中间横杠）。</summary>
    public void MarkLastElemAsSpaceCap()
    {
        if (elements.Count == 0) return;
        elements[elements.Count - 1].isSpaceCap = true;
    }

    /// <summary>整张图的不透明像素总数（自检用）。</summary>
    public int OpaqueCount()
    {
        int n = 0;
        for (int i = 0; i < _px.Length; i++) if (_px[i].a > 8) n++;
        return n;
    }

    /// <summary>某个矩形里有多少个不透明像素（自检用：缝带 / 键帽区域）。</summary>
    public int OpaqueCountIn(int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Inside(x, y) && _px[y * w + x].a > 8) n++;
        return n;
    }

    /// <summary>整张图的不透明像素包围盒（返回 false = 一个像素都没有）。</summary>
    public bool OpaqueBBox(out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = int.MaxValue; minY = int.MaxValue; maxX = int.MinValue; maxY = int.MinValue;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (_px[y * w + x].a <= 8) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX >= minX && maxY >= minY;
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
