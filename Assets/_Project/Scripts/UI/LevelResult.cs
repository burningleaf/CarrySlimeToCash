// LevelResult.cs —— 关卡结算面板（拉模式：逻辑层不会调用本脚本，必须靠自己轮询）
// 职责：Update 中轮询 levelManager.LevelFinished / LevelFailed，延迟 showDelay 秒后弹出面板，
//       填充 金币(FinalCoins) / 血量(SlimeHealth / SlimeMaxHealth) / 时间(秒保留 1 位) / 星级 / 印章文字，
//       并提供 重玩 / 下一关 / 返回关卡选择 三个按钮。
// Inspector 需要拖：levelManager、gameManager、panelRoot，coinText / healthText / timeText / stampText，
//       starImages（3 个）、starOnSprite / starOffSprite，replayButton / nextButton / levelSelectButton。
// 依赖核心脚本：LevelManager（只读结算属性）、GameManager（RestartLevel）。
// 注意：本脚本不修改 Time.timeScale——暂停与恢复由 GameManager / PauseMenu 负责，结算面板不冻结游戏。

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class LevelResult : MonoBehaviour
{
    [Header("核心逻辑引用")]
    [Tooltip("关卡数据中枢：读取结算数据 FinalCoins / SlimeHealth / Stars 与状态标志")]
    public LevelManager levelManager;

    [Tooltip("游戏管理器：重玩按钮调用 RestartLevel()")]
    public GameManager gameManager;

    [Header("面板根节点")]
    [Tooltip("结算面板根物体；Start 时隐藏，满足条件后显示")]
    public GameObject panelRoot;

    [Header("结算文本")]
    [Tooltip("金币文本，显示 FinalCoins")]
    public TMP_Text coinText;

    [Tooltip("血量文本，显示 当前 / 最大")]
    public TMP_Text healthText;

    [Tooltip("时间文本，显示 TotalTime 秒（保留 1 位小数）")]
    public TMP_Text timeText;

    [Tooltip("印章文本，通关显示 winStampText，失败显示 failStampText")]
    public TMP_Text stampText;

    [Header("星级")]
    [Tooltip("3 个星星图标，按关卡得到的星级逐个切换 sprite")]
    public Image[] starImages;

    [Tooltip("点亮星星的 sprite")]
    public Sprite starOnSprite;

    [Tooltip("未点亮星星的 sprite")]
    public Sprite starOffSprite;

    [Header("按钮（可空，为空时自动跳过绑定）")]
    [Tooltip("重玩按钮 → gameManager.RestartLevel()")]
    public Button replayButton;

    [Tooltip("下一关按钮 → 加载 nextSceneName；nextSceneName 为空时自动隐藏")]
    public Button nextButton;

    [Tooltip("返回关卡选择按钮 → 加载 levelSelectSceneName")]
    public Button levelSelectButton;

    [Header("场景名与显示参数")]
    [Tooltip("下一关场景名；留空表示本关是最后一关（自动隐藏 nextButton）")]
    public string nextSceneName = "";

    [Tooltip("关卡选择场景名")]
    public string levelSelectSceneName = "LevelSelect";

    [Tooltip("达成通关/失败条件后，延迟多少秒（不受 timeScale 影响）再弹出面板")]
    public float showDelay = 0.8f;

    [Tooltip("失败（史莱姆死亡）时是否也弹出结算面板")]
    public bool showOnLevelFailed = true;

    [Tooltip("失败时的印章文字")]
    public string failStampText = "史莱姆阵亡…";
    [Tooltip("玩家自己掉进深坑时的印章文本（和史莱姆阵亡区分开）")]
    public string pitFailStampText = "掉进深坑了…";

    [Tooltip("通关时的印章文字")]
    public string winStampText = "抵达收购站！";

    private bool _shown;
    private float _showTimer;

    private void Start()
    {
        _shown = false;
        _showTimer = 0f;

        if (panelRoot != null)
        {
            panelRoot.SetActive(false);
        }

        if (replayButton != null)
        {
            replayButton.onClick.AddListener(OnReplayClicked);
        }

        if (levelSelectButton != null)
        {
            levelSelectButton.onClick.AddListener(OnLevelSelectClicked);
        }

        if (nextButton != null)
        {
            if (string.IsNullOrEmpty(nextSceneName))
            {
                // 最后一关（或未配置下一关）：隐藏"下一关"按钮
                nextButton.gameObject.SetActive(false);
            }
            else
            {
                nextButton.onClick.AddListener(OnNextClicked);
            }
        }
    }

    private void Update()
    {
        if (_shown || levelManager == null)
        {
            return;
        }

        bool finished = levelManager.LevelFinished;
        bool failed = showOnLevelFailed && levelManager.LevelFailed;
        if (!finished && !failed)
        {
            return;
        }

        // 用不受暂停影响的时间累计延迟，本脚本不改动 Time.timeScale
        _showTimer += Time.unscaledDeltaTime;
        // 结算面板要等通关演出播完再弹
        float totalDelay = showDelay + (levelManager != null ? levelManager.GoalCutsceneDuration : 0f);
        if (_showTimer < totalDelay)
        {
            return;
        }

        ShowResult(finished);
    }

    private void ShowResult(bool finished)
    {
        _shown = true;

        if (panelRoot != null)
        {
            panelRoot.SetActive(true);
        }

        if (levelManager == null)
        {
            return;
        }

        if (coinText != null)
        {
            coinText.text = levelManager.FinalCoins.ToString();
        }

        if (healthText != null)
        {
            healthText.text = levelManager.SlimeHealth + " / " + levelManager.SlimeMaxHealth;
        }

        if (timeText != null)
        {
            timeText.text = levelManager.TotalTime.ToString("F1");
        }

        if (stampText != null)
        {
            if (finished) stampText.text = winStampText;
            else if (levelManager.FailReason == LevelFailReason.PlayerFellIntoPit) stampText.text = pitFailStampText;
            else stampText.text = failStampText;
        }

        // 失败时星级全灭
        int stars = finished ? levelManager.Stars : 0;
        RefreshStars(stars);
    }

    private void RefreshStars(int stars)
    {
        if (starImages == null)
        {
            return;
        }

        stars = Mathf.Clamp(stars, 0, starImages.Length);
        for (int i = 0; i < starImages.Length; i++)
        {
            if (starImages[i] == null)
            {
                continue;
            }

            Sprite sprite = i < stars ? starOnSprite : starOffSprite;
            if (sprite != null)
            {
                starImages[i].sprite = sprite;
            }
        }
    }

    private void OnReplayClicked()
    {
        if (gameManager == null)
        {
            return;
        }

        gameManager.RestartLevel();
    }

    private void OnNextClicked()
    {
        if (string.IsNullOrEmpty(nextSceneName))
        {
            return;
        }

        SceneManager.LoadScene(nextSceneName);
    }

    private void OnLevelSelectClicked()
    {
        if (!string.IsNullOrEmpty(levelSelectSceneName))
        {
            SceneManager.LoadScene(levelSelectSceneName);
            return;
        }

        // 未配置场景名时退回 GameManager 的关卡选择出口
        if (gameManager != null)
        {
            gameManager.ExitLevel();
        }
    }
}
