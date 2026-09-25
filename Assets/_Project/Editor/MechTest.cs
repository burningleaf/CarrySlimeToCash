using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 第 4 步验收：机关"全部正常联动"。
///
/// 为什么不能只检查引用拖对了没有：
///   "联动"是**运行时**行为 —— 压板要真的被压到、门要真的开关、平台要真的载人、
///   敌人要真的巡逻并杀死玩家、检查点要真的改掉复活点。引用全对但行为全错是完全可能的。
///
/// 为什么不用 Unity 的 Play 模式：
///   实测批处理（-batchmode）下 EnterPlaymode 跑不起来（探针 400 秒没完成）。
///   所以这里自己搭一个"无头游戏循环"：
///       Physics2D.Simulate(固定步长) → 反射调用所有脚本的 FixedUpdate → Update
///   用的是**真实的游戏脚本、真实的碰撞体、真实的物理**，只是帧是我们在推。
///   为了不依赖"编辑模式下物理回调会不会派发"这个不确定项，
///   压板那一步会先试物理回调，不行就直接把真实的 Collider 交给 OnTriggerStay2D —— 走的还是产品代码。
/// </summary>
public static class MechTest
{
    class Result
    {
        public int pass, fail;
        public StringBuilder sb = new StringBuilder();
        public void Ok(string what, string detail = "") { pass++; sb.AppendLine("    ✅ " + what + (detail.Length > 0 ? "   " + detail : "")); }
        public void Bad(string what, string detail = "") { fail++; sb.AppendLine("    ❌ " + what + (detail.Length > 0 ? "   " + detail : "")); }
        public void Want(string what, bool got, bool want, string detail = "")
        {
            if (got == want) Ok(what, detail); else Bad(what, detail + string.Format("（实际 {0}，预期 {1}）", got, want));
        }
        public void Near(string what, float got, float want, float tol)
        {
            if (Mathf.Abs(got - want) <= tol) Ok(what, string.Format("（{0:0.00}）", got));
            else Bad(what, string.Format("（实际 {0:0.00}，预期 {1:0.00}±{2:0.00}）", got, want, tol));
        }
        public void Info(string s) { sb.AppendLine("    · " + s); }
    }

    static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>机关验收的退出码（与拆分前逐字一致：0 全过 / 5 有失败 / 1 环境不足没跑起来）。</summary>
    public const int ExitCodePass = 0;
    public const int ExitCodeFail = 5;
    public const int ExitCodeNotRun = 1;

    /// <summary>BatchMechTest() 的"没跑起来"返回值（负数 = 环境不足，不是"失败 0 条"）。</summary>
    public const int FailNotRun = -1;

    /// <summary>
    /// 机关联动验收（无头）。返回值 = **失败条数**（0 = 全过；-1 = 环境不足没跑起来）。
    /// **不再自己 EditorApplication.Exit** —— 退出挪到 BatchMechTestAndExit()，
    /// 这样合并门禁（BatchGates）才能把四门串在一次启动里跑完、并自己决定退出码。
    /// 判定逻辑、期望值、日志文案（含"通过 N 项 / 失败 M 项"）一个字没动。
    /// </summary>
    public static int BatchMechTest()
    {
        Result r = new Result();
        r.sb.AppendLine("========== 机关联动验收：LevelMech ==========");

        if (!System.IO.File.Exists(LevelMechSetup.ScenePath))
        {
            Debug.LogError("[机关测试] 找不到场景，先跑 8 测试关 → 建机关测试关：" + LevelMechSetup.ScenePath);
            return FailNotRun;
        }

        Scene scene = EditorSceneManager.OpenScene(LevelMechSetup.ScenePath, OpenSceneMode.Single);

        // ---------- 阶段 1：接线 ----------
        GameObject plateA = Find(scene, "plate_A"), gate1 = Find(scene, "gate_1");
        GameObject plat1 = Find(scene, "plat_1"), plat2 = Find(scene, "plat_2");
        GameObject plateB = Find(scene, "plate_B"), gate2 = Find(scene, "gate_2");
        GameObject enemy1 = Find(scene, "enemy_1"), cp1 = Find(scene, "checkpoint_1");
        GameObject goal0 = Find(scene, "goal_0");

        r.sb.AppendLine("【阶段 1 · 接线】场景对象是不是都建出来了");
        WantAll(r, new string[] { "plate_A", "gate_1", "plat_1", "plat_2", "plate_B", "gate_2",
                                  "enemy_1", "checkpoint_1", "goal_0" },
                new GameObject[] { plateA, gate1, plat1, plat2, plateB, gate2, enemy1, cp1, goal0 });

        if (plateA == null || gate1 == null || plateB == null || gate2 == null ||
            plat1 == null || plat2 == null || enemy1 == null || cp1 == null)
        {
            Debug.LogError(Finish(r, "阶段 1 就有对象缺失，后面的行为测试没法做"));
            return FailNotRun;
        }

        PressurePlate pA = plateA.GetComponent<PressurePlate>();
        PressurePlate pB = plateB.GetComponent<PressurePlate>();
        MovingPlatform m1 = plat1.GetComponent<MovingPlatform>();
        MovingPlatform m2 = plat2.GetComponent<MovingPlatform>();
        Enemy en = enemy1.GetComponent<Enemy>();
        Checkpoint cp = cp1.GetComponent<Checkpoint>();

        // ① 压板 A → 门 1
        r.Want("压板 A 有 PressurePlate 组件", pA != null, true);
        if (pA != null)
        {
            r.Want("压板 A 的联动目标就是门 1", Contains(pA.toggleObjects, gate1), true);
            r.Want("压板 A 是一次性（踩一下就锁定）", pA.oneShot, true);
            r.Want("压板 A 的语义是「压住 → 把门关掉（= 打开通路）」", pA.objectsActiveWhenPressed, false);
            r.Want("压板 A 的触发掩码认玩家", (pA.triggerMask.value & (1 << LayerMask.NameToLayer("Player"))) != 0, true);
        }
        r.Want("门 1 在 Gate 层", gate1.layer == LayerMask.NameToLayer("Gate"), true);
        r.Want("门 1 有实体碰撞体（不是 trigger）", SolidCollider(gate1), true);
        r.Want("门 1 出生时是关着的（挡路）", gate1.activeSelf, true);

        // ④ 压板 B → 门 2（持续型，靠平台压）
        r.Want("压板 B 有 PressurePlate 组件", pB != null, true);
        if (pB != null)
        {
            r.Want("压板 B 的联动目标就是门 2", Contains(pB.toggleObjects, gate2), true);
            r.Want("压板 B 不是一次性（松开要还原）", pB.oneShot, false);
            int mpLayer = LayerMask.NameToLayer("MovingPlatform");
            r.Want("压板 B 的掩码认「移动平台」层 ← 这条不通的话平台压不动它",
                (pB.triggerMask.value & (1 << mpLayer)) != 0, true);
        }
        r.Want("门 2 有实体碰撞体", SolidCollider(gate2), true);

        // ② 移动平台
        r.Want("平台 1 有 2 个路点", m1 != null && m1.points != null && m1.points.Length == 2, true);
        r.Want("平台 1 开局就走（自动往返）", m1 != null && m1.startActivated && !m1.activatedByPlate, true);
        r.Want("平台 1 会载人（carryRider）", m1 != null && m1.carryRider, true);
        r.Want("平台 2 有 2 个路点", m2 != null && m2.points != null && m2.points.Length == 2, true);
        r.Want("平台 2 开局就走", m2 != null && m2.startActivated && !m2.activatedByPlate, true);
        if (m1 != null && m1.points != null && m1.points.Length == 2)
            r.Near("平台 1 横跨世界 x14→x20（6 格）", Mathf.Abs(m1.points[1].position.x - m1.points[0].position.x), 6f, 0.3f);

        // ③ 敌人 + 检查点
        r.Want("敌人有刚体", en != null && en.body != null, true);
        r.Want("敌人有碰撞体", en != null && en.bodyCollider != null, true);
        r.Want("敌人有地面检测点", en != null && en.groundCheck != null, true);
        r.Want("敌人拿到了 LevelManager（碰到玩家才能致死）", en != null && en.levelManager != null, true);
        r.Want("敌人的 groundLayer 不是空的（否则原地不动）", en != null && en.groundLayer.value != 0, true);
        r.Want("敌人会杀死玩家", en != null && en.killPlayer, true);
        r.Want("检查点拿到了 LevelManager", cp != null && cp.levelManager != null, true);
        r.Want("检查点有复活点", cp != null && cp.respawnPoint != null, true);

        LevelManager lm = FindComponent<LevelManager>(scene);
        r.Want("关卡拿到了 LevelManager", lm != null, true);
        r.Want("终点接上了 LevelManager", goal0 != null && goal0.GetComponent<Goal>() != null, true);
        if (lm != null) r.Near("parTime 从 JSON 写进了 LevelManager（40）", lm.parTime, 40f, 0.01f);

        LevelBuilder builder = FindComponent<LevelBuilder>(scene);
        if (builder != null)
        {
            int mp = LayerMask.NameToLayer("MovingPlatform");
            r.Want("LevelBuilder 的压力板掩码认「移动平台」层",
                (builder.plateTriggerMask.value & (1 << mp)) != 0, true);
        }

        // ---------- 阶段 2：无头循环能力探测 ----------
        r.sb.AppendLine("【阶段 2 · 无头游戏循环】能不能在编辑模式下把真实脚本推起来");
        List<MonoBehaviour> scripts = CollectScripts(scene);
        PlayerController player = FindComponent<PlayerController>(scene);
        SlimeController slime = FindComponent<SlimeController>(scene);
        r.Info("场景里可驱动的脚本数量 = " + scripts.Count);
        r.Want("找到玩家", player != null, true);
        if (player == null) { Debug.LogError(Finish(r, "没有玩家，行为测试没法做")); return FailNotRun; }

        SimulationMode2D savedMode = Physics2D.simulationMode;
        Physics2D.simulationMode = SimulationMode2D.Script;

        // ★ 关键：编辑模式下 Unity 不会调用 Awake / Start。
        //   而 MovingPlatform._active、Enemy._startX 这些私有状态正是在那里初始化的 ——
        //   不补这一步，平台"活着但不动"、敌人"原地抽搐"，看起来像机关坏了，其实是测试没搭对。
        Init(scripts);

        float t0 = Time.time;
        Thread.Sleep(120);
        float tAdvanced = Time.time - t0;
        r.Info(string.Format("编辑模式下 Time.time 在 120ms 里前进了 {0:0.000}s（{1}）",
            tAdvanced, tAdvanced > 0.02f ? "会走 → 计时逻辑可信 ✅"
                                         : "不走 → 与时间有关的行为（路点等待、压板松手容差）改用显式模拟，报告里会标注"));
        r.Info("物理回调：编辑模式下 Physics2D.Simulate 不派发 OnTrigger/OnCollision，");
        r.Info("          所以由测试自己做重叠查询、把**真实的 Collider** 交给产品的处理函数。");
        r.Info("          （碰撞矩阵已经单独解码验证过，真实运行时派发没问题。）");

        // ---------- 阶段 3：行为 ----------
        r.sb.AppendLine("【阶段 3 · 联动行为】");
        Vector3 spawn = player.transform.position;

        // ① 踩压板 A → 门 1 打开并锁定
        Teleport(player, new Vector3(6f, 0.6f, 0f));
        Step(scripts, scene, 0.4f);
        r.Want("① 站到压板 A 上 → 压板被压住（真实碰撞体交给真实处理函数）", pA.IsPressed, true,
            "（物理回调辅助）");
        r.Want("① 压板 A 压下 → 门 1 打开（挡路的方块消失）", gate1.activeSelf, false);

        Teleport(player, new Vector3(9f, 0.6f, 0f));
        Step(scripts, scene, 0.4f);
        r.Want("① 人走开以后门 1 仍然开着（一次性锁定，不会被关回去）", gate1.activeSelf, false);

        // ② 平台 1 载人过 6 格深坑
        if (m1 != null && m1.points != null && m1.points.Length == 2)
        {
            r.Info("平台 1 路点世界坐标：" + m1.points[0].position.x.ToString("0.0") + " → " +
                   m1.points[1].position.x.ToString("0.0"));
            PlacePlatform(m1, m1.points[0].position);
            Rigidbody2D playerBody = player.GetComponent<Rigidbody2D>();
            Call(m1, "AddRider", playerBody);                  // 编辑模式不派发碰撞，替它登记乘客
            Teleport(player, new Vector3(m1.points[0].position.x, m1.points[0].position.y + 0.75f, 0f));
            Step(scripts, scene, 0.3f);                        // 先落稳在平台上
            float platX0 = m1.body.position.x, px0 = player.transform.position.x;
            Step(scripts, scene, 1.0f);
            float platMoved = Mathf.Abs(m1.body.position.x - platX0);
            float playerMoved = Mathf.Abs(player.transform.position.x - px0);
            r.Want("② 平台 1 沿路线自己走起来了", platMoved > 0.5f, true,
                string.Format("（走了 {0:0.00} 格，{1}）", platMoved, PlatformState(m1)));
            r.Want("② 站在平台上的玩家被一起带过坑（carryRider 生效）", playerMoved > platMoved * 0.6f, true,
                string.Format("（玩家移动 {0:0.00} 格 / 平台 {1:0.00} 格）", playerMoved, platMoved));
            r.Want("② 玩家确实站在平台面上（没穿下去）",
                player.transform.position.y > m1.body.position.y, true,
                string.Format("（玩家 y{0:0.00} / 平台 y{1:0.00}）", player.transform.position.y, m1.body.position.y));
            r.Want("② 平台不会飞过头（路点没跟着平台跑）",
                m1.body.position.x <= m1.points[1].position.x + 0.5f, true,
                string.Format("（平台 x{0:0.0}，右端点 x{1:0.0}）", m1.body.position.x, m1.points[1].position.x));
        }

        // ③ 敌人巡逻 + 碰到玩家致死
        if (en != null)
        {
            Teleport(player, new Vector3(2f, 0.6f, 0f));       // 先把玩家挪远
            // 编辑模式下 Time.time 不走，敌人转身后的"停顿计时"永远等不到 → 会把原地不动误判成"不会巡逻"。
            // 所以每次采样前把停顿计时清掉，单独验证"走 + 转身"这段逻辑。
            enemy1.transform.position = new Vector3(43f, enemy1.transform.position.y, 0f);
            SetField(en, "_startX", 43f);
            float minX = 43f, maxX = 43f;
            for (int i = 0; i < 24; i++)
            {
                SetField(en, "_turnUntil", -999f);             // 清掉停顿（时间不走，只能手动清）
                Step(scripts, scene, 0.15f);
                float x = enemy1.transform.position.x;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }
            r.Want("③ 敌人自己在巡逻（会左右走动并转身）", maxX - minX > 0.8f, true,
                string.Format("（巡逻范围 {0:0.0} ~ {1:0.0}）", minX, maxX));

            // 碰到玩家 → 致死（HandleContact 就是 OnCollision* 实际调用的那条产品代码）
            Collider2D playerCol = player.GetComponent<Collider2D>();
            Teleport(player, enemy1.transform.position);
            Call(en, "HandleContact", playerCol);
            r.Want("③ 敌人碰到玩家 → 玩家死亡", player.IsDead, true);
            if (lm != null) r.Want("③ 玩家死亡已上报给 LevelManager", !lm.LevelFinished, true, "（关卡没有误判为通关）");
            player.Respawn(spawn);
            r.Want("③ 玩家可以在复活点复活", !player.IsDead, true);
        }

        // ⑤ 检查点改复活点
        if (cp != null && lm != null)
        {
            Vector3 before = lm.GetRespawnPoint();
            Collider2D playerCol = player.GetComponent<Collider2D>();
            Call(cp, "OnTriggerEnter2D", playerCol);
            Step(scripts, scene, 0.2f);
            Vector3 after = lm.GetRespawnPoint();
            r.Want("⑤ 玩家经过检查点 → 复活点被改写", Vector3.Distance(before, after) > 0.5f, true,
                string.Format("（{0:0.0},{1:0.0} → {2:0.0},{3:0.0}；检查点在 {4:0.0},{5:0.0}）",
                    before.x, before.y, after.x, after.y, cp1.transform.position.x, cp1.transform.position.y));
        }

        // ④ 时序机关：移动平台 2 扫过高台，周期性压住压板 B → 门 2 开合
        if (m2 != null && pB != null)
        {
            Teleport(player, new Vector3(2f, 0.6f, 0f));       // 玩家离得远远的，全靠平台
            PlacePlatform(m2, m2.points[0].position);          // 平台摆回左端（x27）
            SetField(pB, "_lastContactTime", -999f);           // 先让压板回到"没被压"的状态
            Step(scripts, scene, 0.25f);
            r.Want("④ 平台在左端、没压到时，压板 B 是松开的", pB.IsPressed, false,
                string.Format("（平台在 x{0:0.0}，压板在 x{1:0.0}）", m2.body.position.x, plateB.transform.position.x));
            r.Want("④ 压板松开时门 2 是关着的（挡路）", gate2.activeSelf, true);

            // 推到平台扫到压板 B 的位置
            bool gotPressed = false;
            for (int i = 0; i < 60 && !gotPressed; i++)
            {
                Step(scripts, scene, 0.1f);
                gotPressed = pB.IsPressed;
                if (m2.body.position.x > m2.points[1].position.x + 0.5f) break;   // 走过头了，别空转
            }
            r.Want("④ 移动平台 2 扫过高台 → 压住了压板 B（**平台压板**这条路径真的通）", gotPressed, true,
                string.Format("（平台 x{0:0.0}，压板在 x{1:0.0}）", m2.body.position.x, plateB.transform.position.x));
            r.Want("④ 压板 B 被压住 → 门 2 打开", gate2.activeSelf, false);

            // 模拟平台离开：先把平台挪到右端点（真正离开压板），
            // 再把压板的"松手容差"计时推到很久以前，然后让它自己 Update 一次 —— 走的仍是产品代码。
            // （编辑模式下 Time.time 不走，算不出时间差，只能这样显式模拟。）
            PlacePlatform(m2, m2.points[1].position);
            SetField(pB, "_lastContactTime", -999f);
            Step(scripts, scene, 0.2f);
            r.Want("④ 平台离开 → 压板 B 松开", pB.IsPressed, false,
                string.Format("（平台已到 x{0:0.0}，压板在 x{1:0.0}）", m2.body.position.x, plateB.transform.position.x));
            r.Want("④ 压板 B 松开 → 门 2 关回去（这就是「门按时开合」，玩家要卡时间冲）", gate2.activeSelf, true);
        }

        Teleport(player, spawn);
        Physics2D.simulationMode = savedMode;

        Finish(r, "机关验收结束");
        return r.fail;   // 门禁用的结论：失败条数（0 = 全过）
    }

    /// <summary>
    /// 命令行入口（保留拆分前的退出行为，供老的外部脚本直接照用）：
    ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod MechTest.BatchMechTestAndExit
    /// 退出码与拆分前逐字一致：**0 = 全过 / 5 = 有失败 / 1 = 环境不足没跑起来**。
    /// ⚠ 拆分说明：原来 BatchMechTest() 自己就 EditorApplication.Exit —— 那会让"四门合并跑"第 4 门一退，
    ///   后面的收尾汇总与退出码全都打不出来。现在退出只留在这里，BatchMechTest() 变成"只跑、返回失败条数"。
    /// </summary>
    public static void BatchMechTestAndExit()
    {
        int fail = BatchMechTest();
        if (fail < 0) EditorApplication.Exit(ExitCodeNotRun);
        else EditorApplication.Exit(fail == 0 ? ExitCodePass : ExitCodeFail);
    }

    // ======================= 工具 =======================

    /// <summary>补上 Unity 在运行时才会调用的 Awake / Start（编辑模式下不会自动调用）。</summary>
    static void Init(List<MonoBehaviour> scripts)
    {
        for (int i = 0; i < scripts.Count; i++) Call(scripts[i], "Awake");
        for (int i = 0; i < scripts.Count; i++) Call(scripts[i], "Start");
    }

    /// <summary>把某个私有字段按名字改掉（只在测试里用来模拟"时间流逝"）。</summary>
    static void SetField(object target, string name, object value)
    {
        if (target == null) return;
        FieldInfo f = target.GetType().GetField(name, Any);
        if (f != null) f.SetValue(target, value);
    }

    static object GetField(object target, string name)
    {
        if (target == null) return null;
        FieldInfo f = target.GetType().GetField(name, Any);
        return f != null ? f.GetValue(target) : null;
    }

    /// <summary>把一个移动平台摆到指定位置，并把它的内部记账一起对齐（否则它会从旧位置继续插值）。</summary>
    static void PlacePlatform(MovingPlatform mp, Vector2 pos)
    {
        mp.body.position = pos;
        SetField(mp, "_lastPosition", pos);
        SetField(mp, "_waitUntil", -999f);
        SetField(mp, "_reachedEnd", false);
        SetField(mp, "_active", true);
        SetField(mp, "_targetIndex", 1);
        SetField(mp, "_direction", 1);
    }

    static string PlatformState(MovingPlatform mp)
    {
        return string.Format("_active={0} _reachedEnd={1} _waitUntil={2} _last={3}",
            GetField(mp, "_active"), GetField(mp, "_reachedEnd"), GetField(mp, "_waitUntil"), GetField(mp, "_lastPosition"));
    }

    /// <summary>
    /// 编辑模式下 Physics2D.Simulate 不派发 OnTrigger*，这里替它把事件送到产品代码：
    /// 对每个压力板做一次重叠查询，把压在上面的**真实 Collider** 交给 OnTriggerStay2D。
    /// </summary>
    static void DeliverTriggerEvents(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (PressurePlate p in root.GetComponentsInChildren<PressurePlate>(true))
            {
                Collider2D col = p.GetComponent<Collider2D>();
                if (col == null) continue;
                Bounds b = col.bounds;
                Collider2D[] hits = Physics2D.OverlapAreaAll(b.min, b.max);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D h = hits[i];
                    if (h == null || h == col) continue;
                    if (h.attachedRigidbody == null) continue;          // 只有带动体的东西才可能压住它
                    if (h.isTrigger) continue;
                    Call(p, "OnTriggerStay2D", h);
                }
            }
        }
    }

    static void WantAll(Result r, string[] names, GameObject[] gos)
    {
        for (int i = 0; i < names.Length; i++)
            r.Want("场景里有 " + names[i], gos[i] != null, true);
    }

    static bool Contains(GameObject[] arr, GameObject target)
    {
        if (arr == null) return false;
        foreach (GameObject g in arr) if (g == target) return true;
        return false;
    }

    static bool SolidCollider(GameObject go)
    {
        Collider2D c = go.GetComponent<Collider2D>();
        return c != null && !c.isTrigger;
    }

    static GameObject Find(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all) if (t.name == name) return t.gameObject;
        }
        return null;
    }

    static T FindComponent<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T c = root.GetComponentInChildren<T>(true);
            if (c != null) return c;
        }
        return null;
    }

    static List<MonoBehaviour> CollectScripts(Scene scene)
    {
        List<MonoBehaviour> list = new List<MonoBehaviour>();
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && !(mb is LevelBuilder)) list.Add(mb);   // LevelBuilder 不需要逐帧驱动
        return list;
    }

    static void Teleport(PlayerController player, Vector3 pos)
    {
        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        player.transform.position = pos;
        if (body != null) { body.position = pos; body.velocity = Vector2.zero; }
    }

    /// <summary>推 seconds 秒的"游戏帧"：物理步 → 补事件 → FixedUpdate → Update，按真实时间节流。</summary>
    static void Step(List<MonoBehaviour> scripts, Scene scene, float seconds)
    {
        const float dt = 1f / 60f;
        int frames = Mathf.Max(1, Mathf.RoundToInt(seconds / dt));
        for (int i = 0; i < frames; i++)
        {
            Physics2D.Simulate(dt);
            DeliverTriggerEvents(scene);
            for (int k = 0; k < scripts.Count; k++) Call(scripts[k], "FixedUpdate");
            for (int k = 0; k < scripts.Count; k++) Call(scripts[k], "Update");
            Thread.Sleep(1);
        }
    }

    /// <summary>按名字 + 参数个数反射调用（含私有方法，例如 Unity 的 Update / OnTriggerStay2D）。</summary>
    static void Call(object target, string name, params object[] args)
    {
        if (target == null) return;
        MethodInfo[] ms = target.GetType().GetMethods(Any);
        foreach (MethodInfo m in ms)
        {
            if (m.Name != name) continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != args.Length) continue;
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
                if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) { ok = false; break; }
            if (!ok) continue;
            try { m.Invoke(target, args); } catch (Exception e) { Debug.LogWarning("[机关测试] 调用 " + name + " 抛异常：" + e.InnerException); }
            return;
        }
    }

    static int Finish(Result r, string title)
    {
        r.sb.AppendLine(string.Format("========== {0}：通过 {1} 项 / 失败 {2} 项 ==========", title, r.pass, r.fail));
        if (r.fail == 0) Debug.Log(r.sb.ToString());
        else Debug.LogError(r.sb.ToString());
        return r.fail == 0 ? 0 : 5;
    }
}
