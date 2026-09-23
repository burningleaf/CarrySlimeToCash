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
        public string source = "默认值（没找到场景里的 PlayerController）";

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
            if (box != null && box.size.y > 0.01f) ab.playerHeight = box.size.y;
            ab.source = "场景里的 PlayerController";
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

    static List<Seg> BuildSegments(LevelData d, HashSet<string> ignore, float stepHeight)
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
        List<Seg> split = new List<Seg>();
        foreach (Seg s in merged)
        {
            List<float[]> cuts = new List<float[]>();
            foreach (TerrainBlock t in d.terrain)
            {
                if (t == null || t.kind != TerrainKind.Ground) continue;
                if (ignore != null && !string.IsNullOrEmpty(t.name) && ignore.Contains(t.name)) continue;
                if (t.TopY <= s.y + stepHeight) continue;            // 矮到能直接迈上去，不算墙
                if (t.MinY > s.y + 0.05f) continue;                  // 悬空的 —— 那是天花板/浮台，不是墙
                float a = Mathf.Max(s.x0, t.MinX);
                float b = Mathf.Min(s.x1, t.MinX + t.Width);
                if (b - a < 0.02f) continue;
                cuts.Add(new float[] { a, b });
            }
            if (cuts.Count == 0) { split.Add(s); continue; }

            cuts.Sort((p, q) => p[0].CompareTo(q[0]));
            float cursor = s.x0;
            foreach (float[] c in cuts)
            {
                if (c[0] > cursor + 0.02f) split.Add(new Seg { x0 = cursor, x1 = c[0], y = s.y, name = s.name });
                if (c[1] > cursor) cursor = c[1];
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

    /// <summary>两点之间地面上的最高障碍顶面（判断这一跳/这一落会不会撞上东西）。</summary>
    static float HighestTopBetween(LevelData d, HashSet<string> ignore, float xa, float xb)
    {
        float lo = Mathf.Min(xa, xb), hi = Mathf.Max(xa, xb);
        if (hi - lo < 0.05f) return float.MinValue;                 // 两点重合，中间没东西
        float best = float.MinValue;
        foreach (TerrainBlock t in d.terrain)
        {
            if (t == null || t.kind != TerrainKind.Ground) continue;
            if (ignore != null && !string.IsNullOrEmpty(t.name) && ignore.Contains(t.name)) continue;
            if (Mathf.Min(hi, t.MinX + t.Width) - Mathf.Max(lo, t.MinX) < 0.05f) continue;
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
            int cs = FindSeg(segs, coinXs[k], coinYs[k]);
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
                float topBetween = HighestTopBetween(d, ignore, ta, tb);
                float apex = Mathf.Min(a.y, b.y) + ab.jumpHeight - 0.05f;
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
                    // 下落途中如果半空有平台，人会落在上面，所以这条"直达"边不成立
                    bool clearFall = topBetween <= b.y + 0.05f;
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
        public List<string> obstacleTests = new List<string>();
        public List<Clearance> clearance = new List<Clearance>();
        public float drainPerSecond = 5f;
        public float parRemain = 0.5f;
        public float parTime = 50f;
        public int coinTotal;
        public bool dumpSegments;

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
    }

    public static Report Solve(LevelData data, Ability ab, HashSet<string> ignore)
    {
        Report r = new Report();
        r.data = data;
        r.ability = ab;
        r.parTime = data.meta.parTime;
        r.drainPerSecond = LevelManager.drainPerSecond;
        r.parRemain = LevelManager.parRemain;

        r.segs = BuildSegments(data, ignore, ab.stepHeight);
        if (r.segs.Count == 0) return r;
        r.clearance = CheckClearance(data, r.segs, ab.playerHeight);

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
        Graph g = BuildGraph(data, ignore, r.segs, ab, data.meta.playerX, goalX, coinXs, coinYs, out topo, out stationCount);
        r.nodeCount = g.n;
        r.stationCount = stationCount;
        foreach (List<Edge> l in g.fwd) r.edgeCount += l.Count;

        r.startSeg = FindSeg(r.segs, data.meta.playerX, data.meta.playerY);
        r.goalSeg = FindSeg(r.segs, goalX, 0f);
        if (r.startSeg < 0 || r.goalSeg < 0) return r;

        int[] prev;
        float[] fromStart = Dijkstra(g.fwd, topo.startNode, out prev);
        int[] prevRev;
        float[] toGoal = Dijkstra(g.rev, topo.goalNode, out prevRev);   // ★ 反向图才是"到终点的距离"

        r.lowerBound = Mathf.Abs(goalX - data.meta.playerX) / Mathf.Max(0.01f, ab.moveSpeed);

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
            float acc = 0f;
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                int u = chain[k], v = chain[k + 1];
                float c; string kind;
                if (!FindEdge(g.fwd, u, v, out c, out kind)) continue;
                acc += c;
                if (kind == "走" && c < 0.35f && k + 2 < chain.Count) continue;   // 段内小碎步不刷屏
                r.pathSteps.Add(string.Format("{0,5:0.00}s  {1,-2} {2} → {3}",
                    c, kind, NodeLabel(r, topo, u), NodeLabel(r, topo, v)));
            }
            r.pathSum = acc;

            // 概览：只记"跨段"的节点，段内走路不刷屏
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
                    r.segs[seg].CenterX, r.segs[seg].y));
            }
            r.pathDesc.Reverse();
        }

        // 死点：连一段上的任何站点都到不了
        for (int i = 0; i < r.segs.Count; i++)
        {
            bool any = false;
            foreach (int nd in topo.nodesOfSeg[i])
                if (fromStart[nd] < float.MaxValue) { any = true; break; }
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
            ci.segId = FindSeg(r.segs, ci.x, ci.y);
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

        // 全收集估算：把每枚拿得到的金币的绕路时间加起来（保守上界，不是精确联合最优）
        float extra = 0f;
        foreach (CoinInfo ci in r.coins) if (ci.Reachable) extra += ci.detour;
        r.allCollectTime = r.minTime >= float.MaxValue ? float.MaxValue : r.minTime + extra;

        // 【最优解 R*】在（站点 × 金币掩码）状态空间上枚举，求"玩家最多能赚多少"。
        // 放在这里是因为它要复用上面已经建好的图 g 与拓扑 topo（⛔ 不重建图）。
        ComputeBestScore(r, g, topo);

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
    ///    线性近似会把"多绕一会儿"算成一直掉钱，R* 就会系统性地不捡后段的币。</summary>
    static int PriceAt(float time, int barMax, float drain)
    {
        if (time >= float.MaxValue) return 0;
        return Mathf.RoundToInt(barMax * Mathf.Clamp01(1f - drain * time / 100f));
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
        r.floorTime = (1f - Mathf.Clamp01(LevelManager.barFloor)) * 100f / Mathf.Max(0.0001f, drain);

        // 冲刺（一枚不捡）永远是合法解 ⇒ R* 不会比它差；先把它算出来当兜底与对照。
        r.rushScore = PriceAt(r.minTime, barMax, drain);
        // 对照用的"贪心全拿"总分（把陷阱金币、甚至求解器拿不到的币也算进分子）——
        // 它是"不思考的玩家"的参照值，**不是可行解**，所以 R* 可能比它低（例如 Level3）。
        r.greedyScore = (r.allCollectTime < float.MaxValue ? PriceAt(r.allCollectTime, barMax, drain) : 0) + r.coinTotal;

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
                r.segs[seg].CenterX, r.segs[seg].y));
        }
    }

    // ======================= 报告 =======================

    public static string BuildReport(LevelData data, Report r)
    {
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
            sb.AppendLine(string.Format("【可达性】起点 → 终点：**可达 ✅**   最短时间 {0:0.0} 秒（parTime {1:0.#} 秒）",
                r.minTime, r.parTime));
        else
            sb.AppendLine("【可达性】起点 → 终点：**不可达 ❌**   玩家根本走不到终点，关卡有断点");

        if (r.pathDesc.Count > 0)
            sb.AppendLine("【最短时间路线】" + string.Join(" → ", r.pathDesc.ToArray()));

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
                string verdict = c.clear <= r.ability.playerHeight + 0.05f
                    ? "玩家钻不过去 ❌（必须绕行 / 翻上去）"
                    : "玩家能钻过去 ✅（但对携带中的史莱姆可能仍然太矮）";
                sb.AppendLine(string.Format("    {0,-26} x[{1,6:0.#},{2,6:0.#}] 净空 {3,5:0.##}  压顶={4,-12} {5}",
                    c.segName, c.fromX, c.toX, c.clear, c.blocker, verdict));
            }
            sb.AppendLine("    ⚠ 求解器是「脚底可站立面」模型，看不见头顶：");
            sb.AppendLine("      上面这些路段的最短时间是按**直接走过去**算的，偏乐观；钻不过去时真实耗时更多。");
            sb.AppendLine("      史莱姆（直径 0.9）+ 举着它的高度，本模型一概不管，需要人工判断。");
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
            // 金币挂在一段玩家钻不进去的走道上（例如 1.0 的矮通道）→ 玩家根本拿不到，别被"0.0s"骗了
            foreach (Clearance cl in r.clearance)
                if (cl.segName == c.segName && cl.clear <= r.ability.playerHeight + 0.05f)
                { where += " ⚠玩家钻不进去"; break; }
            // R* 那一列：这枚币在"收益最大"的那条路线上（没算 R* 时打 ?，不假装）
            string inBest = !r.bestComputed ? " ?"
                          : (r.bestCoinIndexes.Contains(r.coins.IndexOf(c)) ? " ✅" : " —");
            sb.AppendLine(string.Format("    {0,-15} x{1,-6:0.#} {2,4} 元 {3}  {4,5:0.0}s  {5,-32} {6,-11} {7}",
                c.name, c.x, c.value, detour, c.budget, where, verdict, inBest));
        }
        sb.AppendLine(string.Format("    小结：值得捡 {0} 枚 / 不划算 {1} 枚 / 拿不到 {2} 枚", worth, trap, unreach));

        // ---- 收益估算（三行对照：冲刺 / 贪心全拿 R̄ / 最优解 R*）----
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
            sb.AppendLine(string.Format("    冲刺（不捡币）  {0,5:0.0}s → 售价 {1,4} 元 +   0 = {2,4} 元",
                r.minTime, rushPrice, rushPrice));
            // R̄ 保留原来的"全收集"估算（不思考的玩家），但它把陷阱金币、甚至求解器拿不到的币
            // 也算进了分子 ⇒ 只是对照，不是可行解 ⇒ 必须标出来。
            sb.AppendLine(string.Format("    贪心全拿 R̄      {0,5:0.0}s → 售价 {1,4} 元 + {2,3} = {3,4} 元（含陷阱金币/拿不到的币，非最优）",
                r.allCollectTime, allPrice, r.coinTotal, allTotal));

            if (r.bestComputed)
            {
                sb.AppendLine(string.Format("    最优解  R*      {0,5:0.0}s → 售价 {1,4} 元 + {2,3} = {3,4} 元",
                    r.bestTime, r.bestPrice, r.bestCoinValue, r.bestScore));
                if (rushPrice > 0)
                {
                    float gap = r.bestNewGapPercent;
                    string tag = gap < 15f ? "⚠ 差 <15%：收集要素接近假选择"
                               : (gap > 35f ? "⚠ 差 >35%：可能变成猜谜" : "✅ 落在 15%~35% 目标区间内");
                    sb.AppendLine(string.Format("    收益差 = (R* ÷ 冲刺) − 1 = {0:+0.0;-0.0}%   {1}", gap, tag));
                    sb.AppendLine(string.Format("    （旧口径「全收集」的差是 {0:+0.0;-0.0}%，改成 R* 后会变 —— 它剔除了捡了反而亏的币）",
                        (allTotal - rushPrice) * 100f / rushPrice));
                }
            }
            else
            {
                sb.AppendLine(string.Format("    最优解  R*      未算：{0}", r.bestSkipReason));
                if (rushPrice > 0)
                    sb.AppendLine(string.Format("    收益差 = 未算（R* 没出来）；旧口径「全收集」的差是 {0:+0.0;-0.0}%",
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
                sb.AppendLine(string.Format("    路线（逐段合计 {0:0.0}s）：" + string.Join(" → ", r.bestPathDesc.ToArray()),
                    r.bestRouteTime));

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
        return sb.ToString();
    }

    // ======================= 逃课检测 =======================

    /// <summary>
    /// 逐个移除"障碍类"地形块重算最短时间。
    /// 移除后时间几乎不变 → 这个障碍对最优路线没有影响（对玩家层来说是装饰品）。
    /// 注意：这是【几何版】，只看玩家走位；机制层（引导石/扔）的检测要等史莱姆层做出来。
    /// </summary>
    static void RunObstacleTests(LevelData data, Ability ab, Report baseR, int maxTests)
    {
        if (!baseR.goalReachable) return;

        List<string> candidates = new List<string>();
        foreach (TerrainBlock t in data.terrain)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) continue;
            if (t.kind != TerrainKind.Ground) continue;
            if (t.name.StartsWith("Ground", StringComparison.Ordinal)) continue;
            if (t.name.StartsWith("Wall_", StringComparison.Ordinal)) continue;
            candidates.Add(t.name);
        }

        int n = 0;
        foreach (string name in candidates)
        {
            if (n >= maxTests) break;
            n++;

            // ⚠ 这个模型里只有"可站立面"。压在走道头顶的天花板根本不参与建图，
            //    移除它当然算不出变化 —— 直接报"装饰品"是**误判**。
            //    所以先分类：它是不是把某段走道压得玩家钻不过去？
            if (ActsAsLowCeiling(data, baseR.segs, name, ab.playerHeight))
            {
                baseR.obstacleTests.Add(string.Format(
                    "{0,-14} 压在走道头顶的天花板（净空 < 玩家身高 {1:0.##}）→ 面模型看不见它，改看【净空检查】",
                    name, ab.playerHeight));
                continue;
            }

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
            // 两个方向都说明它有用；只有几乎不变才是真的装饰品。
            string verdict;
            if (delta > 0.4f) verdict = "移除后变慢 → 它是必经的落脚点 ✅";
            else if (delta < -0.4f) verdict = "移除后能抄近路 → 它原本挡着路，是真障碍 ✅";
            else if (Mathf.Abs(delta) > 0.05f) verdict = "影响很小 ⚠";
            else verdict = "完全不影响 —— 可以绕过，等于装饰品 ❌";
            baseR.obstacleTests.Add(string.Format("{0,-14} 移除后最短 {1,5:0.0}s（{2:+0.0;-0.0}s）  {3}",
                name, r2.minTime, delta, verdict));
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

    public static void BatchSolverSelfTest()
    {
        Ability ab = new Ability();
        ab.playerHeight = 1f;                  // 与场景里的玩家碰撞盒一致（0.8 × 1.0）
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
        //      这里的答案能手算：barMax = 2×10×5 = 100，drain = (1−0.5)×100/10 = 5（元/秒·血条），
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
                int greedyTotal = PriceAt(r.allCollectTime, barMax, drain) + r.coinTotal;
                WantBool(sb, ref pass, ref fail, "T7 R* 收益 ≥ 冲刺收益", r.bestScore >= rushPrice, true);
                WantBool(sb, ref pass, ref fail, "T7 R* 耗时 ≤ 贪心全拿耗时", r.bestTime <= r.allCollectTime + 0.01f, true);
                WantBool(sb, ref pass, ref fail, "T7 R* 收益 ≥ 贪心全拿收益", r.bestScore >= greedyTotal, true);
            }
        }

        sb.AppendLine(string.Format("========== 自检结果：通过 {0} 项 / 失败 {1} 项 ==========", pass, fail));
        if (fail == 0) Debug.Log(sb.ToString());
        else Debug.LogError(sb.ToString());
        if (!Application.isBatchMode)
            EditorUtility.DisplayDialog("求解器自检",
                string.Format("通过 {0} 项，失败 {1} 项 —— 详细结果看 Console", pass, fail), "好");
    }

    // ======================= 菜单入口 =======================

    [MenuItem("Tools/呆呆史莱姆/◇◇-1 求解器自检（对答案，改完求解器必跑）", false, 85)]
    public static void SolverSelfTestMenu()
    {
        BatchSolverSelfTest();
    }

    [MenuItem("Tools/呆呆史莱姆/◇◇ 求解当前关卡（最短时间 / 金币取舍 / 逃课检测）", false, 86)]
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
        Report r = Solve(d, ab, null);
        r.dumpSegments = true;
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

            Report r = Solve(d, ab, null);
            r.dumpSegments = true;
            RunObstacleTests(d, ab, r, 24);
            Debug.Log(BuildReport(d, r));
        }
        Debug.Log("[求解器] 全部完成");
    }
}
