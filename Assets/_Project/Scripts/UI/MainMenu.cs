// MainMenu.cs —— 主菜单
// 职责：标题界面按钮接线：开始游戏 → 加载关卡选择场景；退出游戏 → 编辑器停止播放 / 打包后 Application.Quit()；
//       重置进度（调试用）→ 先弹确认面板，确认后删除 PlayerPrefs 里的解锁 / 金币 / 星级 / 最佳时间记录。
// Inspector 需要拖：startButton，quitButton（可空）、resetProgressButton（可空）、
//       resetConfirmPanel 与 resetConfirmYesButton / resetConfirmNoButton（可空），audioSource 与 clickClip（可空）。
// 依赖核心脚本：无（只使用 SceneManager 与 PlayerPrefs，场景名与键名全部是 public 字段）。
// 说明：Start 里把 Time.timeScale 复位为 1，避免上一关暂停状态残留。

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    [Header("按钮（后两个可空）")]
    [Tooltip("开始游戏 → 加载 levelSelectSceneName")]
    public Button startButton;

    [Tooltip("退出游戏 → 编辑器下停止播放，打包后 Application.Quit()")]
    public Button quitButton;

    [Tooltip("重置进度（调试用）→ 弹出确认面板；留空则不显示该功能")]
    public Button resetProgressButton;

    [Header("场景名")]
    [Tooltip("关卡选择场景名")]
    public string levelSelectSceneName = "LevelSelect";

    [Header("音效（可空）")]
    [Tooltip("播放 UI 音效的 AudioSource")]
    public AudioSource audioSource;

    [Tooltip("按钮点击音效")]
    public AudioClip clickClip;

    [Header("重置进度确认面板（可空）")]
    [Tooltip("确认面板根物体；Start 时隐藏")]
    public GameObject resetConfirmPanel;

    [Tooltip("确认重置按钮")]
    public Button resetConfirmYesButton;

    [Tooltip("取消重置按钮")]
    public Button resetConfirmNoButton;

    [Header("PlayerPrefs 键名（与核心层 LevelManager 的约定一致，勿随意修改）")]
    [Tooltip("解锁键前缀，实际键为 前缀 + 关卡序号，例如 LevelUnlocked_1")]
    public string unlockedKeyPrefix = "LevelUnlocked_";

    [Tooltip("金币记录键前缀，例如 LevelCoins_1")]
    public string coinsKeyPrefix = "LevelCoins_";

    [Tooltip("星级记录键前缀，例如 LevelStars_1")]
    public string starsKeyPrefix = "LevelStars_";

    [Tooltip("最佳时间键前缀，例如 BestTime_1")]
    public string bestTimeKeyPrefix = "BestTime_";

    [Tooltip("关卡总数：重置进度时删除 1..该值 的所有记录")]
    public int levelCount = 3;

    private void Start()
    {
        // 场景进入状态复位：防止上一关暂停残留
        Time.timeScale = 1f;

        if (startButton != null)
        {
            startButton.onClick.AddListener(OnStartClicked);
        }

        if (quitButton != null)
        {
            quitButton.onClick.AddListener(OnQuitClicked);
        }

        if (resetProgressButton != null)
        {
            resetProgressButton.onClick.AddListener(OnResetProgressClicked);
        }

        if (resetConfirmYesButton != null)
        {
            resetConfirmYesButton.onClick.AddListener(OnResetConfirmYesClicked);
        }

        if (resetConfirmNoButton != null)
        {
            resetConfirmNoButton.onClick.AddListener(OnResetConfirmNoClicked);
        }

        if (resetConfirmPanel != null)
        {
            resetConfirmPanel.SetActive(false);
        }
    }

    private void OnStartClicked()
    {
        PlayClick();

        Time.timeScale = 1f;

        if (string.IsNullOrEmpty(levelSelectSceneName))
        {
            return;
        }

        SceneManager.LoadScene(levelSelectSceneName);
    }

    private void OnQuitClicked()
    {
        PlayClick();

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnResetProgressClicked()
    {
        PlayClick();

        // 配了确认面板就先问一次；没配则直接重置
        if (resetConfirmPanel != null)
        {
            resetConfirmPanel.SetActive(true);
            return;
        }

        DeleteProgress();
    }

    private void OnResetConfirmYesClicked()
    {
        PlayClick();
        DeleteProgress();
    }

    private void OnResetConfirmNoClicked()
    {
        PlayClick();

        if (resetConfirmPanel != null)
        {
            resetConfirmPanel.SetActive(false);
        }
    }

    // 删除 解锁 / 金币 / 星级 / 最佳时间 四类记录（键名与核心层约定一致）
    private void DeleteProgress()
    {
        int count = Mathf.Max(0, levelCount);

        for (int i = 1; i <= count; i++)
        {
            PlayerPrefs.DeleteKey(unlockedKeyPrefix + i);
            PlayerPrefs.DeleteKey(coinsKeyPrefix + i);
            PlayerPrefs.DeleteKey(starsKeyPrefix + i);
            PlayerPrefs.DeleteKey(bestTimeKeyPrefix + i);
        }

        PlayerPrefs.Save();

        if (resetConfirmPanel != null)
        {
            resetConfirmPanel.SetActive(false);
        }
    }

    private void PlayClick()
    {
        if (audioSource == null || clickClip == null)
        {
            return;
        }

        audioSource.PlayOneShot(clickClip);
    }
}
