// UIManager.cs —— 关卡 HUD（拉模式：只读逻辑层数据，绝不修改任何游戏状态）
// 职责：Update 中刷新 金币数量 / mm:ss 计时 / 史莱姆血量条与颜色 / 史莱姆模式中文名 /
//       剩余路径点数 / 携带提示，并提供由关卡脚本调用的教学提示 ShowHint(message[, duration])。
// Inspector 需要拖：levelManager、slimeController、playerInventory、playerController、pathFollow，
//       coinText / timeText / modeText / waypointText / hintText / carryText(可空)，
//       healthFill（Image Type 必须为 Filled）与 healthText(可空)。
// 依赖核心脚本：LevelManager、SlimeController、PlayerInventory、PlayerController、SlimePathFollow（仅读公开属性）。
// 硬性约束：不使用事件系统；任何 Inspector 引用为空时静默跳过该项，不抛 NullReferenceException。

using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UIManager : MonoBehaviour
{
    [Header("核心逻辑引用（Inspector 拖拽；为空时跳过对应项的刷新）")]
    [Tooltip("关卡数据中枢：金币、计时、史莱姆血量、结算数据")]
    public LevelManager levelManager;

    [Tooltip("史莱姆控制器：读取 State（模式）与血量兜底来源")]
    public SlimeController slimeController;

    [Tooltip("玩家物品栏：仅读取 IsSwitchLocked，用于显示物品栏锁定提示")]
    public PlayerInventory playerInventory;

    [Tooltip("玩家控制器：仅读取 IsCarrying，用于显示携带提示")]
    public PlayerController playerController;

    [Tooltip("史莱姆路径点管理：读取 WaypointCount 与 maxWaypoints")]
    public SlimePathFollow pathFollow;

    [Header("HUD 文本")]
    [Tooltip("金币数量文本")]
    public TMP_Text coinText;

    [Tooltip("计时文本，显示格式 mm:ss")]
    public TMP_Text timeText;

    [Tooltip("史莱姆当前模式文本（取 modeDisplayNames 中的中文显示名）")]
    public TMP_Text modeText;

    [Tooltip("路径点文本，显示格式：已放置/上限")]
    public TMP_Text waypointText;

    [Tooltip("教学提示文本（由 ShowHint 写入，计时结束后自动清空）")]
    public TMP_Text hintText;

    [Header("血量条")]
    [Tooltip("血条填充图，Image Type 必须为 Filled（通常 Fill Method = Horizontal）")]
    public Image healthFill;

    [Tooltip("可选：血量数字文本，显示 当前 / 最大；不需要可留空")]
    public TMP_Text healthText;

    [Header("售价条（小黄条，显示当前能卖多少钱）")]
    [Tooltip("售价条填充图，Image Type 必须为 Filled")]
    public Image priceFill;
    [Tooltip("售价数字文本，显示 售价 当前 / 最高")]
    public TMP_Text priceText;
    [Tooltip("售价文字前缀")]
    public string priceLabel = "售价";
    [Tooltip("售价条颜色")]
    public Color priceBarColor = new Color(1f, 0.82f, 0.2f);

    [Tooltip("血量正常时的血条颜色")]
    public Color healthNormalColor = Color.green;

    [Tooltip("血量低于阈值时的血条颜色")]
    public Color healthLowColor = Color.red;

    [Range(0f, 1f)]
    [Tooltip("低血量阈值（0~1）：HealthRatio 小于等于该值时血条切换为 healthLowColor")]
    public float lowHealthThreshold = 0.3f;

    [Header("史莱姆模式显示名（长度 6，索引严格对应 SlimeState：Follow/Stay/Scared/Carried/Thrown/Dead）")]
    public string[] modeDisplayNames = new string[] { "跟随", "待命", "惊吓", "被携带", "投掷中", "阵亡" };

    [Header("提示与文本格式")]
    [Tooltip("ShowHint(message) 单参数版本的默认显示时长（秒）")]
    public float hintDuration = 2f;

    [Tooltip("引导石文本前缀。显示格式：前缀 + 剩余/总数")]
    public string waypointLabel = "引导石";

    [Tooltip("金币补零位数：0 = 不补零；例如填 3 时显示 007")]
    public int coinDigits = 0;

    [Tooltip("可选：携带状态提示文本；留空则不显示携带提示")]
    public TMP_Text carryText;

    [Tooltip("携带史莱姆时的提示文字")]
    public string carryHintText = "携带史莱姆中";

    [Tooltip("携带史莱姆且物品栏被锁定时的提示文字")]
    public string carryLockedHintText = "携带中（物品栏已锁定）";

    // 提示计时：<0 表示常驻（不自动清空），>0 表示剩余秒数，0 表示空闲
    private float _hintTimer;

    // 金币格式化缓存（避免每帧拼接格式字符串）
    private string _coinFormat;
    private int _cachedCoinDigits = -1;

    private void Awake()
    {
        _hintTimer = 0f;
    }

    private void Update()
    {
        RefreshCoins();
        RefreshTime();
        RefreshHealth();
        RefreshMode();
        RefreshWaypoints();
        RefreshCarryHint();
        TickHint();
    }

    /// <summary>显示一条教学提示，时长使用 hintDuration。</summary>
    public void ShowHint(string message)
    {
        ShowHint(message, hintDuration);
    }

    /// <summary>显示一条教学提示；duration 小于等于 0 表示常驻，直到下一次 ShowHint 覆盖。</summary>
    public void ShowHint(string message, float duration)
    {
        if (hintText == null)
        {
            return;
        }

        hintText.text = string.IsNullOrEmpty(message) ? string.Empty : message;
        _hintTimer = duration > 0f ? duration : -1f;
    }

    // ---------------- 以下全部为只读刷新，不调用任何修改游戏状态的方法 ----------------

    private void RefreshCoins()
    {
        if (coinText == null || levelManager == null)
        {
            return;
        }

        int digits = Mathf.Clamp(coinDigits, 0, 10);
        if (_cachedCoinDigits != digits)
        {
            _cachedCoinDigits = digits;
            _coinFormat = digits > 0 ? "D" + digits : null;
        }

        int coins = levelManager.CurrentCoins;
        coinText.text = _coinFormat != null ? coins.ToString(_coinFormat) : coins.ToString();
    }

    private void RefreshTime()
    {
        if (timeText == null || levelManager == null)
        {
            return;
        }

        timeText.text = FormatTime(levelManager.TotalTime);
    }

    private void RefreshHealth()
    {
        float ratio;
        int current;
        int max;

        // 血量以 LevelManager 为准；未拖 LevelManager 时退回 SlimeController
        if (levelManager != null)
        {
            ratio = levelManager.HealthRatio;
            current = levelManager.SlimeHealth;
            max = levelManager.SlimeMaxHealth;
        }
        else if (slimeController != null)
        {
            ratio = slimeController.HealthRatio;
            current = slimeController.CurrentHealth;
            max = slimeController.MaxHealth;
        }
        else
        {
            return;
        }

        ratio = Mathf.Clamp01(ratio);

        if (healthFill != null)
        {
            healthFill.fillAmount = ratio;
            healthFill.color = ratio <= lowHealthThreshold ? healthLowColor : healthNormalColor;
        }

        if (healthText != null)
        {
            healthText.text = current + " / " + max;
        }

        RefreshPrice();
    }

    /// <summary>
    /// 售价条 = 史莱姆的血条。史莱姆是货物，运久了会蔫，售价跟着掉。
    /// 所以这一条既是血量也是钱：掉血和时间流逝都会让它变短。
    /// </summary>
    private void RefreshPrice()
    {
        if (priceFill == null && priceText == null) return;

        int now = levelManager != null ? levelManager.CurrentPrice : 0;
        int max = levelManager != null ? levelManager.BarMax : 0;

        if (priceFill != null)
        {
            priceFill.fillAmount = max > 0 ? Mathf.Clamp01((float)now / max) : 0f;
            priceFill.color = priceBarColor;
        }

        if (priceText != null)
        {
            priceText.text = priceLabel + " " + now + " / " + max;
        }
    }

    private void RefreshMode()
    {
        if (modeText == null || slimeController == null)
        {
            return;
        }

        SlimeState state = slimeController.State;
        int index = (int)state;

        // 越界或显示名未配置时回退为枚举名，绝不越界访问 modeDisplayNames
        if (modeDisplayNames != null && index >= 0 && index < modeDisplayNames.Length && !string.IsNullOrEmpty(modeDisplayNames[index]))
        {
            modeText.text = modeDisplayNames[index];
        }
        else
        {
            modeText.text = state.ToString();
        }
    }

    private void RefreshWaypoints()
    {
        if (waypointText == null || pathFollow == null)
        {
            return;
        }

        // 显示【剩余】而不是已放置：玩家关心的是"我还有几颗能用"。
        // 放一颗少一颗，取消（Q 撤销）一颗返还一颗。
        waypointText.text = waypointLabel + " " + pathFollow.StonesRemaining + "/" + pathFollow.stoneCount;
    }

    private void RefreshCarryHint()
    {
        if (carryText == null)
        {
            return;
        }

        bool carrying = playerController != null && playerController.IsCarrying;
        bool locked = playerInventory != null && playerInventory.IsSwitchLocked;

        if (!carrying && !locked)
        {
            carryText.text = string.Empty;
            return;
        }

        carryText.text = locked ? carryLockedHintText : carryHintText;
    }

    private void TickHint()
    {
        if (hintText == null || _hintTimer <= 0f)
        {
            return;
        }

        // 用 deltaTime：暂停（timeScale = 0）时提示自然冻结
        _hintTimer -= Time.deltaTime;
        if (_hintTimer <= 0f)
        {
            _hintTimer = 0f;
            hintText.text = string.Empty;
        }
    }

    // 秒 → mm:ss（60 为时间单位换算常量，非玩法数值）
    private string FormatTime(float seconds)
    {
        if (seconds < 0f)
        {
            seconds = 0f;
        }

        int totalSeconds = (int)seconds;
        int minutes = totalSeconds / 60;
        int secs = totalSeconds % 60;
        return minutes.ToString("00") + ":" + secs.ToString("00");
    }
}
