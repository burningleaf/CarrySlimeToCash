// ---------------------------------------------------------------------------
// GoalStationGenerator.cs —— 用代码画「史莱姆收购站」（= 关卡终点 Goal）的整张贴图（Editor only）
//
// 用户要的东西（原话）：
//   "把我们收购站的贴图（即关卡终点）做出来并把贴图关联上，贴图风格为，木制小屋，里面有人收货，
//    看起来里面卖的是史莱姆制品，终点的房子上写着，史莱姆收购站。"
//   ⇒ 木制小屋（木墙 + 坡屋顶）+ 柜台后有人收货 + 柜台上摆着史莱姆制品 + 招牌写「史莱姆收购站」。
//
// 为什么另开一个文件、而不是加进 PixelArtGenerator：
//   PixelArtGenerator 的画布/描边/单位缩放是一整套共享静态状态（它的文件头约定 5 专门警告过这个坑），
//   本图是"一次性整图"、尺寸也更大（160×160 而不是 128×128，见下面「尺寸为什么是 160」），
//   硬塞进去要动它的共享状态机 —— 违反"尽可能少的改动已经确认好的功能"。所以这里自带一套最小画布。
//
// 尺寸为什么是 160（而不是照抄环境的 128）：
//   PPU 规则（VisualFix.WantedPpu 是唯一权威）：**世界精灵 PPU = 贴图像素宽 ⇒ 1 张贴图 = 1×1 世界单位**。
//   终点在世界里的判定框是 JSON 里的 sizeX×sizeY：Level0/1/2/LevelMech 都是 2×2 单位，
//   Level3 是 1.5×2 单位。屏幕像素/世界单位 = 屏高 ÷ (2×cameraSize)，cameraSize=6.5、1080p ⇒ 83.1 px/单位。
//   ⇒ 2×2 的终点在 1080p 上是 **166×166 屏幕像素**：贴图用 160 px 宽 ⇒ 只放大 1.04 倍（几乎 1:1，像素不糊）；
//     用 128 ⇒ 放大 1.30 倍。招牌文字要够清楚，所以选 160。
//
// 招牌文字（6 个字）怎么来的 —— 必须写清楚，因为这决定了"能离屏复现"：
//   ⚠ 本文件里的 6 个 24×24 点阵是**从工程自带的思源黑体里取样出来的**（不是手画的、不是第三方素材）：
//        字体文件：Assets/_Project/Art/UI/SourceHanSansSC-Medium.ttf
//                  （17,857,640 B / MD5 76361DA7B2089AEAD180369A2B66F5A8 / family "Source Han Sans SC Medium"）
//        取样方式：PrivateFontCollection + Graphics.DrawString，字号 24 px（GraphicsUnit.Pixel，
//                  与 CJK 的 24 px 步进 1:1 对齐），TextRenderingHint.AntiAliasGridFit，
//                  再按 alpha ≥ 110 二值化成 1bit（像素风只用硬边）。
//        取样脚本：_workflow/_证据_t97_字形取样.ps1（纯本机进程内 GDI+，不装东西、不联网）
//        核对表：  _workflow/_证据_t97_字形点阵.txt（6 张 ASCII 点阵 + hex，可逐格对）
//   ⇒ 好处：生成结果**与 Unity 版本无关、可离屏逐像素复现**，Unity 侧不需要动态字体/字体图集
//     （动态字体的图集在 batchmode 下是否可读、字形是否命中，都是本机测不了的风险）。
//     若以后要换字/换字号：重跑取样脚本 → 把新的 hex 抄进下面 SignGlyphs 即可。
//
// 用法：
//   菜单  Tools / 呆呆史莱姆 / 4 生成素材 / 收购站贴图
//   Unity.exe -batchmode -quit -executeMethod GoalStationGenerator.BatchGenerateGoalStation
//   只写 1 个 PNG：Assets/_Project/Art/Environment/Sprite_GoalStation.png（同路径覆盖）
//   写完自己把导入设置设成像素风（Point / PPU=像素宽 / Single / Center），不依赖别处自愈。
//
// 不做什么：不动脚本逻辑、不动场景、不动预制体、不动关卡 JSON、不改 Goal 的判定（史莱姆进区即通关）。
// ---------------------------------------------------------------------------

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class GoalStationGenerator
{
    // =======================================================================
    // 一、开关与路径
    // =======================================================================

    /// <summary>勾上后：生成收购站贴图（默认开）。</summary>
    public static bool enableGoalStation = true;

    /// <summary>写出路径（Unity 侧，正斜杠）。同路径覆盖，场景里的引用不需要重新拖。</summary>
    public const string StationPath = "Assets/_Project/Art/Environment/Sprite_GoalStation.png";

    /// <summary>画布尺寸。160 = 1080p 下 2×2 单位的终点只放大 1.04 倍（见文件头）。</summary>
    public static int canvasW = 160;
    public static int canvasH = 160;

    // =======================================================================
    // 二、调色板（像素风、彩度高但明度够；招牌是浅底深字，对比度约 10:1）
    // =======================================================================

    public static Color32 clear        = new Color32(  0,   0,   0,   0);

    // 石基
    public static Color32 stone        = new Color32(138, 138, 148, 255);
    public static Color32 stoneLight   = new Color32(174, 174, 184, 255);
    public static Color32 stoneDark    = new Color32(104, 104, 116, 255);

    // 木墙
    public static Color32 wood         = new Color32(192, 140,  86, 255);
    public static Color32 woodLight    = new Color32(220, 174, 118, 255);
    public static Color32 woodDark     = new Color32(146,  98,  56, 255);

    // 屋顶（红棕瓦）
    public static Color32 roof         = new Color32(172,  78,  62, 255);
    public static Color32 roofLight    = new Color32(208, 110,  90, 255);
    public static Color32 roofDark     = new Color32(124,  50,  42, 255);

    // 招牌
    public static Color32 signFace     = new Color32(238, 208, 148, 255);
    public static Color32 signEdge     = new Color32( 94,  58,  34, 255);
    public static Color32 signText     = new Color32( 50,  30,  16, 255);

    // 柜台
    public static Color32 counterTop   = new Color32(216, 172, 112, 255);
    public static Color32 counterFront = new Color32(164, 116,  68, 255);

    // 店内暗部
    public static Color32 interior     = new Color32( 66,  44,  36, 255);

    // 史莱姆制品
    public static Color32 slime        = new Color32(108, 214, 120, 255);
    public static Color32 slimeLight   = new Color32(184, 246, 188, 255);
    public static Color32 slimeDark    = new Color32( 64, 160,  86, 255);

    // 玻璃罐 / 软木塞
    public static Color32 glass        = new Color32(210, 240, 248, 255);
    public static Color32 glassDark    = new Color32(158, 206, 224, 255);
    public static Color32 cork         = new Color32(208, 164, 110, 255);

    // 店员
    public static Color32 skin         = new Color32(246, 200, 154, 255);
    public static Color32 skinShade    = new Color32(214, 162, 116, 255);
    public static Color32 hair         = new Color32( 72,  50,  38, 255);
    public static Color32 shirt        = new Color32( 92, 148, 206, 255);
    public static Color32 apron        = new Color32(234, 234, 240, 255);

    // =======================================================================
    // 三、招牌文字（「史莱姆收购站」6 字；点阵来源见文件头）
    //     每字 24×24；每个字符串 = 24 行 × 每行 24 bit（6 个 hex 字符），行序自上而下。
    // =======================================================================

    public const string SignText = "史莱姆收购站";

    /// <summary>每字一格，格宽 = 格高 = 24 px。</summary>
    public const int SignCell = 24;

    /// <summary>字形来源（写进日志，便于复核）。</summary>
    public const string SignFontPath = "Assets/_Project/Art/UI/SourceHanSansSC-Medium.ttf";
    public const int SignFontPx = 24;
    public const int SignThreshold = 110;

    public static readonly string[] SignGlyphs = new string[]
    {
        "000000001800001800001800003c001ffff81ffff8181818181818181818183c181ffff8" +   // 史
        "1ffff80018000e180006380007300003f00001e00003fc001fbff87e07fe30000c000000",

        "0000000181800381c03ffffc0381c00181800018000038001ffff80638600618600318e0" +   // 莱
        "0318c00338c03ffffe00fe0000ff0001db800399c00f18f03c183e30180c001800000000",

        "0000000c00000c1ffc0c1ffc08180c181b8c7f998c7f98cc19980c19981c11fffe33381c" +   // 姆
        "331908331b883311881f30d80e30180f3ffe0f3ffe1f80183980387000f02000e0000000",

        "00000000c30000c30000c70030c60030c70030cffe30ce1c30cc1830de1830fe1830fe30" +   // 收
        "30d33030c33031c1e03fc1e03fc0c038c1e000c3f000c73800ce1c00dc0e00c804000000",

        "0000000006003fc6003fc60030ce0020cffc26dc0c26d80c26fa0c26d30c26c70c26c60c" +   // 购
        "26c6cc26c44c26cc6c26cfec241fec0d082c0d800c19c01c38c0187060f82000f0000000",

        "0000000401800601800601800601803fe1c03fe1fe0001fe10c18010c180108180198180" +   // 站
        "199ffc199ffc19980c19180c1b180c03d80c7fd80c7c180c001ffc001ffc00180c000000",
    };

    // =======================================================================
    // 四、布局（设计坐标：x 向右、y 向上，y=0 是画布底边；全部是闭区间）
    //     拆成这些 public 常量而不是写死在画法里（铁律：数值不许埋在方法里）。
    // =======================================================================

    // 石基
    public const int BaseX0 = 4, BaseX1 = 155, BaseY0 = 0, BaseY1 = 7;

    // 木墙（店面）
    public const int WallX0 = 8, WallX1 = 152, WallY0 = 8, WallY1 = 111;

    // 屋檐横板（墙顶与屋顶之间那条深色板）
    public const int FasciaX0 = 0, FasciaX1 = 159, FasciaY0 = 104, FasciaY1 = 111;

    // 屋顶梯形：檐口在 RoofEaveY，屋脊在 RoofRidgeY
    public const int RoofEaveX0 = 0, RoofEaveX1 = 159, RoofEaveY = 112;
    public const int RoofRidgeX0 = 42, RoofRidgeX1 = 117, RoofRidgeY = 147;
    public const int RoofCourseStep = 6;      // 每 6 行一道瓦棱线
    public const int RidgeX0 = 36, RidgeX1 = 123, RidgeY0 = 144, RidgeY1 = 151;

    // 烟囱（坐在右坡上，木屋的识别度靠它）
    public const int ChimneyX0 = 104, ChimneyX1 = 119, ChimneyY0 = 150, ChimneyY1 = 159;
    public const int ChimneyBrickStep = 4;

    // 招牌：板子 + 内面；文字 6 格 × 24 px
    public const int SignX0 = 4, SignX1 = 155, SignY0 = 74, SignY1 = 103;
    public const int SignFaceX0 = 6, SignFaceX1 = 153, SignFaceY0 = 76, SignFaceY1 = 101;
    public const int TextX0 = 8, TextY0 = 77;

    // 柜台
    public const int CounterX0 = 12, CounterX1 = 147, CounterY0 = 18, CounterY1 = 35;
    public const int CounterTopY0 = 30, CounterTopY1 = 35;
    public const int CounterPlankStep = 8;

    // 店门洞（店内暗部）
    public const int DoorX0 = 100, DoorX1 = 141, DoorY0 = 36, DoorY1 = 73;
    public const int InteriorShelfY = 60, InteriorJarW = 6, InteriorJarH = 7;
    public static readonly int[] InteriorJarXs = new int[] { 104, 128 };

    // 木箱（顶上露出两只史莱姆）
    public const int CrateX0 = 18, CrateX1 = 44, CrateY0 = 36, CrateY1 = 58;
    public const int CrateBlobAX0 = 20, CrateBlobAX1 = 30, CrateBlobATop = 66;
    public const int CrateBlobBX0 = 32, CrateBlobBX1 = 42, CrateBlobBTop = 64;

    // 大玻璃罐（装着史莱姆）
    public const int JarBX0 = 48, JarBX1 = 66, JarBY0 = 36, JarBY1 = 61;

    // 小玻璃罐
    public const int JarSX0 = 70, JarSX1 = 82, JarSY0 = 36, JarSY1 = 53;

    // 托盘上的史莱姆（"卖的就是史莱姆制品"最直白的一件）
    public const int PlateX0 = 86, PlateX1 = 100, PlateY0 = 36, PlateY1 = 40;
    public const int BlobX0 = 87, BlobX1 = 99, BlobY0 = 40, BlobY1 = 49;

    // 店员（站在门洞里收货）
    public const int KeeperX0 = 106, KeeperX1 = 134;
    public const int KeeperBodyY0 = 36, KeeperBodyY1 = 56;
    public const int KeeperHeadY0 = 56, KeeperHeadY1 = 74;
    public const int KeeperArmY0 = 44, KeeperArmY1 = 52;
    public const int KeeperArmLX0 = 98, KeeperArmLX1 = 106;
    public const int KeeperArmRX0 = 134, KeeperArmRX1 = 142;

    // =======================================================================
    // 五、入口
    // =======================================================================

    [MenuItem("Tools/呆呆史莱姆/4 生成素材/收购站贴图", false, 402)]
    public static void GenerateGoalStationMenu()
    {
        int opaque = GenerateOne();
        EditorUtility.DisplayDialog(
            "收购站贴图生成完毕",
            opaque > 0
                ? "已写出 " + StationPath + "\n不透明像素 " + opaque + " / " + (canvasW * canvasH) + "。\n" +
                  "招牌 6 字（" + SignText + "）：每字 " + SignCell + "×" + SignCell + " px，取自 " + SignFontPath + "。"
                : "⚠ 画出 0 个不透明像素 —— 看 Console 的报错。",
            "好");
    }

    /// <summary>命令行入口：Unity.exe -batchmode -quit -executeMethod GoalStationGenerator.BatchGenerateGoalStation</summary>
    public static void BatchGenerateGoalStation()
    {
        if (!enableGoalStation)
        {
            UnityEngine.Debug.Log("[收购站] enableGoalStation = false，跳过。");
            return;
        }
        int opaque = GenerateOne();
        UnityEngine.Debug.Log("[收购站] 完成：不透明像素 " + opaque + " / " + (canvasW * canvasH));
    }

    /// <summary>画 + 写盘 + 设导入设置。返回不透明像素数（0 = 失败，已打日志）。</summary>
    public static int GenerateOne()
    {
        try
        {
            Color32[] px = BuildStationPixels();
            int opaque = CountOpaque(px);
            if (opaque <= 0)
            {
                UnityEngine.Debug.LogError("[收购站] 画出 0 个不透明像素，已中止写入（防回归：白干一场还报成功）。");
                return 0;
            }

            Texture2D tex = new Texture2D(canvasW, canvasH, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);              // ⚠ Unity 的 SetPixels32 是从【左下角】开始填：px[y*w+x]，y=0 = 底边
            tex.Apply(false, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absDir = Path.Combine(Application.dataPath, ToAbsoluteDir(StationPath));
            if (!Directory.Exists(absDir)) Directory.CreateDirectory(absDir);
            File.WriteAllBytes(Path.Combine(absDir, Path.GetFileName(StationPath)), png);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(StationPath, ImportAssetOptions.ForceUpdate);
            ApplyImportSettings(StationPath);

            int signInk = CountSignInk();
            UnityEngine.Debug.Log("[收购站] 写出 " + StationPath + "  (" + canvasW + "×" + canvasH + ")，不透明像素 " +
                                  opaque + "/" + (canvasW * canvasH) + " (" + (100f * opaque / (canvasW * canvasH)).ToString("0.0") +
                                  "%)，招牌墨 " + signInk + " px（6 字，每字约 " + (signInk / 6) + " px）。PPU = " + canvasW +
                                  "（世界精灵规则：PPU = 像素宽 ⇒ 本图 = 1×1 世界单位）");
            return opaque;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[收购站] 失败：" + StationPath + " → " + ex);
            return 0;
        }
    }

    // =======================================================================
    // 六、画法（纯像素逻辑：只用 int/float 运算 + Color32，不碰任何 Unity 对象）
    //     离屏验证台就是把这一段拿去跑的，所以这里不许出现别的东西。
    // =======================================================================

    public static Color32[] BuildStationPixels()
    {
        int w = canvasW, h = canvasH;
        Color32[] px = new Color32[w * h];
        Fill(px, w, h, 0, 0, w - 1, h - 1, clear);

        DrawStoneBase(px, w, h);
        DrawWall(px, w, h);
        DrawDoor(px, w, h);
        DrawKeeper(px, w, h);
        DrawFascia(px, w, h);
        DrawRoof(px, w, h);
        DrawGoods(px, w, h);
        DrawCounter(px, w, h);
        DrawSign(px, w, h);

        return px;
    }

    /// <summary>石基（房子的"台座"，让房子踩在地上而不是浮着）。</summary>
    static void DrawStoneBase(Color32[] px, int w, int h)
    {
        Fill(px, w, h, BaseX0, BaseY0, BaseX1, BaseY1, stone);
        HLine(px, w, h, BaseY1, BaseX0, BaseX1, stoneLight);          // 顶面高光
        HLine(px, w, h, BaseY0, BaseX0, BaseX1, stoneDark);           // 底边压暗
        HLine(px, w, h, BaseY0 + 1, BaseX0, BaseX1, stoneDark);
        // 砖缝
        for (int x = BaseX0 + 8; x < BaseX1; x += 16) VLine(px, w, h, x, BaseY0 + 2, BaseY1 - 1, stoneDark);
    }

    /// <summary>木墙：横板 + 四角立柱 + 几条亮板高光。</summary>
    static void DrawWall(Color32[] px, int w, int h)
    {
        Fill(px, w, h, WallX0, WallY0, WallX1, WallY1, wood);
        for (int y = WallY0 + 6; y <= WallY1 - 2; y += 7) HLine(px, w, h, y, WallX0, WallX1, woodDark);
        for (int y = WallY0 + 3; y <= WallY1 - 6; y += 14) HLine(px, w, h, y, WallX0 + 3, WallX1 - 3, woodLight);
        VLine(px, w, h, WallX0, WallY0, WallY1, woodDark);
        VLine(px, w, h, WallX0 + 1, WallY0, WallY1, woodDark);
        VLine(px, w, h, WallX1, WallY0, WallY1, woodDark);
        VLine(px, w, h, WallX1 - 1, WallY0, WallY1, woodDark);
        HLine(px, w, h, WallY0, WallX0, WallX1, woodDark);
    }

    /// <summary>门洞：店内暗部（店员站在里面），里面再摆一层"货架 + 小罐"，透出"里面也在卖史莱姆制品"。</summary>
    static void DrawDoor(Color32[] px, int w, int h)
    {
        Fill(px, w, h, DoorX0, DoorY0, DoorX1, DoorY1, interior);
        VLine(px, w, h, DoorX0, DoorY0, DoorY1, woodDark);
        VLine(px, w, h, DoorX1, DoorY0, DoorY1, woodDark);
        HLine(px, w, h, DoorY1, DoorX0, DoorX1, woodDark);
        // 店内货架（暗一点，别抢前景）
        HLine(px, w, h, InteriorShelfY, DoorX0 + 2, DoorX1 - 2, woodDark);
        VLine(px, w, h, DoorX0 + 2, InteriorShelfY, DoorY1 - 1, woodDark);
        VLine(px, w, h, DoorX1 - 2, InteriorShelfY, DoorY1 - 1, woodDark);
        for (int i = 0; i < InteriorJarXs.Length; i++)
        {
            int jx = InteriorJarXs[i];
            Fill(px, w, h, jx, InteriorShelfY + 1, jx + InteriorJarW, InteriorShelfY + InteriorJarH, slimeDark);
            Fill(px, w, h, jx, InteriorShelfY + InteriorJarH / 2, jx + InteriorJarW, InteriorShelfY + InteriorJarH, slime);
            HLine(px, w, h, InteriorShelfY + InteriorJarH, jx - 1, jx + InteriorJarW + 1, cork);
        }
    }

    /// <summary>店员：身体 + 围裙 + 头（帽子/头发 + 眼睛）+ 两只手搭在柜台上。</summary>
    static void DrawKeeper(Color32[] px, int w, int h)
    {
        // 身体与围裙
        Fill(px, w, h, KeeperX0, KeeperBodyY0, KeeperX1, KeeperBodyY1, shirt);
        Fill(px, w, h, KeeperX0 + 4, KeeperBodyY0, KeeperX1 - 4, KeeperBodyY1 - 6, apron);

        // 两条手臂（朝柜台方向）＋手
        Fill(px, w, h, KeeperArmLX0, KeeperArmY0, KeeperArmLX1, KeeperArmY1, shirt);
        Fill(px, w, h, KeeperArmRX0, KeeperArmY0, KeeperArmRX1, KeeperArmY1, shirt);
        Fill(px, w, h, KeeperArmLX0, KeeperArmY0, KeeperArmLX0 + 2, KeeperArmY1, skinShade);
        Fill(px, w, h, KeeperArmRX1 - 2, KeeperArmY0, KeeperArmRX1, KeeperArmY1, skinShade);

        // 头（把四角切掉一点点，像素风里的"圆"）
        Fill(px, w, h, KeeperX0 + 4, KeeperHeadY0, KeeperX1 - 4, KeeperHeadY1 - 2, skin);
        Fill(px, w, h, KeeperX0 + 5, KeeperHeadY0 - 2, KeeperX1 - 5, KeeperHeadY0, skin);
        HLine(px, w, h, KeeperHeadY1 - 2, KeeperX0 + 4, KeeperX1 - 4, hair);     // 发际
        Fill(px, w, h, KeeperX0 + 4, KeeperHeadY1 - 4, KeeperX1 - 4, KeeperHeadY1 - 3, hair);
        Fill(px, w, h, KeeperX0 + 4, KeeperHeadY1 - 6, KeeperX0 + 6, KeeperHeadY1 - 3, hair);
        Fill(px, w, h, KeeperX1 - 6, KeeperHeadY1, KeeperX1 - 4, KeeperHeadY1 - 3, hair);
        // 眼睛 + 嘴（2×2 的方块，像素风的常规做法）
        Fill(px, w, h, KeeperX0 + 6, KeeperHeadY0 + 5, KeeperX0 + 7, KeeperHeadY0 + 6, hair);
        Fill(px, w, h, KeeperX1 - 7, KeeperHeadY0 + 5, KeeperX1 - 6, KeeperHeadY0 + 6, hair);
        HLine(px, w, h, KeeperHeadY0 + 2, KeeperX0 + 9, KeeperX1 - 9, skinShade);
    }

    /// <summary>屋檐深色横板。</summary>
    static void DrawFascia(Color32[] px, int w, int h)
    {
        Fill(px, w, h, FasciaX0, FasciaY0, FasciaX1, FasciaY1, woodDark);
        HLine(px, w, h, FasciaY1, FasciaX0, FasciaX1, roofDark);
        HLine(px, w, h, FasciaY0, FasciaX0, FasciaX1, wood);
    }

    /// <summary>坡屋顶：梯形本体 + 瓦棱线 + 屋脊压条 + 两侧斜边描线 + 右坡烟囱。</summary>
    static void DrawRoof(Color32[] px, int w, int h)
    {
        int span = RoofRidgeY - RoofEaveY;
        for (int y = RoofEaveY; y <= RoofRidgeY; y++)
        {
            float t = span <= 0 ? 0f : (y - RoofEaveY) / (float)span;
            int x0 = RoofEaveX0 + (int)((RoofRidgeX0 - RoofEaveX0) * t + 0.5f);
            int x1 = RoofEaveX1 - (int)((RoofEaveX1 - RoofRidgeX1) * t + 0.5f);
            if (x1 < x0) continue;
            int course = (y - RoofEaveY) % RoofCourseStep;
            Color32 body = course == 0 ? roofDark : roof;
            Fill(px, w, h, x0, y, x1, y, body);
            if (course == 1) HLine(px, w, h, y, x0 + 2, x1 - 2, roofLight);   // 瓦口高光
            HLine(px, w, h, y, x0, Math.Min(x0 + 1, x1), roofDark);           // 左斜边
            HLine(px, w, h, y, Math.Max(x0, x1 - 1), x1, roofDark);           // 右斜边
        }
        HLine(px, w, h, RoofRidgeY, RoofRidgeX0, RoofRidgeX1, roofLight);
        // 屋脊压条
        Fill(px, w, h, RidgeX0, RidgeY0, RidgeX1, RidgeY1, roofDark);
        HLine(px, w, h, RidgeY1, RidgeX0, RidgeX1, roofLight);

        // 烟囱：砖块 + 亮顶 + 一道深色砖缝
        Fill(px, w, h, ChimneyX0, ChimneyY0, ChimneyX1, ChimneyY1, stoneDark);
        for (int y = ChimneyY0; y <= ChimneyY1; y += ChimneyBrickStep)
            HLine(px, w, h, y, ChimneyX0, ChimneyX1, stone);
        for (int x = ChimneyX0 + 3; x < ChimneyX1; x += 7)
            VLine(px, w, h, x, ChimneyY0, ChimneyY1 - 2, stone);
        HLine(px, w, h, ChimneyY1, ChimneyX0 - 1, ChimneyX1 + 1, stoneLight);
        VLine(px, w, h, ChimneyX0, ChimneyY0, ChimneyY1, stoneDark);
        VLine(px, w, h, ChimneyX1, ChimneyY0, ChimneyY1, stoneDark);
    }

    /// <summary>柜台上的货：木箱（顶上两只史莱姆）、大罐、小罐、托盘上的史莱姆。</summary>
    static void DrawGoods(Color32[] px, int w, int h)
    {
        // ---- 木箱 ----
        Fill(px, w, h, CrateX0, CrateY0, CrateX1, CrateY1, woodLight);
        HLine(px, w, h, CrateY1, CrateX0, CrateX1, woodDark);
        HLine(px, w, h, CrateY0, CrateX0, CrateX1, woodDark);
        VLine(px, w, h, CrateX0, CrateY0, CrateY1, woodDark);
        VLine(px, w, h, CrateX1, CrateY0, CrateY1, woodDark);
        HLine(px, w, h, CrateY0 + 8, CrateX0, CrateX1, woodDark);       // 横板缝
        // 箱顶两只史莱姆（"卖的是史莱姆制品"）
        DrawSlimeBlob(px, w, h, CrateBlobAX0, CrateY1 + 1, CrateBlobAX1, CrateBlobATop);
        DrawSlimeBlob(px, w, h, CrateBlobBX0, CrateY1 + 1, CrateBlobBX1, CrateBlobBTop);

        // ---- 大玻璃罐（罐身 + 史莱姆 + 瓶颈 + 软木塞 + 标签条）----
        int bodyTopB = JarBY1 - 9;
        Fill(px, w, h, JarBX0, JarBY0, JarBX1, bodyTopB, glass);
        Fill(px, w, h, JarBX0, JarBY0, JarBX1, JarBY0 + 14, slime);
        HLine(px, w, h, JarBY0 + 15, JarBX0 + 1, JarBX1 - 1, slimeLight);
        VLine(px, w, h, JarBX0 + 3, JarBY0 + 3, bodyTopB - 2, slimeLight);
        Fill(px, w, h, JarBX0, JarBY0 + 6, JarBX1, JarBY0 + 9, slimeLight);        // 标签条
        HLine(px, w, h, JarBY0 + 5, JarBX0, JarBX1, slimeDark);
        VLine(px, w, h, JarBX0, JarBY0, bodyTopB, glassDark);
        VLine(px, w, h, JarBX1, JarBY0, bodyTopB, glassDark);
        HLine(px, w, h, JarBY0, JarBX0, JarBX1, glassDark);
        Fill(px, w, h, JarBX0 + 4, bodyTopB, JarBX1 - 4, JarBY1 - 3, glass);         // 瓶颈
        VLine(px, w, h, JarBX0 + 4, bodyTopB, JarBY1 - 3, glassDark);
        VLine(px, w, h, JarBX1 - 4, bodyTopB, JarBY1 - 3, glassDark);
        Fill(px, w, h, JarBX0 + 3, JarBY1 - 3, JarBX1 - 3, JarBY1, cork);            // 软木塞

        // ---- 小玻璃罐 ----
        int bodyTopS = JarSY1 - 8;
        Fill(px, w, h, JarSX0, JarSY0, JarSX1, bodyTopS, glass);
        Fill(px, w, h, JarSX0, JarSY0, JarSX1, JarSY0 + 9, slime);
        HLine(px, w, h, JarSY0 + 10, JarSX0 + 1, JarSX1 - 1, slimeLight);
        VLine(px, w, h, JarSX0 + 2, JarSY0 + 2, bodyTopS - 2, slimeLight);
        VLine(px, w, h, JarSX0, JarSY0, bodyTopS, glassDark);
        VLine(px, w, h, JarSX1, JarSY0, bodyTopS, glassDark);
        HLine(px, w, h, JarSY0, JarSX0, JarSX1, glassDark);
        Fill(px, w, h, JarSX0 + 3, bodyTopS, JarSX1 - 3, JarSY1 - 3, glass);
        VLine(px, w, h, JarSX0 + 3, bodyTopS, JarSY1 - 3, glassDark);
        VLine(px, w, h, JarSX1 - 3, bodyTopS, JarSY1 - 3, glassDark);
        Fill(px, w, h, JarSX0 + 2, JarSY1 - 3, JarSX1 - 2, JarSY1, cork);

        // ---- 托盘 + 史莱姆 ----
        Fill(px, w, h, PlateX0, PlateY0, PlateX1, PlateY1, stoneLight);
        HLine(px, w, h, PlateY0, PlateX0, PlateX1, stoneDark);
        DrawSlimeBlob(px, w, h, BlobX0, PlateY1 + 1, BlobX1, BlobY1);
        // 史莱姆的两只眼睛（识别度）
        Fill(px, w, h, BlobX0 + 4, BlobY1 - 4, BlobX0 + 5, BlobY1 - 3, hair);
        Fill(px, w, h, BlobX1 - 5, BlobY1 - 4, BlobX1 - 4, BlobY1 - 3, hair);
    }

    /// <summary>画一只史莱姆（切角当圆、上浅下深、底边压暗）。</summary>
    static void DrawSlimeBlob(Color32[] px, int w, int h, int x0, int y0, int x1, int y1)
    {
        Fill(px, w, h, x0 + 1, y0, x1 - 1, y1, slime);
        Fill(px, w, h, x0, y0 + 1, x1, y1 - 1, slime);
        Fill(px, w, h, x0 + 1, y1 - 2, x1 - 1, y1, slimeLight);
        HLine(px, w, h, y0, x0 + 2, x1 - 2, slimeDark);
    }

    /// <summary>木柜台：正面竖板缝 + 台面（台面比正面亮一档，做出厚度）。</summary>
    static void DrawCounter(Color32[] px, int w, int h)
    {
        Fill(px, w, h, CounterX0, CounterY0, CounterX1, CounterY1, counterFront);
        for (int x = CounterX0 + CounterPlankStep; x < CounterX1; x += CounterPlankStep)
            VLine(px, w, h, x, CounterY0 + 1, CounterTopY0 - 1, woodDark);
        Fill(px, w, h, CounterX0, CounterTopY0, CounterX1, CounterTopY1, counterTop);
        HLine(px, w, h, CounterTopY1, CounterX0, CounterX1, woodLight);
        HLine(px, w, h, CounterTopY0 - 1, CounterX0, CounterX1, woodDark);
        VLine(px, w, h, CounterX0, CounterY0, CounterY1, woodDark);
        VLine(px, w, h, CounterX1, CounterY0, CounterY1, woodDark);
        HLine(px, w, h, CounterY0, CounterX0, CounterX1, woodDark);
    }

    /// <summary>招牌：深色板 + 浅色内面 + 6 个烤进去的字。</summary>
    static void DrawSign(Color32[] px, int w, int h)
    {
        Fill(px, w, h, SignX0, SignY0, SignX1, SignY1, signEdge);
        Fill(px, w, h, SignFaceX0, SignFaceY0, SignFaceX1, SignFaceY1, signFace);
        HLine(px, w, h, SignFaceY0, SignFaceX0, SignFaceX1, woodDark);          // 内面下沿压暗（厚度感）
        VLine(px, w, h, SignFaceX1, SignFaceY0, SignFaceY1, woodDark);

        for (int i = 0; i < SignGlyphs.Length && i < SignText.Length; i++)
            StampGlyph(px, w, h, i, TextX0 + i * SignCell, TextY0, signText);
    }

    // =======================================================================
    // 七、像素基元 / 字形
    // =======================================================================

    static void Fill(Color32[] px, int w, int h, int x0, int y0, int x1, int y1, Color32 c)
    {
        if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
        if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > w - 1) x1 = w - 1;
        if (y1 > h - 1) y1 = h - 1;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * w;
            for (int x = x0; x <= x1; x++) px[row + x] = c;
        }
    }

    static void HLine(Color32[] px, int w, int h, int y, int x0, int x1, Color32 c)
    {
        Fill(px, w, h, x0, y, x1, y, c);
    }

    static void VLine(Color32[] px, int w, int h, int x, int y0, int y1, Color32 c)
    {
        Fill(px, w, h, x, y0, x, y1, c);
    }

    /// <summary>把第 i 个字的点阵盖到 (x0, y0)（y0 = 该格底边，行序自上而下 ⇒ 要翻过来）。</summary>
    static void StampGlyph(Color32[] px, int w, int h, int glyph, int x0, int y0, Color32 c)
    {
        string hex = SignGlyphs[glyph];
        for (int row = 0; row < SignCell; row++)
        {
            int y = y0 + (SignCell - 1 - row);
            for (int col = 0; col < SignCell; col++)
            {
                if (GlyphBit(hex, col, row)) Fill(px, w, h, x0 + col, y, x0 + col, y, c);
            }
        }
    }

    /// <summary>取点阵某一位：列 col（0 = 最左）、行 row（0 = 最上）。</summary>
    static bool GlyphBit(string hex, int col, int row)
    {
        int bit = row * SignCell + col;
        int nibble = HexVal(hex[bit / 4]);
        int shift = 3 - (bit % 4);
        return ((nibble >> shift) & 1) == 1;
    }

    static int HexVal(char ch)
    {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
        return 0;
    }

    static int CountOpaque(Color32[] px)
    {
        int n = 0;
        for (int i = 0; i < px.Length; i++) if (px[i].a >= 128) n++;
        return n;
    }

    /// <summary>招牌文字的墨量（写日志用，也是"文字真的画进去了"的自检）。</summary>
    public static int CountSignInk()
    {
        int n = 0;
        for (int g = 0; g < SignGlyphs.Length; g++)
        {
            string hex = SignGlyphs[g];
            for (int row = 0; row < SignCell; row++)
                for (int col = 0; col < SignCell; col++)
                    if (GlyphBit(hex, col, row)) n++;
        }
        return n;
    }

    // =======================================================================
    // 八、写盘辅助（与 PixelArtGenerator 同口径）
    // =======================================================================

    /// <summary>"Assets/xxx/yyy.png" → "xxx"（**目录**，文件名必须剥掉）。</summary>
    static string ToAbsoluteDir(string unityPath)
    {
        string p = unityPath.Replace('\\', '/');
        const string prefix = "Assets/";
        if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p.Substring(prefix.Length);
        p = p.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(p);
    }

    /// <summary>像素风导入设置：Point / 不压缩 / PPU = 像素宽（= VisualFix 的世界精灵规则）/ Single / Center。</summary>
    static void ApplyImportSettings(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            UnityEngine.Debug.LogWarning("[收购站] 取不到 TextureImporter：" + path);
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;

        TextureImporterSettings s = new TextureImporterSettings();
        importer.ReadTextureSettings(s);

        s.textureType         = TextureImporterType.Sprite;
        s.spriteMode          = (int)SpriteImportMode.Single;
        s.filterMode          = FilterMode.Point;
        s.mipmapEnabled       = false;
        s.wrapMode            = TextureWrapMode.Clamp;
        s.wrapModeU           = TextureWrapMode.Clamp;
        s.wrapModeV           = TextureWrapMode.Clamp;
        s.alphaIsTransparency = true;
        s.alphaSource         = TextureImporterAlphaSource.FromInput;
        s.spritePixelsPerUnit = canvasW;                   // ⚠ 世界精灵：PPU = 贴图像素宽 ⇒ 本图 = 1×1 世界单位
        s.spriteMeshType      = SpriteMeshType.FullRect;
        s.npotScale           = TextureImporterNPOTScale.None;
        s.sRGBTexture         = true;
        s.spriteAlignment     = (int)SpriteAlignment.Center;

        importer.SetTextureSettings(s);

        importer.textureCompression  = TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled       = false;
        importer.filterMode          = FilterMode.Point;
        importer.wrapMode            = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = true;
        importer.npotScale           = TextureImporterNPOTScale.None;
        importer.spritePixelsPerUnit = canvasW;
        importer.maxTextureSize      = 2048;

        EditorUtility.SetDirty(importer);
        importer.SaveAndReimport();
    }
}
