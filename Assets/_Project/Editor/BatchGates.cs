using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 一次 Unity 启动跑完"四道门"（求解器自检 → 求解四关 → 关卡往返 → 机关联动）。
///
/// 为什么要有它：
///   这四道门原本是 4~5 条 -executeMethod，每条都是一次冷启动（约 1.5~3 分钟，
///   其中约 95% 花在引擎启动 / 场景 I/O 上）—— 真正的计算只有毫秒级。
///   所以"分开跑"的时间几乎全花在重复启动；这里把它们串进**一次**启动：
///   顺序固定、每步单独 try/catch（一步抛异常不影响后面几步）、每步记耗时、
///   结尾给**机器可读**的一行汇总 + 一行判据，失败时批处理以非零码退出。
///
/// 怎么跑：
///   批处理：Unity.exe -batchmode -quit -projectPath &lt;工程&gt; -executeMethod BatchGates.RunAll -logFile -
///   编辑器：菜单 Tools/呆呆史莱姆/▨▨ 全量门禁（四门一次跑完）  —— 会先弹确认框
///
/// 判据（**真结论**，各门自己给）：
///   1/4 求解器自检  LevelSolver.BatchSolverSelfTest()     → 失败条数
///   2/4 求解四关    LevelSolver.BatchSolveAll()           → 这一门只出报告、没有过/不过的概念 ⇒ 只看有没有抛异常
///   3/4 关卡往返    LevelDataWindow.BatchRoundTripAll()   → 失败关卡数
///   4/4 机关联动    LevelMechSetup.BatchCreateMechLevel() → 建成功？
///                   MechTest.BatchMechTest()              → 失败条数（它已不再自行 EditorApplication.Exit）
///   任一门"返回值非 0 / false / 抛异常"⇒ FAIL。
///
/// 退出码：FAIL 且批处理 ⇒ EditorApplication.Exit(1)（**先落日志再退**）；成功不动，由 -quit 正常退 0。
///
/// ⏳ 历史（第一版的样子）：那时四个门都只打日志、不返回结果，而且 MechTest.BatchMechTest() 自己就会
///   EditorApplication.Exit ⇒ 门禁只能靠"有没有抛异常"判，收尾的汇总行根本打不出来、退出码还会被覆盖。
///   现在四门都改成**返回结论**、退出拆到 MechTest.BatchMechTestAndExit()，这两条都已解决。
///   ⚠ 连带的行为变化：`-executeMethod MechTest.BatchMechTest` **不再自己退出**（只跑并返回失败条数）；
///     要保留"跑完按结果退"的老行为，请用 `-executeMethod MechTest.BatchMechTestAndExit`
///     （退出码与拆分前逐字一致：0 全过 / 5 有失败 / 1 环境不足没跑起来）。
/// </summary>
public static class BatchGates
{
    // ---------------- 可调项（数值不写死在方法里）----------------

    /// <summary>菜单优先级（30~37 是诊断组，这里接在它后面）。</summary>
    public const int MenuPriority = 38;

    /// <summary>菜单路径（逐字固定，方便照抄）。</summary>
    public const string MenuPath = "Tools/呆呆史莱姆/▨▨ 全量门禁（四门一次跑完）";

    /// <summary>日志统一前缀（脚本 grep 用）。</summary>
    public const string Tag = "[GATES] ";

    /// <summary>汇总行的行内标签（完整形式 = Tag + 本串 + 各字段）。</summary>
    public const string SummaryLabel = "SUMMARY ";

    /// <summary>通过判据行的正文（完整形式 = Tag + 本串）。</summary>
    public const string PassText = "ALL PASS";

    /// <summary>失败判据行的正文（完整形式 = Tag + 本串）。</summary>
    public const string FailText = "FAIL";

    /// <summary>失败时给进程的退出码（非零）。</summary>
    public const int FailExitCode = 1;

    /// <summary>某一步抛异常时的内部取值（汇总里打成 error）。</summary>
    public const int ErrorValue = -1;

    /// <summary>因为上一步没成功而没跑的步骤取值（汇总里打成 skip；照样算 FAIL）。</summary>
    public const int SkippedValue = -2;

    /// <summary>汇总字段含义的固定提示行（每次跑都打，免得看错）。</summary>
    public const string SummaryNote =
        "字段含义：selftest / roundtrip / mech = 各门返回的失败条数（0 = 全过；error = 抛异常；skip = 上一步没成功所以没跑）；" +
        "solve = ok / error（这一门只出报告，没有过 / 不过的概念，只看有没有抛异常）。";

    // ---------------- 入口 ----------------

    /// <summary>菜单入口：编辑器里点（非批处理）。</summary>
    [MenuItem(MenuPath, false, MenuPriority)]
    public static void RunAllMenu()
    {
        bool go = EditorUtility.DisplayDialog("全量门禁",
            "依次跑完四道门：\n" +
            "  1/4 求解器自检\n" +
            "  2/4 求解四关\n" +
            "  3/4 关卡往返\n" +
            "  4/4 机关联动\n\n" +
            "判据是各门自己返回的结论（失败条数 / 建成功与否），任一门失败或抛异常就判 FAIL。\n" +
            "过程会写 Console，期间请不要操作编辑器。\n\n" +
            "开始跑？",
            "开始", "取消");
        if (!go) { Debug.Log(Tag + "已取消：没有跑任何门"); return; }
        RunAll();
    }

    /// <summary>批处理入口：Unity.exe -batchmode -quit -projectPath &lt;工程&gt; -executeMethod BatchGates.RunAll</summary>
    public static void RunAll()
    {
        System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
        Debug.Log(Tag + "===== 全量门禁：四门一次跑完（batchMode=" + Application.isBatchMode + "）=====");

        // ---------- 前 3 门：一步抛异常不影响后面几步 ----------
        int selftest = RunCount("1/4", "求解器自检", LevelSolver.BatchSolverSelfTest);
        bool solve = RunPlain("2/4", "求解四关", LevelSolver.BatchSolveAll);
        int roundtrip = RunCount("3/4", "关卡往返", LevelDataWindow.BatchRoundTripAll);

        // ---------- 4/4 机关联动：先建关，再验收 ----------
        int mech = RunFlag("4/4a", "机关关建关", LevelMechSetup.BatchCreateMechLevel);
        if (mech == 0)
        {
            mech = RunCount("4/4b", "机关联动验收", MechTest.BatchMechTest);
        }
        else
        {
            mech = SkippedValue;
            Debug.Log(Tag + "step 4/4b 机关联动验收 SKIP（4/4a 建关没成功，验收没跑）");
        }

        // ---------- 收尾：汇总 + 判据（现在一定打得到，因为第 4 门不再自行退出）----------
        bool allPass = selftest == 0 && solve && roundtrip == 0 && mech == 0;
        Debug.Log(Tag + SummaryLabel + Summary(selftest, solve, roundtrip, mech, total.ElapsedMilliseconds));
        Debug.Log(Tag + "NOTE " + SummaryNote);
        Debug.Log(Tag + (allPass ? PassText : FailText));

        if (!allPass && Application.isBatchMode)
        {
            // ⚠ 顺序有讲究：先把汇总 / 判据写进日志，再退；否则 -quit / Exit 会把这几行吞掉
            Debug.Log(Tag + "失败 ⇒ 以非零码退出：" + FailExitCode);
            EditorApplication.Exit(FailExitCode);
        }
    }

    // ---------------- 跑一步（三种签名各一个，判据都取返回值）----------------

    /// <summary>跑"返回失败条数"的门。返回：≥0 = 失败条数（0 = 全过）；ErrorValue = 抛异常。</summary>
    static int RunCount(string index, string name, Func<int> body)
    {
        int value = ErrorValue;
        bool threw = RunGuarded(index, name, delegate { value = body(); }, out long ms);
        if (threw) value = ErrorValue;
        LogDone(index, name, ms, value);
        return value;
    }

    /// <summary>跑"返回是否成功"的门。返回：0 = 成功；1 = 返回 false；ErrorValue = 抛异常。</summary>
    static int RunFlag(string index, string name, Func<bool> body)
    {
        int value = ErrorValue;
        bool threw = RunGuarded(index, name, delegate { value = body() ? 0 : 1; }, out long ms);
        if (threw) value = ErrorValue;
        LogDone(index, name, ms, value);
        return value;
    }

    /// <summary>跑"没有结论"的门（只关心有没有抛异常，例如求解四关只出报告）。true = 没抛异常。</summary>
    static bool RunPlain(string index, string name, Action body)
    {
        bool threw = RunGuarded(index, name, body, out long ms);
        int value = threw ? ErrorValue : 0;
        LogDone(index, name, ms, value);
        return !threw;
    }

    /// <summary>共用壳：打 START、单独 try/catch、记耗时。true = 抛异常了。</summary>
    static bool RunGuarded(string index, string name, Action body, out long elapsedMs)
    {
        Debug.Log(Tag + "step " + index + " " + name + " START");
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        bool threw = false;
        try
        {
            body();
        }
        catch (Exception e)
        {
            threw = true;
            Debug.LogError(Tag + "step " + index + " " + name + " THREW：" + Brief(e));
        }
        sw.Stop();
        elapsedMs = sw.ElapsedMilliseconds;
        return threw;
    }

    /// <summary>每步收尾行（格式固定，供脚本 grep）。</summary>
    static void LogDone(string index, string name, long ms, int value)
    {
        string result = (value == 0) ? "ok" : "fail";
        Debug.Log(Tag + "step " + index + " " + name + " done in " + ms + " ms result=" + result + " fails=" + ValueText(value));
    }

    /// <summary>汇总行（不含 Tag / SUMMARY 标签）：字段顺序与名字固定，供脚本 grep。</summary>
    static string Summary(int selftest, bool solve, int roundtrip, int mech, long totalMs)
    {
        return "selftest=" + ValueText(selftest) +
               " solve=" + (solve ? "ok" : "error") +
               " roundtrip=" + ValueText(roundtrip) +
               " mech=" + ValueText(mech) +
               " total=" + totalMs + "ms";
    }

    /// <summary>取值的字面量：0 / 失败条数 / error（抛异常）/ skip（上一步没成功）。</summary>
    static string ValueText(int value)
    {
        if (value == SkippedValue) return "skip";
        if (value < 0) return "error";
        return value.ToString();
    }

    /// <summary>异常摘要（类型 + 消息，不带堆栈，免得把日志刷爆）。</summary>
    static string Brief(Exception e) { return e.GetType().Name + ": " + e.Message; }
}
