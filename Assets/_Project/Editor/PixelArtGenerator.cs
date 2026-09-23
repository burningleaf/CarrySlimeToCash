// ---------------------------------------------------------------------------
// PixelArtGenerator.cs —— 用代码画像素美术 PNG（Editor only）
//
// 为什么存在这件事：
//   整个游戏的外观只靠 2 个共享 sprite：
//       blockSprite  —— 地形 / 台阶 / 门 / 移动平台 / 压力板 / 路牌底板
//       circleSprite —— 金币 / 回血球 / 终点 / 检查点 / 敌人 / 路径点
//   每类物件靠 KindStyle.color 染色区分。所以只要把这 2 张图换成"好看的中性形状"，
//   全场景立刻一起变好看，而且零接线（同名覆盖 / 同路径覆盖）。
//
// 核心约定（改了就会出错）：
//   1. 底色必须【亮】。SpriteRenderer.color 与贴图【相乘】，贴图偏暗 → 染什么都发黑。
//      所以所有"中性"素材都画成接近白（0.9+），真正的中性灰/暗色只用在描边和五官上。
//   2. 会横向/纵向拉伸的图（Sprite_Box / Sprite_Ground / UI_Panel）内部【不能有花纹】，
//      只有描边 + 顶部高光带，拉伸时才不会糊。
//   3. Pivot 统一 Center。这个工程的角色精灵挂在 transform 中心、与碰撞盒同心，
//      用 BottomCenter 会让角色浮起来半个身位。
//   4. 尺寸必须和真工程原素材一致：环境/面板 128×128，图标/Square_White 16×16，
//      角色/道具/特效 32×32。
//      ⚠【PPU 不在这里定】工程口径是「世界精灵 PPU = 贴图像素宽」（128px→128、16px→16，
//      都是 1×1 世界单位），唯一出处是 VisualFix.WantedPpu（路牌另有 40 的规则）。
//      下面的 pixelsPerUnit 只是"写盘那一刻的落盘值"：本类生成流程末尾会自动调用
//      VisualFix.BatchFixImportPpu() 自愈 ⇒ 最终口径由 WantedPpu 裁决，改错也不会留后遗症。
//      为什么加这一步：2026-09-23 的事故就是把 128px 的图落盘成 PPU32 → 地图像素放大 4 倍、
//      HUD 撑满屏（碰撞体却是对的 ⇒ "走起来对、看着不对"），整关没法玩。
//   5. 【画布是共享的】—— 见下面 canvas / W / H / k / stroke 五个静态字段。
//      历史 bug：每个 Paint* 自己 new 一个 Drawing 往里面画，
//      而 Paint() 返回的是另一个没人写过的 Drawing 的缓冲区 → 23 张图全是全透明（97 字节）。
//
// 用法：
//   菜单  Tools / 呆呆史莱姆 / ▨ 生成像素素材（会重写 Art，PPU 自动修正）   ← 人用
//   Unity.exe -batchmode -quit -executeMethod PixelArtGenerator.BatchGeneratePixelArt   ← 机器用
//   （流5 的一键入口 Flow5Tools.BatchGenerateAllAssets 会转调本方法）
//
// 只生成 / 覆盖 Art 下的 PNG，不动脚本、不动场景、不动预制体、不动关卡 JSON。
// 写盘结束后统一跑一次 VisualFix.BatchFixImportPpu()（生成即自愈，见上面约定 4）。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class PixelArtGenerator
{
    // =======================================================================
    // 一、开关与路径
    // =======================================================================

    /// <summary>勾上后：生成 Art\Environment 下的 4 张环境图。</summary>
    public static bool enableEnvironment = true;

    /// <summary>勾上后：生成 Art\UI 下的图标与面板。</summary>
    public static bool enableUi = true;

    /// <summary>勾上后：生成角色 / 敌人 / 道具 / 特效。</summary>
    public static bool enableCharacters = true;
    public static bool enableItems = true;
    public static bool enableVfx = true;

    /// <summary>Unity 侧路径（正斜杠）。写盘时用 Application.dataPath 拼绝对路径。</summary>
    public const string DirEnvironment = "Assets/_Project/Art/Environment";
    public const string DirUi          = "Assets/_Project/Art/UI";
    public const string DirPlayer      = "Assets/_Project/Art/Characters/Player";
    public const string DirSlime       = "Assets/_Project/Art/Characters/Slime";
    public const string DirEnemy       = "Assets/_Project/Art/Characters/Enemy";
    public const string DirItems       = "Assets/_Project/Art/Items";
    public const string DirVfx         = "Assets/_Project/Art/VFX";

    /// <summary>1 格 = 1 单位；素材像素尺寸 = 每单位像素数 → 32px 的图正好 1 格，128px 的图 4 格。</summary>
    public static float pixelsPerUnit = 32f;

    // =======================================================================
    // 二、尺寸（必须与真工程原素材 .meta 一致，见文件头约定 4）
    // =======================================================================

    /// <summary>环境地形与 UI 面板的边长（4×4 世界单位）。</summary>
    public const int SizeLarge = 128;

    /// <summary>图标与 Square_White 的边长（0.5×0.5 世界单位）。</summary>
    public const int SizeSmall = 16;

    /// <summary>角色 / 道具 / 特效的边长（1×1 世界单位）。</summary>
    public const int SizeMedium = 32;

    // =======================================================================
    // 三、调色板（全是亮色，见文件头约定 1）
    // =======================================================================

    // 中性底（会被 KindStyle.color 相乘）
    public static Color32 fill      = new Color32(238, 240, 247, 255); // 主体
    public static Color32 highlight = new Color32(255, 255, 255, 255); // 高光
    public static Color32 mid       = new Color32(216, 220, 232, 255); // 次亮
    public static Color32 shade     = new Color32(198, 203, 219, 255); // 略暗
    public static Color32 edge      = new Color32(132, 136, 154, 255); // 环境描边（浅，拉伸后不刺眼）
    public static Color32 outline   = new Color32( 74,  78,  98, 255); // 角色/道具描边
    public static Color32 detail    = new Color32( 62,  66,  86, 255); // 眼睛等细节
    public static Color32 glow      = new Color32(255, 252, 236, 255); // 发光高光
    public static Color32 white     = new Color32(255, 255, 255, 255);
    public static Color32 clear     = new Color32(  0,   0,   0,   0);

    // 地形专用
    public static Color32 grassTop  = new Color32(226, 246, 198, 255); // 地面顶部草色（上色后更绿）
    public static Color32 grassEdge = new Color32(192, 222, 158, 255);
    public static Color32 soil      = new Color32(206, 199, 186, 255); // 土（上色后成地形色）

    // 角色专用
    public static Color32 bodyFill   = new Color32(250, 249, 244, 255);
    public static Color32 bodyShade  = new Color32(214, 212, 206, 255);
    public static Color32 faceDetail = new Color32( 58,  60,  78, 255);

    // 史莱姆专用
    public static Color32 slimeFill     = new Color32(248, 252, 250, 255);
    public static Color32 slimeShade    = new Color32(206, 216, 214, 255);
    public static Color32 slimeHurtTint = new Color32(255, 146, 132, 255); // 受伤：浅灰 + 红调（可染色）

    // 云朵瓶图标专用（浅蓝玻璃 + 棕色软木塞；云用中性的 fill/mid）
    public static Color32 glassFill = new Color32(196, 232, 246, 255); // 玻璃主体
    public static Color32 glassDeep = new Color32(150, 205, 232, 255); // 瓶底/厚处
    public static Color32 corkFill  = new Color32(206, 162, 108, 255); // 软木塞
    public static Color32 corkShade = new Color32(172, 128,  84, 255); // 塞顶一条深色

    // =======================================================================
    // 四、共享画布（⚠ 全类唯一，见文件头约定 5）
    // =======================================================================

    /// <summary>本次绘制用的画布。Paint() 每次新建并赋给它，所有 Paint* 都往它上面画。</summary>
    private static Drawing canvas;

    /// <summary>画布像素尺寸，由 Paint() 按条目设置。</summary>
    private static int W;
    private static int H;

    /// <summary>单位 → 像素的比例。坐标一律按 32 单位空间描述，乘 k 得像素。</summary>
    private static float k = 1f;

    /// <summary>描边线宽（像素）。128×128 用 4，16×16 图标用 1，32×32 用 1~2。</summary>
    private static int stroke = 1;

    // =======================================================================
    // 五、姿势常量
    // =======================================================================

    private const int PoseIdle     = 0;
    private const int PoseJump     = 1;
    private const int PoseFall     = 2;
    private const int PoseCarry    = 3;
    private const int PoseStruggle = 4;

    private delegate void SpritePainter(int variant);

    // =======================================================================
    // 六、入口
    // =======================================================================

    [MenuItem("Tools/呆呆史莱姆/▨ 生成像素素材（会重写 Art，PPU 自动修正）", false, 20)]
    public static void GenerateAllMenu()
    {
        int n = GenerateAll();
        EditorUtility.DisplayDialog(
            "像素素材生成完毕",
            "本次写出 " + n + " 个 PNG（其余按开关跳过）。\n\n" +
            "环境图是同路径覆盖 → LevelBuilder 的 blockSprite / circleSprite 引用会自动指向新图。\n" +
            "PPU 已由 VisualFix.WantedPpu 自动修正（世界精灵 = 贴图像素宽，路牌 = 40）。",
            "好");
    }

    /// <summary>命令行入口：Unity.exe -batchmode -quit -executeMethod PixelArtGenerator.BatchGeneratePixelArt</summary>
    public static void BatchGeneratePixelArt()
    {
        int n = GenerateAll();
        UnityEngine.Debug.Log("[PixelArtGenerator] 批处理完成，写出 " + n + " 个 PNG。");
    }

    /// <summary>另一个可选的执行方法名。转调同一个实现。</summary>
    public static void BatchGenerate() { BatchGeneratePixelArt(); }

    private static int GenerateAll()
    {
        var list = BuildTable();
        var groups = new Dictionary<string, bool>();
        groups["env"]    = enableEnvironment;
        groups["ui"]     = enableUi;
        groups["items"]  = enableItems;
        groups["vfx"]    = enableVfx;
        groups["player"] = enableCharacters;
        groups["slime"]  = enableCharacters;
        groups["enemy"]  = enableCharacters;

        int written = 0;
        for (int i = 0; i < list.Count; i++)
        {
            Entry e = list[i];
            bool on;
            if (!groups.TryGetValue(e.group, out on) || !on) continue;
            if (WriteOne(e)) written++;
        }

        // ⚠ 生成即自愈（唯一收口点）：上面写盘用的是 pixelsPerUnit，只是"落盘值"。
        //   最终 PPU 口径由 VisualFix.WantedPpu 裁决（世界精灵 = 贴图像素宽、路牌 = 40）。
        //   2026-09-23 的事故就是这里少了这一步：128px 的图落盘成 PPU32 → 地图放大 4 倍 / HUD 撑满屏。
        //   ApplyImportSettings() 已经把贴图重新导入过，所以这里读到的宽高是真实的。
        VisualFix.BatchFixImportPpu();

        return written;
    }

    // =======================================================================
    // 七、清单
    // =======================================================================

    private class Entry
    {
        public string group;
        public string path;
        public int width;
        public int height;
        public SpritePainter painter;
        public int variant;

        public Entry(string group, string path, int width, int height, SpritePainter painter, int variant)
        {
            this.group = group; this.path = path;
            this.width = width; this.height = height;
            this.painter = painter; this.variant = variant;
        }
    }

    private static List<Entry> BuildTable()
    {
        var L = new List<Entry>();

        // ---- A. 环境：同路径覆盖，价值最高 ----
        L.Add(new Entry("env", DirEnvironment + "/Sprite_Box.png",    SizeLarge,  SizeLarge,  PaintBox,    PoseIdle));
        L.Add(new Entry("env", DirEnvironment + "/Sprite_Circle.png", SizeLarge,  SizeLarge,  PaintCircle, PoseIdle));
        L.Add(new Entry("env", DirEnvironment + "/Sprite_Ground.png", SizeLarge,  SizeLarge,  PaintGround, PoseIdle));
        L.Add(new Entry("env", DirEnvironment + "/Square_White.png",  SizeSmall,  SizeSmall,  PaintWhite,  PoseIdle));

        // ---- B. UI ----
        L.Add(new Entry("ui", DirUi + "/UI_Panel.png",        SizeLarge, SizeLarge, PaintPanel,   PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_Whistle.png",    SizeSmall, SizeSmall, PaintWhistle, PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_GuideStone.png", SizeSmall, SizeSmall, PaintStone,   PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_None.png",       SizeSmall, SizeSmall, PaintNone,    PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_Slot4.png",      SizeSmall, SizeSmall, PaintSlot4,   PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_CloudBottle.png", SizeSmall, SizeSmall, PaintCloudBottle, PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_StarOn.png",     SizeSmall, SizeSmall, PaintStarOn,  PoseIdle));
        L.Add(new Entry("ui", DirUi + "/Icon_StarOff.png",    SizeSmall, SizeSmall, PaintStarOff, PoseIdle));

        // ---- C. 角色（第 2 轮接线）----
        L.Add(new Entry("player", DirPlayer + "/Sprite_Player_Idle.png",  SizeMedium, SizeMedium, PaintPlayer, PoseIdle));
        L.Add(new Entry("player", DirPlayer + "/Sprite_Player_Jump.png",  SizeMedium, SizeMedium, PaintPlayer, PoseJump));
        L.Add(new Entry("player", DirPlayer + "/Sprite_Player_Fall.png",  SizeMedium, SizeMedium, PaintPlayer, PoseFall));
        L.Add(new Entry("player", DirPlayer + "/Sprite_Player_Carry.png", SizeMedium, SizeMedium, PaintPlayer, PoseCarry));

        L.Add(new Entry("slime", DirSlime + "/Sprite_Slime_Idle.png",     SizeMedium, SizeMedium, PaintSlime, PoseIdle));
        L.Add(new Entry("slime", DirSlime + "/Sprite_Slime_Struggle.png", SizeMedium, SizeMedium, PaintSlime, PoseStruggle));
        L.Add(new Entry("slime", DirSlime + "/Sprite_Slime_Hurt.png",     SizeMedium, SizeMedium, PaintSlime, PoseCarry));

        L.Add(new Entry("enemy", DirEnemy + "/Sprite_Enemy.png", SizeMedium, SizeMedium, PaintEnemy, PoseIdle));

        // ---- C2. 道具 ----
        L.Add(new Entry("items", DirItems + "/Sprite_Coin.png", SizeMedium, SizeMedium, PaintCoin, PoseIdle));
        L.Add(new Entry("items", DirItems + "/Sprite_Orb.png",  SizeMedium, SizeMedium, PaintOrb,  PoseIdle));

        // ---- C3. 特效 ----
        L.Add(new Entry("vfx", DirVfx + "/Sprite_Spark.png", SizeMedium, SizeMedium, PaintSpark, PoseIdle));
        L.Add(new Entry("vfx", DirVfx + "/Sprite_Dust.png",  SizeMedium, SizeMedium, PaintDust,  PoseIdle));

        return L;
    }

    // =======================================================================
    // 八、写盘 + 导入设置
    // =======================================================================

    private static bool WriteOne(Entry e)
    {
        string fileName = Path.GetFileName(e.path);
        try
        {
            Color32[] px = Paint(e.width, e.height, e.painter, e.variant);
            int opaque = CountOpaque(px);
            if (opaque <= 0)
            {
                // 防回归：画出全透明图 = 白干一场，而且日志还会"成功"。
                UnityEngine.Debug.LogError("[PixelArtGenerator] " + e.path + " 画出 0 个不透明像素，" +
                                           "画布/画笔不是同一个（历史 bug），已中止本次写入。");
                return false;
            }

            Texture2D tex = new Texture2D(e.width, e.height, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absDir = Path.Combine(Application.dataPath, ToAbsoluteDir(e.path));
            if (!Directory.Exists(absDir)) Directory.CreateDirectory(absDir);

            File.WriteAllBytes(Path.Combine(absDir, fileName), png);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(e.path, ImportAssetOptions.ForceUpdate);

            ApplyImportSettings(e.path);

            UnityEngine.Debug.Log("[PixelArtGenerator] 写出 " + e.path + "  (" + e.width + "x" + e.height +
                                  ")，不透明像素 " + opaque + "/" + (e.width * e.height) +
                                  " (" + (100f * opaque / (e.width * e.height)).ToString("0.0") + "%)");
            return true;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[PixelArtGenerator] 失败：" + e.path + " → " + ex);
            return false;
        }
    }

    /// <summary>"Assets/xxx/yyy.png" → "xxx"（**目录**）。文件名必须剥掉。</summary>
    private static string ToAbsoluteDir(string unityPath)
    {
        string p = unityPath.Replace('\\', '/');
        const string prefix = "Assets/";
        if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p.Substring(prefix.Length);
        p = p.Replace('/', Path.DirectorySeparatorChar);
        // 必须把文件名也剥掉：留着的话 Directory.CreateDirectory 会
        //   · 撞上同名文件 → IOException
        //   · 或悄悄建出一个叫 "Sprite_Box.png" 的**目录**，再把 PNG 写进去（看起来还"成功"）
        return Path.GetDirectoryName(p);
    }

    /// <summary>不透明像素计数（>=128），用来在日志里一眼看出"有没有画出来"。</summary>
    private static int CountOpaque(Color32[] px)
    {
        int n = 0;
        for (int i = 0; i < px.Length; i++) if (px[i].a >= 128) n++;
        return n;
    }

    /// <summary>
    /// ⚠ 这里是历史上出 bug 的地方：
    ///   旧版是 `var d = new Drawing(w,h); painter(variant); return d.ToPixels();`，
    ///   而每个 Paint* 方法**自己也 new 了一个 Drawing** 并画在那上面，
    ///   于是返回的 d 从没被写过 → 全透明 PNG（32×32 恰好 97 字节）。
    ///   现在改为：画布只在 Paint() 里建一次，赋给静态 canvas，Paint* 直接用 canvas。
    /// </summary>
    private static Color32[] Paint(int w, int h, SpritePainter painter, int variant)
    {
        canvas = new Drawing(w, h);
        W = w;
        H = h;
        // 单位比例按"素材类型"定，不是无脑 w/32：
        //   128×128 的环境/面板是 4 格宽 → k=4，32 单位空间铺满
        //   16×16 的图标是 0.5 格宽  → k=0.5，仍按 32 单位空间描述，缩小一半画
        //   32×32 的角色/道具/特效是 1 格宽 → k=1
        k = w / 32f;
        // 描边：128 给 2px（4px 会把 128 的图糊成边框），16 给 1px，32 给 1px
        stroke = w >= 96 ? 2 : 1;
        painter(variant);
        return canvas.ToPixels();
    }

    /// <summary>像素风导入设置。Pivot 统一 Center（见文件头约定 3）。</summary>
    private static void ApplyImportSettings(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            UnityEngine.Debug.LogWarning("[PixelArtGenerator] 取不到 TextureImporter：" + path);
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
        s.spritePixelsPerUnit = pixelsPerUnit;
        s.spriteMeshType      = SpriteMeshType.FullRect;   // 大方块图用 FullRect，拉伸时更稳
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
        importer.spritePixelsPerUnit = pixelsPerUnit;
        importer.maxTextureSize      = 2048;

        EditorUtility.SetDirty(importer);
        importer.SaveAndReimport();
    }

    // =======================================================================
    // 九、绘图基元
    // =======================================================================

    private class Drawing
    {
        public readonly int w;
        public readonly int h;
        private readonly Color32[] px;

        public Drawing(int width, int height)
        {
            w = width; h = height;
            px = new Color32[width * height];
        }

        public Color32[] ToPixels() { return px; }

        public bool In(int x, int y) { return x >= 0 && x < w && y >= 0 && y < h; }

        public Color32 Get(int x, int y) { return px[y * w + x]; }

        public void Clear(Color32 c)
        {
            for (int i = 0; i < px.Length; i++) px[i] = c;
        }

        /// <summary>src over dst（写素材时透明处不会被染成黑色）。</summary>
        public void Set(int x, int y, Color32 c)
        {
            if (!In(x, y)) return;
            int i = y * w + x;
            Color32 d = px[i];
            if (c.a >= 255) { px[i] = c; return; }
            if (c.a == 0) return;
            int sa = c.a;
            int ia = 255 - sa;
            px[i] = new Color32(
                (byte)((c.r * sa + d.r * ia) / 255),
                (byte)((c.g * sa + d.g * ia) / 255),
                (byte)((c.b * sa + d.b * ia) / 255),
                (byte)(sa + d.a * ia / 255));
        }

        public void Fill(Color32 c) { Clear(c); }

        public void Rect(int x0, int y0, int x1, int y1, Color32 c)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Set(x, y, c);
        }

        public void Circle(float cx, float cy, float r, Color32 c)
        {
            int x0 = (int)Mathf.Floor(cx - r), x1 = (int)Mathf.Ceil(cx + r);
            int y0 = (int)Mathf.Floor(cy - r), y1 = (int)Mathf.Ceil(cy + r);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r * r + 0.25f) Set(x, y, c);
                }
        }

        public void RoundedRect(int x0, int y0, int x1, int y1, int radius, Color32 c)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float ax = Mathf.Max(Mathf.Max(x0 + radius, x) - x, Mathf.Min(x1 - radius, x) - x);
                    float ay = Mathf.Max(Mathf.Max(y0 + radius, y) - y, Mathf.Min(y1 - radius, y) - y);
                    float dx = Mathf.Max(0f, ax - 1f);
                    float dy = Mathf.Max(0f, ay - 1f);
                    float lim = Mathf.Max(1f, radius - 1f);
                    if (dx * dx + dy * dy <= lim * lim) Set(x, y, c);
                }
        }

        public void Line(int x0, int y0, int x1, int y1, int thickness, Color32 c)
        {
            int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
            int steps = Mathf.Max(dx, dy);
            float r = Mathf.Max(1f, thickness * 0.5f);
            if (steps == 0) { Circle(x0, y0, r, c); return; }
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Circle(Mathf.Lerp(x0, x1, t), Mathf.Lerp(y0, y1, t), r, c);
            }
        }

        /// <summary>给几何蒙版描边（向外扩 stroke 像素）。必须最后画，否则会盖掉五官。</summary>
        public void StrokeMask(bool[] mask, int sw, Color32 c)
        {
            bool[] ring = Ring(mask, w, h, sw);
            for (int i = 0; i < px.Length; i++)
                if (ring[i]) px[i] = c;
        }

        /// <summary>这条线的这一段该不该画（虚线）。</summary>
        public static bool Dash(int a, int period)
        {
            if (period < 1) period = 1;
            int m = a % period;
            if (m < 0) m += period;
            return m < period / 2;
        }
    }

    // =======================================================================
    // 十、蒙版工具
    // =======================================================================

    private static bool[] FillMask(Color32[] p)
    {
        var m = new bool[p.Length];
        for (int i = 0; i < p.Length; i++) m[i] = p[i].a >= 128;
        return m;
    }

    /// <summary>mask 之外、但落在 mask 的 dist 像素邻域内的像素（描边环）。</summary>
    private static bool[] Ring(bool[] m, int w, int h, int dist)
    {
        if (dist < 1) dist = 1;
        int d2 = dist * dist;
        var r = new bool[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (m[y * w + x]) continue;
                bool hit = false;
                for (int dy = -dist; dy <= dist && !hit; dy++)
                    for (int dx = -dist; dx <= dist; dx++)
                    {
                        if (dx * dx + dy * dy > d2) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        if (m[ny * w + nx]) { hit = true; break; }
                    }
                if (hit) r[y * w + x] = true;
            }
        return r;
    }

    /// <summary>单位空间（32 单位）的圆盘蒙版。</summary>
    private static bool[] DiscMaskU(float cxu, float cyu, float ru)
    {
        var m = new bool[W * H];
        float cx = cxu * k, cy = cyu * k, r = ru * k;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                if (dx * dx + dy * dy <= r * r) m[y * W + x] = true;
            }
        return m;
    }

    /// <summary>单位空间的椭圆蒙版（半轴 rx/ry，中心 cx/cy）。</summary>
    private static bool[] EllipseMaskU(float cxu, float cyu, float rxu, float ryu)
    {
        var m = new bool[W * H];
        float cx = cxu * k, cy = cyu * k, rx = rxu * k, ry = ryu * k;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float nx = (x + 0.5f - cx) / rx;
                float ny = (y + 0.5f - cy) / ry;
                if (nx * nx + ny * ny <= 1f) m[y * W + x] = true;
            }
        return m;
    }

    private static bool[] PolyMask(Vector2[] poly)
    {
        var m = new bool[W * H];
        int n = poly.Length;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                bool inside = false;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    if (((poly[i].y > fy) != (poly[j].y > fy)) &&
                        (fx < (poly[j].x - poly[i].x) * (fy - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                        inside = !inside;
                }
                m[y * W + x] = inside;
            }
        return m;
    }

    private static void PaintMask(bool[] mask, Color32 c)
    {
        Color32[] p = canvas.ToPixels();
        for (int i = 0; i < p.Length; i++) if (mask[i]) p[i] = c;
    }

    private static void PaintMaskIf(bool[] mask, Color32 c, Func<int, int, bool> where)
    {
        Color32[] p = canvas.ToPixels();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (mask[i] && where(x, y)) p[i] = c;
            }
    }

    private static bool[] ShiftMask(bool[] m, int dx, int dy)
    {
        var r = new bool[m.Length];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int sx = x - dx, sy = y - dy;
                if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
                if (m[sy * W + sx]) r[y * W + x] = true;
            }
        return r;
    }

    private static void SubtractMask(bool[] target, bool[] other)
    {
        for (int i = 0; i < target.Length; i++) if (other[i]) target[i] = false;
    }

    private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

    private static Color32 Mix(Color32 a, Color32 b, float t)
    {
        t = Clamp01(t);
        return new Color32(
            (byte)(a.r + (b.r - a.r) * t),
            (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t),
            (byte)(a.a + (b.a - a.a) * t));
    }

    // =======================================================================
    // 十一、环境：方块 / 圆 / 地面 / 纯白
    // =======================================================================

    /// <summary>
    /// 中性方块。会被拉伸成各种长宽比（最极端 45 格宽 × 2 格高），所以内部【不要】花纹。
    /// 128×128 下：顶部高光带 3 单位 = 12px，描边 4px。
    /// </summary>
    private static void PaintBox(int variant)     { DrawNeutralBox(3f, false); }
    private static void PaintGround(int variant)  { DrawNeutralBox(4f, true); }

    private static void DrawNeutralBox(float bandU, bool ground)
    {
        Color32 bodyCol = ground ? soil : fill;
        canvas.Fill(bodyCol);

        int bandBottom = H - (int)Mathf.Round(bandU * k);
        int bandTop = H - 1;

        // 顶部亮带：草（地面）或纯高光（方块）
        for (int y = bandBottom; y <= bandTop; y++)
            for (int x = 0; x < W; x++)
                canvas.Set(x, y, ground ? grassTop : highlight);

        // 亮带下沿的分界线
        int sep = bandBottom - stroke;
        for (int x = 0; x < W; x++)
            canvas.Set(x, sep, ground ? grassEdge : Mix(highlight, bodyCol, 0.45f));

        // 底部略暗（厚度感）
        for (int y = 0; y < stroke; y++)
            for (int x = 0; x < W; x++)
                canvas.Set(x, y, ground ? Mix(soil, shade, 0.8f) : Mix(fill, shade, 0.75f));

        // 四边描边
        Color32 sideCol = Mix(edge, bodyCol, 0.25f);
        Color32 topCol  = Mix(edge, highlight, 0.30f);
        for (int i = 0; i < W; i++)
        {
            for (int t = 0; t < stroke; t++)
            {
                canvas.Set(i, t, sideCol);              // 下
                canvas.Set(i, H - 1 - t, topCol);       // 上
            }
        }
        for (int i = 0; i < H; i++)
        {
            for (int t = 0; t < stroke; t++)
            {
                canvas.Set(t, i, sideCol);              // 左
                canvas.Set(W - 1 - t, i, sideCol);      // 右
            }
        }
    }

    /// <summary>中性球体：亮底 + 描边 + 左上月牙高光 + 右下略暗。</summary>
    private static void PaintCircle(int variant)
    {
        const float cu = 16f, ru = 14f;                  // 单位空间
        canvas.Fill(clear);

        bool[] body = DiscMaskU(cu, cu, ru);
        PaintMask(body, fill);

        // 右下略暗
        bool[] shadeMask = (bool[])body.Clone();
        SubtractMask(shadeMask, ShiftMask(body, -(int)(3 * k), (int)(3 * k)));
        PaintMaskIf(shadeMask, shade, delegate(int x, int y) { return x + y > W - 1; });

        // 左上月牙高光（几何遮罩，先画完再描边，否则描边会把整个轮廓刷成高光色）
        bool[] cres = (bool[])body.Clone();
        bool[] inner = DiscMaskU(cu - 2.5f, cu + 2.5f, ru * 0.74f);
        SubtractMask(cres, ShiftMask(inner, (int)(3 * k), -(int)(3 * k)));
        PaintMask(cres, highlight);

        canvas.StrokeMask(body, stroke, edge);

        // 高光点 + 右下暗点，球感更明确
        canvas.Circle((cu - 5.5f) * k, (cu + 6.5f) * k, 1.6f * k, highlight);
        canvas.Circle((cu + 5.5f) * k, (cu - 6.0f) * k, 2.6f * k, Mix(fill, shade, 0.55f));
    }

    private static void PaintWhite(int variant)
    {
        canvas.Fill(white);
    }

    // =======================================================================
    // 十二、UI（图标按 32 单位空间描述，k = W/32，16×16 时 k=0.5、描边 1px）
    // =======================================================================

    /// <summary>面板底：圆角 + 深描边 + 接近纯白的中心。会被拉伸。</summary>
    private static void PaintPanel(int variant)
    {
        canvas.Fill(clear);
        int inset = Mathf.Max(1, Mathf.RoundToInt(2 * k));
        int radius = Mathf.Max(2, Mathf.RoundToInt(6 * k));

        canvas.RoundedRect(inset, inset, W - 1 - inset, H - 1 - inset, radius, fill);

        int b2 = Mathf.Max(inset + 1, Mathf.RoundToInt(5 * k));
        canvas.RoundedRect(b2, b2, W - 1 - b2, H - 1 - b2, Mathf.Max(2, radius - 1), highlight);

        int b3 = Mathf.Max(b2 + 1, Mathf.RoundToInt(8 * k));
        canvas.RoundedRect(b3, b3, W - 1 - b3, H - 1 - b3, Mathf.Max(2, radius - 2), white);

        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, stroke, Mix(outline, fill, 0.15f));
    }

    private static void PaintWhistle(int variant)
    {
        canvas.Fill(clear);

        // 挂绳：从主体右上角垂出去的一条细弧（先画，接头被主体压住）
        for (int i = 0; i <= 26; i++)
        {
            float p = i / 26f;
            canvas.Circle((13f + 15f * p) * k, (18f + 10f * p - 16f * p * p) * k, 1f * k, mid);
        }

        // 哨子主体 + 右侧小圆头
        canvas.RoundedRect(U(5), U(10), U(18), U(22), 3, fill);
        canvas.Circle(20f * k, 16f * k, 5f * k, fill);
        // 左侧吹口
        canvas.Rect(U(3), U(14), U(5), U(18), mid);
        canvas.Circle(4f * k, 16f * k, 1.2f * k, detail);

        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, 1, outline);

        // 一点高光
        canvas.Rect(U(8), U(18), U(14), U(19), highlight);
        canvas.Circle(19f * k, 18f * k, 1.2f * k, highlight);
    }

    private static void PaintStone(int variant)
    {
        canvas.Fill(clear);

        // 圆角菱形（1 单位 = 0.5px）
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - 16f * k) / k;
                float dy = Mathf.Abs(y + 0.5f - 16f * k) / k;
                if (dx + dy <= 13f) canvas.Set(x, y, fill);
            }

        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, 1, outline);

        // 中间的发光纹路
        int vein = Mathf.Max(1, Mathf.RoundToInt(2f * k));
        canvas.Line(U(16), U(10), U(16), U(21), vein, glow);
        canvas.Line(U(16), U(17), U(11), U(21), 1, glow);
        canvas.Line(U(16), U(14), U(21), U(18), 1, glow);
        canvas.Circle(16f * k, 21f * k, 1.6f * k, glow);
    }

    private static void PaintNone(int variant)
    {
        canvas.Fill(clear);
        // 虚线方框（1 单位 = 2px）：逐段画，段长 3、间隔 2
        int inset = U(7);
        int size = U(18);
        int dash = U(3);
        int period = U(5);
        int t = Mathf.Max(1, Mathf.RoundToInt(1f * k));

        for (int i = 0; i < size; i++)
        {
            if ((i % period) >= dash) continue;
            int a = inset + i;
            canvas.Rect(a, inset, a, inset + t - 1, mid);
            canvas.Rect(a, inset + size - t, a, inset + size - 1, mid);
            canvas.Rect(inset, a, inset + t - 1, a, mid);
            canvas.Rect(inset + size - t, a, inset + size - 1, a, mid);
        }
    }

    private static void PaintSlot4(int variant)
    {
        canvas.Fill(clear);
        int t = Mathf.Max(1, Mathf.RoundToInt(1f * k));
        int o = U(5), s = U(22);                 // 外框

        // 实线方框
        canvas.Rect(o, o, o + s, o + t - 1, mid);
        canvas.Rect(o, o + s - t + 1, o + s, o + s, mid);
        canvas.Rect(o, o, o + t - 1, o + s, mid);
        canvas.Rect(o + s - t + 1, o, o + s, o + s, mid);

        // "?"（像素点画，不用字体）：弧顶 + 右竖 + 中横 + 短竖 + 方点
        canvas.Rect(U(12), U(20), U(19), U(22), white);    // 上横（笔画高 2）
        canvas.Rect(U(18), U(15), U(20), U(22), white);    // 右竖
        canvas.Rect(U(15), U(13), U(20), U(15), white);    // 中横
        canvas.Rect(U(15), U(11), U(17), U(15), white);    // 中竖
        canvas.Rect(U(15), U(7),  U(17), U(9),  white);    // 方点
    }

    /// <summary>
    /// 跳跃云朵瓶（第 4 格）：下半 = 软木塞 + 玻璃瓶，上半 = 一朵 3 瓣白云。
    /// 16×16 下只求"剪影一眼认出云 + 瓶"，所以云独立摆在瓶口上方（不塞进瓶里 —— 塞进去会和玻璃糊成一团），
    /// 细节只留玻璃左侧一条高光和云顶一个亮点。选中/普通/锁定三态由 slotFrames 的边框颜色表达 ⇒ 每个物品只要一张图。
    /// </summary>
    private static void PaintCloudBottle(int variant)
    {
        canvas.Fill(clear);

        // 1) 瓶子（下半）：瓶身 → 瓶底深色带 → 颈 → 软木塞（塞顶压一条深色）
        canvas.RoundedRect(U(8), U(2), U(22), U(12), Mathf.Max(1, U(4)), glassFill);
        canvas.Rect(U(10), U(2), U(20), U(4),  glassDeep);
        canvas.Rect(U(12), U(14), U(18), U(16), glassFill);
        canvas.Rect(U(10), U(18), U(20), U(20), corkFill);
        canvas.Rect(U(10), U(20), U(20), U(20), corkShade);

        // 2) 云（上半，3 瓣）：先画一层偏下的阴影（mid），再画白云本体
        canvas.Circle(11f * k, 24.0f * k, 3.0f * k, mid);
        canvas.Circle(16f * k, 25.6f * k, 3.8f * k, mid);
        canvas.Circle(21f * k, 24.0f * k, 3.0f * k, mid);
        canvas.Circle(11f * k, 24.6f * k, 3.0f * k, fill);
        canvas.Circle(16f * k, 26.2f * k, 3.8f * k, fill);
        canvas.Circle(21f * k, 24.6f * k, 3.0f * k, fill);

        // 3) 描边（与其它图标同一套：FillMask → StrokeMask 向外扩 stroke 像素）
        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, stroke, outline);

        // 4) 最后一层细节（必须在描边之后，否则会被描边盖掉）
        canvas.Rect(U(6), U(4), U(6), U(8), highlight);            // 玻璃左侧高光
        canvas.Circle(16f * k, 25.4f * k, 1.4f * k, highlight);    // 云顶亮点
    }

    private static Vector2[] StarPoints(float cu, float outer, float innerRatio)
    {
        // 屏幕坐标（y 向上）。-90° 起、逆时针 → 顶部是尖角，上排两个肩膀。
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float ang = (-90f + 36f * i) * Mathf.Deg2Rad;
            float r = (i % 2 == 0) ? outer : outer * innerRatio;
            pts[i] = new Vector2((cu + Mathf.Cos(ang) * r) * k, (cu + Mathf.Sin(ang) * r) * k);
        }
        return pts;
    }

    private static void PaintStarOn(int variant)
    {
        canvas.Fill(clear);
        bool[] star = PolyMask(StarPoints(16f, 14.5f, 0.46f));
        PaintMask(star, glow);
        canvas.StrokeMask(star, 1, outline);
        // 内部一点亮，星星有厚度（同一个中心，小一圈）
        bool[] core = PolyMask(StarPoints(16f, 8.5f, 0.46f));
        PaintMask(core, highlight);
    }

    private static void PaintStarOff(int variant)
    {
        canvas.Fill(clear);
        bool[] star = PolyMask(StarPoints(16f, 14.5f, 0.46f));
        canvas.StrokeMask(star, 1, highlight);   // 只有轮廓，空心
    }

    // =======================================================================
    // 十三、玩家 32×32（Center 轴心；脚在贴图底部附近）
    // =======================================================================

    private static void PaintPlayer(int pose)
    {
        canvas.Fill(clear);

        // k=1（32×32），下面的"单位"就是像素；全部保持在 0..32 内。
        // 比例：脚 y≈5，身体 y≈8..17，头 y≈18..30（头占约 1/3，不能更大，否则身体被挤没）
        float headCx = 16f, headCy = 24f, headR = 5.8f;
        float bodyCy = 13f, bodyRx = 4.2f, bodyRy = 4f;
        float legY = 5f, legR = 1.7f;

        if (pose == PoseJump)      { headCy = 25f;   headR = 6.0f; bodyCy = 14f;  legY = 6.5f; legR = 1.4f; }
        else if (pose == PoseFall) { headCy = 23.5f; headR = 5.6f; bodyCy = 12.5f; legY = 4.5f; legR = 1.9f; }

        // 腿（先画，被身体压住）
        float spread = (pose == PoseFall) ? 3.6f : 2.6f;
        canvas.Circle((16f - spread) * k, legY * k, legR * k, bodyFill);
        canvas.Circle((16f + spread) * k, legY * k, legR * k, bodyFill);
        canvas.Rect(15, 5, 16, 11, bodyFill);

        // 身体
        canvas.Circle(16f * k, bodyCy * k, bodyRx * k, bodyFill);
        canvas.Rect(13, 10, 18, 16, bodyFill);

        // 头
        canvas.Circle(headCx * k, headCy * k, headR * k, bodyFill);
        canvas.Rect(12, (int)(headCy - headR), 19, (int)(headCy + headR), bodyFill);

        // 手臂
        if (pose == PoseCarry)
        {
            // 双手举到头顶，托着一个东西
            canvas.Rect(8, 20, 9, 28, bodyFill);
            canvas.Rect(22, 20, 23, 28, bodyFill);
            canvas.Circle(8.5f * k, 28f * k, 1.5f * k, bodyFill);
            canvas.Circle(22.5f * k, 28f * k, 1.5f * k, bodyFill);
            canvas.Rect(11, 29, 20, 31, bodyFill);          // 抱着的方块
        }
        else if (pose == PoseJump)
        {
            canvas.Rect(10, 15, 11, 20, bodyFill);
            canvas.Rect(20, 15, 21, 20, bodyFill);
        }
        else if (pose == PoseFall)
        {
            canvas.Rect(9, 14, 10, 17, bodyFill);
            canvas.Rect(21, 14, 22, 17, bodyFill);
        }
        else
        {
            canvas.Rect(11, 10, 12, 15, bodyFill);
            canvas.Rect(19, 10, 20, 15, bodyFill);
        }

        // 五官（描边之前画）
        canvas.Circle(14f * k, (headCy + 1.5f) * k, 1.1f * k, faceDetail);
        canvas.Circle(18f * k, (headCy + 1.5f) * k, 1.1f * k, faceDetail);
        canvas.Circle(14.3f * k, (headCy + 1.9f) * k, 0.45f * k, highlight);
        canvas.Circle(18.3f * k, (headCy + 1.9f) * k, 0.45f * k, highlight);

        // 面部朝向：把外扩 2px 的那圈压暗在左半，做出体积感；描边用外扩 1px
        bool[] body = FillMask(canvas.ToPixels());
        bool[] inner = Ring(body, W, H, 1);
        bool[] outer = Ring(body, W, H, 2);
        SubtractMask(outer, inner);                       // 只留轮廓【外】面那一圈
        bool[] bodyIn = (bool[])body.Clone();
        SubtractMask(bodyIn, inner);                      // 身体去掉边缘
        bool[] rim = Ring(bodyIn, W, H, 1);
        SubtractMask(rim, inner);
        SubtractMask(rim, outer);
        PaintMaskIf(rim, bodyShade, delegate(int x, int y) { return x < W / 2; });
        canvas.StrokeMask(body, 1, outline);
    }

    // =======================================================================
    // 十四、史莱姆 32×32
    // =======================================================================

    private static void PaintSlime(int state)
    {
        bool struggle = (state == PoseStruggle);
        bool hurt = (state == PoseCarry);
        // 圆顶要基本铺满画布：基准 y=0、横半轴 14、顶半轴 30 → 顶到 y≈30、最宽处 y≈2
        float cu = 16f, baseY = 0f;
        float rxu = struggle ? 15.5f : 14f;
        float ryTop = struggle ? 20f : 30f;
        float ryBot = struggle ? 16f : 14f;

        canvas.Fill(clear);

        // 半圆顶 + 平底
        var body = new bool[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float nx = (x + 0.5f - cu * k) / (rxu * k);
                float ny = (y + 0.5f - baseY * k);
                float r = ny >= 0f ? ny / (ryTop * k) : ny / (ryBot * k);
                float wob = (ny > 0f && struggle) ? Mathf.Sin(nx * 6f) * 0.06f : 0f;
                if (nx * nx + r * r <= 1f + wob) body[y * W + x] = true;
            }

        PaintMask(body, hurt ? Mix(slimeFill, slimeHurtTint, 0.55f) : slimeFill);

        // 底部略暗
        PaintMaskIf(body, hurt ? Mix(slimeShade, slimeHurtTint, 0.5f) : slimeShade,
                    delegate(int x, int y) { return y <= baseY * k + ryBot * k * 0.18f; });

        // 描边（五官之前）
        canvas.StrokeMask(body, 1, outline);

        // 顶部高光点
        canvas.Circle((cu - 4.5f) * k, (baseY + ryTop - 2.5f) * k, 1.6f * k, white);
        canvas.Circle((cu - 5.0f) * k, (baseY + ryTop - 2.0f) * k, 0.9f * k, highlight);

        // 眼睛
        float eyeY = (baseY + (struggle ? 7f : 18f)) * k;
        if (struggle)
        {
            int t = Mathf.Max(1, Mathf.RoundToInt(1.5f * k));
            canvas.Line(U(10), (int)(eyeY + 2 * k), U(13), (int)(eyeY - 2 * k), t, detail);
            canvas.Line(U(10), (int)(eyeY - 2 * k), U(13), (int)(eyeY + 2 * k), t, detail);
            canvas.Line(U(19), (int)(eyeY + 2 * k), U(22), (int)(eyeY - 2 * k), t, detail);
            canvas.Line(U(19), (int)(eyeY - 2 * k), U(22), (int)(eyeY + 2 * k), t, detail);
            canvas.Line(U(1), (int)(eyeY + 4 * k), U(3), (int)(eyeY + 2 * k), 1, mid);
            canvas.Line(U(29), (int)(eyeY + 4 * k), U(31), (int)(eyeY + 2 * k), 1, mid);
        }
        else if (hurt)
        {
            canvas.Rect(U(10), (int)(eyeY - k), U(13), (int)(eyeY + k), detail);
            canvas.Rect(U(19), (int)(eyeY - k), U(22), (int)(eyeY + k), detail);
            canvas.Circle(cu * k, eyeY - 5f * k, 2.0f * k, detail);
            canvas.Circle(cu * k, eyeY - 4.5f * k, 1.1f * k, slimeShade);
        }
        else
        {
            canvas.Circle(11.5f * k, eyeY, 1.6f * k, detail);
            canvas.Circle(20.5f * k, eyeY, 1.6f * k, detail);
            canvas.Circle(12.0f * k, eyeY + 0.6f * k, 0.6f * k, highlight);
            canvas.Circle(21.0f * k, eyeY + 0.6f * k, 0.6f * k, highlight);
        }
    }

    // =======================================================================
    // 十五、敌人 32×32（带尖刺的圆球）
    // =======================================================================

    private static void PaintEnemy(int variant)
    {
        // 中心放 (15.5, 15.5)、最远半径 14.5 → 0.5..30.5，正好不切边
        const float cu = 15.5f, rbody = 10.5f, rspike = 14.5f;
        const int N = 8;
        canvas.Fill(clear);

        var radii = new float[N];
        var dirx = new float[N];
        var diry = new float[N];
        for (int i = 0; i < N; i++)
        {
            float ang = (-90f + 45f * i) * Mathf.Deg2Rad;
            radii[i] = (i % 2 == 0) ? rspike : 11.5f;
            dirx[i] = Mathf.Cos(ang);
            diry[i] = Mathf.Sin(ang);
        }

        // 先用一个临时画布定形状（它不属于最终输出，只是几何草稿）
        var shape = new Drawing(W, H);
        for (int i = 0; i < N; i++)
            shape.Line((int)(cu * k), (int)(cu * k),
                       (int)((cu + dirx[i] * radii[i]) * k), (int)((cu + diry[i] * radii[i]) * k),
                       Mathf.Max(2, Mathf.RoundToInt(4f * k)), fill);
        shape.Circle(cu * k, cu * k, rbody * k, fill);
        bool[] mask = FillMask(shape.ToPixels());

        PaintMask(mask, fill);

        // 尖刺顶端亮一点
        for (int i = 0; i < N; i++)
        {
            if (i % 2 != 0) continue;
            canvas.Circle((cu + dirx[i] * (rspike - 2.5f)) * k, (cu + diry[i] * (rspike - 2.5f)) * k, 1.2f * k, highlight);
        }

        // 描边必须在五官之前，否则会把眼睛盖掉
        canvas.StrokeMask(mask, 1, outline);

        // 凶眼睛 + 斜眉
        canvas.Circle((cu - 4.5f) * k, (cu + 2.0f) * k, 2.4f * k, highlight);
        canvas.Circle((cu + 4.5f) * k, (cu + 2.0f) * k, 2.4f * k, highlight);
        canvas.Circle((cu - 4.2f) * k, (cu + 1.7f) * k, 1.4f * k, detail);
        canvas.Circle((cu + 4.2f) * k, (cu + 1.7f) * k, 1.4f * k, detail);
        canvas.Circle((cu - 4.8f) * k, (cu + 2.3f) * k, 0.5f * k, white);
        canvas.Circle((cu + 3.6f) * k, (cu + 2.3f) * k, 0.5f * k, white);
        canvas.Line(U(10), U(11), U(14), U(8), 2, detail);
        canvas.Line(U(21), U(11), U(17), U(8), 2, detail);
    }

    // =======================================================================
    // 十六、道具
    // =======================================================================

    private static void PaintCoin(int variant)
    {
        const float cu = 15.5f;
        canvas.Fill(clear);

        canvas.Circle(cu * k, cu * k, 14f * k, fill);      // 外圆
        canvas.Circle(cu * k, cu * k, 10.2f * k, mid);     // 内圈
        canvas.Circle(cu * k, cu * k, 9.0f * k, fill);
        canvas.Rect(U(15), U(8), U(16), U(23), mid);       // 中间竖线 → 像 ¢

        // 左上更亮（光从左上打）
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Color32 c = canvas.Get(x, y);
                if (c.a < 128) continue;
                float dx = (x - 9f * k) / k, dy = (y - 22f * k) / k;
                float t = Clamp01((Mathf.Sqrt(dx * dx + dy * dy) - 4f) / 9f);
                canvas.Set(x, y, Mix(highlight, c, t));
            }

        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, 1, outline);
    }

    private static void PaintOrb(int variant)
    {
        const float cu = 15.5f;
        canvas.Fill(clear);

        canvas.Circle(cu * k, cu * k, 14f * k, fill);
        canvas.Circle(cu * k, cu * k, 10.5f * k, mid);
        canvas.Circle(cu * k, cu * k, 9.2f * k, fill);

        // 中间的 "+"
        canvas.Rect(U(14), U(8), U(17), U(23), highlight);
        canvas.Rect(U(8), U(14), U(23), U(17), highlight);

        bool[] body = FillMask(canvas.ToPixels());
        canvas.StrokeMask(body, 1, outline);
        canvas.Circle(9.5f * k, 21.5f * k, 1.6f * k, highlight);
    }

    // =======================================================================
    // 十七、特效 32×32
    // =======================================================================

    /// <summary>四角星闪光。d = |x|+|y|（菱形距离），边缘柔化。</summary>
    private static void PaintSpark(int variant)
    {
        const float c = 16f;
        canvas.Fill(clear);

        // 让菱形铺满 32×32：顶点到中心的曼哈顿距离约 15
        const float rOuter = 15f, rInner = 7f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = Mathf.Abs(x + 0.5f - c) + Mathf.Abs(y + 0.5f - c);
                float outerT = Clamp01((rOuter - d) / 6f);
                float innerT = Clamp01((rInner - d) / 5f);
                if (outerT <= 0.02f) continue;
                Color32 col = Mix(mid, highlight, innerT);
                col.a = (byte)(255f * outerT);
                canvas.Set(x, y, col);
            }
    }

    /// <summary>柔和的灰尘团：径向衰减 + 噪声，不留硬边。</summary>
    private static void PaintDust(int variant)
    {
        const float c = 16f;
        const float rad = 13.5f;
        canvas.Fill(clear);

        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float dx = (x + 0.5f - c) / rad;
                float dy = (y + 0.5f - c) / rad;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float n = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
                n = n - Mathf.Floor(n);
                float wobble = 1f + (n - 0.5f) * 0.18f;

                float a = Clamp01(1f - dist * wobble);
                a = a * a * 0.9f;
                if (a <= 0.02f) continue;

                Color32 col = Mix(shade, highlight, Clamp01(1.2f - dist * 1.6f));
                col.a = (byte)(255f * a);
                canvas.Set(x, y, col);
            }
    }

    // =======================================================================
    // 十八、小工具
    // =======================================================================

    /// <summary>32 单位空间坐标 → 像素。</summary>
    private static int U(float units) { return (int)Mathf.Round(units * k); }
}
