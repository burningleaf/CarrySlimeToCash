// ---------------------------------------------------------------------------
// LevelSolver.cs —— P2 关卡求解器（只放在 Editor 文件夹，不进游戏包）
//
// 它回答四个问题：
//   M1 最短通关时间
//   可达性 / 死点（哪些平台根本跳不上去）
//   每枚金币的"绕路代价"对比它的"面值能换几秒"→ 直接看出哪些金币是假选择
//   逃课检测：逐个移除障碍重算，看这个障碍到底影不影响最优路线
//
// ⚠ 合规说明（原规则禁止"寻路 / A*"）：
//   1. 本文件在 Editor 文件夹，**不打包进游戏**，所以"游戏里没有寻路"依然成立
//   2. 求解器用的不是网格寻路，而是"可站立面端点"抽象图上的最短路（节点几十个，不是格子）
//   3. 游戏内的实时反馈将来只做"这一段跳得过去吗"的局部几何判断，不做全局搜索
//
// 建图模型（第一版在这里踩过坑，记下来）：
//   节点 = **每段的左右两个端点**，不是整段。
//   第一版把整段当成一个节点，段内走路算 0 秒 —— 结果 Ground_E/F/G 合并成 118→210 一段后，
//   求解器以为"跳上这一段就算到终点"，而终点在 x=192、落地点在 x=118，中间 74 格没算，
//   报出 Level1 = 7.0 秒（直线下界是 24 秒）。所以现在段内走路必须按 长度 ÷ 移速 收费。
//
// 本阶段【只建模玩家层】。史莱姆层（引导石下限 M5）留到下一步 ——
// 史莱姆的实际行为比模型复杂得多，先做玩家层结论才可信。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LevelSolver
{
    /// <summary>
    /// 一枚金币"被夹在矮通道里"的判据：最近的落脚面**在币的上方**、且高出超过这个值 ⇒ 这枚币拿不到。
    /// 工程约定：数值放可调字段，不写死在方法里。
    /// 为什么需要它（本轮改造·净空进模型）：净空进模型后，矮通道下面的走道会被切开，那里的币会被 FindSeg
    /// 挂到 2 格高的天花板顶面上、算成 detour = 0 的假可达 —— 明明玩家根本进不去那块地方。
    /// ⚠ 只看"面在币上方"这一侧：反过来（币悬在落脚面上方 2~3 格，LevelMech 就有这种摆放）
    /// 是**放行**的 —— 那是关卡设计里"跳起来吃"的币，老口径一直认，不在这次改动范围内。
    /// </summary>
    public static float coinStandTolerance = 1.0f;

    // ======================= 能力参数 =======================

    [Serializable]
    public class Ability
    {
        public float moveSpeed = 6.5f;
        public float carryMoveSpeed = 3.5f;
        public float jumpHeight = 3f;
        public float carryJumpHeight = 1.8f;
        public float gravityScale = 3f;
        public float stepHeight = 0.5f;
        public float playerHeight = 1.2f;               // 玩家碰撞盒高（= 需要的最小净空）
        /// <summary>玩家碰撞盒**宽**（= 需要的最小缺口宽度）。
        /// Player.prefab：m_Size.x(1) × m_LocalScale.x(0.8) = **0.8**（本轮改造起真正用上：窄槽钻不钻得过去）。</summary>
        public float playerWidth = 0.8f;
        public string source = "默认值（没找到场景里的 PlayerController）";

        /// <summary>史莱姆的尺寸与能力（本轮改造·携带与史莱姆）。它跟玩家**不是**一套数值：
        /// 更矮（直径 0.9）、台阶更小（0.35）、重力更大（4），而且**自己不会跳**。</summary>
        public SlimeCaps slime = new SlimeCaps();

        public float Gravity { get { return 9.81f * Mathf.Max(0.01f, gravityScale); } }

        /// <summary>携带态：移速/跳高换成 carry* 那两个值，其余（净空高度/宽度/重力/台阶）不变。</summary>
        public Ability Carrying()
        {
            Ability c = new Ability();
            c.moveSpeed = carryMoveSpeed;
            c.carryMoveSpeed = carryMoveSpeed;
            c.jumpHeight = carryJumpHeight;
            c.carryJumpHeight = carryJumpHeight;
            c.gravityScale = gravityScale;
            c.stepHeight = stepHeight;
            c.playerHeight = playerHeight;
            c.playerWidth = playerWidth;
            c.slime = slime;
            c.source = string.Format("{0} → 携带态（移速 {1:0.##} / 跳高 {2:0.##}）", source, carryMoveSpeed, carryJumpHeight);
            return c;
        }
    }

    /// <summary>
    /// 史莱姆的尺寸与能力（本轮改造·携带与史莱姆）。**默认值全部有出处**，优先从场景里的 SlimeController / 它的碰撞体读：
    ///   · 直径 0.9（圆形碰撞体；项目文档与报告一直用这个数）
    ///   · moveSpeed = SlimeController.followSpeed（默认 6）
    ///   · stepHeight = SlimeController.maxStepHeight（默认 0.35 —— 比玩家的 0.5 小）
    ///   · maxDropHeight = SlimeController.maxDropHeight（默认 8；超过它会摔死/回救）
    ///   · gravityScale = Rigidbody2D.gravityScale（SlimeDemoSetup.cs:430 给的是 **4**，玩家是 3）
    ///   · 自己**不会跳**：只有玩家在 [jumpFollowDelay, jumpFollowWindow] 内起跳时会「跟跳」，
    ///     高度 = 玩家跳高 × jumpHeightRatio（SlimeController.cs:640-660 的 JumpFollow）
    /// </summary>
    [Serializable]
    public class SlimeCaps
    {
        public float diameter = 0.9f;
        public float moveSpeed = 6f;
        public float stepHeight = 0.35f;
        public float maxDropHeight = 8f;
        public float gravityScale = 4f;
        public float followJumpRatio = 1f;
        public bool stopAtLedge = true;
        public string source = "默认值（没找到场景里的 SlimeController）";
        public float Gravity { get { return 9.81f * Mathf.Max(0.01f, gravityScale); } }
    }

    /// <summary>尽量从场景里的 PlayerController 读，保证求解器跟真实手感一致。</summary>
    public static Ability ReadAbility(Scene scene)
    {
        Ability ab = new Ability();
        if (!scene.IsValid()) return ab;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            PlayerController pc = root.GetComponentInChildren<PlayerController>(true);
            if (pc == null) continue;
            ab.moveSpeed = pc.moveSpeed;
            ab.carryMoveSpeed = pc.carryMoveSpeed;
            ab.jumpHeight = pc.jumpHeight;
            ab.carryJumpHeight = pc.carryJumpHeight;
            ab.gravityScale = pc.gravityScale;
            BoxCollider2D box = pc.GetComponent<BoxCollider2D>();
            // ⚠ 碰撞盒的世界高度 = box.size.y × transform 缩放（本轮改造·玩家高度）。
            //   Player.prefab 是 m_Size(1,1) × m_LocalScale(0.8,1.2) ⇒ 真实高度 **1.2**。
            //   以前只读 box.size.y = 1.0（少 0.2），净空判据整体偏松 —— 1.0~1.25 之间的矮通道会被误判成"钻得过去"。
            float scaleY = pc.transform != null ? pc.transform.lossyScale.y : 1f;
            if (box != null && box.size.y * scaleY > 0.01f) ab.playerHeight = box.size.y * scaleY;
            // 宽度同理（本轮改造·携带与史莱姆）：窄槽能不能钻过去要看这个值，以前只读高度、宽度等于没进模型。
            float scaleX = pc.transform != null ? pc.transform.lossyScale.x : 1f;
            if (box != null && box.size.x * scaleX > 0.01f) ab.playerWidth = box.size.x * scaleX;
            ab.source = string.Format("场景里的 PlayerController（碰撞盒 {0:0.##}×{1:0.##}）",
                ab.playerWidth, ab.playerHeight);
            break;
        }

        // ---- 史莱姆：优先从场景里的 SlimeController / 它的碰撞体读（本轮改造·携带与史莱姆）----
        // 读不到的项保留脚本科默认值，并在 source 里写明读了哪些、用的是多少（⛔ 不悄悄写死）。
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            SlimeController sc = root.GetComponentInChildren<SlimeController>(true);
            if (sc == null) continue;
            SlimeCaps sl = ab.slime;
            if (sc.followSpeed > 0f) sl.moveSpeed = sc.followSpeed;
            if (sc.maxStepHeight > 0f) sl.stepHeight = sc.maxStepHeight;
            if (sc.maxDropHeight > 0f) sl.maxDropHeight = sc.maxDropHeight;
            if (sc.jumpHeightRatio > 0f) sl.followJumpRatio = sc.jumpHeightRatio;
            sl.stopAtLedge = sc.stopAtLedge;
            Rigidbody2D srb = sc.body != null ? sc.body : sc.GetComponent<Rigidbody2D>();
            if (srb != null && srb.gravityScale > 0f) sl.gravityScale = srb.gravityScale;
            CircleCollider2D cc = sc.GetComponent<CircleCollider2D>();
            if (cc != null && cc.radius > 0.01f)
            {
                float sScale = sc.transform != null ? sc.transform.lossyScale.x : 1f;
                sl.diameter = cc.radius * 2f * sScale;
            }
            sl.source = string.Format(
                "场景里的 SlimeController（followSpeed {0:0.##} / 台阶 {1:0.##} / 重力 {2:0.##} / 直径 {3:0.##} / 跟跳比例 {4:0.##}）",
                sl.moveSpeed, sl.stepHeight, sl.gravityScale, sl.diameter, sl.followJumpRatio);
            break;
        }
        return ab;
    }

    // ======================= 可站立面 =======================

    public class Seg
    {
        public int id;
        public float x0, x1, y;
        public string name = "";
        public float Length { get { return x1 - x0; } }
        public float CenterX { get { return (x0 + x1) * 0.5f; } }
        public float EndX(int side) { return side == 0 ? x0 : x1; }   // side: 0=左 1=右
        public override string ToString()
        {
            return string.Format("#{0} x[{1:F1},{2:F1}] 顶y={3:F2} {4}", id, x0, x1, y, name);
        }
    }

    public class Edge
    {
        public int to;
        public float cost;
        public string kind;
    }

    public class Graph
    {
        public int n;
        public List<Edge>[] fwd;
        public List<Edge>[] rev;

        public Graph(int n)
        {
            this.n = n;
            fwd = new List<Edge>[n];
            rev = new List<Edge>[n];
            for (int i = 0; i < n; i++) { fwd[i] = new List<Edge>(); rev[i] = new List<Edge>(); }
        }

        public void Add(int from, int to, float cost, string kind)
        {
            if (from < 0 || to < 0 || from >= n || to >= n) return;
            // 同一条边只留更快的
            for (int i = 0; i < fwd[from].Count; i++)
            {
                Edge e = fwd[from][i];
                if (e.to != to) continue;
                if (cost < e.cost)
                {
                    e.cost = cost; e.kind = kind;
                    // ⚠ 反向图里那条对应的边**必须一起改**。
                    //    只改正向，会让"到终点的距离"一直用旧的贵价：
                    //    金币绕路 = 从起点过去 + 从金币回终点，回程全被系统性高估。
                    //    （真事：柱顶金币的绕路代价正好翻倍，因为"跳下来 0.89s"盖住了"掉下来 0.43s"。）
                    for (int j = 0; j < rev[to].Count; j++)
                        if (rev[to][j].to == from) { rev[to][j].cost = cost; rev[to][j].kind = kind; }
                }
                return;
            }
            fwd[from].Add(new Edge { to = to, cost = cost, kind = kind });
            rev[to].Add(new Edge { to = from, cost = cost, kind = kind });
        }
    }

    /// <param name="needClear">玩家碰撞盒高（= 需要的最小净空）。</param>
    /// <param name="cutLowCeiling">
    /// true = 把"矮到钻不过去"的天花板也当成切点（净空进模型，本轮改造）；
    /// false = 只在"墙"处切（用来量【净空检查】那张表：切完再量就量不到"这里原本是走道"了）。
    /// </param>
    /// <param name="minSlotWidth">
    /// 本轮改造·携带与史莱姆：实体**宽**（玩家 0.8 / 史莱姆 0.9）。两块墙之间夹出来的"窄槽"，宽度小于它 ⇒ 该实体钻不过去，
    /// 直接把那一小段丢掉（0 = 不启用；量净空表的那次调用就是 0）。
    /// 判据只作用于**被墙切出来的中间小段**（两侧都是墙），走道两端与尾巴不受影响。
    /// </param>
    static List<Seg> BuildSegments(LevelData d, HashSet<string> ignore, float stepHeight, float needClear,
                                   bool cutLowCeiling, float minSlotWidth)
    {
        List<Seg> raw = new List<Seg>();

        foreach (TerrainBlock t in d.terrain)
        {
            if (t == null) continue;
            if (t.kind != TerrainKind.Ground) continue;        // 水 / 伤害 / 装饰都站不住
            if (ignore != null && !string.IsNullOrEmpty(t.name) && ignore.Contains(t.name)) continue;

            float top = t.TopY;
            if (top > 14f) continue;                            // 边界墙顶在 47，不是给人站的
            if (t.Height > 6f && t.Width < 3f) continue;        // 又高又细 = 墙

            raw.Add(new Seg { x0 = t.MinX, x1 = t.MinX + t.Width, y = top, name = t.name ?? "" });
        }

        // 同一高度、水平相接或重叠的合并成一段（同一条连续走道）
        raw.Sort((a, b) => Mathf.Abs(a.y - b.y) > 0.01f ? a.y.CompareTo(b.y) : a.x0.CompareTo(b.x0));
        List<Seg> merged = new List<Seg>();
        foreach (Seg s in raw)
        {
            Seg last = merged.Count > 0 ? merged[merged.Count - 1] : null;
            if (last != null && Mathf.Abs(last.y - s.y) < 0.01f && s.x0 <= last.x1 + 0.02f)
            {
                last.x1 = Mathf.Max(last.x1, s.x1);
                if (!string.IsNullOrEmpty(s.name) && last.name.IndexOf(s.name, StringComparison.Ordinal) < 0)
                    last.name = string.IsNullOrEmpty(last.name) ? s.name : last.name + "+" + s.name;
            }
            else
            {
                merged.Add(new Seg { x0 = s.x0, x1 = s.x1, y = s.y, name = s.name });
            }
        }

        // ---- 立在走道上的墙，必须把走道切断 ----
        //
        // 不切会出大事：一块 5 格高的墙立在地面中间，合并后"地面"还是连续的一整段，
        // 于是求解器认为玩家能从墙里穿过去，报出一条现实中走不通的路线。
        // （自检用例 T4 就是拿这个钉出来的。）
        // 判据：底面接着这条走道（不是悬空的）+ 顶面高到迈不上去（超过 stepHeight）。
        //
        // ---- 矮天花板也切断（本轮改造：净空进模型）----
        // 同理：悬在走道上方、矮到玩家钻不过去的地形（例如 1.0 净空的隧道底），
        // 在"脚底可站立面"模型里本来等于不存在 ⇒ 玩家被算成"直接走过去"，时间偏乐观。
        // 判据与【净空检查】里那句"玩家钻不过去"**必须是同一个不等式**：
        //     clear = 天花板底面 − 走道顶面 ≤ playerHeight + 0.05
        // 否则会出现"报表说钻不过去、模型却让走过去"的自相矛盾。
        // 切开之后：中间那段走道不复存在（玩家站不进去），两侧要通过"跳/落到天花板顶面"连通。
        List<Seg> split = new List<Seg>();
        foreach (Seg s in merged)
        {
            List<float[]> cuts = new List<float[]>();
            foreach (TerrainBlock t in d.terrain)
            {
                if (t == null || t.kind != TerrainKind.Ground) continue;
                if (ignore != null && !string.IsNullOrEmpty(t.name) && ignore.Contains(t.name)) continue;
                if (t.TopY <= s.y + stepHeight) continue;            // 矮到能直接迈上去，不算障碍
                bool isWall = t.MinY <= s.y + 0.05f;                 // 底面接着这条走道 = 立着的墙
                float clear = t.MinY - s.y;                          // 头顶净空（<0 = 就是墙）
                bool isLowCeiling = cutLowCeiling && !isWall && clear <= needClear + 0.05f;
                if (!isWall && !isLowCeiling) continue;              // 悬空且够高 = 天花板/浮台，钻得过去
                float a = Mathf.Max(s.x0, t.MinX);
                float b = Mathf.Min(s.x1, t.MinX + t.Width);
                if (b - a < 0.02f) continue;
                cuts.Add(new float[] { a, b });
            }
            if (cuts.Count == 0) { split.Add(s); continue; }

            cuts.Sort((p, q) => p[0].CompareTo(q[0]));
            float cursor = s.x0;
            bool cursorIsWallCut = false;          // cursor 左边是不是"被墙切出来的"
            foreach (float[] c in cuts)
            {
                if (c[0] > cursor + 0.02f)
                {
                    float len = c[0] - cursor;
                    // 窄槽（两侧都是墙、且窄过实体宽度）⇒ 这个实体钻不过去，整段丢掉（本轮改造·携带与史莱姆）
                    bool narrowSlot = cursorIsWallCut && minSlotWidth > 0f && len < minSlotWidth;
                    if (!narrowSlot)
                        split.Add(new Seg { x0 = cursor, x1 = c[0], y = s.y, name = s.name });
                }
                if (c[1] > cursor) cursor = c[1];
                cursorIsWallCut = true;            // 下面这一段（如果有）左边一定是墙
            }
            if (cursor < s.x1 - 0.02f) split.Add(new Seg { x0 = cursor, x1 = s.x1, y = s.y, name = s.name });
        }
        merged = split;

        for (int i = 0; i < merged.Count; i++) merged[i].id = i;
        return merged;
    }

    // ======================= 跳跃能力包络 =======================

    /// <summary>从高度 A 跳到高度 A+dy，最大能跨多少水平距离。dy 超过跳高返回 -1。</summary>
    static float MaxJumpDx(float dy, Ability ab)
    {
        if (dy > ab.jumpHeight + 0.01f) return -1f;
        float t1 = Mathf.Sqrt(2f * ab.jumpHeight / ab.Gravity);                        // 起跳到顶点
        float t2 = Mathf.Sqrt(2f * Mathf.Max(0f, ab.jumpHeight - dy) / ab.Gravity);     // 顶点落到目标
        return (t1 + t2) * ab.moveSpeed;
    }

    static float JumpTime(float dy, Ability ab)
    {
        float t1 = Mathf.Sqrt(2f * ab.jumpHeight / ab.Gravity);
        float t2 = Mathf.Sqrt(2f * Mathf.Max(0f, ab.jumpHeight - dy) / ab.Gravity);
        return t1 + t2;
    }

    /// <summary>
    /// 两点之间地面上的最高障碍顶面（判断这一跳/这一落会不会撞上东西）。
    /// ⚠ 本轮修正：只比**顶面**会把"头顶很高的楼板"误判成障碍 —— 它的底面已经高过本次动作的上限，
    ///   玩家是从它**下面**飞过去的，它挡不到路。判据：
    ///     障碍 = 与 [xa,xb] 水平重叠、且**底面 MinY ≤ ceiling** 的 Ground 块（ceiling = 本次动作的高度上限）。
    ///   ceiling 的取法（不拿走道的 y 当阈值）：
    ///     · 跳：**起跳点 + 跳高** 再减掉一点余量（= 跳跃顶点 apex），飞不到那么高就不可能撞上；
    ///     · 落：**起跳点高度**（下落路径的最高处就是起点）。
    ///   ⛔ 这条只作用于"路上挡不挡"，与"哪些块是可站立面"（BuildSegments）完全无关。
    /// </summary>
    static float HighestTopBetween(LevelData d, HashSet<string> ignore, float xa, float xb, float ceiling)
    {
        float lo = Mathf.Min(xa, xb), hi = Mathf.Max(xa, xb);
        if (hi - lo < 0.05f) return float.MinValue;                 // 两点重合，中间没东西
        float best = float.MinValue;
        foreach (TerrainBlock t in d.terrain)
        {
            if (t == null || t.kind != TerrainKind.Ground) continue;
            if (ignore != null && !string.IsNullOrEmpty(t.name) && ignore.Contains(t.name)) continue;
            if (Mathf.Min(hi, t.MinX + t.Width) - Mathf.Max(lo, t.MinX) < 0.05f) continue;
            if (t.MinY > ceiling) continue;                         // ★ 底面已经在本次动作上限之上 ⇒ 从它下面过去，不挡路
            if (t.TopY > best) best = t.TopY;
        }
        return best;
    }

    // ======================= 建图（节点 = 段上的"站点"） =======================
    //
    // 为什么不是"每段一个节点"，也不是"每段两端两个节点"：
    //   · 每段一个节点 → 段内走路免费，跳上一段 92 格长的地面就等于到了终点（报出 7 秒，实际 24 秒）
    //   · 每段两个端点   → 起跳必须恰好发生在端点。竖井里 Step_1(104~108) 嵌在 PitFloor(92~114) 里，
    //                      玩家是在台阶正下方往上跳（dx=0），端点组合最小也有 6 格 → 报"不可达"
    //
    // 正确做法：**每段上取若干"站点"**
    //   两端 + 出生点 / 终点 / 每枚金币的 x + 与每个邻段的"最接近点"
    //   段内相邻站点连"走"边（按 Δx ÷ 移速 收费）
    //   跨段只在两段的**最接近点对**之间连"跳 / 落"边
    // 这样段内走路要花时间，垂直跳也能成立。

    class Station
    {
        public int seg;
        public float x;
    }

    class Topology
    {
        public int startNode, goalNode;
        public List<int> coinNodes = new List<int>();
        public int[] nodeSeg = new int[0];              // 节点 → 段
        public float[] nodeX = new float[0];            // 节点 → x（虚拟节点也有）
        public List<List<int>> nodesOfSeg = new List<List<int>>();
    }

    /// <summary>把 value 收进列表（容差 0.01 视为同一个点）。</summary>
    static void CollectX(List<float> xs, float value)
    {
        for (int i = 0; i < xs.Count; i++)
            if (Mathf.Abs(xs[i] - value) < 0.01f) return;
        xs.Add(value);
    }

    /// <summary>求 A、B 两段之间的最接近点对，返回水平距离。</summary>
    static float ClosestPair(Seg a, Seg b, out float ta, out float tb)
    {
        if (b.x0 > a.x1) { ta = a.x1; tb = b.x0; return b.x0 - a.x1; }          // B 在右
        if (a.x0 > b.x1) { ta = a.x0; tb = b.x1; return a.x0 - b.x1; }          // B 在左
        float mid = (Mathf.Max(a.x0, b.x0) + Mathf.Min(a.x1, b.x1)) * 0.5f;      // 水平重叠
        ta = mid; tb = mid;
        return 0f;
    }

    static Graph BuildGraph(LevelData d, HashSet<string> ignore, List<Seg> segs, Ability ab,
                            float spawnX, float goalX,
                            List<float> coinXs, List<float> coinYs,
                            out Topology topo, out int stationCount)
    {
        topo = new Topology();

        // ---- 1) 收集每段上的站点 x ----
        List<List<float>> perSeg = new List<List<float>>();
        for (int i = 0; i < segs.Count; i++)
            perSeg.Add(new List<float> { segs[i].x0, segs[i].x1 });

        int sSeg = FindSeg(segs, spawnX, 0f);
        int gSeg = FindSeg(segs, goalX, 0f);
        if (sSeg >= 0) CollectX(perSeg[sSeg], spawnX);
        if (gSeg >= 0) CollectX(perSeg[gSeg], goalX);

        // ⚠ 金币找段必须带上它自己的高度：柱顶的金币（y=3.25）如果只按 x 找，
        //    会被挂到下面 y=0 的地面上，算出来"绕路 0 秒"，等于白捡（踩过）
        List<int> coinSeg = new List<int>();
        for (int k = 0; k < coinXs.Count; k++)
        {
            int cs = FindCoinSeg(segs, coinXs[k], coinYs[k]);   // 本轮改造：带高度容差，矮通道下的币不再假可达
            coinSeg.Add(cs);
            if (cs >= 0) CollectX(perSeg[cs], coinXs[k]);
        }

        // 两两之间的最接近点也要成为站点（否则垂直跳无处落脚）
        for (int i = 0; i < segs.Count; i++)
        {
            for (int j = 0; j < segs.Count; j++)
            {
                if (i == j) continue;
                float ta, tb;
                ClosestPair(segs[i], segs[j], out ta, out tb);
                CollectX(perSeg[i], ta);
                CollectX(perSeg[j], tb);
            }
        }

        // ---- 2) 排序 + 编号 ----
        List<Station> stations = new List<Station>();
        List<List<int>> nodesOfSeg = new List<List<int>>();
        for (int i = 0; i < segs.Count; i++)
        {
            List<float> xs = perSeg[i];
            xs.Sort();
            List<int> ids = new List<int>();
            foreach (float x in xs)
            {
                ids.Add(stations.Count);
                stations.Add(new Station { seg = i, x = x });
            }
            nodesOfSeg.Add(ids);
        }
        stationCount = stations.Count;

        topo.nodeSeg = new int[stations.Count];
        for (int i = 0; i < stations.Count; i++) topo.nodeSeg[i] = stations[i].seg;
        topo.nodesOfSeg = nodesOfSeg;

        topo.startNode = stations.Count;
        topo.goalNode = stations.Count + 1;
        for (int k = 0; k < coinXs.Count; k++) topo.coinNodes.Add(stations.Count + 2 + k);

        topo.nodeX = new float[stations.Count + 2 + coinXs.Count];
        for (int i = 0; i < stations.Count; i++) topo.nodeX[i] = stations[i].x;
        topo.nodeX[topo.startNode] = spawnX;
        topo.nodeX[topo.goalNode] = goalX;
        for (int k = 0; k < coinXs.Count; k++) topo.nodeX[topo.coinNodes[k]] = coinXs[k];

        Graph g = new Graph(stations.Count + 2 + coinXs.Count);

        // ---- 3) 段内走路：相邻站点相连 ----
        for (int i = 0; i < segs.Count; i++)
        {
            List<int> ids = nodesOfSeg[i];
            for (int a = 0; a + 1 < ids.Count; a++)
            {
                float dx = Mathf.Abs(stations[ids[a + 1]].x - stations[ids[a]].x);
                float cost = dx / Mathf.Max(0.01f, ab.moveSpeed);
                g.Add(ids[a], ids[a + 1], cost, "走");
                g.Add(ids[a + 1], ids[a], cost, "走");
            }
        }

        // ---- 4) 跨段：只在最接近点对之间连边 ----
        for (int i = 0; i < segs.Count; i++)
        {
            for (int j = 0; j < segs.Count; j++)
            {
                if (i == j) continue;
                Seg a = segs[i], b = segs[j];
                float dy = b.y - a.y;

                float ta, tb;
                float dx = ClosestPair(a, b, out ta, out tb);

                int na = NodeAt(nodesOfSeg[i], stations, ta);
                int nb = NodeAt(nodesOfSeg[j], stations, tb);
                if (na < 0 || nb < 0) continue;

                // 平走：高度差在台阶以内
                if (Mathf.Abs(dy) <= ab.stepHeight && dx <= 0.05f)
                {
                    g.Add(na, nb, dx / Mathf.Max(0.01f, ab.moveSpeed), "走");
                    continue;
                }

                // 跳过去 / 跳上去
                //
                // 但两点之间如果夹着一堵高过跳跃顶点的墙，这一跳是撞墙，不能连边
                // （光把走道切断还不够：切断后的两截地面自己又会连出一条"跨越墙"的跳边）
                // ⚠ 一次判据审查后引入：ceiling 传**本次跳跃的顶点**（起跳点 + 跳高 - 余量）——
                //   底面已经在顶点之上的楼板（Level3 的 U01_West 那种：平跳时头顶挂着一大块）不该算障碍，
                //   玩家是从它**下面**飞过去的，不是爬上去。
                // ⚠ 后续复核修正：**这个"顶点豁免"只对不失去高度的跳（dy ≥ 0）成立**。
                //   dy ≥ 0 时 min(a.y,b.y) == a.y，顶点就是"起跳点 + 跳高"；dy < 0 时真实顶点**仍然**是
                //   "起跳点 + 跳高"（不是 b.y + 跳高 —— 那正是修之前把它算低了的原因），但**不能拿它当豁免阈值**：
                //   楼板底面只要低于顶点就会被算进障碍，而判据"最高顶面 ≤ apex"判不出
                //   "能不能**整段**飞在楼板顶上" —— 飞得比它高 ≠ 有足够横向时间一直飞在它上面。
                //   真反例（Level1，实测）：Zig_2[164,170]顶5 → Ground[176,210]顶0，dx=6 / dy=-5，
                //   中间 Zig_3[170,176] 顶7.5/底6.0（底面只比起跳点高 1.00）：跨 6 格同时落 5 格
                //   至少要飞高 r*=1.13 > 1.00，且"晚点抬头"只会让可用时间更短 ⇒ 真机撞在 Zig_3 侧面。
                //   所以 dy < 0 一律**关掉豁免**（ceiling 抬到极大 ⇒ 回到那次修正之前"只看最高顶面"的老判据）。
                // ⚠ 已知简化（本轮不修，精确版留待后续）：dy < 0 时用老判据**偏保守**，
                //   会拒掉少数其实做得到的下落跳（整块楼板都挂在顶点之上、压根碰不到的那种）。
                float apex = Mathf.Min(a.y, b.y) + ab.jumpHeight - 0.05f;          // 「能不能翻过去」的上界
                float ceil = dy >= -0.01f ? a.y + ab.jumpHeight - 0.05f : float.MaxValue;   // 「哪些楼板可豁免」
                float topBetween = HighestTopBetween(d, ignore, ta, tb, ceil);
                bool clearJump = topBetween <= apex;

                float maxDx = MaxJumpDx(dy, ab);
                if (clearJump && maxDx > 0f && dx <= maxDx) g.Add(na, nb, JumpTime(dy, ab), "跳");

                // 直接走下去（不跳，靠重力）
                //
                // ⚠ 这里**不能**因为上面已经连了"跳"边就 continue：
                //    从地面掉进 6 格深的井里，重力下落 0.64s，而"跳过去"要 1.23s。
                //    之前用 continue 短路，导致所有"往下跳"都被按跳的耗时计价，
                //    求解器于是宁可爬柱子绕路，也不肯直接走进坑里（报出的路线就是错的）。
                if (dy < -0.5f)
                {
                    float fallT = Mathf.Sqrt(2f * (-dy) / ab.Gravity);
                    // 下落途中如果半空有平台，人会落在上面，所以这条"直达"边不成立。
                    // ⚠ 同一处修正：下落路径的最高处就是**起跳点**，所以 ceiling 传 a.y。
                    float topBetweenFall = HighestTopBetween(d, ignore, ta, tb, a.y + 0.05f);
                    bool clearFall = topBetweenFall <= b.y + 0.05f;
                    if (clearFall && dx <= fallT * ab.moveSpeed) g.Add(na, nb, fallT, "落");
                }
            }
        }

        // ---- 5) 虚拟起点 / 终点 ----
        if (sSeg >= 0)
        {
            int n = NodeAt(nodesOfSeg[sSeg], stations, spawnX);
            if (n >= 0) g.Add(topo.startNode, n, 0f, "起");
        }
        if (gSeg >= 0)
        {
            int n = NodeAt(nodesOfSeg[gSeg], stations, goalX);
            if (n >= 0) g.Add(n, topo.goalNode, 0f, "终");
        }

        // ---- 6) 虚拟金币节点（双向，方便算"绕过去再回来"）----
        for (int k = 0; k < coinXs.Count; k++)
        {
            int cs = coinSeg[k];
            if (cs < 0) continue;
            int n = NodeAt(nodesOfSeg[cs], stations, coinXs[k]);
            if (n < 0) continue;
            g.Add(n, topo.coinNodes[k], 0f, "币");
            g.Add(topo.coinNodes[k], n, 0f, "币");
        }

        return g;
    }

    static int NodeAt(List<int> ids, List<Station> stations, float x)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (int id in ids)
        {
            float d = Mathf.Abs(stations[id].x - x);
            if (d < bestD) { bestD = d; best = id; }
        }
        return bestD < 0.05f ? best : (best >= 0 ? best : -1);   // 站点是精确收集的，取最近即可
    }

    /// <summary>取 u→v 之间最便宜的那条边的耗时与动作名（回溯路线用）。</summary>
    static bool FindEdge(List<Edge>[] g, int u, int v, out float cost, out string kind)
    {
        cost = 0f; kind = "?";
        if (u < 0 || v < 0 || u >= g.Length) return false;
        bool found = false;
        foreach (Edge e in g[u])
        {
            if (e.to != v) continue;
            if (!found || e.cost < cost) { cost = e.cost; kind = e.kind; found = true; }
        }
        return found;
    }

    static string NodeLabel(Report r, Topology topo, int node)
    {
        if (node < 0) return "?";
        float x = node < topo.nodeX.Length ? topo.nodeX[node] : 0f;
        if (node == topo.startNode) return string.Format("出生点(x{0:0.#})", x);
        if (node == topo.goalNode) return string.Format("终点(x{0:0.#})", x);
        if (node >= topo.nodeSeg.Length) return string.Format("金币(x{0:0.#})", x);
        int seg = topo.nodeSeg[node];
        if (seg < 0 || seg >= r.segs.Count) return string.Format("?x{0:0.#}", x);
        string nm = string.IsNullOrEmpty(r.segs[seg].name) ? "#" + seg : r.segs[seg].name;
        return string.Format("{0}(x{1:0.#}@y{2:0.##})", nm, x, r.segs[seg].y);
    }


    // ======================= 最短路 =======================

    static float[] Dijkstra(List<Edge>[] g, int src, out int[] prev)
    {
        int n = g.Length;
        float[] dist = new float[n];
        bool[] done = new bool[n];
        prev = new int[n];
        for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
        if (n == 0 || src < 0 || src >= n) return dist;

        dist[src] = 0f;
        for (int iter = 0; iter < n; iter++)
        {
            int u = -1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0) break;
            done[u] = true;

            foreach (Edge e in g[u])
            {
                if (done[e.to]) continue;
                float nd = dist[u] + e.cost;
                if (nd < dist[e.to]) { dist[e.to] = nd; prev[e.to] = u; }
            }
        }
        return dist;
    }

    static int FindSeg(List<Seg> segs, float x, float y)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < segs.Count; i++)
        {
            Seg s = segs[i];
            if (x < s.x0 - 0.6f || x > s.x1 + 0.6f) continue;
            float d = Mathf.Abs(s.y - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    static int FindSeg(List<Seg> segs, float x)
    {
        // 不关心高度时，取"顶面最低"的那一段（离地面最近，最可能是玩家实际走的）
        int best = -1;
        float bestY = float.MaxValue;
        for (int i = 0; i < segs.Count; i++)
        {
            Seg s = segs[i];
            if (x < s.x0 - 0.6f || x > s.x1 + 0.6f) continue;
            if (s.y < bestY) { bestY = s.y; best = i; }
        }
        return best;
    }

    /// <summary>
    /// 一枚金币挂在**哪块落脚面**上：x 在段内 + 高度差不超过 coinStandTolerance。
    /// 为什么不直接用 FindSeg：它只按 x 找、再取最近的 y，差 2 格也照样返回。
    /// 净空进模型（本轮改造）之后，矮通道下面的走道被切掉了，那些币会被"挂"到 2 格高的天花板顶面上，
    /// 变成 detour = 0 的假可达 —— 明明玩家根本进不去那块地方。
    /// </summary>
    static int FindCoinSeg(List<Seg> segs, float x, float y)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < segs.Count; i++)
        {
            Seg s = segs[i];
            if (x < s.x0 - 0.6f || x > s.x1 + 0.6f) continue;
            float d = Mathf.Abs(s.y - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return -1;
        // 落脚面在币的**上面**、且高出 coinStandTolerance ⇒ 币被夹在矮通道里，玩家进不去 ⇒ 拿不到。
        // （面在币下方（币悬空摆放）一律放行，见字段注释。）
        if (segs[best].y - y > coinStandTolerance) return -1;
        return best;
    }

    // ======================= 求解结果 =======================

    public class CoinInfo
    {
        public string name;
        public float x;
        public float y;
        public int value;
        public float detour;
        public float budget;
        public int segId = -1;
        public string segName = "";
        public bool Reachable { get { return detour < 900f; } }
        public bool WorthIt { get { return Reachable && detour <= budget; } }
    }

    public class Report
    {
        public LevelData data;
        public Ability ability;
        public List<Seg> segs = new List<Seg>();
        public int nodeCount, edgeCount, stationCount;
        public bool goalReachable;
        public float minTime = float.MaxValue;
        public float lowerBound;
        public List<string> pathDesc = new List<string>();
        public List<string> pathSteps = new List<string>();
        public float pathSum;
        public int startSeg = -1, goalSeg = -1;
        public List<Seg> unreachable = new List<Seg>();
        public List<CoinInfo> coins = new List<CoinInfo>();
        public float allCollectTime = float.MaxValue;
        // ---- 全收集（精确，可行解）—— 本轮改造·全收集口径 ----
        /// <summary>求解器**实际拿得到**的金币集合（位 0 = r.coins[0]），"全收集"就按这个集合算。</summary>
        public int collectableMask;
        public int collectableCount;
        /// <summary>精确全收集耗时 = minTime[collectableMask][goal]（掩码枚举里的真实最优，不是逐枚相加）。</summary>
        public float allCollectExactTime = float.MaxValue;
        /// <summary>精确全收集总分 = 售价(exact) + 可收集金币的面值合计。</summary>
        public int allCollectExactScore;
        /// <summary>allCollectTime 是不是精确值（false = 掩码枚举跑不动，退回"逐枚绕路相加"的估算）。</summary>
        public bool allCollectIsExact;
        /// <summary>旧口径的"逐枚绕路相加"估算值（含拿不到的币 ⇒ **不可行**）。
        /// 只留给 r.greedyScore 做历史对照，不再出现在三行对照里（本轮改造·全收集口径）。</summary>
        public float allCollectEstimateTime = float.MaxValue;
        /// <summary>可收集金币的面值合计（= maskSum[collectableMask]）。</summary>
        public int collectableValue;
        public List<string> obstacleTests = new List<string>();
        public List<Clearance> clearance = new List<Clearance>();
        /// <summary>每秒掉多少钱（元/秒）。**与 LevelManager 同源**：这里只是一份拷贝，
        /// SolveCore 会从 LevelManager.drainPerSecond 读进来（默认值也直接取它，不写第二个 5）。
        /// ⚠ 别把它当独立常数改 —— 全局口径在 LevelManager（hpPerSecond × moneyPerHp = 5 元/秒）。</summary>
        public float drainPerSecond = LevelManager.drainPerSecond;
        public float parRemain = 0.5f;
        public float parTime = 50f;
        public int coinTotal;
        public bool dumpSegments;

        // ---- 【性能】求解耗时（只给报告最末尾那两行用；负数 = 没测 ⇒ 该行不打印）----
        /// <summary>Solve 段耗时（毫秒）：由两个入口在调用 Solve 前起表、调用后落值。</summary>
        public double solveMs = -1.0;
        /// <summary>其中 R* 掩码枚举的累计耗时（毫秒）：围着真实那一次 ComputeBestScore 调用累加。</summary>
        public double bestScoreMs = -1.0;

        // ---- 【最优解 R*】掩码枚举的结果（玩家最多能赚多少）----
        public int coinCount;                        // n：金币总数（含求解器"拿不到"的）
        public long maskCount;                       // 2^n：枚举过的掩码总数
        public bool bestComputed;                    // R* 有没有算出来（n 太大 / 不可达时为 false）
        public string bestSkipReason = "";           // 没算的原因（要明确写出来，不许静默）
        public float bestTime = float.MaxValue;      // R* 的耗时
        public int bestPrice;                        // R* 的售价部分
        public int bestCoinValue;                    // R* 捡到的金币面值合计
        public int bestScore;                        // R* 总分 = 售价 + 金币
        public int bestCoinCount;                    // R* 捡了几枚
        public List<int> bestCoinIndexes = new List<int>();    // R* 选中的金币下标（对应 coins）
        public List<string> bestPathDesc = new List<string>(); // R* 路线概览（段名，同 pathDesc 风格）
        public int rushScore;                        // 冲刺（一枚不捡）的总分
        public int greedyScore;                      // 贪心全拿（含拿不到的币）的总分
        public float floorTime;                      // 掉血下限时刻：越过它之后时间不再掉血（免费）
        public float bestNewGapPercent;              // 收益差 = (R* ÷ 冲刺 − 1) × 100
        public float bestRouteTime;                  // R* 路线逐段最短时间之和（应当 = bestTime，用来钉回溯）
        // R* 选中、但被**线性判据**标成"不划算"的币：它们为什么还是被捡（边际耗时 vs 单枚绕路）
        public List<int> bestCheatIndexes = new List<int>();
        public List<float> bestCheatMarginal = new List<float>();     // 在最优路线里多带上它的边际耗时
        public List<float> bestCheatSolo = new List<float>();         // 它的单枚绕路代价（WorthIt 用的就是它）

        // =====================================================================
        // 本轮改造·携带与史莱姆：携带态 / 史莱姆 —— 三种口径**并列**（不是覆盖），外加"谜题可解"三态
        // =====================================================================
        /// <summary>携带态（carryMoveSpeed / carryJumpHeight）跑出来的同一份报告。</summary>
        public Report carry;
        /// <summary>史莱姆**独自**能不能走到终点（它自己不会跳；台阶 0.35、重力 4）。起点 = 史莱姆出生点。</summary>
        public Report slimeAlone;
        /// <summary>史莱姆在「玩家在旁跟跳」口径下能不能走到终点（跟跳高度 = 玩家跳高 × 比例）。</summary>
        public Report slimeFollowJump;
        /// <summary>本关 minTime 路线上的每一跳（给"携带第一个卡在哪"用）。</summary>
        public List<Hop> pathHops = new List<Hop>();
        /// <summary>哪些段从这个起点能站上去（按段下标）。</summary>
        public bool[] reachableSegs = new bool[0];
    }

    /// <summary>一小段"跨段动作"的几何：u → v 是跳 / 落 / 走，dy 是高度差、dx 是水平距离。
    /// 用于"携带第一个卡在哪"与"史莱姆能不能自己过"的诊断（本轮改造·携带与史莱姆）。</summary>
    public class Hop
    {
        public int fromNode = -1, toNode = -1;
        public int fromSeg = -1, toSeg = -1;
        public string fromName = "?", toName = "?";
        public float fromX, toX, fromY, toY;
        public float dy, dx, cost, fromTime;
        public string kind = "?";

        public string Trans()
        {
            return string.Format("{0}(x{1:0.#}@y{2:0.#}) --{3}--> {4}(x{5:0.#}@y{6:0.#})",
                fromName, fromX, fromY, kind, toName, toX, toY);
        }
    }

    // =====================================================================
    // 本轮改造·携带与史莱姆：三种口径**并列** —— 空手 / 携带 / 史莱姆（自己 / 跟跳）
    //   ⛔ 不是覆盖：主 Report 仍是"空手玩家"的最短路；携带与史莱姆各自挂一份 Report。
    //   为什么要三态：通关条件是「史莱姆进终点」，所以"空手可达"根本不能证明这关能过。
    // =====================================================================

    public static Report Solve(LevelData data, Ability ab, HashSet<string> ignore)
    {
        Report r = SolveCore(data, ab, ignore, data.meta.playerX, data.meta.playerY);
        // ⚠ 只在**主调用**里附三态：障碍检测那一串 Solve(ignore=...) 子调用不重复算（省 3 倍时间）
        if (ignore == null && r.data != null)
        {
            r.carry = SolveCore(data, ab.Carrying(), null, data.meta.playerX, data.meta.playerY);
            r.slimeAlone = SolveCore(data, SlimeAbility(ab, false), null, data.meta.slimeX, data.meta.slimeY);
            r.slimeFollowJump = SolveCore(data, SlimeAbility(ab, true), null, data.meta.slimeX, data.meta.slimeY);
        }
        return r;
    }

    /// <summary>史莱姆专用的 Ability：身高/宽度都取它的**直径**（0.9，绝不与玩家的 1.2 混用）；
    /// 移速/台阶/重力取史莱姆自己的；`withFollowJump=false` 时跳高 = 0（它自己不会跳）。</summary>
    static Ability SlimeAbility(Ability ab, bool withFollowJump)
    {
        Ability s = new Ability();
        s.moveSpeed = ab.slime.moveSpeed;
        s.carryMoveSpeed = ab.slime.moveSpeed;
        s.gravityScale = ab.slime.gravityScale;
        s.stepHeight = ab.slime.stepHeight;
        s.playerHeight = ab.slime.diameter;
        s.playerWidth = ab.slime.diameter;
        s.jumpHeight = withFollowJump ? ab.jumpHeight * ab.slime.followJumpRatio : 0f;
        s.carryJumpHeight = s.jumpHeight;
        s.slime = ab.slime;
        s.source = withFollowJump
            ? string.Format("史莱姆·跟跳（玩家在 jumpFollowWindow 内起跳时它跟着跳 {0:0.##} 格；重力 {1:0.##}）",
                s.jumpHeight, s.gravityScale)
            : string.Format("史莱姆·自己（**不会跳**；移速 {0:0.##} / 台阶 {1:0.##} / 重力 {2:0.##}）",
                s.moveSpeed, s.stepHeight, s.gravityScale);
        return s;
    }

    /// <summary>把一条走/跳/落边打包成 Hop（给"卡在哪"诊断用）。</summary>
    static Hop MakeHop(Report r, Topology topo, int u, int v, string kind, float cost, float fromTime, int stationCount)
    {
        Hop h = new Hop();
        h.fromNode = u; h.toNode = v;
        h.kind = kind; h.cost = cost; h.fromTime = fromTime;
        h.fromSeg = (u >= 0 && u < stationCount) ? topo.nodeSeg[u] : -1;
        h.toSeg = (v >= 0 && v < stationCount) ? topo.nodeSeg[v] : -1;
        h.fromX = u < topo.nodeX.Length ? topo.nodeX[u] : 0f;
        h.toX = v < topo.nodeX.Length ? topo.nodeX[v] : 0f;
        h.fromY = h.fromSeg >= 0 && h.fromSeg < r.segs.Count ? r.segs[h.fromSeg].y : 0f;
        h.toY = h.toSeg >= 0 && h.toSeg < r.segs.Count ? r.segs[h.toSeg].y : 0f;
        h.fromName = SegLabel(r, h.fromSeg);
        h.toName = SegLabel(r, h.toSeg);
        h.dy = h.toY - h.fromY;
        h.dx = Mathf.Abs(h.toX - h.fromX);
        return h;
    }

    static string SegLabel(Report r, int seg)
    {
        if (seg < 0 || seg >= r.segs.Count) return "出生/终点";
        return string.IsNullOrEmpty(r.segs[seg].name) ? "#" + seg : r.segs[seg].name;
    }

    static Report SolveCore(LevelData data, Ability ab, HashSet<string> ignore, float startX, float startY)
    {
        Report r = new Report();
        r.data = data;
        r.ability = ab;
        r.parTime = data.meta.parTime;
        r.drainPerSecond = LevelManager.drainPerSecond;
        r.parRemain = LevelManager.parRemain;

        // 建图：带上本实体的**宽度**（玩家 0.8 / 史莱姆 0.9）——窄过它的槽直接丢掉（本轮改造·携带与史莱姆）
        r.segs = BuildSegments(data, ignore, ab.stepHeight, ab.playerHeight, true, ab.playerWidth);
        if (r.segs.Count == 0) return r;
        // ⚠ 净空表要在**没被矮天花板切开**的走道上量（本轮改造·净空进模型）：
        //   切完之后"隧道下面"已经不是可站立面了，报表就再也说不出"这里原本能走、只是钻不过去"。
        //   切分与这份表用的是**同一个不等式**（见 BuildSegments 里那段注释），所以两者不会打架。
        r.clearance = CheckClearance(data,
            BuildSegments(data, ignore, ab.stepHeight, ab.playerHeight, false, 0f), ab.playerHeight);

        LevelObject goal = data.FindFirst(LevelObjectKind.Goal);
        float goalX = goal != null ? goal.Center.x : 0f;

        List<float> coinXs = new List<float>();
        List<float> coinYs = new List<float>();
        foreach (LevelObject o in data.objects)
            if (o != null && o.kind == LevelObjectKind.Coin)
            {
                coinXs.Add(o.Center.x);
                coinYs.Add(o.Center.y);
            }

        Topology topo;
        int stationCount;
        Graph g = BuildGraph(data, ignore, r.segs, ab, startX, goalX, coinXs, coinYs, out topo, out stationCount);
        r.nodeCount = g.n;
        r.stationCount = stationCount;
        foreach (List<Edge> l in g.fwd) r.edgeCount += l.Count;

        r.startSeg = FindSeg(r.segs, startX, startY);
        r.goalSeg = FindSeg(r.segs, goalX, 0f);
        if (r.startSeg < 0 || r.goalSeg < 0) return r;

        int[] prev;
        float[] fromStart = Dijkstra(g.fwd, topo.startNode, out prev);
        int[] prevRev;
        float[] toGoal = Dijkstra(g.rev, topo.goalNode, out prevRev);   // ★ 反向图才是"到终点的距离"

        r.lowerBound = Mathf.Abs(goalX - startX) / Mathf.Max(0.01f, ab.moveSpeed);

        r.goalReachable = fromStart[topo.goalNode] < float.MaxValue;
        if (r.goalReachable)
        {
            r.minTime = fromStart[topo.goalNode];

            // 回溯路线：先取出完整节点链（含虚拟的起点/终点/金币节点）
            List<int> chain = new List<int>();
            for (int cur = topo.goalNode; cur >= 0; cur = prev[cur])
            {
                if (chain.Count > g.n + 2) break;        // 保险：prev 链异常时不至于死循环
                chain.Add(cur);
                if (cur == topo.startNode) break;
            }
            chain.Reverse();

            // 逐"动作"打印：走 / 跳 / 落 + 单步耗时。只打印段的切换会漏掉中间过程，
            // 看不出"为什么绕了这根柱子"，所以这里把每一次跨段动作都列出来。
            r.pathSteps = new List<string>();
            r.pathHops = new List<Hop>();                       // 本轮改造·携带与史莱姆：给"携带第一个卡在哪"用
            float acc = 0f;
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                int u = chain[k], v = chain[k + 1];
                float c; string kind;
                if (!FindEdge(g.fwd, u, v, out c, out kind)) continue;
                acc += c;
                r.pathHops.Add(MakeHop(r, topo, u, v, kind, c, acc - c, stationCount));
                if (kind == "走" && c < 0.35f && k + 2 < chain.Count) continue;   // 段内小碎步不刷屏
                r.pathSteps.Add(string.Format("{0,5:0.00}s  {1,-2} {2} → {3}",
                    c, kind, NodeLabel(r, topo, u), NodeLabel(r, topo, v)));
            }
            r.pathSum = acc;

            // 概览：只记"跨段"的节点，段内走路不刷屏。
            // ⚠ 括号里的 x 是**该段上真正换段/到达的那个站点**（topo.nodeX[cur]），不是段中心。
            //   历史坑（路线标签查证）：这里原来印 segs[seg].CenterX，于是"A(x97@y0) → B(x85@y2.5)"
            //   会被读成"从 x97 跳到 x85、dx=12" —— 而 97/85 只是两个段的中点，真实跳点是两段的
            //   最近点对（Tunnel_1 落在 G06b.. 的 x 区间里 ⇒ dx=0 的垂直跳）。用户据此误判过报告不可信。
            int lastSeg = -1;
            for (int cur = topo.goalNode; cur >= 0; cur = prev[cur])
            {
                if (cur == topo.startNode) break;
                if (cur >= stationCount) continue;
                int seg = topo.nodeSeg[cur];
                if (seg < 0 || seg == lastSeg) continue;
                lastSeg = seg;
                r.pathDesc.Add(string.Format("{0}(x{1:0.#}@y{2:0.#})",
                    string.IsNullOrEmpty(r.segs[seg].name) ? "#" + seg : r.segs[seg].name,
                    topo.nodeX[cur], r.segs[seg].y));
            }
            r.pathDesc.Reverse();
        }

        // 死点：连一段上的任何站点都到不了。顺手把"哪些段站得上去"记下来（本轮改造·携带与史莱姆：携带/史莱姆对比要用）
        r.reachableSegs = new bool[r.segs.Count];
        for (int i = 0; i < r.segs.Count; i++)
        {
            bool any = false;
            foreach (int nd in topo.nodesOfSeg[i])
                if (fromStart[nd] < float.MaxValue) { any = true; break; }
            r.reachableSegs[i] = any;
            if (!any) r.unreachable.Add(r.segs[i]);
        }

        // 金币取舍
        for (int k = 0; k < coinXs.Count; k++)
        {
            LevelObject o = null;
            int seen = -1;
            foreach (LevelObject cand in data.objects)
            {
                if (cand == null || cand.kind != LevelObjectKind.Coin) continue;
                seen++;
                if (seen == k) { o = cand; break; }
            }
            if (o == null) continue;

            CoinInfo ci = new CoinInfo();
            ci.name = string.IsNullOrEmpty(o.name) ? o.id : o.name;
            ci.x = o.Center.x;
            ci.y = o.Center.y;
            ci.value = Mathf.RoundToInt(o.Param(0, data.meta.coinValue));
            // ⚠ 找段要带上高度：柱顶的金币如果只按 x 找，会被挂到下面 y=0 的地面上，
            //    算出来"绕路 0 秒"，等于白捡 —— 这是错的（求解器 v4 修掉的真 bug）
            ci.segId = FindCoinSeg(r.segs, ci.x, ci.y);   // 本轮改造：同上
            if (ci.segId >= 0) ci.segName = r.segs[ci.segId].name;
            r.coinTotal += ci.value;

            int cn = topo.coinNodes[k];
            if (fromStart[cn] < float.MaxValue && toGoal[cn] < float.MaxValue)
                ci.detour = Mathf.Max(0f, fromStart[cn] + toGoal[cn] - r.minTime);
            else
                ci.detour = 9999f;

            ci.budget = ci.value / Mathf.Max(0.0001f, r.drainPerSecond);
            r.coins.Add(ci);
        }

        // 全收集（本轮改造·全收集口径）：口径改成"求解器**实际拿得到**的币"这一个集合 ——
        //   · 先把它做成位掩码（可收集掩码）；
        //   · 精确耗时 = minTime[可收集掩码][goal]（掩码枚举的精确最优 ⇒ 这是一条**真实存在的路线**）；
        //   · 掩码枚举跑不动时（币 > MaxMaskCoins）才退回"逐枚绕路相加"的估算，并在报表里标明是估算。
        r.collectableMask = 0;
        r.collectableCount = 0;
        r.collectableValue = 0;
        for (int k = 0; k < r.coins.Count && k < 30; k++)      // 30 = 位掩码的安全上限（int）
        {
            if (!r.coins[k].Reachable) continue;
            r.collectableMask |= 1 << k;
            r.collectableCount++;
            r.collectableValue += r.coins[k].value;
        }
        // 兜底值（估算）：拿得到的币的绕路时间逐枚相加。ComputeBestScore 算得动时会用精确值覆盖它。
        float extra = 0f;
        foreach (CoinInfo ci in r.coins) if (ci.Reachable) extra += ci.detour;
        r.allCollectEstimateTime = r.minTime >= float.MaxValue ? float.MaxValue : r.minTime + extra;
        r.allCollectTime = r.allCollectEstimateTime;
        r.allCollectIsExact = false;

        // 【最优解 R*】在（站点 × 金币掩码）状态空间上枚举，求"玩家最多能赚多少"。
        // 放在这里是因为它要复用上面已经建好的图 g 与拓扑 topo（⛔ 不重建图）。
        // 【性能】围着**真实那一次**枚举计时（⛔ 不是另外再跑一遍）；若真的被调用多次就累加。
        System.Diagnostics.Stopwatch perfR = System.Diagnostics.Stopwatch.StartNew();
        ComputeBestScore(r, g, topo);
        perfR.Stop();
        r.bestScoreMs = (r.bestScoreMs < 0.0 ? 0.0 : r.bestScoreMs) + perfR.Elapsed.TotalMilliseconds;

        return r;
    }

    // ======================= 最优解 R*（掩码枚举） =======================
    //
    // 为什么要它：原来的收益差用"无脑把所有金币都拿"当分子 —— 里面混着被标成"不划算 ❌"的
    //   陷阱金币，还混着求解器根本到不了的币，于是"奖励丰不丰厚"这个数字是虚的。
    //   玩家实际会做的是**取舍**：R* = 在"捡哪些币"的所有组合里，结算收益最大的那个。
    //
    // 口径是「⊇ mask」而不是「恰好 mask」—— 这条是精确性的关键，理由：
    //   金币是【走过节点就收】，玩家没法"路过却不捡"。若按"恰好收集了 mask"理解，
    //   转移 mask → mask|bit_j 就隐含了"从 v 走到 coin_j 的路上不会碰到别的未收金币"，
    //   这个假设不成立 ⇒ 算出来的 minTime 未必是"恰好 mask"可达的，分数会虚高。
    //   「⊇ mask」= 收集了 mask 里的币、允许路上多收，它可以证明精确（双向夹逼）：
    //     ① 任意 mask 的 minTime[mask] 都由**某条真实路径**实现（其实收集合 C ⊇ mask）
    //        ⇒ score(mask) = price(t) + Σ_mask ≤ price(t) + Σ_C = 该路径真实得分 ≤ 真最优
    //        ⇒ max ≤ 真最优
    //     ② 设真最优路径 P* 的实收集合是 S、耗时 t*，则 mask = S 时 minTime[S] ≤ t*
    //        ⇒ score(S) ≥ price(t*) + Σ_S = 真最优 ⇒ max ≥ 真最优
    //   ⇒ 两边夹住，R* 就是真最优。（②成立的前提：S 本身是一个合法掩码 —— 它就是那个集合。）
    //
    // 实现：按整数递增枚举 mask。每轮到 mask 先做一次**多源 Dijkstra 闭包**（初值就是
    //   minTime[mask][*]，沿现有走/跳/落边松弛）得到"从任意位置继续自由移动"的最短时间；
    //   再用闭包结果向 mask|bit_j 松弛"最后一个币是 coin_j"。复用现有 Graph/Dijkstra/边权。

    /// <summary>掩码枚举的金币上限。2^16 = 65536 个掩码 × 站点数仍是秒级；
    /// 超过就只警告并跳过 R*（⛔ 不静默、不崩）—— 现在四关最多 16 枚。</summary>
    public const int MaxMaskCoins = 16;

    /// <summary>含 clamp01 的真实结算售价（= 顶部售价条能卖多少钱）。
    /// ⚠ 不许换成线性近似：越过掉血下限 floorTime 之后时间**免费**，
    ///    线性近似会把"多绕一会儿"算成一直掉钱，R* 就会系统性地不捡后段的币。
    /// 口径：满格售价 = 血条点数 × 每点单价 ⇒ **血条点数 = barMax ÷ moneyPerHp**
    /// （旧口径这里的分母是写死的 100，就是"血条恒 100 点"的隐含假设；新口径按 2×parTime 点算）。</summary>
    static int PriceAt(float time, int barMax, float drain)
    {
        if (time >= float.MaxValue) return 0;
        float barPoints = Mathf.Max(1f, barMax / Mathf.Max(0.0001f, LevelManager.moneyPerHp));
        return Mathf.RoundToInt(barMax * Mathf.Clamp01(1f - drain * time / barPoints));
    }

    /// <summary>多源 Dijkstra 闭包：把 dist[off .. off+N) 当成"已有的到达时间"，
    /// 沿边松弛出"从这里继续自由移动"的最短时间（就地修改）。
    /// 为什么需要它：⊇ 口径下路径可以路过别的金币、可以先走到任意站点再回头，
    /// 所以每次都要把"到任意站点的最短时间"闭包出来，才能正确地向下一个币转移。</summary>
    static void Closure(List<Edge>[] g, float[] dist, int off, bool[] done)
    {
        int n = g.Length;
        System.Array.Clear(done, 0, n);
        for (int iter = 0; iter < n; iter++)
        {
            int u = -1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (!done[i] && dist[off + i] < best) { best = dist[off + i]; u = i; }
            if (u < 0) break;                       // 剩下的都到不了
            done[u] = true;

            List<Edge> edges = g[u];
            for (int k = 0; k < edges.Count; k++)
            {
                Edge e = edges[k];
                if (done[e.to]) continue;
                float nd = dist[off + u] + e.cost;
                if (nd < dist[off + e.to]) dist[off + e.to] = nd;
            }
        }
    }

    /// <summary>枚举所有金币掩码，求 R*；并把选中集合/路线填进 Report。</summary>
    static void ComputeBestScore(Report r, Graph g, Topology topo)
    {
        int n = r.coins.Count;
        int N = g.n;
        r.coinCount = n;
        r.maskCount = n >= 62 ? long.MaxValue : (1L << n);

        int barMax = LevelManager.BarMaxFor(r.parTime);
        float drain = LevelManager.DrainRateFor(r.parTime);
        // 掉血下限时刻：血条从满掉到 barFloor 比例要多久 = (1 − barFloor) × 血条点数 ÷ 掉血速度。
        // 新口径：点数 = 2×parTime、速度 = 1 点/秒 ⇒ 1.5 × parTime
        // （旧口径 0.75×100 ÷ (50/parTime) 也是 1.5 × parTime —— 逐格等值，这里只是把 100 换成点数）。
        r.floorTime = (1f - Mathf.Clamp01(LevelManager.barFloor)) * LevelManager.BarPointsFor(r.parTime)
                    / Mathf.Max(0.0001f, drain);

        // 冲刺（一枚不捡）永远是合法解 ⇒ R* 不会比它差；先把它算出来当兜底与对照。
        r.rushScore = PriceAt(r.minTime, barMax, drain);
        // 对照用的"贪心全拿"总分（把陷阱金币、甚至求解器拿不到的币也算进分子）——
        // 它是"不思考的玩家"的参照值，**不是可行解**，所以 R* 可能比它低（例如 Level3）。
        // ⚠ 本轮改造·全收集口径：这里**故意**用旧口径的估算值 allCollectEstimateTime（不是精确全收集），
        //   就是为了保留"不可行解"这个历史对照值；三行对照里已经不再印它。
        r.greedyScore = (r.allCollectEstimateTime < float.MaxValue ? PriceAt(r.allCollectEstimateTime, barMax, drain) : 0) + r.coinTotal;

        if (!r.goalReachable)
        {
            r.bestComputed = false;
            r.bestSkipReason = "起点到不了终点（先修地形断点），R* 无从谈起";
            return;
        }
        if (n > MaxMaskCoins)
        {
            r.bestComputed = false;
            r.bestSkipReason = string.Format("金币 {0} 枚 > 上限 {1} 枚，掩码枚举会爆（该指标跳过）", n, MaxMaskCoins);
            return;
        }
        if (r.startSeg < 0 || topo.startNode < 0 || topo.goalNode < 0)
        {
            r.bestComputed = false;
            r.bestSkipReason = "起点/终点没有落脚段，图不完整";
            return;
        }

        int masks = 1 << n;                       // n = 0 时 = 1（只有空集），不崩
        int[] prev;                               // 只用来拿"从起点出发"的初始行
        float[] fromStart = Dijkstra(g.fwd, topo.startNode, out prev);

        float[] dist = new float[(long)masks * N];
        for (int i = 0; i < dist.Length; i++) dist[i] = float.MaxValue;
        for (int v = 0; v < N; v++) dist[v] = fromStart[v];      // mask = 0：普通最短路

        // mask 的"面值合计"递推（低一位去掉 + 那一枚的面值），避免每个掩码都重扫一遍
        int[] maskSum = new int[masks];
        for (int m = 1; m < masks; m++)
        {
            int low = m & (-m);
            int j = 0;
            while ((low >> j) != 1) j++;
            maskSum[m] = maskSum[m ^ low] + (j < n ? r.coins[j].value : 0);
        }

        // 回溯用：mask 是从哪个币转进来的（捡币顺序由 RecoverBestOrder 反推，见下）
        bool[] done = new bool[N];
        float bestScore = float.MinValue, bestTime = float.MaxValue;
        int bestMask = 0;
        // 每个掩码"到终点的最短时间"（= 该 mask 的 minTime[mask][goal]）。留着只为下面算边际耗时：
        // 边际 = bestTime − minTime[bestMask 去掉那一枚][goal] ⇒ 直接回答"这枚币在最优路线里到底多花几秒"。
        float[] goalOfMask = new float[masks];
        for (int m = 0; m < masks; m++) goalOfMask[m] = float.MaxValue;

        for (int mask = 0; mask < masks; mask++)
        {
            int off = mask * N;
            Closure(g.fwd, dist, off, done);      // 闭包：minTime[mask][全部 v]

            float t = dist[off + topo.goalNode];
            goalOfMask[mask] = t;
            if (t < float.MaxValue)
            {
                int score = PriceAt(t, barMax, drain) + maskSum[mask];
                // 分数相同时取更快的（路线更好看，也更好解释）
                if (score > bestScore || (score == bestScore && t < bestTime))
                { bestScore = score; bestTime = t; bestMask = mask; }
            }

            for (int j = 0; j < n; j++)
            {
                if ((mask & (1 << j)) != 0) continue;
                int coinNode = topo.coinNodes[j];
                float tc = dist[off + coinNode];
                if (tc >= float.MaxValue) continue;            // 这枚（现在还）到不了
                int nm = mask | (1 << j);
                if (tc < dist[nm * N + coinNode]) dist[nm * N + coinNode] = tc;
            }
        }

        r.bestComputed = true;
        r.bestTime = bestTime == float.MaxValue ? r.minTime : bestTime;
        r.bestPrice = PriceAt(r.bestTime, barMax, drain);
        r.bestCoinValue = maskSum[bestMask];
        r.bestScore = r.bestPrice + r.bestCoinValue;
        r.bestCoinCount = CountBits(bestMask);
        r.bestNewGapPercent = r.rushScore > 0 ? (r.bestScore - r.rushScore) * 100f / r.rushScore : 0f;

        // ---- 全收集（精确，可行解）—— 本轮改造·全收集口径 ----
        // goalOfMask[可收集掩码] 就是"把所有拿得到的币都收完再进终点"的**精确**最短耗时
        // （掩码枚举本身就是精确的，见上面那段证明），所以这一行不再是逐枚相加的估算，而是**真实路线**。
        // 两个恒成立关系（报表口径也这么写）：
        //   · R* 耗时 ≤ 全收集耗时：R* 的掩码 ⊆ 可收集掩码，而"⊇ 小掩码"的可行路径集更大 ⇒ 它的最优 ≤ 全收集的最优
        //   · R* 收益 ≥ 全收集收益：全收集只是全体掩码里的一个，R* 取的就是全体最大
        if (r.collectableMask > 0 && r.collectableMask < masks)
        {
            float tAll = goalOfMask[r.collectableMask];
            if (tAll < float.MaxValue)
            {
                r.allCollectExactTime = tAll;
                r.allCollectExactScore = PriceAt(tAll, barMax, drain) + maskSum[r.collectableMask];
                r.collectableValue = maskSum[r.collectableMask];
                r.allCollectTime = tAll;              // ← 覆盖兜底的估算值
                r.allCollectIsExact = true;
            }
        }

        // 捡币顺序：从终点往回推。⚠ 不能拿"最后一次转移记下的那个币"当最后一个 ——
        // 目标值取的是 min_j (T[mask][j] + sp(coin_j → goal))，那个 argmin 未必是最后一次转移的币，
        // 用它会让路线白跑一大圈（真踩过：报出来的路线耗时 63s，而 R* 只有 24s）。
        r.bestCoinIndexes = RecoverBestOrder(topo, g, dist, N, bestMask, n);

        // 线性判据（WorthIt）用的是【单枚往返】的绕路代价，而 R* 是【联合】最优：
        //   · 顺路捎带时，某枚币的边际耗时可以远低于它的单枚绕路 ⇒ 线性判据说"不划算"的币也可能该捡；
        //   · 一旦耗时越过掉血下限 floorTime，之后时间免费，后段的币更是白捡。
        // 这两件事都不是 bug，但会让"R* 只捡值得捡的币"这种口径不成立 ——
        // 所以把数字算出来（只对"选中但被判不划算"的少数几枚），让报告自己解释清楚。
        foreach (int j in r.bestCoinIndexes)
        {
            if (j < 0 || j >= n || r.coins[j].WorthIt) continue;
            float without = goalOfMask[bestMask ^ (1 << j)];
            r.bestCheatIndexes.Add(j);
            r.bestCheatMarginal.Add(without < float.MaxValue ? Mathf.Max(0f, r.bestTime - without) : -1f);
            r.bestCheatSolo.Add(r.coins[j].detour);
        }

        BuildBestRoute(r, g, topo);
    }

    static int CountBits(int m)
    {
        int c = 0;
        while (m != 0) { c += m & 1; m >>= 1; }
        return c;
    }

    /// <summary>
    /// 反推最优解里"捡币顺序"：从终点往回走，每一步在剩下的币里挑
    /// `T[mask][j] + sp(coin_j → next)` 最小的那枚当"这一段的最后一个"。
    /// 为什么必须这么做：目标值 = min_j(T[mask][j] + sp(coin_j→goal))，这个 argmin 未必是
    /// "最后一次松弛记下的那个币"；拿错的话，打印出来的路线会比 bestTime 长一大截
    /// （数字看着自洽、路线却是假的 —— 所以 BuildBestRoute 还会累加逐段耗时互相验证）。
    /// </summary>
    static List<int> RecoverBestOrder(Topology topo, Graph g, float[] dist, int N, int bestMask, int n)
    {
        List<int> order = new List<int>();
        int mask = bestMask;
        int next = topo.goalNode;                  // 先当成"从终点往回看"
        int guard = 0;

        while (mask != 0 && guard++ <= n + 1)
        {
            // sp(coin_j → next)：在**反向图**上从 next 求一次最短路（复用现有 Dijkstra）
            int[] prevRev;
            float[] toNext = Dijkstra(g.rev, next, out prevRev);

            int bestJ = -1;
            float bestV = float.MaxValue;
            for (int j = 0; j < n; j++)
            {
                if ((mask & (1 << j)) == 0) continue;
                int cn = topo.coinNodes[j];
                float at = dist[(long)mask * N + cn];              // T[mask][j]
                if (at >= float.MaxValue || toNext[cn] >= float.MaxValue) continue;
                float v = at + toNext[cn];
                if (v < bestV) { bestV = v; bestJ = j; }
            }
            if (bestJ < 0) break;                                  // 理论上不会：mask 可达就一定挑得出
            order.Add(bestJ);
            mask ^= (1 << bestJ);
            next = topo.coinNodes[bestJ];
        }

        order.Reverse();                                           // 变成"先去哪枚，最后去哪枚"
        return order;
    }

    /// <summary>
    /// 重建 R* 的路线概览：按"捡币顺序"逐段用现有 Dijkstra 取最短段路径，拼成一条真走得到的路线。
    /// 段与段之间可能路过别的金币 —— 这正是「⊇ mask」口径允许的（多捡不亏，只是分数按 mask 算）。
    /// </summary>
    static void BuildBestRoute(Report r, Graph g, Topology topo)
    {
        List<int> stops = new List<int>();
        stops.Add(topo.startNode);
        foreach (int j in r.bestCoinIndexes)
        {
            if (j < 0 || j >= topo.coinNodes.Count) continue;
            stops.Add(topo.coinNodes[j]);
        }
        stops.Add(topo.goalNode);

        List<int> chain = new List<int>();
        float routeTime = 0f;
        for (int k = 0; k + 1 < stops.Count; k++)
        {
            int a = stops[k], b = stops[k + 1];
            if (a == b)
            {
                if (chain.Count == 0 || chain[chain.Count - 1] != b) chain.Add(b);
                continue;
            }
            int[] prev;
            float[] legDist = Dijkstra(g.fwd, a, out prev);
            if (legDist[b] < float.MaxValue) routeTime += legDist[b];      // 逐段最短时间之和（应 = bestTime）
            List<int> leg = new List<int>();
            for (int cur = b; cur >= 0; cur = prev[cur])
            {
                leg.Add(cur);
                if (cur == a) break;
                if (leg.Count > g.n + 2) break;                  // 保险：prev 链异常时不死循环
            }
            if (leg.Count == 0 || leg[leg.Count - 1] != a)
            {
                // 这一段重建不出来（理论上不会发生）：至少把端点接上，别让路线断掉
                if (chain.Count == 0 || chain[chain.Count - 1] != a) chain.Add(a);
                if (chain.Count == 0 || chain[chain.Count - 1] != b) chain.Add(b);
                continue;
            }
            leg.Reverse();
            for (int q = 0; q < leg.Count; q++)
            {
                if (q == 0 && chain.Count > 0 && chain[chain.Count - 1] == leg[0]) continue;   // 接缝去重
                chain.Add(leg[q]);
            }
        }

        // 压成段名概览（与 pathDesc 同口径：只记跨段的切换，段内走路不刷屏）
        // ⚠ 同 pathDesc 的路线标签查证修复：括号里的 x 是**该段上真正换段/到达的站点**（topo.nodeX[cur]），
        //   不是段中心 —— 否则"A(x97@y0) → B(x85@y2.5)"会被读成 dx=12 的非法跳。
        r.bestRouteTime = routeTime;
        int lastSeg = -1;
        foreach (int cur in chain)
        {
            if (cur < 0 || cur >= topo.nodeSeg.Length) continue;      // 跳过虚拟节点
            int seg = topo.nodeSeg[cur];
            if (seg < 0 || seg == lastSeg || seg >= r.segs.Count) continue;
            lastSeg = seg;
            r.bestPathDesc.Add(string.Format("{0}(x{1:0.#}@y{2:0.#})",
                string.IsNullOrEmpty(r.segs[seg].name) ? "#" + seg : r.segs[seg].name,
                topo.nodeX[cur], r.segs[seg].y));
        }
    }

    // ======================= 报告 =======================

    public static string BuildReport(LevelData data, Report r)
    {
        // 【性能】只测"报告组装"这一段（⛔ 不含调用方的 Debug.Log / 场景打开）
        System.Diagnostics.Stopwatch perfAsm = System.Diagnostics.Stopwatch.StartNew();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== 关卡求解报告：" + data.meta.levelId + " ==========");
        sb.AppendLine(string.Format(
            "【能力参数】来源={0}  移速 {1:0.##}  跳高 {2:0.##}  重力 {3:0.##}",
            r.ability.source, r.ability.moveSpeed, r.ability.jumpHeight, r.ability.Gravity));
        sb.AppendLine(string.Format("【地形图】可站立面 {0} 段 / 站点 {1} 个 / 连边 {2} 条",
            r.segs.Count, r.stationCount, r.edgeCount));

        if (r.startSeg < 0 || r.goalSeg < 0)
        {
            sb.AppendLine("【结果】找不到起点或终点所在的可站立面 —— 检查出生点/终点是不是悬空的");
            sb.AppendLine("==========================================");
            return sb.ToString();
        }

        // 自检：任何走法都不可能比"直线跑"更快。求解器算错时这一条会立刻暴露。
        if (r.minTime < float.MaxValue)
            sb.AppendLine(string.Format("【自检】直线下界 {0:0.0}s（水平距离 ÷ 移速）｜ 求解结果 {1:0.0}s  {2}",
                r.lowerBound, r.minTime,
                r.minTime >= r.lowerBound - 0.01f ? "✅ 合理" : "❌ 比直线还快，模型有 bug"));

        if (r.goalReachable)
        {
            sb.AppendLine(string.Format("【可达性】起点 → 终点：**可达 ✅**   最短时间 {0:0.0} 秒（parTime {1:0.#} 秒）",
                r.minTime, r.parTime));
            sb.AppendLine("    口径：**空手玩家**（移速/跳高用空手值）、**已计入净空**（矮通道已切开，时间含翻越/绕行）、");
            sb.AppendLine("    **已计入宽度**（窄过玩家 0.8 的槽钻不过去）。携带态与史莱姆见下面两节 —— 不是这里这一条。");
            sb.AppendLine("    **未计**：机关（门 / 压板 / 移动平台 / 敌人）—— 求解器对它们是盲的，只能靠真人试玩。");
        }
        else
            sb.AppendLine("【可达性】起点 → 终点：**不可达 ❌**   玩家（空手）根本走不到终点，关卡有断点");

        // =====================================================================
        // 本轮改造·携带与史莱姆：三态「谜题可解」 + 携带可达性 + 史莱姆通道
        //   通关条件是「史莱姆进终点」⇒ 只报"空手可达"是不够的，必须把三种口径并列说清。
        // =====================================================================
        if (r.carry != null)
        {
            sb.AppendLine("【谜题可解性·三态】通关条件是「**史莱姆**进终点」，所以「空手可达」不能证明这关能过：");
            sb.AppendLine("    ① 玩家**空手** → 终点：" + ReachLine(r));
            sb.AppendLine("    ② 玩家**携带**史莱姆 → 终点：" + ReachLine(r.carry));
            sb.AppendLine("    ③ **史莱姆单独** → 终点：" + ReachLine(r.slimeAlone));
            sb.AppendLine("    ④ 史莱姆**跟跳**（玩家在旁）→ 终点：" + ReachLine(r.slimeFollowJump));
            sb.AppendLine("    ⇒ " + PuzzleVerdict(r));
        }

        // ---- 【携带可达性】(本轮改造·携带与史莱姆) ----
        if (r.carry != null)
        {
            Ability cab = r.carry.ability;
            sb.AppendLine("【携带可达性】移速/跳高换成携带值（净空 1.2、宽度 0.8 与空手相同）");
            sb.AppendLine(string.Format("    携带包络：移速 {0:0.##}、跳高 {1:0.##}（空手是 {2:0.##} / {3:0.##}）",
                cab.moveSpeed, cab.jumpHeight, r.ability.moveSpeed, r.ability.jumpHeight));
            if (r.carry.goalReachable)
            {
                sb.AppendLine(string.Format("    携带也能到终点 ✅：{0:0.0}s（比空手 {1:0.0}s 慢 {2:+0.0;-0.0}s）",
                    r.carry.minTime, r.minTime, r.carry.minTime - r.minTime));
                sb.AppendLine("    ⇒ 史莱姆可以被搬运到终点（不必依赖机关）。");
            }
            else
            {
                sb.AppendLine("    携带**到不了终点 ❌** —— 下面这一跳就是第一个卡住的地方：");
                Hop blocker = FirstBlockedHop(r, cab);
                if (blocker != null)
                {
                    sb.AppendLine("      " + blocker.Trans());
                    if (blocker.dy > cab.jumpHeight + 0.01f)
                        sb.AppendLine(string.Format(
                            "      需要 dy = {0:+0.##;-0.##} 格 ⇒ **跳不上去**（携带跳高只有 {1:0.##}，差 {2:0.##} 格）；" +
                            "必须先找落脚点，或者靠机关/分段搬运",
                            blocker.dy, cab.jumpHeight, blocker.dy - cab.jumpHeight));
                    else if (blocker.dy >= -0.5f)
                    {
                        // 一次判据审查后：平跳（dy≈0）现在也会被拦下来 —— 措辞必须说清是"平跳跨距不够"
                        string jumpKind = blocker.dy <= 0.01f ? "平跳" : "上跳";
                        float span = MaxJumpDx(blocker.dy, cab);
                        sb.AppendLine(string.Format(
                            "      需要横跨 dx = {0:0.##} 格（dy={1:+0.##;-0.##} 的{2}，携带最多跨 {3:0.##}，差 {4:0.##}）",
                            blocker.dx, blocker.dy, jumpKind, span, blocker.dx - span));
                    }
                    else
                        sb.AppendLine(string.Format(
                            "      需要落下去 dy = {0:0.##} / 横跨 dx = {1:0.##} 格（携带落下去最多跨 {2:0.##}，差 {3:0.##}）",
                            blocker.dy, blocker.dx, FallMaxDx(blocker.dy, cab), blocker.dx - FallMaxDx(blocker.dy, cab)));
                    sb.AppendLine("    ⇒ 史莱姆**不能靠玩家一路搬过去**，必须靠机关/引导石/分段搬运 ——"
                                  + "而求解器对机关是盲的，这一条只能真人验证。");
                }
                else
                    sb.AppendLine("      （找不到「单个卡住的跳」：可能是整片连通域进不去，看下面的段级对比。）");
            }
        }

        // ---- 【史莱姆通道】(本轮改造·携带与史莱姆) ----
        if (r.slimeAlone != null)
        {
            SlimeCaps sl = r.ability.slime;
            sb.AppendLine("【史莱姆通道】史莱姆直径 " + sl.diameter.ToString("0.##") + "（与玩家的 1.2 **分开算**，绝不混用）");
            sb.AppendLine("    能力来源：" + sl.source);
            sb.AppendLine(string.Format("    数值：直径 {0:0.##} / 移速 {1:0.##} / 台阶 {2:0.##} / 重力 {3:0.##} / 跟跳比例 {4:0.##}（玩家跳高 {5:0.##}）",
                sl.diameter, sl.moveSpeed, sl.stepHeight, sl.gravityScale, sl.followJumpRatio, r.ability.jumpHeight));
            // ① 净空角度：谁钻得过谁钻不过（判据与【净空检查】同一个不等式：clear ≤ 需求 + 0.05）
            if (r.clearance.Count == 0)
                sb.AppendLine("    ① 净空：没有任何走道头顶 4 格内有东西 —— 玩家与史莱姆都能直着走过去");
            else
            {
                sb.AppendLine("    ① 净空（谁钻得过）—— 判据：净空 ≤ 需求 + 0.05 就钻不过去");
                foreach (Clearance c in r.clearance)
                {
                    bool playerOk = c.clear > r.ability.playerHeight + 0.05f;
                    bool slimeOk = c.clear > sl.diameter + 0.05f;
                    string verdict = playerOk && slimeOk ? "玩家 ✅ / 史莱姆 ✅"
                                   : (!playerOk && slimeOk ? "玩家 ❌ / **史莱姆 ✅**（它能钻、玩家得翻过去）"
                                   : (!playerOk && !slimeOk ? "玩家 ❌ / **史莱姆也 ❌**（必须被搬过去）" : "玩家 ✅ / 史莱姆 ❌"));
                    sb.AppendLine(string.Format("      {0,-22} x[{1,6:0.#},{2,6:0.#}] 净空 {3,5:0.##} 压顶={4,-12} {5}",
                        c.segName, c.fromX, c.toX, c.clear, c.blocker, verdict));
                }
            }
            // ② 宽度角度：窄槽（比谁的身体还窄）—— 两边解出来的段集合差集就是答案
            //   onlyInPlayer = 玩家(0.8)解里有、史莱姆(0.9)解里没有 ⇒ 玩家能过、史莱姆过不去（窄槽）
            //   onlyInSlime  = 史莱姆解里有、玩家解里没有   ⇒ 史莱姆能过、玩家过不去（矮通道之类，它比玩家小）
            List<string> onlyInPlayer = new List<string>(), onlyInSlime = new List<string>();
            DiffSegSets(r.segs, r.slimeAlone.segs, onlyInPlayer, onlyInSlime);
            if (onlyInPlayer.Count == 0 && onlyInSlime.Count == 0)
                sb.AppendLine("    ② 宽度（窄槽）：本关没有「谁钻得过谁钻不过」的窄槽（两侧段集合一致）");
            else
            {
                if (onlyInPlayer.Count > 0)
                {
                    sb.AppendLine("    ② 宽度：这些地方**史莱姆过不去、玩家能过**（窄过史莱姆 0.9 的槽）：");
                    foreach (string s2 in onlyInPlayer) sb.AppendLine("      " + s2);
                }
                if (onlyInSlime.Count > 0)
                {
                    sb.AppendLine("    ② 宽度/净空：这些地方**玩家过不去、史莱姆能过**（它比玩家小）：");
                    foreach (string s2 in onlyInSlime) sb.AppendLine("      " + s2);
                }
            }
            // ③ 结论：它能不能自己到终点 / 必须被搬的位置
            if (r.slimeAlone.goalReachable)
                sb.AppendLine(string.Format("    ③ 史莱姆**自己能**走到终点 ✅（{0:0.0}s）⇒ 这一关不依赖玩家搬运它",
                    r.slimeAlone.minTime));
            else if (r.slimeFollowJump != null && r.slimeFollowJump.goalReachable)
                sb.AppendLine(string.Format("    ③ 史莱姆自己到不了，但**跟着玩家跳**能到 ✅（{0:0.0}s）⇒ 玩家得在旁边给它带跳",
                    r.slimeFollowJump.minTime));
            else
            {
                sb.AppendLine("    ③ 史莱姆**自己到不了终点 ❌**（跟跳也到不了）⇒ 这些段它必须被玩家搬运/或靠机关：");
                List<string> stuck = new List<string>();
                for (int i = 0; i < r.slimeAlone.reachableSegs.Length && i < r.slimeAlone.segs.Count; i++)
                    if (!r.slimeAlone.reachableSegs[i])
                        stuck.Add(string.Format("{0} x[{1:0.#},{2:0.#}]@y{3:0.#}",
                            SegLabel(r.slimeAlone, i), r.slimeAlone.segs[i].x0, r.slimeAlone.segs[i].x1, r.slimeAlone.segs[i].y));
                for (int i = 0; i < stuck.Count && i < 10; i++) sb.AppendLine("      " + stuck[i]);
                if (stuck.Count > 10) sb.AppendLine(string.Format("      … 共 {0} 段", stuck.Count));
            }
        }

        if (r.pathDesc.Count > 0)
        {
            sb.AppendLine("【最短时间路线】" + string.Join(" → ", r.pathDesc.ToArray()));
            sb.AppendLine("    （括号里是该段上**真正换段的站点** x@y，不是段中心；只列换段点，段内走路不单独列出。" +
                          "相邻两项之间的跳/落必须满足 dx ≤ 跳跃上限，见路线标签查证诊断）");
        }

        if (r.pathSteps != null && r.pathSteps.Count > 0)
        {
            sb.AppendLine(string.Format("【路线明细】逐动作（合计 {0:0.0}s，与最短时间对得上说明回溯无误）：", r.pathSum));
            foreach (string s in r.pathSteps) sb.AppendLine("    " + s);
        }

        if (r.unreachable.Count > 0)
        {
            sb.AppendLine(string.Format("【死点】{0} 段可站立面从起点到不了（玩家永远上不去）：", r.unreachable.Count));
            for (int i = 0; i < r.unreachable.Count && i < 12; i++)
                sb.AppendLine("    " + r.unreachable[i]);
        }

        if (r.dumpSegments)
        {
            sb.AppendLine(string.Format("【可站立面清单】共 {0} 段：", r.segs.Count));
            foreach (Seg s in r.segs)
                sb.AppendLine(string.Format("    {0,-9} x[{1,7:0.##},{2,7:0.##}] 宽{3,5:0.##} 顶y={4,6:0.##}  {5}",
                    "#" + s.id, s.x0, s.x1, s.Length, s.y, s.name));
        }

        // ---- 净空（头顶高度）----
        sb.AppendLine(string.Format("【净空检查】玩家碰撞盒高 {0:0.##} 格（净空低于它 = 玩家钻不过去）", r.ability.playerHeight));
        if (r.clearance.Count == 0)
        {
            sb.AppendLine("    没有任何走道头顶 4 格以内有东西 —— 全是露天 ✅");
        }
        else
        {
            foreach (Clearance c in r.clearance)
            {
                bool blocked = c.clear <= r.ability.playerHeight + 0.05f;   // ★ 与建图切分同一个不等式
                string verdict = blocked
                    ? "玩家钻不过去 ❌ → 该段已就地切开，模型只认翻越/绕行"
                    : "玩家能钻过去 ✅（但对携带中的史莱姆可能仍然太矮）";
                sb.AppendLine(string.Format("    {0,-26} x[{1,6:0.#},{2,6:0.#}] 净空 {3,5:0.##}  压顶={4,-12} {5}",
                    c.segName, c.fromX, c.toX, c.clear, c.blocker, verdict));
            }
            sb.AppendLine(string.Format(
                "    ✅ 上面标了❌的矮通道**已计入模型**：那条走道在净空 < {0:0.##} 的地方被切开，",
                r.ability.playerHeight + 0.05f));
            sb.AppendLine("       玩家只能从天花板顶面翻过去（或绕行）—— 所以【可达性】的时间**已经包含**翻越代价。");
            sb.AppendLine("    ⚠ 仍未建模的**只剩**：机关（门 / 压板 / 移动平台 / 敌人）—— 求解器对它们是盲的。");
            sb.AppendLine("       携带态与史莱姆（直径 0.9）**已经进了模型**，见下面的【携带可达性】与【史莱姆通道】。");
        }

        // ---- 金币取舍 ----
        sb.AppendLine("【金币取舍】每秒时间价值 " + r.drainPerSecond.ToString("0.##") + " 元（= 全局掉钱速度）");
        sb.AppendLine("    名字             位置       面值   绕路代价   预算    挂在哪一段（y=金币高度）      结论         R*选中");
        int worth = 0, trap = 0, unreach = 0;
        foreach (CoinInfo c in r.coins)
        {
            string verdict;
            if (!c.Reachable) { verdict = "拿不到 ❌"; unreach++; }
            else if (c.WorthIt) { verdict = "值得捡 ✅"; worth++; }
            else { verdict = "不划算 ❌"; trap++; }

            string detour = c.Reachable ? string.Format("{0,7:0.0}s", c.detour) : "  无法到达";
            string where = string.IsNullOrEmpty(c.segName) ? "（找不到落脚面 ❌）" : c.segName;
            where = where + string.Format("(y{0:0.#})", c.y);
            // 金币正好落在"矮通道下面"→ 玩家根本进不去那块地方，别被 0.0s 骗了。
            // ⚠ 本轮改造：改成按**位置**判（币的 x 在压顶块的 x 区间里、且币在它底面之下），
            //   不再拿段名比 —— 净空进模型后走道会被切开，段名对不上，而且"顶上的币"会被误判。
            float underClear;
            if (UnderLowCeiling(r, c, out underClear))
                where += string.Format(" ⚠在矮通道下（净空 {0:0.##} < 玩家 {1:0.##}）", underClear, r.ability.playerHeight);
            // R* 那一列：这枚币在"收益最大"的那条路线上（没算 R* 时打 ?，不假装）
            string inBest = !r.bestComputed ? " ?"
                          : (r.bestCoinIndexes.Contains(r.coins.IndexOf(c)) ? " ✅" : " —");
            sb.AppendLine(string.Format("    {0,-15} x{1,-6:0.#} {2,4} 元 {3}  {4,5:0.0}s  {5,-32} {6,-11} {7}",
                c.name, c.x, c.value, detour, c.budget, where, verdict, inBest));
        }
        sb.AppendLine(string.Format("    小结：值得捡 {0} 枚 / 不划算 {1} 枚 / 拿不到 {2} 枚", worth, trap, unreach));

        // ---- 收益估算（三行对照：冲刺（不捡币） / 全收集（精确，可行解） / 最优解 R*）----
        if (r.goalReachable)
        {
            int barMax = LevelManager.BarMaxFor(r.parTime);
            float drain = LevelManager.DrainRateFor(r.parTime);
            int rushPrice = PriceAt(r.minTime, barMax, drain);
            int allPrice = 0, allTotal = 0;
            if (r.allCollectTime < float.MaxValue)
            {
                allPrice = PriceAt(r.allCollectTime, barMax, drain);
                allTotal = allPrice + r.coinTotal;
            }

            sb.AppendLine("【收益估算】");
            sb.AppendLine(string.Format("    冲刺（不捡币）      {0,5:0.0}s → 售价 {1,4} 元 +   0 = {2,4} 元",
                r.minTime, rushPrice, rushPrice));
            // ---- 全收集：精确可行解（本轮改造·全收集口径）----
            // 口径 = 求解器**拿得到**的那一撮币（可收集掩码）。耗时取 minTime[可收集掩码][goal]，
            // 是掩码枚举里的精确最优 ⇒ 它对应一条**真实存在的路线**，不再"逐枚相加、重复计算共享路段"。
            if (r.allCollectTime < float.MaxValue)
            {
                allPrice = PriceAt(r.allCollectTime, barMax, drain);
                allTotal = allPrice + r.collectableValue;
                sb.AppendLine(string.Format("    全收集（{0}）  {1,5:0.0}s → 售价 {2,4} 元 + {3,3} = {4,4} 元",
                    r.allCollectIsExact ? "精确，可行解" : "估算，不可行", r.allCollectTime, allPrice,
                    r.collectableValue, allTotal));
            }
            if (!r.allCollectIsExact)
                sb.AppendLine(string.Format("      ⚠ 这行是**估算**（逐枚绕路相加、含求解器拿不到的币 ⇒ 未必可行）：{0}",
                    r.bestComputed ? "掩码枚举算得动时会给精确值，但它没跑成" : r.bestSkipReason));
            // 把"不可行 / 不划算"的币单独点出来 —— 免得读者以为"全收集"包含了它们
            List<string> un = new List<string>(), unworth = new List<string>();
            foreach (CoinInfo ci in r.coins)
            {
                if (!ci.Reachable) un.Add(ci.name);
                else if (!ci.WorthIt) unworth.Add(ci.name);
            }
            if (un.Count > 0 || unworth.Count > 0)
            {
                sb.AppendLine(string.Format("    不在全收集口径内的币：拿不到 {0} 枚{1}；单枚判据不划算 {2} 枚{3}",
                    un.Count, un.Count > 0 ? "（" + string.Join("、", un.ToArray()) + "）" : "",
                    unworth.Count, unworth.Count > 0 ? "（" + string.Join("、", unworth.ToArray()) + "）" : ""));
            }

            if (r.bestComputed)
            {
                sb.AppendLine(string.Format("    最优解  R*          {0,5:0.0}s → 售价 {1,4} 元 + {2,3} = {3,4} 元",
                    r.bestTime, r.bestPrice, r.bestCoinValue, r.bestScore));
                sb.AppendLine("    口径：**R* ≥ 全收集 恒成立**（R* 是全体掩码上的最大值，而「全收集」只是其中一个掩码）；");
                sb.AppendLine("          R* 也可能恰好**等于**全收集 —— 那就说明本关没有「捡了反而不划算」的币。");
                if (rushPrice > 0)
                {
                    float gap = r.bestNewGapPercent;
                    string tag = gap < 15f ? "⚠ 差 <15%：收集要素接近假选择"
                               : (gap > 35f ? "⚠ 差 >35%：可能变成猜谜" : "✅ 落在 15%~35% 目标区间内");
                    sb.AppendLine(string.Format("    收益差 = (R* ÷ 冲刺) − 1 = {0:+0.0;-0.0}%   {1}", gap, tag));
                    sb.AppendLine("    ⚠ 收益差的分子只有 R* 与冲刺，与「全收集」那一行的口径无关 ⇒ 它不受本行改动影响。");
                    // 旧口径（逐枚相加、含拿不到的币）留一句备查：它是个**不可行**的对照值
                    if (r.allCollectEstimateTime < float.MaxValue)
                    {
                        int legacyTotal = PriceAt(r.allCollectEstimateTime, barMax, drain) + r.coinTotal;
                        sb.AppendLine(string.Format("    （旧口径「贪心全拿」= {0} 元：逐枚绕路相加、还把拿不到的币算进分子 ⇒ 不可行解，已不参与对照）",
                            legacyTotal));
                    }
                }
            }
            else
            {
                sb.AppendLine(string.Format("    最优解  R*          未算：{0}", r.bestSkipReason));
                if (rushPrice > 0 && allTotal > 0)
                    sb.AppendLine(string.Format("    收益差 = 未算（R* 没出来）；全收集那一行的差是 {0:+0.0;-0.0}%",
                        (allTotal - rushPrice) * 100f / rushPrice));
            }
        }

        // ---- 最优解 R*：玩家最多能赚多少（掩码枚举的结果）----
        if (r.goalReachable && r.bestComputed)
        {
            sb.AppendLine(string.Format("【最优解 R*】玩家最多能赚多少（金币 {0} 枚 → 掩码 {1} 个）",
                r.coinCount, r.maskCount));
            sb.AppendLine(string.Format("    耗时 {0:0.0}s → 售价 {1} 元 + {2} 元 = {3} 元",
                r.bestTime, r.bestPrice, r.bestCoinValue, r.bestScore));

            string names = "（一枚不捡）";
            if (r.bestCoinIndexes.Count > 0)
            {
                List<string> nm = new List<string>();
                foreach (int j in r.bestCoinIndexes)
                    if (j >= 0 && j < r.coins.Count) nm.Add(r.coins[j].name);
                names = string.Join(", ", nm.ToArray());
            }
            sb.AppendLine(string.Format("    捡了 {0} 枚：{1}", r.bestCoinCount, names));

            if (r.bestPathDesc.Count > 0)
            {
                sb.AppendLine(string.Format("    路线（逐段合计 {0:0.0}s，与 R* 耗时对得上说明每段都是真实最短路）：" +
                                            string.Join(" → ", r.bestPathDesc.ToArray()),
                    r.bestRouteTime));
                sb.AppendLine("      （括号里是该段上**真正换段的站点** x@y，不是段中心；只列换段点，段内走路不单独列出）");
            }

            // ⚠ 线性判据（WorthIt）与真实结算不一致的地方，必须自己说出来：
            //   WorthIt 比的是【单枚往返】的绕路代价；R* 比的是【联合】路线。
            //   两种情况下线性判据说"不划算"的币捡了其实赚：① 顺路捎带（边际远小于单枚绕路）；
            //   ② 耗时越过掉血下限（此后时间免费）。这条不是 bug，但会影响
            //   "R* 只捡值得捡的币"这类验收口径 —— 所以把数字写出来，别让人猜。
            sb.AppendLine(string.Format("    · 线性判据核对：R* 耗时 {0:0.0}s，掉血下限 {1:0.0}s（{2}）",
                r.bestTime, r.floorTime, r.bestTime > r.floorTime ? "已越过 ⇒ 之后时间免费" : "未越过 ⇒ 时间一直值钱"));
            if (r.bestCheatIndexes.Count == 0)
            {
                sb.AppendLine("      R* 选中的币全部也是线性判据认「值得捡」的（0 枚例外）");
            }
            else
            {
                sb.AppendLine(string.Format("      ⚠ 有 {0} 枚被线性判据标「不划算 ❌」但 R* 仍要捡：", r.bestCheatIndexes.Count));
                for (int k = 0; k < r.bestCheatIndexes.Count; k++)
                {
                    int j = r.bestCheatIndexes[k];
                    string nm = (j >= 0 && j < r.coins.Count) ? r.coins[j].name : ("#" + j);
                    sb.AppendLine(string.Format("        {0}：单枚绕路 {1:0.0}s > 预算 {2:0.0}s，但在最优路线里边际只多 {3:0.0}s",
                        nm, r.bestCheatSolo[k], (j >= 0 && j < r.coins.Count) ? r.coins[j].budget : 0f,
                        r.bestCheatMarginal[k]));
                }
            }
        }
        else if (r.goalReachable && !r.bestComputed)
        {
            sb.AppendLine("【最优解 R*】未算：" + r.bestSkipReason);
        }

        // ---- 逃课 / 装饰障碍检测 ----
        if (r.obstacleTests.Count > 0)
        {
            sb.AppendLine("【逃课 / 装饰障碍检测】把某个障碍移除后重算最短时间，变化越小说明它越可有可无");
            sb.AppendLine("    " + string.Join("\n    ", r.obstacleTests.ToArray()));
        }

        sb.AppendLine("==========================================");

        // 【性能】求解耗时（两个入口都填了才打印；⛔ 这两行必须是报告的**最末两行**）
        perfAsm.Stop();
        if (r.solveMs >= 0.0)
        {
            sb.AppendLine(string.Format("【性能】求解耗时 {0:F2} ms", r.solveMs + perfAsm.Elapsed.TotalMilliseconds));
            if (r.bestScoreMs >= 0.0)
                sb.AppendLine(string.Format("【性能】其中 R* 掩码枚举 {0:F2} ms", r.bestScoreMs));
        }

        return sb.ToString();
    }

    // ======================= 逃课检测 =======================

    /// <summary>
    /// 这枚金币是不是落在"矮到钻不过去"的天花板下面（进不去那块地方 ⇒ 拿不到）。
    /// 判据（本轮改造·口径措辞）：压顶块的 x 区间包含币的 x，且币比压顶块的**底面**还低。
    /// 用位置判而不是段名比 —— 净空进模型后走道会被切开，段名对不上；
    /// 而且"顶在隧道顶面上的币"（x 也在区间里、但 y 更高）必须不算在里面。
    /// </summary>
    static bool UnderLowCeiling(Report r, CoinInfo c, out float clear)
    {
        clear = 0f;
        foreach (Clearance cl in r.clearance)
        {
            if (cl.clear > r.ability.playerHeight + 0.05f) continue;      // 钻得过去 → 不算
            if (c.x < cl.fromX - 0.05f || c.x > cl.toX + 0.05f) continue; // 不在压顶块的 x 区间里
            if (string.IsNullOrEmpty(cl.blocker)) continue;
            float bottom = float.MaxValue;
            foreach (TerrainBlock t in r.data.terrain)
            {
                if (t == null || t.name != cl.blocker) continue;
                if (t.MinY < bottom) bottom = t.MinY;
            }
            if (bottom == float.MaxValue) continue;
            if (c.y >= bottom - 0.05f) continue;                          // 币在压顶块之上（例如隧道顶面上）→ 拿得到
            clear = cl.clear;
            return true;
        }
        return false;
    }

    /// <summary>这块地形"托着"哪几枚金币（时间判据看不见这件事，必须自己点出来）。</summary>
    static string CoinsOnBlock(Report r, string blockName)
    {
        List<string> hit = new List<string>();
        foreach (CoinInfo c in r.coins)
        {
            if (c.segId < 0 || c.segId >= r.segs.Count) continue;
            string segName = r.segs[c.segId].name;
            if (string.IsNullOrEmpty(segName)) continue;
            foreach (string part in segName.Split('+'))
                if (part == blockName) { hit.Add(c.name); break; }
        }
        if (hit.Count == 0) return "";
        string head = hit.Count <= 3
            ? string.Join("、", hit.ToArray())
            : string.Join("、", hit.GetRange(0, 3).ToArray()) + " 等";
        return string.Format("〔它托着 {0}（共 {1} 枚币）—— 时间判据看不见「平台承托金币」这件事〕", head, hit.Count);
    }

    /// <summary>
    /// 逐个移除"障碍类"地形块重算最短时间。
    /// 移除后时间几乎不变 → 这个障碍对**最短时间**没有影响（不等于"没用"：见下面的注解）。
    /// 注意：这是【几何版】，只看玩家走位；机制层（引导石/扔）的检测要等史莱姆层做出来。
    /// </summary>
    static void RunObstacleTests(LevelData data, Ability ab, Report baseR, int maxTests)
    {
        if (!baseR.goalReachable) return;

        List<string> candidates = new List<string>();
        List<string> damageBlocks = new List<string>();
        foreach (TerrainBlock t in data.terrain)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) continue;
            // 水 / 尖刺：时间判据看不见"掉血"，所以不做移除测试，但必须列出来并标注（本轮改造·口径措辞）
            if (t.kind == TerrainKind.Water || t.kind == TerrainKind.Hazard)
            {
                if (!damageBlocks.Contains(t.name)) damageBlocks.Add(t.name);
                continue;
            }
            if (t.kind != TerrainKind.Ground) continue;
            if (t.name.StartsWith("Ground", StringComparison.Ordinal)) continue;
            if (t.name.StartsWith("Wall_", StringComparison.Ordinal)) continue;
            candidates.Add(t.name);
        }

        // 伤害地形必须自己说出来：它对**最短时间**没有影响，但对玩家是掉血/秒杀（本轮改造·口径措辞）
        foreach (string name in damageBlocks)
            baseR.obstacleTests.Add(string.Format(
                "{0,-14} 是伤害地形（掉进去会掉血）→ 对**最短时间**无影响，时间判据看不见它；不做移除测试",
                name));

        int n = 0;
        foreach (string name in candidates)
        {
            if (n >= maxTests) break;
            n++;

            HashSet<string> ignore = new HashSet<string>();
            ignore.Add(name);
            Report r2 = Solve(data, ab, ignore);
            if (!r2.goalReachable)
            {
                baseR.obstacleTests.Add(string.Format("{0,-14} 移除后【不可达】→ 必经障碍 ✅", name));
                continue;
            }
            float delta = r2.minTime - baseR.minTime;
            // 移除之后变**快**（delta < 0）= 它原本在挡路；变**慢** = 它原本是必经的落脚点。
            // 两个方向都说明它有用；只有几乎不变才是"对最短时间没影响"。
            // ⚠ 本轮改造：措辞改成"对**最短时间**"的口径 —— "装饰品"这个词太满：
            //   模型只算得到"玩家走位 + 最短时间"，看不见 ① 承载金币 ② 掉血/伤害 ③ 机制联动。
            string verdict;
            if (delta > 0.4f) verdict = "移除后变慢 → 它是必经的落脚点 ✅";
            else if (delta < -0.4f) verdict = "移除后能抄近路 → 它原本挡着路，是真障碍 ✅";
            else if (Mathf.Abs(delta) > 0.05f) verdict = "对最短时间影响很小 ⚠（<0.4s）";
            else verdict = "对最短时间无影响 → 最短路可以绕过它（不等于「没用」，见括注）";

            // 它是压顶的矮天花板吗？是的话现在已经按"钻不过去"切开进模型了（本轮改造·净空进模型）
            string tag = ActsAsLowCeiling(data, baseR.segs, name, ab.playerHeight)
                ? "〔压顶矮天花板：净空 < 玩家身高，已按「钻不过去」切开进模型〕" : "";
            // 它托着金币吗？时间判据看不见这件事
            string carries = CoinsOnBlock(baseR, name);

            baseR.obstacleTests.Add(string.Format("{0,-14} 移除后最短 {1,5:0.0}s（{2:+0.0;-0.0}s）  {3}{4}{5}",
                name, r2.minTime, delta, verdict, tag, carries));
        }
    }

    /// <summary>这块地形自己会不会变成一段"可站立面"（判据与 BuildSegments 保持一致）。</summary>
    static bool YieldsSegment(LevelData d, string name)
    {
        foreach (TerrainBlock t in d.terrain)
        {
            if (t == null || t.name != name) continue;
            if (t.kind != TerrainKind.Ground) return false;
            if (t.TopY > 14f) return false;                     // 边界墙顶
            if (t.Height > 6f && t.Width < 3f) return false;     // 又高又细 = 墙
            return true;
        }
        return false;
    }

    // ======================= 净空检查 =======================
    //
    // 为什么单独做这一件事：
    //   求解器的图 = "脚底能踩什么"。它天生看不见"头顶有什么"。
    //   于是 1.0 的矮通道在模型里等于不存在，玩家被算成"直接走过去"，
    //   报出的时间偏乐观，而且移除它会得到"没影响"的错误结论。
    //   这里补一次纯几何的高度比较（不是寻路，不违反禁则），把这类地形标出来。

    public class Clearance
    {
        public string segName, blocker;
        public float fromX, toX, clear;
    }

    static List<Clearance> CheckClearance(LevelData d, List<Seg> segs, float needClear)
    {
        List<Clearance> res = new List<Clearance>();
        foreach (Seg s in segs)
        {
            float worst = float.MaxValue;
            string who = "";
            float lo = 0f, hi = 0f;

            foreach (TerrainBlock t in d.terrain)
            {
                if (t == null || t.kind != TerrainKind.Ground) continue;
                float c = t.MinY - s.y;                          // 这块地形的**底面**在我头顶多高
                if (c < 0.05f) continue;                         // 不在头顶（含我自己）
                if (c > 4f) continue;                            // 4 格以上不算压顶，是天空
                float a = Mathf.Max(s.x0, t.MinX);
                float b = Mathf.Min(s.x1, t.MinX + t.Width);
                if (b - a < 0.05f) continue;                     // 水平方向不重叠

                if (c < worst - 0.01f) { worst = c; who = t.name; lo = a; hi = b; }
                else if (Mathf.Abs(c - worst) <= 0.01f)
                {
                    if (a < lo) lo = a;
                    if (b > hi) hi = b;
                }
            }

            if (worst < 4f)
                res.Add(new Clearance { segName = s.name, blocker = who, fromX = lo, toX = hi, clear = worst });
        }
        return res;
    }

    /// <summary>这块地形有没有"压在某段走道头顶、且矮到玩家钻不过去"——真正的天花板。</summary>
    static bool ActsAsLowCeiling(LevelData d, List<Seg> segs, string name, float needClear)
    {
        foreach (TerrainBlock t in d.terrain)
        {
            if (t == null || t.name != name || t.kind != TerrainKind.Ground) continue;
            foreach (Seg s in segs)
            {
                if (Mathf.Min(s.x1, t.MinX + t.Width) - Mathf.Max(s.x0, t.MinX) < 0.05f) continue;
                float c = t.MinY - s.y;
                if (c > 0.05f && c <= needClear + 0.05f) return true;
            }
        }
        return false;
    }

    // ======================= 本轮改造·携带与史莱姆：三态与两种通道判断的小工具 =======================

    /// <summary>三态里的一行：可达就报时间，不可达就报 ❌（并说清是"起点没落脚面"还是"走不到"）。</summary>
    static string ReachLine(Report x)
    {
        if (x == null) return "（未算）";
        if (x.startSeg < 0) return "**起点没有落脚面 ❌**";
        if (x.goalReachable) return string.Format("**可达 ✅** {0:0.0}s", x.minTime);
        return "**不可达 ❌**";
    }

    /// <summary>把"这关能不能过"写成一句不含糊的话（通关条件是史莱姆进终点）。</summary>
    static string PuzzleVerdict(Report r)
    {
        bool bare = r.goalReachable;
        bool carry = r.carry != null && r.carry.goalReachable;
        bool alone = r.slimeAlone != null && r.slimeAlone.goalReachable;
        bool follow = r.slimeFollowJump != null && r.slimeFollowJump.goalReachable;

        string playerWay = bare
            ? (carry ? "玩家空手与携带都能到终点" : "玩家空手能到终点，**但带不动史莱姆**（携带态到不了）")
            : "玩家空手都到不了终点（地形有断点）";
        string slimeWay = alone ? "史莱姆能**自己**走过去 ⇒ 不必依赖搬运"
                        : (follow ? "史莱姆自己走不过去，但能**跟着玩家跳**过去 ⇒ 玩家得在旁边带它"
                        : (carry ? "史莱姆**必须被玩家搬过去**（它自己/跟跳都到不了）"
                                 : "史莱姆**没有任何工具级路径**到终点（自己 / 跟跳 / 被搬都不行）"));
        return playerWay + "；" + slimeWay + "。⛔ 机关（门/压板/移动平台/敌人）不在模型里，最终以真人试玩为准。";
    }

    /// <summary>空手最快路线上，**第一个**被携带包络卡住的跳（没有就返回 null）。</summary>
    static Hop FirstBlockedHop(Report r, Ability cab)
    {
        if (r.pathHops == null) return null;
        foreach (Hop h in r.pathHops)
            if (BlockedBy(h, cab)) return h;
        return null;
    }

    /// <summary>
    /// 这一跳超出了该能力包络吗？（dy 跳不上去 / 缺口跨不过 / 落下去太远）
    /// ⚠ 一次判据审查后修正：**平跳也要按跨距上限判**——原来 dy≈0 直接 `return false`，
    ///   于是报表里"携带态第一个卡住的跳"会跳过前面那些跨不过去的平跳，报到很后面去（它不是第一个）。
    ///   判据与建图时**生成"跳"边的规则**完全对齐（都是 `dx ≤ MaxJumpDx(dy)`），所以结论与数字不受影响。
    ///   `kind == "走"` 的边走道不受跳跃包络约束（窄槽/矮顶在建图那一步就处理掉了），直接放行。
    /// </summary>
    static bool BlockedBy(Hop h, Ability ab)
    {
        if (h.kind == "走") return false;
        if (h.dy > ab.jumpHeight + 0.01f) return true;              // 跳不上去（含平跳时跳高为 0 的情形）
        if (h.dy < -0.5f) return h.dx > FallMaxDx(h.dy, ab);        // 落：按下落时间能横跨多少
        float maxDx = MaxJumpDx(h.dy, ab);                          // 平跳 / 上跳：按该 dy 的跨距上限
        return maxDx <= 0f || h.dx > maxDx;
    }

    /// <summary>落下去时最多能横跨多少（dy &lt; 0 才有意义）。</summary>
    static float FallMaxDx(float dy, Ability ab)
    {
        if (dy >= -0.01f) return 0f;
        return Mathf.Sqrt(2f * (-dy) / ab.Gravity) * ab.moveSpeed;
    }

    /// <summary>两份解出来的段集合做差（同一个高度上，x 区间**没被并集完全覆盖**才算"独有"）。
    /// ⚠ 不能用"区间有重叠"来判同一段：净空切开后，史莱姆那边是一整条 [0,40]、玩家那边是 [0,10]+[20,40]，
    ///   用重叠判会把这条差异吞掉（T11a 就是这么暴露出来的）。</summary>
    static void DiffSegSets(List<Seg> a, List<Seg> b, List<string> onlyInA, List<string> onlyInB)
    {
        for (int i = 0; i < a.Count; i++) if (!CoveredBy(b, a[i])) onlyInA.Add(Desc(a[i]));
        for (int i = 0; i < b.Count; i++) if (!CoveredBy(a, b[i])) onlyInB.Add(Desc(b[i]));
    }

    /// <summary>段 s 的 x 区间是否被 list 里**同高度**的若干段并集完全覆盖。</summary>
    static bool CoveredBy(List<Seg> list, Seg s)
    {
        float need0 = s.x0 + 0.05f, need1 = s.x1 - 0.05f;
        if (need1 <= need0) return true;                       // 本身比容差还短
        List<float[]> spans = new List<float[]>();
        for (int i = 0; i < list.Count; i++)
            if (Mathf.Abs(list[i].y - s.y) < 0.05f) spans.Add(new float[] { list[i].x0, list[i].x1 });
        spans.Sort((p, q) => p[0].CompareTo(q[0]));
        float cur = need0;
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i][0] > cur + 0.02f) break;              // 断了
            if (spans[i][1] > cur) cur = spans[i][1];
            if (cur >= need1) return true;
        }
        return cur >= need1;
    }

    static string Desc(Seg s)
    {
        return string.Format("{0} x[{1:0.##},{2:0.##}]@y{3:0.##}",
            string.IsNullOrEmpty(s.name) ? "#?" : s.name, s.x0, s.x1, s.y);
    }

    // ======================= 求解器自检（对答案） =======================
    //
    // 为什么要有这个：求解器算错了，报告看上去一样"很像那么回事"。
    //   （真事：一开始把"掉进井里"按"跳过去"计价，报告就绕柱子走，数字看着也挺合理。）
    // 所以造几个**答案能手算**的迷你关卡，把结果钉死。以后改求解器、改关卡数据，
    // 跑一遍就知道有没有算坏 —— 这是防止"把已经做好的东西修坏"的唯一办法。

    static int Q(float world) { return Mathf.RoundToInt(world / LevelUnits.Q); }

    static LevelData NewTestLevel(string id, float parTime)
    {
        LevelData d = new LevelData();
        d.meta.levelId = id;
        d.meta.parTime = parTime;
        d.meta.coinValue = 10;
        d.meta.hasSpawn = true;
        return d;
    }

    static void AddSlab(List<TerrainBlock> list, string name, float x0, float x1, float top, float thickness)
    {
        list.Add(new TerrainBlock(name, Q(x0), Q(top - thickness), Q(x1 - x0), Q(thickness), TerrainKind.Ground));
    }

    static LevelObject Pt(LevelObjectKind kind, string id, float x, float y)
    {
        LevelObject o = new LevelObject();
        o.kind = kind; o.id = id; o.name = id;
        o.qx = Q(x); o.qy = Q(y);
        return o;
    }

    static void Say(StringBuilder sb, ref int pass, ref int fail, bool ok, string what, string detail)
    {
        if (ok) pass++; else fail++;
        sb.AppendLine(string.Format("    {0} {1}   {2}", ok ? "✅" : "❌", what, detail));
    }

    /// <summary>自检用：按名字找金币下标（找不到 = -1）。</summary>
    static int CoinIndexByName(Report r, string name)
    {
        for (int i = 0; i < r.coins.Count; i++) if (r.coins[i].name == name) return i;
        return -1;
    }
    static void WantBool(StringBuilder sb, ref int pass, ref int fail, string what, bool got, bool want)
    {
        Say(sb, ref pass, ref fail, got == want, what,
            string.Format("（实际 {0}，预期 {1}）", got, want));
    }

    static void WantNear(StringBuilder sb, ref int pass, ref int fail, string what, float got, float want, float tol)
    {
        Say(sb, ref pass, ref fail, Mathf.Abs(got - want) <= tol, what,
            string.Format("（实际 {0:0.00}，预期 {1:0.00} ±{2:0.00}）", got, want, tol));
    }

    /// <summary>
    /// 求解器自检。返回值 = **失败条数**（0 = 全过），给合并门禁（BatchGates）判过用。
    /// 判据逻辑、阈值、期望值、日志文案一个字没动 —— 只是把原来只打印的 fail 计数器返回出去。
    /// </summary>
    public static int BatchSolverSelfTest()
    {
        Ability ab = new Ability();
        // ⚠ 自检桩用**显式**的玩家高度，不依赖场景（场景里读不到就退回默认值，手算答案就没法固定）。
        //    真实碰撞盒 = Player.prefab 的 m_Size(1,1) × m_LocalScale(0.8,1.2) ⇒ **0.8 宽 × 1.2 高**（本轮改造·玩家高度）。
        //    这里写死数值**不违反**"数值不写死在方法里"：它是测试的**输入基准**，改它 = 换一套手算答案。
        const float selfTestPlayerHeight = 1.2f;
        ab.playerHeight = selfTestPlayerHeight;
        int pass = 0, fail = 0;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== 求解器自检（每个用例的答案都是手算的） ==========");

        // ---- 用例 1：一条直路。答案 = 18 格 ÷ 6.5 = 2.77 秒 ----
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 20f, 0f, 1f);
            LevelData d = NewTestLevel("T1_直路", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 19f, 0.5f) };
            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T1 直路可达", r.goalReachable, true);
            WantNear(sb, ref pass, ref fail, "T1 直路耗时", r.minTime, 18f / 6.5f, 0.06f);
        }

        // ---- 用例 2：4 格宽的坑。空手跳最远 5.87 格 → 跳得过去 ----
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "A", 0f, 10f, 0f, 1f);
            AddSlab(ts, "B", 14f, 24f, 0f, 1f);
            LevelData d = NewTestLevel("T2_四格坑", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 23f, 0.5f) };
            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T2 四格坑跳得过去", r.goalReachable, true);
            // 9/6.5 走 + 0.903 跳 + 9/6.5 走
            WantNear(sb, ref pass, ref fail, "T2 耗时", r.minTime, 9f / 6.5f + 0.9034f + 9f / 6.5f, 0.08f);
        }

        // ---- 用例 3：6 格宽的坑（> 5.87）→ 过不去。这就是"逃课图必须能报出来" ----
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "A", 0f, 10f, 0f, 1f);
            AddSlab(ts, "B", 16f, 26f, 0f, 1f);
            LevelData d = NewTestLevel("T3_六格坑", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 25f, 0.5f) };
            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T3 六格坑过不去（空手跳最远 5.87 格）", r.goalReachable, false);
        }

        // ---- 用例 4：5 格高墙挡住去路，只能借旁边的台阶翻过去 ----
        //      拆掉台阶就翻不过去 → 证明"必经障碍 / 逃课"这条判定是真的在算，不是摆设
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "Ground", 0f, 20f, 0f, 1f);
            AddSlab(ts, "Wall", 10f, 11f, 5f, 5f);       // 顶 5 格：空手跳 3 格，上不去
            AddSlab(ts, "Step", 8f, 9.5f, 2.5f, 1f);     // 顶 2.5 格：站上去才够得着墙顶
            LevelData d = NewTestLevel("T4_台阶翻墙", 20f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 19f, 0.5f) };

            Report r0 = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T4 借台阶能翻过 5 格高墙", r0.goalReachable, true);

            HashSet<string> ign = new HashSet<string>();
            ign.Add("Step");
            Report r1 = Solve(d, ab, ign);
            WantBool(sb, ref pass, ref fail, "T4 抽掉台阶后翻不过去（必经障碍判定有效）", r1.goalReachable, false);

            // 障碍检测本身也要能说出"墙是必经的"
            RunObstacleTests(d, ab, r0, 24);
            string joined = string.Join(" | ", r0.obstacleTests.ToArray());
            WantBool(sb, ref pass, ref fail, "T4 障碍检测报出 Wall 是必经障碍",
                joined.Contains("Wall") && joined.Contains("不可达"), true);
        }

        // ---- 用例 5：金币取舍。预算 = 10 元 ÷ 5 元/秒 = 2 秒 ----
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 40f, 0f, 1f);
            LevelData d = NewTestLevel("T5_金币", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] {
                Pt(LevelObjectKind.Coin, "Near", 5f, 0.5f),     // 顺路，绕路 0
                Pt(LevelObjectKind.Coin, "Far", 35f, 0.5f),      // 终点在 9，来回 52 格 = 8 秒
                Pt(LevelObjectKind.Goal, "Goal", 9f, 0.5f) };
            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T5 两枚金币都找得到", r.coins.Count == 2, true);
            if (r.coins.Count == 2)
            {
                WantNear(sb, ref pass, ref fail, "T5 顺路金币的绕路代价", r.coins[0].detour, 0f, 0.05f);
                WantNear(sb, ref pass, ref fail, "T5 远处金币的绕路代价", r.coins[1].detour, 8f, 0.15f);
                WantBool(sb, ref pass, ref fail, "T5 顺路的 → 值得捡", r.coins[0].WorthIt, true);
                WantBool(sb, ref pass, ref fail, "T5 绕 8 秒的 → 不划算", r.coins[1].WorthIt, false);
            }
        }

        // ---- 用例 6：悬空台上的金币。绕路 = 跳上去 + 掉下来 ----
        //      专门钉死"反向图"那个 bug：回程必须按**掉下来**（0.43s）算，不能按跳下来（0.89s）算。
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 40f, 0f, 1f);
            AddSlab(ts, "Ledge", 8f, 10f, 2.75f, 1.25f);     // 悬空 1.5 格，顶面 2.75
            LevelData d = NewTestLevel("T6_悬空金币", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] {
                Pt(LevelObjectKind.Coin, "OnLedge", 9f, 3.25f),
                Pt(LevelObjectKind.Goal, "Goal", 19f, 0.5f) };
            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T6 悬空台金币找到了", r.coins.Count == 1, true);
            if (r.coins.Count == 1)
            {
                // 8/6.5 走 + 跳上 2.75（0.582）+ 掉下 2.75（0.432）+ 10/6.5 走 - 直线 18/6.5
                float want = 8f / 6.5f + 0.58186f + 0.43230f + 10f / 6.5f - 18f / 6.5f;
                WantNear(sb, ref pass, ref fail, "T6 绕路代价（回程按掉落计价）", r.coins[0].detour, want, 0.06f);
            }
        }

        // ---- 用例 7：掩码枚举出来的最优收益 R*。1 枚顺路 + 1 枚绕 8 秒（预算只有 2 秒）----
        //      为什么必须钉这一条：R* 是"玩家最多能赚多少"，它的口径差一点（例如按"恰好 mask"
        //      而不是"⊇ mask"算），报告里的收益差就会系统性偏大，而报告看上去依然"很像那么回事"。
        //      这里的答案能手算（新口径：血条 = 2×parTime 点、每点 5 元、掉血 1 点/秒 = 5 元/秒）：
        //      parTime = 10 ⇒ barMax = 2×10×5 = 100 元、血条 = 2×10 = 20 点、每秒掉 1 点（= 5 元），
        //      冲刺 1.23s → 94 元；顺路币 1.23s → 94+10 = 104；绕远币 9.23s → 54+10 = 64；
        //      两枚都拿 9.23s → 54+20 = 74 ⇒ R* = 104（只捡顺路那枚）。
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 40f, 0f, 1f);
            LevelData d = NewTestLevel("T7_掩码最优", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] {
                Pt(LevelObjectKind.Coin, "Near", 5f, 0.5f),     // 顺路，绕路 0
                Pt(LevelObjectKind.Coin, "Far", 35f, 0.5f),      // 终点在 9，来回 52 格 = 8 秒 > 预算 2 秒
                Pt(LevelObjectKind.Goal, "Goal", 9f, 0.5f) };
            Report r = Solve(d, ab, null);

            if (!r.bestComputed)
            {
                // 算不出来就是失败（不静默跳过，否则"少算一条"会被当成通过）
                WantBool(sb, ref pass, ref fail, "T7 R* 算得出来（否则整条指标失效）", false, true);
            }
            else
            {
                WantBool(sb, ref pass, ref fail, "T7 R* 只捡划算的那 1 枚", r.bestCoinCount == 1, true);
                WantBool(sb, ref pass, ref fail, "T7 R* 不含陷阱金币（Far）", r.bestCoinIndexes.Contains(1), false);

                int barMax = LevelManager.BarMaxFor(r.parTime);
                float drain = LevelManager.DrainRateFor(r.parTime);
                int rushPrice = PriceAt(r.minTime, barMax, drain);
                // 本轮改造·全收集口径：对照值改成**精确全收集**（r.allCollectTime 现在 = minTime[可收集掩码][goal]，
                // 是可行解）。两条断言的**布尔期望值没变**，只是措辞从"贪心全拿"改成"全收集（可行解）"。
                int allTotal = r.allCollectIsExact
                    ? PriceAt(r.allCollectTime, barMax, drain) + r.collectableValue
                    : PriceAt(r.allCollectTime, barMax, drain) + r.coinTotal;
                WantBool(sb, ref pass, ref fail, "T7 R* 收益 ≥ 冲刺收益", r.bestScore >= rushPrice, true);
                WantBool(sb, ref pass, ref fail, "T7 R* 耗时 ≤ 全收集（精确）耗时", r.bestTime <= r.allCollectTime + 0.01f, true);
                WantBool(sb, ref pass, ref fail, "T7 R* 收益 ≥ 全收集（精确）收益", r.bestScore >= allTotal, true);
            }
        }

        // ---- 用例 8（本轮新增）：矮天花板挡住地面 ⇒ 只能翻上去 ----
        //   地形：走道 G 顶 y=0、x[0,40]；天花板 Block 顶 y=2.0、厚 1.0（底面 y=1.0、x[10,20]）。
        //        底面 − 走道顶 = 净空 **1.0 ≤ 玩家 1.2 + 0.05** ⇒ 钻不过去（与【净空检查】同一个不等式）。
        //   出生 x=1，终点 x=39。
        //   手算（把答案写死在这里，改求解器就必须重新对一遍）：
        //     ① 地上直走 = 38 ÷ 6.5 = **5.846s**  ← 这条**不可行**（天花板下的走道已被切开）
        //     ② 翻越：走 9 格（1.3846）+ 跳上天花板顶 dy=+2.0（JumpTime=(t1+t2)=0.7122）
        //             + 顶上走 10 格（1.5385）+ 从 x=20 落回地面 dy=-2.0（sqrt(2×2/29.43)=0.3687）
        //             + 再走 19 格（2.9231） = **6.927s**
        //     ③ 拆掉 Block 之后又可以直走 ⇒ 回到 5.846s（证明"时间差就是它挡的"）
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 40f, 0f, 1f);
            AddSlab(ts, "Block", 10f, 20f, 2f, 1f);          // 顶 2.0、底面 1.0 ⇒ 净空 1.0
            LevelData d = NewTestLevel("T8_矮天花板", 20f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 39f, 0.5f) };

            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T8 翻过去之后仍然可达", r.goalReachable, true);
            // 这条是 T8 的核心：时间必须等于"翻越"的那一套，而不是"钻过去"的 5.85s
            WantNear(sb, ref pass, ref fail, "T8 耗时 = 翻越耗时（9/6.5 + 跳上 2.0 + 10/6.5 + 落回 2.0 + 19/6.5）",
                r.minTime, 9f / 6.5f + 0.712210f + 10f / 6.5f + 0.368668f + 19f / 6.5f, 0.06f);
            WantBool(sb, ref pass, ref fail, "T8 没有被当成「钻得过去」（≠ 38/6.5 = 5.85s）",
                Mathf.Abs(r.minTime - 38f / 6.5f) > 0.2f, true);
            WantBool(sb, ref pass, ref fail, "T8 【净空检查】报出这条矮通道",
                r.clearance.Count >= 1 && r.clearance[0].clear <= ab.playerHeight + 0.05f, true);

            HashSet<string> ign = new HashSet<string>();
            ign.Add("Block");
            Report r2 = Solve(d, ab, ign);
            WantNear(sb, ref pass, ref fail, "T8 拆掉天花板后可以直走（38 格 ÷ 6.5）", r2.minTime, 38f / 6.5f, 0.06f);
        }

        // ---- 用例 9（本轮新增）：全收集 = 精确可行解，且 R* ≥ 全收集 ----
        //   地形：走道 G 顶 y=0、x[0,40]；出生 x=1，终点 x=9。金币三枚：
        //     Near   x5  （顺路，绕路 0）
        //     Far    x35 （来回 52 格 = 8s > 预算 2s ⇒ 单枚判据"不划算"，但**拿得到**）
        //     Walled x60 （地面上没有这个 x ⇒ 求解器找不到落脚面 ⇒ **拿不到**）
        //   手算（moveSpeed 6.5；parTime 10 ⇒ barMax = 2×10×5 = 100 元、血条 = 2×10 = 20 点、掉血 1 点/秒）：
        //     ① 可收集集合 = {Near, Far}（Walled 不在内）⇒ 掩码 = 0b011
        //     ② 全收集**精确**耗时 = min( 1→Near→Far→goal , 1→Far→Near→goal )
        //          近→远：4/6.5(0.6154) + 30/6.5(4.6154) + 26/6.5(4.0000) = 9.2308s   ← 取它
        //          远→近：34/6.5(5.2308) + 30/6.5(4.6154) + 4/6.5(0.6154) = 10.4615s
        //        ⇒ 它**对应一条真实路线**（这就是"可行解"的意思）
        //     ③ 全收集总分 = PriceAt(9.2308) + 20 = Round(100×(1−1×9.2308/20)) + 20 = 54 + 20 = 74
        //     ④ R*：只捡 Near（1.2308s）⇒ PriceAt = Round(100×(1−1×1.2308/20)) = 94，+10 = 104 ⇒ R* = 104
        //        ⇒ R* 耗时 1.2308 ≤ 全收集 9.2308 ✅、R* 收益 104 ≥ 全收集 74 ✅（两个恒成立关系）
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G", 0f, 40f, 0f, 1f);
            LevelData d = NewTestLevel("T9_全收集可行解", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] {
                Pt(LevelObjectKind.Coin, "Near", 5f, 0.5f),
                Pt(LevelObjectKind.Coin, "Far", 35f, 0.5f),
                Pt(LevelObjectKind.Coin, "Walled", 60f, 0.5f),
                Pt(LevelObjectKind.Goal, "Goal", 9f, 0.5f) };
            Report r = Solve(d, ab, null);

            WantBool(sb, ref pass, ref fail, "T9 三枚币都进了金币表", r.coins.Count == 3, true);
            WantBool(sb, ref pass, ref fail, "T9 可收集集合 = {Near, Far}（Walled 拿不到）",
                r.collectableCount == 2 && CoinIndexByName(r, "Walled") >= 0 &&
                !r.coins[CoinIndexByName(r, "Walled")].Reachable, true);
            WantBool(sb, ref pass, ref fail, "T9 全收集耗时是**精确值**（不是逐枚相加的估算）", r.allCollectIsExact, true);
            WantNear(sb, ref pass, ref fail, "T9 全收集精确耗时（手算 9.2308 = 1→Near→Far→goal）",
                r.allCollectTime, 9.2308f, 0.06f);
            WantNear(sb, ref pass, ref fail, "T9 全收集总分（手算 54 + 20 = 74）", r.allCollectExactScore, 74f, 0.5f);
            WantBool(sb, ref pass, ref fail, "T9 R* 耗时 ≤ 全收集耗时（恒成立）",
                r.bestTime <= r.allCollectTime + 0.01f, true);
            WantBool(sb, ref pass, ref fail, "T9 R* 收益 ≥ 全收集收益（恒成立）",
                r.bestScore >= r.allCollectExactScore, true);
            WantBool(sb, ref pass, ref fail, "T9 R* 只捡顺路的那一枚（Far 虽拿得到但不划算）",
                r.bestComputed && r.bestCoinCount == 1, true);
        }

        // ---- 用例 10（本轮新增·携带与史莱姆）：一条"只有空手能过、携带过不去"的走道 ----
        //   地形：G1 顶 y=0、x[0,10]；G2 顶 y=2.2、x[12,20]（比携带跳高 1.8 高 0.4，空手 3.0 够）。
        //   出生 x=1，终点 x=18。
        //   手算：
        //     ① 空手：这一跳 dy=+2.2、dx=2
        //          跳高 3.0 时跨距 = (t1+t2)×6.5 = (0.45152 + 0.23315)×6.5 = 4.45 ≥ 2 ✅ ⇒ 能过
        //          耗时 = 9/6.5(1.3846) + JumpTime(2.2)=0.68467 + 6/6.5(0.9231) = **2.9924s**
        //     ② 携带：dy=2.2 > carryJumpHeight 1.8 ⇒ MaxJumpDx 返回 -1 ⇒ **不生成跳边** ⇒ 不可达 ❌
        //     ③ 史莱姆单独：它不会跳（跳高 0）⇒ 2 格缺口跨不过去 ❌；跟跳（3.0）才过得了
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G1", 0f, 10f, 0f, 1f);
            AddSlab(ts, "G2", 12f, 20f, 2.2f, 1f);
            LevelData d = NewTestLevel("T10_只有空手能过", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 18f, 2.7f) };

            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T10 空手能跳过 2.2 格台阶", r.goalReachable, true);
            WantNear(sb, ref pass, ref fail, "T10 空手耗时（9/6.5 + 跳 2.2 + 6/6.5）", r.minTime, 2.9924f, 0.06f);
            WantBool(sb, ref pass, ref fail, "T10 携带态**过不去**（携带跳高 1.8 < 2.2）",
                r.carry != null && !r.carry.goalReachable, true);
            Hop b10 = FirstBlockedHop(r, r.carry.ability);
            WantBool(sb, ref pass, ref fail, "T10 携带第一个卡住的就是那一跳（dy=+2.2 / dx=2）",
                b10 != null && Mathf.Abs(b10.dy - 2.2f) < 0.06f && Mathf.Abs(b10.dx - 2f) < 0.06f, true);
            WantBool(sb, ref pass, ref fail, "T10 史莱姆单独过不去（它不会跳）",
                r.slimeAlone != null && !r.slimeAlone.goalReachable, true);
            WantBool(sb, ref pass, ref fail, "T10 史莱姆跟跳能过去",
                r.slimeFollowJump != null && r.slimeFollowJump.goalReachable, true);
        }

        // ---- 用例 11（本轮新增·携带与史莱姆）：史莱姆与玩家的"通道差" ----
        //   ⚠ 先说清一个**几何事实**（查证后才发现，写在这里免得后人再踩）：
        //     ① 按**净空**：史莱姆 0.9 比玩家 1.2 **矮** ⇒ "史莱姆过不去、玩家能过"是不成立的；
        //        能成立的是反过来 —— 净空 1.0 时**史莱姆能钻、玩家不能**（T11a 钉这个方向）。
        //     ② 按**宽度**：关卡数据的世界单位是 0.25 格（LevelUnits.Q），槽宽只能是 0.25 的倍数，
        //        **做不出 0.8~0.9 之间的槽** ⇒ 宽度上也分不出"玩家 0.8 过得了、史莱姆 0.9 过不了"。
        //        宽度规则仍然有用（它挡住 ≤0.75 的槽：这种槽**两边都过不去**），所以 T11b 钉它的机制。
        //     两条合起来：**"史莱姆钻不过、玩家能钻过"在这个项目里做不出来**，最接近的是 T11a（方向相反）。
        {
            // ---- T11a：1.0 净空的通道 ⇒ 史莱姆能钻过去、玩家必须翻上去 ----
            //   地形：G 顶 y=0、x[0,40]；天花板 C 顶 2.0 / 厚 1.0（底面 1.0、x[10,20]）
            //   出生 x=1（史莱姆出生也放 1），终点 x=30。
            //   手算：
            //     · 史莱姆（0.9 < 净空 1.0+0.05）⇒ 通道对它**不切**，它能一路走过去（移速取它自己的 6.0）：
            //         29 ÷ 6.0（史莱姆 followSpeed 是 6）= **4.8333s**
            //     · 玩家（1.2 > 1.0）⇒ 通道被切开，只能翻越：
            //         9/6.5(1.3846) + 跳上 2.0(0.71221) + 10/6.5(1.5385) + 落回 2.0(0.36867)
            //         + 10/6.5(1.5385) = **5.5425s**
            List<TerrainBlock> tsA = new List<TerrainBlock>();
            AddSlab(tsA, "G", 0f, 40f, 0f, 1f);
            AddSlab(tsA, "C", 10f, 20f, 2f, 1f);
            LevelData dA = NewTestLevel("T11a_矮通道", 20f);
            dA.terrain = tsA.ToArray();
            dA.meta.playerX = 1f; dA.meta.playerY = 0.5f;
            dA.meta.slimeX = 1f; dA.meta.slimeY = 0.5f;
            dA.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 30f, 0.5f) };

            Report rA = Solve(dA, ab, null);
            WantNear(sb, ref pass, ref fail, "T11a 玩家必须翻越（9/6.5+跳2.0+10/6.5+落2.0+10/6.5）",
                rA.minTime, 9f / 6.5f + 0.712210f + 10f / 6.5f + 0.368668f + 10f / 6.5f, 0.06f);
            WantBool(sb, ref pass, ref fail, "T11a 史莱姆能**钻过去**（0.9 < 净空 1.0）── 它只走 29 格、移速 6.0",
                rA.slimeAlone != null && Mathf.Abs(rA.slimeAlone.minTime - 29f / 6f) <= 0.06f, true);
            List<string> onlyP = new List<string>(), onlyS = new List<string>();
            if (rA.slimeAlone != null) DiffSegSets(rA.segs, rA.slimeAlone.segs, onlyP, onlyS);
            WantBool(sb, ref pass, ref fail, "T11a 【史莱姆通道】把它列成「玩家过不去、史莱姆能过」", onlyS.Count > 0, true);

            // ---- T11b：0.75 格宽的窄槽 ⇒ 玩家与史莱姆**都钻不过去**（宽度规则的机制）----
            //   地形：地面 G 顶 y=0、x[0,20]；两块墙顶 1.0（底面贴地）：A x[8,8.5]、B x[9.25,9.75]
            //         ⇒ 槽 = [8.5, 9.25] = **0.75 格**（0.25 网格上最窄且能"卡住 0.8/0.9"的值）
            //   手算：0.75 < 玩家 0.8 < 史莱姆 0.9 ⇒ 槽对两边都被丢掉；
            //         玩家还能**跳过**这 1.25 格缺口（跳高 3.0 时跨距 5.87）⇒ 仍然可达；
            //         史莱姆单独不会跳 ⇒ 过不去 ❌。
            List<TerrainBlock> tsB = new List<TerrainBlock>();
            AddSlab(tsB, "G", 0f, 20f, 0f, 1f);
            AddSlab(tsB, "WallA", 8f, 8.5f, 1f, 1f);
            AddSlab(tsB, "WallB", 9.25f, 9.75f, 1f, 1f);
            LevelData dB = NewTestLevel("T11b_窄槽", 20f);
            dB.terrain = tsB.ToArray();
            dB.meta.playerX = 1f; dB.meta.playerY = 0.5f;
            dB.meta.slimeX = 1f; dB.meta.slimeY = 0.5f;
            dB.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 19f, 0.5f) };

            List<Seg> rawB = BuildSegments(dB, null, ab.stepHeight, ab.playerHeight, true, 0f);   // 不启用宽度过滤
            bool rawHasSlot = false;
            foreach (Seg s in rawB)
                if (Mathf.Abs(s.y) < 0.05f && Mathf.Abs(s.Length - 0.75f) < 0.06f) rawHasSlot = true;

            Report rB = Solve(dB, ab, null);
            bool playerHasSlot = false;
            foreach (Seg s in rB.segs)
                if (Mathf.Abs(s.y) < 0.05f && Mathf.Abs(s.Length - 0.75f) < 0.06f) playerHasSlot = true;
            bool slimeHasSlot = false;
            if (rB.slimeAlone != null)
                foreach (Seg s in rB.slimeAlone.segs)
                    if (Mathf.Abs(s.y) < 0.05f && Mathf.Abs(s.Length - 0.75f) < 0.06f) slimeHasSlot = true;

            WantBool(sb, ref pass, ref fail, "T11b 不启用宽度过滤时确实有这条 0.75 的槽", rawHasSlot, true);
            WantBool(sb, ref pass, ref fail, "T11b 玩家（0.8）与史莱姆（0.9）**都**钻不过去这条槽",
                !playerHasSlot && !slimeHasSlot, true);
            WantBool(sb, ref pass, ref fail, "T11b 玩家还能跳过这 1.25 格缺口到终点", rB.goalReachable, true);
            WantBool(sb, ref pass, ref fail, "T11b 史莱姆单独过不去（它不会跳）",
                rB.slimeAlone != null && !rB.slimeAlone.goalReachable, true);
        }

        // ---- 用例 12（本轮新增）：底面高于跳跃顶点的"头顶楼板"不该挡住下面的平跳 ----
        //   地形：G1 顶 y=0、x[0,10]；G2 顶 y=0、x[14,24]（4 格宽坑）；**楼板 Roof 顶 12、厚 2（底面 10）**、
        //         x[8,16]（正是 Level3 的 U01_West 那种：高悬在坑上方）。
        //   出生 x=1，终点 x=23。
        //   手算：
        //     · 跳跃顶点 apex = min(0,0) + 跳高 3.0 − 0.05 = **2.95**；楼板底面 = **10 > 2.95**
        //       ⇒ 玩家从楼板**下面**飞过去，它不该算障碍 ⇒ 平跳 dx=4、dy=0 合法（跨距上限 5.87）
        //     · 于是耗时 = 9/6.5 (1.38462) + JumpTime(0)=0.90304 + 9/6.5 (1.38462) = **3.6723s**
        //       （= 用例 T2 的答案 ⇒ 加不加这块楼板，时间**一样**）
        //     · 反面：把 ceiling 抬到 100（假装楼板够低）时 HighestTopBetween 会看到 12 ⇒ 平跳被否 ⇒ 才变成 5.23 那套
        {
            List<TerrainBlock> ts = new List<TerrainBlock>();
            AddSlab(ts, "G1", 0f, 10f, 0f, 1f);
            AddSlab(ts, "G2", 14f, 24f, 0f, 1f);
            AddSlab(ts, "Roof", 8f, 16f, 12f, 2f);          // 顶 12 / 厚 2 ⇒ 底面 10
            LevelData d = NewTestLevel("T12_头顶楼板", 10f);
            d.terrain = ts.ToArray();
            d.meta.playerX = 1f; d.meta.playerY = 0.5f;
            d.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 23f, 0.5f) };

            Report r = Solve(d, ab, null);
            WantBool(sb, ref pass, ref fail, "T12 楼板下平跳仍然可达（楼板底面 10 > 顶点 2.95）", r.goalReachable, true);
            WantNear(sb, ref pass, ref fail, "T12 耗时 = 跨坑平跳（9/6.5 + 0.90304 + 9/6.5）", r.minTime, 3.6723f, 0.06f);

            float apex = 0f + ab.jumpHeight - 0.05f;                     // 2.95
            float seenLow = HighestTopBetween(d, null, 10f, 14f, apex);   // 正确的 ceiling
            float seenHigh = HighestTopBetween(d, null, 10f, 14f, 100f);  // 假装 ceiling 很高 ⇒ 才看得到楼板
            WantBool(sb, ref pass, ref fail, "T12 判定式：ceiling=顶点(2.95) 时看不到楼板 / 抬高后才看到（10→12）",
                seenLow == float.MinValue && Mathf.Abs(seenHigh - 12f) < 0.01f, true);

            HashSet<string> ign = new HashSet<string>();
            ign.Add("Roof");
            Report r2 = Solve(d, ab, ign);
            WantBool(sb, ref pass, ref fail, "T12 拆掉楼板后时间不变（它本来就没挡路）",
                Mathf.Abs(r2.minTime - r.minTime) < 0.01f, true);
        }

        // ---- 用例 13（本轮新增）：携带态的**平跳跨距上限** ----
        //   手算（carryJumpHeight 1.8、carryMoveSpeed 3.5、Gravity 29.43）：
        //     t1 = t2 = sqrt(2×1.8/29.43) = 0.349749 ⇒ MaxJumpDx(0) = (t1+t2)×3.5 = **2.4482 格**
        //     （对照：空手 sqrt(2×3/29.43)=0.451524 ⇒ (t1+t2)×6.5 = **5.8698 格**）
        //   于是"平跳"也必须按这个上限判：dx=4 的平跳对携带态是**过不去**的（差 1.552），
        //   这正是此前复核指出的"报表里第一个卡住的跳其实是 idx4 那一跳"。
        {
            Ability cab = ab.Carrying();
            WantNear(sb, ref pass, ref fail, "T13 携带态平跳跨距上限 = (t1+t2)×3.5", MaxJumpDx(0f, cab), 2.4482f, 0.01f);
            WantNear(sb, ref pass, ref fail, "T13 空手平跳跨距上限 = (t1+t2)×6.5", MaxJumpDx(0f, ab), 5.8698f, 0.01f);

            Hop flat4 = new Hop(); flat4.kind = "跳"; flat4.dy = 0f; flat4.dx = 4f;
            Hop flat2 = new Hop(); flat2.kind = "跳"; flat2.dy = 0f; flat2.dx = 2f;
            Hop walkFar = new Hop(); walkFar.kind = "走"; walkFar.dy = 0f; walkFar.dx = 99f;
            WantBool(sb, ref pass, ref fail, "T13 平跳 4 格：携带过不去 / 空手过得去",
                BlockedBy(flat4, cab) && !BlockedBy(flat4, ab), true);
            WantBool(sb, ref pass, ref fail, "T13 平跳 2 格：携带过得去（2 < 2.4482）", BlockedBy(flat2, cab), false);
            WantBool(sb, ref pass, ref fail, "T13 走边不受跳跃包络约束", BlockedBy(walkFar, cab), false);

            // 落到模型上：T2 那 4 格坑，携带态**过不去**（只能跳 2.4482 格）
            List<TerrainBlock> tsC = new List<TerrainBlock>();
            AddSlab(tsC, "A", 0f, 10f, 0f, 1f);
            AddSlab(tsC, "B", 14f, 24f, 0f, 1f);
            LevelData dC = NewTestLevel("T13_携带平跳", 10f);
            dC.terrain = tsC.ToArray();
            dC.meta.playerX = 1f; dC.meta.playerY = 0.5f;
            dC.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 23f, 0.5f) };
            Report rC = Solve(dC, ab, null);
            Hop firstBlocked = FirstBlockedHop(rC, rC.carry.ability);
            WantBool(sb, ref pass, ref fail, "T13 4 格坑：空手跳得过、携带过不去",
                rC.goalReachable && rC.carry != null && !rC.carry.goalReachable, true);
            WantBool(sb, ref pass, ref fail, "T13 「第一个卡住的跳」报的就是它（dx=4 平跳，差 1.55）",
                firstBlocked != null && firstBlocked.kind == "跳" && Mathf.Abs(firstBlocked.dx - 4f) < 0.06f
                && Mathf.Abs(firstBlocked.dy) < 0.06f, true);
        }

        // ---- 用例 14（后续复核后新增）：**下落跳（dy<0）的头顶楼板**必须算障碍 ----
        //   前一处修正处理的就是这一类：dy<0 时把"顶点"算成 min(a.y,b.y)+跳高（= 落地侧抬跳高），
        //   楼板底面只要比它高一点点就被当空气 ⇒ 这一跳被误放行。
        //   同构几何（与 Level1 的 Zig_2→Ground 那一跳同型：dx=6 / dy=-5，
        //   中间一块"底面只比起跳点高 1.00"的楼板）：
        //     · G1 顶 5、x[0,6]（出生 x=1 站在上面）
        //     · Slab 顶 30、厚 24 ⇒ **底面 6.0**、x[6,12]（顶 30 > 14 ⇒ 求解器不当它是可站立面，
        //       所以只能从它下面钻过去或从它顶上飞过去，**不能踩上去绕**）
        //     · G2 顶 0、x[12,24]（终点 x=23）
        //   手算（空手：跳高 3.0、移速 6.5、g = 9.81×3 = 29.43）：
        //     · 这一跳要跨 dx=6.00 同时落 dy=-5.00 ⇒ 横跨耗时 t = 6/6.5 = **0.92308s**
        //     · **至少要飞多高**（r*）：由 -5 = v0·t − ½·g·t² 解出
        //         v0 = (½×29.43×0.92308² − 5) / 0.92308 = (12.53822 − 5)/0.92308 = **8.16644**
        //         r* = v0²/(2g) = 66.69073/58.86 = **1.1330 格**
        //       ⇒ 想跨过去，脚必须比起点高 1.13 格；而楼板底面只比起点高 **1.00 格** ⇒ **真机撞楼板侧面**
        //       （"晚点抬头"只会更糟：抬头越晚，剩下的时间越短，需要的 r 更大）
        //     · 纯靠重力走下去只够横跨 sqrt(2×5/29.43)×6.5 = **3.789 格** < 6 ⇒ 不跳也过不去
        //     · 跨距上限 MaxJumpDx(-5) = (t1+t2)×6.5 = (0.451524+0.737335)×6.5 = **7.7275 ≥ 6**
        //       ⇒ **距离上是够得着的**，挡住它的只可能是那块楼板 —— 这正是本用例要钉死的东西
        //   于是：有 Slab ⇒ 终点**不可达**；忽略 Slab ⇒ 同一跳合法，
        //         耗时 = (6-1)/6.5 + JumpTime(-5) + (23-12)/6.5 = 0.76923 + 1.18886 + 1.69231 = **3.6504s**
        {
            List<TerrainBlock> t14 = new List<TerrainBlock>();
            AddSlab(t14, "G1", 0f, 6f, 5f, 1f);
            AddSlab(t14, "Slab", 6f, 12f, 30f, 24f);        // 顶 30 / 厚 24 ⇒ 底面 6.0（只比起跳点高 1.00）
            AddSlab(t14, "G2", 12f, 24f, 0f, 1f);
            LevelData d14 = NewTestLevel("T14_下落跳头顶楼板", 20f);
            d14.terrain = t14.ToArray();
            d14.meta.playerX = 1f; d14.meta.playerY = 5.5f;
            d14.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 23f, 0.5f) };

            Report r14 = Solve(d14, ab, null);
            WantBool(sb, ref pass, ref fail, "T14 下落跳头顶有楼板（底面只高 1.00 < r*=1.13）⇒ 终点不可达",
                r14.goalReachable, false);
            WantBool(sb, ref pass, ref fail, "T14 判定式：dy<0 用老判据（只看最高顶面）时，这块楼板算得进障碍（顶 30）",
                Mathf.Abs(HighestTopBetween(d14, null, 6f, 12f, float.MaxValue) - 30f) < 0.01f, true);
            WantNear(sb, ref pass, ref fail, "T14 这一跳的跨距上限（dy=-5）= (t1+t2)×6.5（够得着 ⇒ 挡它的只能是楼板）",
                MaxJumpDx(-5f, ab), 7.7275f, 0.01f);
            WantNear(sb, ref pass, ref fail, "T14 纯下落只够横跨 sqrt(2×5/g)×6.5（不跳就过不去）",
                Mathf.Sqrt(2f * 5f / ab.Gravity) * ab.moveSpeed, 3.7890f, 0.01f);
            float tCross = 6f / ab.moveSpeed;
            float v0Need = (0.5f * ab.Gravity * tCross * tCross - 5f) / tCross;
            float rStar = v0Need * v0Need / (2f * ab.Gravity);
            WantNear(sb, ref pass, ref fail, "T14 跨这一跳至少要飞高 r* = v0²/(2g)", rStar, 1.1330f, 0.01f);
            WantBool(sb, ref pass, ref fail, "T14 可用净空（楼板底面 6.0 − 起跳点 5.0 = 1.00）< r* ⇒ 真机做不到",
                6f - 5f < rStar, true);

            HashSet<string> ign14 = new HashSet<string>();
            ign14.Add("Slab");
            Report r14b = Solve(d14, ab, ign14);
            WantBool(sb, ref pass, ref fail, "T14 反面：把 Slab 去掉，同一跳就合法（可达）",
                r14b.goalReachable, true);
            WantNear(sb, ref pass, ref fail, "T14 反面耗时 = 5/6.5 + JumpTime(-5) + 11/6.5",
                r14b.minTime, 3.6504f, 0.03f);

            // T14 补充（**判别性**用例）：把楼板从"顶 30 的粗柱子"换成 Level1 Zig_3 那种**薄楼板**
            //   （顶 7.5 / 厚 1.5 ⇒ 底面 6.0，同样只比起跳点高 1.00）。
            //   ⚠ 为什么必须补这一条：上面那根"粗柱子"顶 30 高过一切，**两种修法都会判它挡路**，
            //     分不出对错。薄楼板才分得出：
            //     · 薄楼板的**顶 7.5 ≤ 误用的豁免阈值 7.95** ⇒ 被误判成"翻得过去" ⇒ 多放行一条
            //       G1(x6) --跳--> G2(x12)（dx=6 / dy=-5，1.18886s），总耗时 0.76923+1.18886+1.69231 = **3.6504s**；
            //     · 正确判据下这条边不存在，只能 **G1 → 楼板顶 → G2**：
            //       0.76923 + 跳上楼板 0.63586 + 楼板顶走 6 格 0.92308 + 落 0.71392 + 1.69231 = **4.7344s**；
            //     · 而那条直跳（1.18886）确实比绕路（0.63586+0.92308+0.71392 = 2.27286）快
            //       ⇒ 一旦被误放行，最短时间就会掉到 3.65 ⇒ 这条断言能把它当场抓出来。
            List<TerrainBlock> t14c = new List<TerrainBlock>();
            AddSlab(t14c, "G1", 0f, 6f, 5f, 1f);
            AddSlab(t14c, "Ledge", 6f, 12f, 7.5f, 1.5f);    // 顶 7.5 / 厚 1.5 ⇒ 底面 6.0
            AddSlab(t14c, "G2", 12f, 24f, 0f, 1f);
            LevelData d14c = NewTestLevel("T14b_薄楼板", 20f);
            d14c.terrain = t14c.ToArray();
            d14c.meta.playerX = 1f; d14c.meta.playerY = 5.5f;
            d14c.objects = new LevelObject[] { Pt(LevelObjectKind.Goal, "Goal", 23f, 0.5f) };
            Report r14c = Solve(d14c, ab, null);
            WantNear(sb, ref pass, ref fail, "T14 薄楼板 ⇒ 只能上楼板顶绕（0.76923+0.63586+0.92308+0.71392+1.69231）",
                r14c.minTime, 4.7344f, 0.04f);
            WantBool(sb, ref pass, ref fail, "T14 这条直跳确实更快（1.18886 < 绕路 2.27286）⇒ 本用例能区分对错",
                JumpTime(-5f, ab) < 0.635857f + 6f / ab.moveSpeed + 0.713922f, true);
        }

        sb.AppendLine(string.Format("========== 自检结果：通过 {0} 项 / 失败 {1} 项 ==========", pass, fail));
        if (fail == 0) Debug.Log(sb.ToString());
        else Debug.LogError(sb.ToString());
        if (!Application.isBatchMode)
            EditorUtility.DisplayDialog("求解器自检",
                string.Format("通过 {0} 项，失败 {1} 项 —— 详细结果看 Console", pass, fail), "好");

        return fail;   // 门禁用的结论：失败条数（0 = 全过）
    }

    // ======================= 菜单入口 =======================

    [MenuItem("Tools/呆呆史莱姆/◇◇-1 求解器自检（对答案，改完求解器必跑）", false, 17)]
    public static void SolverSelfTestMenu()
    {
        BatchSolverSelfTest();
    }

    [MenuItem("Tools/呆呆史莱姆/◇◇ 求解当前关卡（最短时间 / 金币取舍 / 逃课检测）", false, 16)]
    public static void SolveActiveMenu()
    {
        Scene scene = EditorSceneManager.GetActiveScene();
        string json = LevelDataWindow.LevelsDir + "/" + scene.name + ".json";
        if (!System.IO.File.Exists(json))
        {
            Debug.LogError("[求解器] 找不到关卡数据：" + json + "（先用 ⓪-1 导出）");
            return;
        }
        LevelData d = LevelData.FromJson(System.IO.File.ReadAllText(json, Encoding.UTF8));
        if (d == null) { Debug.LogError("[求解器] JSON 解析失败：" + json); return; }

        Ability ab = ReadAbility(scene);
        System.Diagnostics.Stopwatch perfSw = System.Diagnostics.Stopwatch.StartNew();   // 【性能】只夹"求解"这一段
        Report r = Solve(d, ab, null);
        r.dumpSegments = true;
        perfSw.Stop(); r.solveMs = perfSw.Elapsed.TotalMilliseconds;                     // ⛔ 不含下面的逃课检测（它要重解 24 次）
        RunObstacleTests(d, ab, r, 24);

        Debug.Log(BuildReport(d, r));
        if (!Application.isBatchMode)
            EditorUtility.DisplayDialog("求解报告", "已输出到 Console（Window → General → Console）", "好");
    }

    /// <summary>命令行入口：三关一起算。Unity.exe ... -executeMethod LevelSolver.BatchSolveAll</summary>
    public static void BatchSolveAll()
    {
        foreach (string name in LevelDataWindow.LevelSceneNames)
        {
            string path = LevelDataWindow.SceneDir + "/" + name + ".unity";
            string json = LevelDataWindow.LevelsDir + "/" + name + ".json";
            if (!System.IO.File.Exists(json)) { Debug.LogWarning("[求解器] 缺少 " + json + "，跳过"); continue; }

            LevelData d = LevelData.FromJson(System.IO.File.ReadAllText(json, Encoding.UTF8));
            if (d == null) { Debug.LogError("[求解器] 解析失败：" + json); continue; }

            Ability ab = new Ability();
            if (System.IO.File.Exists(path))
            {
                Scene sc = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                ab = ReadAbility(sc);
            }

            System.Diagnostics.Stopwatch perfSw = System.Diagnostics.Stopwatch.StartNew();   // 【性能】只夹"求解"这一段
            Report r = Solve(d, ab, null);
            r.dumpSegments = true;
            perfSw.Stop(); r.solveMs = perfSw.Elapsed.TotalMilliseconds;                     // ⛔ 不含下面的逃课检测（它要重解 24 次）
            RunObstacleTests(d, ab, r, 24);
            Debug.Log(BuildReport(d, r));
        }
        Debug.Log("[求解器] 全部完成");
    }
}
