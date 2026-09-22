using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>关卡失败的原因。结算面板用它选印章文字。</summary>
public enum LevelFailReason
{
    None = 0,
    SlimeDied = 1,          // 史莱姆阵亡
    PlayerFellIntoPit = 2   // 玩家掉进深坑
}

/// <summary>
/// 职责：关卡数据中枢。金币计数、计时、结算公式、检查点、玩家死亡复活、史莱姆死亡失败重开、写 PlayerPrefs。
/// Inspector：拖 SlimeController、PlayerController、起始点 Transform。
/// 依赖：SlimeController、PlayerController；被 PlayerController / SlimeController / Coin / Goal / Hazard / Enemy / Checkpoint / 所有 UI 直接引用。
/// 注意：逻辑层不引用任何 UI 脚本，UI 主动来读这里的只读属性。
/// </summary>
public class LevelManager : MonoBehaviour
{
    [Header("关卡标识")]
    [Tooltip("第几关，1/2/3，用于解锁下一关的 PlayerPrefs 键")]
    public int levelIndex = 1;

    [Header("引用（拖拽）")]
    public SlimeController slime;
    public PlayerController player;
    [Tooltip("玩家出生点，也是没有检查点时的复活点")]
    public Transform startPoint;

    [Header("结算参数")]
    [Tooltip("标准通关时间（秒）。取 10 的倍数可以保证所有换算都是整数。售价条满格 = 10 × parTime")]
    public float parTime = 50f;
    [Tooltip("售价条满格覆盖值。0 = 自动 = 10 × parTime（推荐，保证全是整数）")]
    public int barMaxOverride = 0;
    [Tooltip("本关全部金币面值之和。由 LevelBuilder 自动写入；用于算星级基准分")]
    public int coinTotalInLevel = 0;
    [Tooltip("三星门槛：相对「基准分」的比例，长度必须为 3")]
    public float[] starRatios = new float[] { 0.6f, 0.9f, 1.1f };

    [Header("失败与复活")]
    [Tooltip("史莱姆死亡后自动重开的延迟")]
    public float failRestartDelay = 1.5f;
    [Tooltip("玩家死亡后从检查点复活的延迟")]
    public float playerRespawnDelay = 1.0f;
    [Tooltip("玩家死亡后超过这个时间还没复活就强制复活（防死锁保险）")]
    public float playerDeathTimeout = 4f;
    [Tooltip("玩家掉出关卡下界的死亡高度")]
    public float playerDeathFallY = -20f;

    [Header("存档")]
    public bool saveProgress = true;

    // ---------------- 运行时只读数据（UI 与结算面板来读） ----------------
    public int CurrentCoins { get; private set; }
    public float TotalTime { get; private set; }
    public bool LevelFinished { get; private set; }
    public bool LevelFailed { get; private set; }
    /// <summary>失败原因。结算面板靠它决定印章文字（"史莱姆阵亡"还是"掉进深坑"）。</summary>
    public LevelFailReason FailReason { get; private set; }
    public int SlimeHealth { get { return slime != null ? slime.CurrentHealth : 0; } }
    public int SlimeMaxHealth { get { return slime != null ? slime.MaxHealth : 0; } }
    public float HealthRatio { get { return slime != null ? slime.HealthRatio : 0f; } }

    /// <summary>售价条满格 = 本关理论最高售价。规则：10 × parTime（= 2 × parTime × drainPerSecond）。</summary>
    public int BarMax
    {
        get
        {
            if (barMaxOverride > 0) return barMaxOverride;
            return Mathf.RoundToInt(2f * parTime * drainPerSecond);
        }
    }

    /// <summary>史莱姆当前售价 = 满格 × 血条剩余比例。血条掉了多少，售价就掉了多少。</summary>
    public int CurrentPrice
    {
        get { return Mathf.RoundToInt(BarMax * HealthRatio); }
    }

    /// <summary>
    /// 最终收益 = 史莱姆售价（按剩余血量算）+ 已收集金币。
    /// 金币按面值直接计入，**不参与血量缩放** —— 这样玩家能心算「绕这一下值不值」，
    /// 也让「远处 / 危险处的金币面值调高」成为一个有效的取舍杠杆。
    /// </summary>
    public int FinalCoins
    {
        get { return CurrentPrice + CurrentCoins; }
    }

    /// <summary>星级基准分 = 标准时间通关（血条剩 parRemain）+ 全收集 时的收益。</summary>
    public int BaselineScore
    {
        get { return Mathf.RoundToInt(BarMax * parRemain) + coinTotalInLevel; }
    }

    /// <summary>本关理论最高收益（满血 + 全收集）。</summary>
    public int MaxPossibleCoins
    {
        get { return BarMax + coinTotalInLevel; }
    }

    /// <summary>星级：最终收益达到「基准分 × starRatios[i]」即为 (i+1) 星。
    /// 不计成绩的关卡（教程关 saveProgress = false）恒为 0 星。</summary>
    public int Stars
    {
        get
        {
            if (levelFailedFlag) return 0;
            if (!saveProgress) return 0;   // 教程关不算成绩
            int stars = 0;
            if (starRatios != null)
            {
                int baseline = BaselineScore;
                for (int i = 0; i < starRatios.Length && i < 3; i++)
                {
                    if (FinalCoins >= Mathf.RoundToInt(baseline * starRatios[i])) stars = i + 1;
                }
            }
            return Mathf.Clamp(stars, 0, 3);
        }
    }

    // ================= 全局经济常数 =================
    // 所有关卡共用。改这里 = 改全局手感。
    // 之所以用 public static 字段而不是 const：既满足"数值不写死在方法里"，又不会被每关 JSON 固化住
    // （MonoBehaviour 的序列化字段会盖掉代码默认值，这个坑踩过两次）。

    /// <summary>每秒掉多少钱。全局统一：改这一个数，所有关卡的时间压力一起变。</summary>
    public static float drainPerSecond = 5f;
    /// <summary>标准时间通关时售价条应该剩多少（0~1）。0.5 = 正好掉一半。</summary>
    public static float parRemain = 0.5f;
    /// <summary>时间掉血的下限（0~1）。**时间不会饿死史莱姆，危险会。**</summary>
    public static float barFloor = 0.25f;

    // ================= 公式本体 =================
    // 运行时属性与编辑器「收益公式自检」共用这几个静态方法，
    // 保证代码里跑的、和文档里算的，永远是同一个公式。

    /// <summary>
    /// 每秒掉多少血（血条单位 0~maxHealth）。
    /// 由"标准时间时血条剩 parRemain"反推：drainRate × parTime = (1 − parRemain) × 100。
    /// parTime = 50 秒时正好是 1.0 血/秒。
    /// </summary>
    public static float DrainRateFor(float parTime)
    {
        if (parTime <= 0.01f) return 0f;
        return (1f - Mathf.Clamp01(parRemain)) * 100f / parTime;
    }

    public float DrainRate { get { return DrainRateFor(parTime); } }

    /// <summary>售价条满格的通用算法（与 BarMax 同一套规则，供编辑器自检用）。</summary>
    public static int BarMaxFor(float parTime)
    {
        return Mathf.RoundToInt(2f * parTime * drainPerSecond);
    }

    /// <summary>每秒时间价值 = drainPerSecond（全局常数，就是为了让它一眼可见）。</summary>
    public static float SecondsValue()
    {
        return drainPerSecond;
    }

    /// <summary>一枚金币的「绕路预算」（秒）：绕路超过这个时间就不值得捡。</summary>
    public static float CoinDetourBudget(int coinValue)
    {
        return drainPerSecond > 0.0001f ? coinValue / drainPerSecond : 999f;
    }

    /// <summary>给定用时和血量比例算售价。与运行时 CurrentPrice 同源。</summary>
    public static int PriceAt(float parTime, float elapsed, float healthRatio)
    {
        int barMax = BarMaxFor(parTime);
        float drain = DrainRateFor(parTime);
        float hp = Mathf.Clamp01(healthRatio) * 100f - drain * Mathf.Max(0f, elapsed);
        float floor = Mathf.Clamp01(barFloor) * 100f;
        hp = Mathf.Max(floor, hp);
        return Mathf.RoundToInt(barMax * Mathf.Clamp01(hp / 100f));
    }

    private bool levelFailedFlag;
    private Vector3 _respawnPoint;
    private bool _playerDeathRunning;
    private bool _slimeDeathRunning;
    private bool _pitDeathRunning;
    private float _playerDeathTimer;

    void Awake()
    {
        Time.timeScale = 1f;
        CurrentCoins = 0;
        TotalTime = 0f;
        LevelFinished = false;
        LevelFailed = false;
        levelFailedFlag = false;
        // 出生点没配时不要退回 (0,0,0)（那可能在地下或悬崖边），改用玩家自己的初始位置
        _respawnPoint = startPoint != null
            ? startPoint.position
            : (player != null ? player.transform.position : Vector3.zero);
    }

    void Update()
    {
        if (LevelFinished || LevelFailed) return;
        TotalTime += Time.deltaTime;

        // 玩家掉出关卡
        if (player != null && !player.IsDead && player.transform.position.y < playerDeathFallY)
        {
            OnPlayerDied();
        }

        // 玩家失控看门狗：关卡还在进行，玩家却处于"死亡 / 无法操控"状态超过 playerDeathTimeout 就强制复活。
        // 兜底保险 —— 无论什么原因（暂停打断协程、状态没清干净、连续坠落、脚本报错…）都不会永久卡死。
        if (player != null && (!player.ControlEnabled || player.IsDead))
        {
            _playerDeathTimer += Time.deltaTime;
            if (_playerDeathTimer > playerDeathTimeout)
            {
                _playerDeathTimer = 0f;
                _playerDeathRunning = false;
                player.Respawn(_respawnPoint);
                Debug.LogWarning("[LevelManager] 玩家失控超过 " + playerDeathTimeout + " 秒，已强制复活（防死锁保险触发）");
            }
        }
        else
        {
            _playerDeathTimer = 0f;
        }
    }

    /// <summary>通关演出时长（由 Goal 写入）。UI 读它来决定结算面板延迟多久弹。</summary>
    public float GoalCutsceneDuration { get; private set; }

    public void SetGoalCutsceneDuration(float seconds)
    {
        GoalCutsceneDuration = Mathf.Max(0f, seconds);
    }

    // ---------------- 金币 ----------------
    public void AddCoin(int amount)
    {
        if (LevelFinished || LevelFailed) return;
        CurrentCoins += amount;
    }

    // ---------------- 检查点与玩家复活 ----------------
    public void SetCheckpoint(Vector3 worldPosition)
    {
        _respawnPoint = worldPosition;
    }

    public Vector3 GetRespawnPoint()
    {
        return _respawnPoint;
    }

    /// <summary>玩家碰怪 / 掉出地图死亡：从最近检查点复活，金币与物品全部保留，不重开关卡。</summary>
    public void OnPlayerDied()
    {
        if (LevelFinished || LevelFailed || _playerDeathRunning) return;
        _playerDeathRunning = true;
        StartCoroutine(PlayerDeathRoutine());
    }

    /// <summary>
    /// 玩家掉进深坑：**直接关卡失败**，不给复活。
    /// 由深坑底部那块看不见的"死亡判定块"（Hazard + fatalToPlayer）调用。
    /// </summary>
    public void OnPlayerFellIntoPit()
    {
        if (LevelFinished || LevelFailed || _pitDeathRunning) return;
        _pitDeathRunning = true;
        FailReason = LevelFailReason.PlayerFellIntoPit;
        if (player != null) player.Kill();
        FailLevel();
    }

    /// <summary>
    /// 关卡失败：停掉操控 → 等 failRestartDelay → 重开本关。
    /// 史莱姆死亡和玩家掉深坑都走这里，保证"失败"只有一种表现。
    /// </summary>
    public void FailLevel()
    {
        if (LevelFinished || LevelFailed) return;
        LevelFailed = true;
        levelFailedFlag = true;
        if (player != null) player.SetControlEnabled(false);
        StartCoroutine(FailRestartRoutine());
    }

    private IEnumerator PlayerDeathRoutine()
    {
        if (player != null) player.Kill();
        yield return new WaitForSeconds(playerRespawnDelay);

        if (player != null) player.Respawn(_respawnPoint);
        _playerDeathTimer = 0f;
        _playerDeathRunning = false;
    }

    // ---------------- 史莱姆死亡 = 关卡失败 ----------------
    public void OnSlimeDied()
    {
        if (LevelFinished || LevelFailed || _slimeDeathRunning) return;
        _slimeDeathRunning = true;
        FailReason = LevelFailReason.SlimeDied;
        FailLevel();
    }

    private IEnumerator FailRestartRoutine()
    {
        yield return new WaitForSeconds(failRestartDelay);
        RestartLevel();
    }

    // ---------------- 到达收购站 = 通关 ----------------
    public void OnSlimeReachedGoal()
    {
        if (LevelFinished || LevelFailed) return;
        LevelFinished = true;
        if (player != null) player.SetControlEnabled(false);
        SaveProgress();
    }

    private void SaveProgress()
    {
        if (!saveProgress) return;

        PlayerPrefs.SetInt("LevelCoins_" + levelIndex, FinalCoins);
        PlayerPrefs.SetInt("LevelStars_" + levelIndex, Stars);

        float best = PlayerPrefs.GetFloat("BestTime_" + levelIndex, 99999f);
        if (TotalTime < best) PlayerPrefs.SetFloat("BestTime_" + levelIndex, TotalTime);

        PlayerPrefs.SetInt("LevelUnlocked_" + (levelIndex + 1), 1);
        PlayerPrefs.Save();
    }

    /// <summary>重开当前关卡。</summary>
    public void RestartLevel()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
