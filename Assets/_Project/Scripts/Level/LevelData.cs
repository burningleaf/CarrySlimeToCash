// ---------------------------------------------------------------------------
// LevelData.cs —— 关卡数据的唯一真相源
//
// 运行时脚本（可打包）：不引用 UnityEditor，不用 ScriptableObject。
// 官方关卡、玩家自制地图、求解器、生成器全部共用这一套结构。
//
// 坐标约定（务必先读）：
//   存储单位 = 1/4 格整数（quarter unit，记作 q），world = q * 0.25
//   地形块 TerrainBlock：qx/qy = 矩形【左下角】，qw/qh = 尺寸
//   物件   LevelObject：qx/qy = 【中心】，qw/qh = 尺寸（0 = 用该类的默认尺寸）
//   理由：地形要算"可站立顶面"，左下角最直观；物件跟随 Unity 的中心轴心。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>格坐标与世界坐标的换算。全工程只此一处定义。</summary>
public static class LevelUnits
{
    /// <summary>一格 = 1 Unity 单位；存储精度 = 1/4 格。</summary>
    public const float Q = 0.25f;

    public static float ToWorld(int q) { return q * Q; }
    public static int ToQuarter(float world) { return Mathf.RoundToInt(world / Q); }
    public static float SnapWorld(float world) { return ToWorld(ToQuarter(world)); }
    /// <summary>量化误差（世界单位）。调用方用它决定要不要报警告。</summary>
    public static float QuantizeError(float world) { return Mathf.Abs(world - SnapWorld(world)); }
}

/// <summary>地形种类。Ground 同时代表"地面"和"墙"——求解器只看几何，不看名字。</summary>
public enum TerrainKind
{
    Ground = 0,           // 实心可站立
    OneWayPlatform = 1,   // 单向平台（可从下方穿过）
    Water = 2,            // 水：史莱姆需要被携带才能过
    Hazard = 3,           // 尖刺等伤害地形
    Decor = 4             // 纯装饰，无碰撞
}

/// <summary>物件种类。</summary>
public enum LevelObjectKind
{
    Coin = 0,
    SlimeOrb = 1,
    Waypoint = 2,          // 引导石
    Goal = 3,              // 终点
    PressurePlate = 4,
    MovingPlatform = 5,
    Gate = 6,              // 被压力板开关的静态方块/门
    Checkpoint = 7,
    Enemy = 8
}

/// <summary>机关联动方式。</summary>
public enum LinkMode
{
    Toggle = 0,      // 踩住时改变，松开还原
    OpenOnce = 1,    // 触发一次后永久打开
    CloseOnce = 2,   // 触发一次后永久关闭
    Reverse = 3      // 取反
}

[Serializable]
public class TerrainBlock
{
    public string name;
    public int qx;      // 左下角
    public int qy;
    public int qw;      // 尺寸（必须 > 0）
    public int qh;
    public TerrainKind kind;
    /// <summary>按 kind 解释的私有参数。Water: p[0]=每秒伤害；Hazard: p[0]=伤害, p[1]=killPlayer, p[2]=damageOnce。</summary>
    public float[] p;

    public TerrainBlock() { name = ""; p = new float[0]; }

    public TerrainBlock(string name, int qx, int qy, int qw, int qh, TerrainKind kind, params float[] p)
    {
        this.name = name ?? "";
        this.qx = qx; this.qy = qy; this.qw = qw; this.qh = qh;
        this.kind = kind;
        this.p = p ?? new float[0];
    }

    public float MinX { get { return LevelUnits.ToWorld(qx); } }
    public float MinY { get { return LevelUnits.ToWorld(qy); } }
    public float Width { get { return LevelUnits.ToWorld(qw); } }
    public float Height { get { return LevelUnits.ToWorld(qh); } }
    public float CenterX { get { return MinX + Width * 0.5f; } }
    public float CenterY { get { return MinY + Height * 0.5f; } }
    /// <summary>顶面高度——求解器判断"能不能站上去"就靠它。</summary>
    public float TopY { get { return MinY + Height; } }
    public Vector2 Center { get { return new Vector2(CenterX, CenterY); } }
    public Vector2 Size { get { return new Vector2(Width, Height); } }

    public float Param(int i, float fallback)
    {
        return (p != null && i >= 0 && i < p.Length) ? p[i] : fallback;
    }
}

[Serializable]
public class LevelObject
{
    public string id;       // 关卡内唯一，links 靠它引用（替代 FindObjectOfType）
    public string name;     // 显示用名字；空则由生成器自动命名
    public LevelObjectKind kind;
    public int qx;          // 中心（1/4 格整数）
    public int qy;
    // 尺寸用【世界浮点】，不吸附网格：它是由碰撞体量出来的技术值（例如金币 0.6、
    // 终点触发区 1.6），吸附到 1/4 格会把它悄悄改掉。0 = 用该类的默认尺寸。
    public float sizeX;
    public float sizeY;
    /// <summary>该类私有参数，见 LevelBuilder 里的注释。</summary>
    public float[] p;
    /// <summary>路径点（1/4 格，相对本物件中心的成对偏移 dx,dy）。目前只有 MovingPlatform 用。</summary>
    public int[] pts;

    public LevelObject()
    {
        id = ""; name = ""; p = new float[0]; pts = new int[0];
    }

    public Vector2 Center { get { return new Vector2(LevelUnits.ToWorld(qx), LevelUnits.ToWorld(qy)); } }

    public float Param(int i, float fallback)
    {
        return (p != null && i >= 0 && i < p.Length) ? p[i] : fallback;
    }

    public int PointCount { get { return pts != null ? pts.Length / 2 : 0; } }

    public Vector2 PointOffset(int index)
    {
        if (pts == null || index < 0 || index * 2 + 1 >= pts.Length) return Vector2.zero;
        return new Vector2(LevelUnits.ToWorld(pts[index * 2]), LevelUnits.ToWorld(pts[index * 2 + 1]));
    }
}

[Serializable]
public class LevelLink
{
    public string sourceId;   // 触发者（压力板）
    public string targetId;   // 被触发者（移动平台 / Gate）
    public LinkMode mode;
    public float delay;

    public LevelLink() { sourceId = ""; targetId = ""; }
    public LevelLink(string sourceId, string targetId, LinkMode mode, float delay)
    {
        this.sourceId = sourceId; this.targetId = targetId; this.mode = mode; this.delay = delay;
    }
}

[Serializable]
public class Mural
{
    public int qx;              // 中心
    public int qy;
    /// <summary>牌面尺寸（世界浮点）。占位阶段用大一点，方便走查时看清文字</summary>
    public float sizeX = 3.2f;
    public float sizeY = 1.6f;
    /// <summary>代号，例如 H2 / R1，方便施工和走查时对号</summary>
    public string muralId;
    /// <summary>
    /// 【占位阶段专用】这块牌子上写什么。
    /// 正式版这里应该是象形图（无文字教学），现在先用文字把"该画什么"写在牌子上，
    /// 好处是走一遍就能判断牌子位置和阅读节奏对不对。美术做完后把 LevelBuilder.showMuralLabels 关掉即可。
    /// </summary>
    public string label;
    /// <summary>正式版用：象形图在 LevelBuilder.muralSprites 里的下标</summary>
    public int sprIndex;

    public Mural() { muralId = ""; label = ""; }
    public Mural(string id, string label, int qx, int qy, float sizeX, float sizeY)
    {
        this.muralId = id ?? ""; this.label = label ?? "";
        this.qx = qx; this.qy = qy;
        this.sizeX = sizeX; this.sizeY = sizeY;
        this.sprIndex = -1;
    }
    public Vector2 Center { get { return new Vector2(LevelUnits.ToWorld(qx), LevelUnits.ToWorld(qy)); } }
}

[Serializable]
public class LevelMeta
{
    public string levelId = "level";
    public string displayName = "";

    // ---------------- 结算参数（真相源在这里） ----------------
    // LevelBuilder 会把这些值写进场景里的 LevelManager。
    // 之所以放数据里而不是只留在组件上：MonoBehaviour 的序列化字段会盖掉代码默认值，
    // 改代码对已经存在的场景毫无影响（这个坑踩过两次）。
    //
    // 每关只需要一个数：parTime。其余全部由全局常数推出来：
    //   售价条满格 = 10 × parTime      每秒掉 5 元（全局）      标准时间时血条剩 50%（全局）
    //   → 取 10 的倍数可以保证所有换算都是整数：1 秒 = 5 元、1 血 = parTime/10 元、1 枚金币 = 2 秒
    /// <summary>标准通关时间（秒）。取 10 的倍数可以保证所有换算都是整数。</summary>
    public float parTime = 50f;
    /// <summary>售价条满格覆盖值。0 = 自动 = 10 × parTime（推荐，保证全是整数）</summary>
    public int barMaxOverride = 0;
    /// <summary>星级门槛：相对「基准分」的比例（基准分 = 满格 × 50% + 全关金币总额）。长度必须为 3。</summary>
    public float[] starRatios = new float[] { 0.6f, 0.9f, 1.1f };

    public int startStones = 3;        // D1：开局给几颗引导石
    public int coinValue = 10;         // 默认金币面值

    // ---------------- 每关发放哪些物品 ----------------
    // 用户口径（2026-09-23）：每个关卡发放的物品可以不同，但 demo 阶段四件全给。
    // 这条"可调"链路的三个端点：
    //   数据 LevelMeta.itemGrants → LevelBuilder.Build 写进场景 PlayerInventory.itemGrants
    //   → PlayerInventory.IsSlotAvailable 决定第 N 格能不能选中、能不能用它的能力
    //   （导出时 LevelDataWindow.ExportScene 必须读回来，否则重建会把它重置成"全给"）
    /// <summary>物品开关个数 = 物品栏槽位数。索引 = 槽位序号 = ItemType 的值。</summary>
    public const int ItemGrantCount = 4;

    /// <summary>本关发放哪些物品。索引：0=空手(None) 1=哨子(Whistle) 2=引导石(GuideStone) 3=跳跃云朵瓶(CloudBottle)。
    ///  true = 本关发放这件物品。demo 阶段口径 = 全部 true（四件全给）。
    ///  ⚠ 索引 0（空手）永远可用：它不是"发放的物品"，而是"没有物品"这个状态，关掉等于把抓取 / 投掷 / 放置全砍了。</summary>
    public bool[] itemGrants = AllItemGrants();

    /// <summary>demo 口径的默认发放：全部发放。</summary>
    public static bool[] AllItemGrants()
    {
        bool[] grants = new bool[ItemGrantCount];
        for (int i = 0; i < grants.Length; i++) grants[i] = true;
        return grants;
    }

    /// <summary>把物品开关补齐成 ItemGrantCount 长：缺的按"发放"兜底、多出来的丢掉。
    ///  老 JSON（没有这个字段）读进来就是 demo 口径的"四件全给"。</summary>
    public void NormalizeItemGrants()
    {
        if (itemGrants != null && itemGrants.Length == ItemGrantCount) return;

        bool[] grants = AllItemGrants();
        if (itemGrants != null)
        {
            int n = Mathf.Min(itemGrants.Length, ItemGrantCount);
            for (int i = 0; i < n; i++) grants[i] = itemGrants[i];
        }
        itemGrants = grants;
    }

    // ---------------- 关卡编号与存档 ----------------
    /// <summary>第几关（写进 LevelManager.levelIndex，决定 PlayerPrefs 的键）。
    /// 教程关 = 0：通关会解锁 LevelUnlocked_1，但自己不占正式关的编号</summary>
    public int levelIndex = 1;
    /// <summary>是否记录成绩（金币/星级/最佳时间）。教程关 = false，不计星级</summary>
    public bool saveProgress = true;

    // ---------------- 相机 ----------------
    public bool useCameraBounds = true;
    public float camMinX = -10f;
    public float camMinY = -6f;
    public float camMaxX = 150f;
    public float camMaxY = 12f;
    public float cameraSize = 6.5f;

    // 出生点：用【世界浮点】而不是 1/4 格整数。
    // 原因：出生 y 是从碰撞体半高推出来的技术值（玩家 1.2 高 → 0.6），它本来
    // 就不该落在网格上；强行量化到 0.5 会让角色出生时嵌进地面 0.1。
    public bool hasSpawn = false;
    public float playerX = 0f;
    public float playerY = 0f;
    public float slimeX = 0f;
    public float slimeY = 0f;

    public Vector2 PlayerSpawn { get { return new Vector2(playerX, playerY); } }
    public Vector2 SlimeSpawn { get { return new Vector2(slimeX, slimeY); } }
}

[Serializable]
public class LevelData
{
    public int formatVersion = 1;
    public LevelMeta meta = new LevelMeta();
    public TerrainBlock[] terrain = new TerrainBlock[0];
    public LevelObject[] objects = new LevelObject[0];
    public LevelLink[] links = new LevelLink[0];
    public Mural[] murals = new Mural[0];

    // ---------------- 序列化 ----------------

    public string ToJson()
    {
        return JsonUtility.ToJson(this, true);
    }

    public static LevelData FromJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        LevelData d = null;
        try { d = JsonUtility.FromJson<LevelData>(json); }
        catch (Exception e) { Debug.LogError("[LevelData] JSON 解析失败：" + e.Message); return null; }
        if (d != null) d.EnsureNoNulls();
        return d;
    }

    /// <summary>JsonUtility 遇到空数组会给出 null，统一补齐，免得下游到处判空。</summary>
    public void EnsureNoNulls()
    {
        if (meta == null) meta = new LevelMeta();
        if (meta.starRatios == null || meta.starRatios.Length < 3)
            meta.starRatios = new float[] { 0.6f, 0.9f, 1.1f };
        meta.NormalizeItemGrants();      // 老 JSON 没这个字段 → 补齐成 demo 口径的"四件全给"
        if (terrain == null) terrain = new TerrainBlock[0];
        if (objects == null) objects = new LevelObject[0];
        if (links == null) links = new LevelLink[0];
        if (murals == null) murals = new Mural[0];
        for (int i = 0; i < terrain.Length; i++)
        {
            if (terrain[i] == null) terrain[i] = new TerrainBlock();
            if (terrain[i].p == null) terrain[i].p = new float[0];
        }
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) objects[i] = new LevelObject();
            if (objects[i].p == null) objects[i].p = new float[0];
            if (objects[i].pts == null) objects[i].pts = new int[0];
        }
    }

    // ---------------- 查询 ----------------

    public LevelObject FindObject(string id)
    {
        if (string.IsNullOrEmpty(id) || objects == null) return null;
        for (int i = 0; i < objects.Length; i++)
            if (objects[i] != null && objects[i].id == id) return objects[i];
        return null;
    }

    public LevelObject FindFirst(LevelObjectKind kind)
    {
        if (objects == null) return null;
        for (int i = 0; i < objects.Length; i++)
            if (objects[i] != null && objects[i].kind == kind) return objects[i];
        return null;
    }

    public int CountOf(LevelObjectKind kind)
    {
        int n = 0;
        if (objects == null) return 0;
        for (int i = 0; i < objects.Length; i++)
            if (objects[i] != null && objects[i].kind == kind) n++;
        return n;
    }

    /// <summary>整个关卡的世界包围盒（含地形与物件），相机边界与缩略图要用。</summary>
    public bool GetBounds(out Vector2 min, out Vector2 max)
    {
        min = Vector2.zero; max = Vector2.zero;
        bool any = false;
        for (int i = 0; i < terrain.Length; i++)
        {
            TerrainBlock t = terrain[i];
            if (t == null) continue;
            Vector2 a = new Vector2(t.MinX, t.MinY);
            Vector2 b = new Vector2(t.MinX + t.Width, t.MinY + t.Height);
            if (!any) { min = a; max = b; any = true; }
            else { min = Vector2.Min(min, a); max = Vector2.Max(max, b); }
        }
        for (int i = 0; i < objects.Length; i++)
        {
            LevelObject o = objects[i];
            if (o == null) continue;
            Vector2 c = o.Center;
            if (!any) { min = c; max = c; any = true; }
            else { min = Vector2.Min(min, c); max = Vector2.Max(max, c); }
        }
        return any;
    }

    // ---------------- 静态校验（P0 只做最基本的一致性检查） ----------------

    public List<string> Validate()
    {
        List<string> w = new List<string>();
        EnsureNoNulls();

        if (CountOf(LevelObjectKind.Goal) == 0) w.Add("没有终点（Goal）");
        if (CountOf(LevelObjectKind.Goal) > 1) w.Add("终点不止一个：求解器只会用第一个");

        HashSet<string> ids = new HashSet<string>();
        for (int i = 0; i < objects.Length; i++)
        {
            LevelObject o = objects[i];
            if (string.IsNullOrEmpty(o.id)) { w.Add("第 " + i + " 个物件没有 id"); continue; }
            if (!ids.Add(o.id)) w.Add("id 重复：" + o.id);
        }
        for (int i = 0; i < terrain.Length; i++)
        {
            TerrainBlock t = terrain[i];
            if (t.qw <= 0 || t.qh <= 0)
                w.Add("地形块尺寸非法（" + (string.IsNullOrEmpty(t.name) ? "#" + i : t.name) + "）qw=" + t.qw + " qh=" + t.qh);
        }
        for (int i = 0; i < links.Length; i++)
        {
            LevelLink l = links[i];
            if (FindObject(l.sourceId) == null) w.Add("联动源的 id 不存在：" + l.sourceId);
            if (FindObject(l.targetId) == null) w.Add("联动目标的 id 不存在：" + l.targetId);
        }
        if (meta.startStones < 0) w.Add("startStones 为负");
        if (meta.itemGrants != null && meta.itemGrants.Length > 0 && !meta.itemGrants[0])
            w.Add("itemGrants[0]（空手）写 false 不生效：空手永远可用，要限制请关索引 1/2/3");
        return w;
    }

    /// <summary>
    /// 规范化签名：按 (kind, 坐标, 名字) 排序后逐行输出。
    /// 用于"往返一致性检查"——场景里的遍历顺序不稳定，不能直接比 JSON 字符串。
    /// </summary>
    public List<string> CanonicalLines()
    {
        EnsureNoNulls();
        List<string> lines = new List<string>();

        lines.Add(string.Format("META {0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}",
            meta.levelId, meta.parTime, JoinFloats(meta.starRatios),
            meta.startStones, meta.coinValue,
            "idx" + meta.levelIndex + (meta.saveProgress ? "/save" : "/nosave"),
            meta.playerX.ToString("0.####") + "," + meta.playerY.ToString("0.####"),
            meta.slimeX.ToString("0.####") + "," + meta.slimeY.ToString("0.####"),
            meta.useCameraBounds ? "bounds" : "free",
            meta.camMinX.ToString("0.###") + "," + meta.camMinY.ToString("0.###") + ".." +
            meta.camMaxX.ToString("0.###") + "," + meta.camMaxY.ToString("0.###"),
            meta.cameraSize.ToString("0.###"),
            // 追加在【末尾】：老日志里前面 token 的位置不动，比对旧记录时只多一列。
            "items" + JoinBools(meta.itemGrants)));

        List<string> t = new List<string>();
        for (int i = 0; i < terrain.Length; i++)
        {
            TerrainBlock b = terrain[i];
            t.Add(string.Format("T {0}|{1}|{2},{3}|{4},{5}|{6}",
                b.kind, string.IsNullOrEmpty(b.name) ? "-" : b.name,
                b.qx, b.qy, b.qw, b.qh, JoinFloats(b.p)));
        }
        t.Sort(StringComparer.Ordinal);
        lines.AddRange(t);

        List<string> o = new List<string>();
        for (int i = 0; i < objects.Length; i++)
        {
            LevelObject b = objects[i];
            o.Add(string.Format("O {0}|{1}|{2}|{3},{4}|{5},{6}|{7}|{8}",
                b.kind, b.id, string.IsNullOrEmpty(b.name) ? "-" : b.name,
                b.qx, b.qy, b.sizeX.ToString("0.###"), b.sizeY.ToString("0.###"),
                JoinFloats(b.p), JoinInts(b.pts)));
        }
        o.Sort(StringComparer.Ordinal);
        lines.AddRange(o);

        List<string> l = new List<string>();
        for (int i = 0; i < links.Length; i++)
            l.Add(string.Format("L {0}->{1}|{2}|{3}", links[i].sourceId, links[i].targetId, (int)links[i].mode, links[i].delay));
        l.Sort(StringComparer.Ordinal);
        lines.AddRange(l);

        List<string> m = new List<string>();
        for (int i = 0; i < murals.Length; i++)
            m.Add(string.Format("U {0}|{1},{2}|{3}", murals[i].muralId, murals[i].qx, murals[i].qy, murals[i].sprIndex));
        m.Sort(StringComparer.Ordinal);
        lines.AddRange(m);

        return lines;
    }

    static string JoinFloats(float[] a)
    {
        if (a == null || a.Length == 0) return "-";
        string s = "";
        for (int i = 0; i < a.Length; i++) { if (i > 0) s += ","; s += a[i].ToString("0.###"); }
        return s;
    }

    static string JoinInts(int[] a)
    {
        if (a == null || a.Length == 0) return "-";
        string s = "";
        for (int i = 0; i < a.Length; i++) { if (i > 0) s += ","; s += a[i]; }
        return s;
    }

    /// <summary>物品开关在 META 行里的写法：1,0,1,1（顺序 = 槽位序号）。</summary>
    static string JoinBools(bool[] a)
    {
        if (a == null || a.Length == 0) return "-";
        string s = "";
        for (int i = 0; i < a.Length; i++) { if (i > 0) s += ","; s += (a[i] ? "1" : "0"); }
        return s;
    }
}
