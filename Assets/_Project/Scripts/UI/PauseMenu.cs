// PauseMenu.cs —— Esc 暂停菜单（唯一读取 Esc 的脚本，GameManager 不读输入）
// 职责：Update 中检测 Esc 键开关暂停面板；打开时 gameManager.SetPaused(true) 并显示 panelRoot，
//       关闭时 gameManager.SetPaused(false) 并隐藏 panelRoot；四个按钮分别恢复 / 重开 / 退出关卡 / 退出游戏。
// Inspector 需要拖：gameManager、panelRoot，resumeButton / restartButton / exitLevelButton / quitButton，
//       audioSource（可空）与 openClip / clickClip（可空）。
// 依赖核心脚本：GameManager（SetPaused / RestartLevel / ExitLevel / QuitGame）。
// 说明：暂停期间 Time.timeScale = 0，但 Update 与 Input.GetKeyDown 仍然每帧执行，因此 Esc 依然有效。

using UnityEngine;
using UnityEngine.UI;

public class PauseMenu : MonoBehaviour
{
    [Header("核心逻辑引用")]
    [Tooltip("游戏管理器：暂停 / 重开 / 退出关卡 / 退出游戏")]
    public GameManager gameManager;

    [Header("面板")]
    [Tooltip("暂停面板根物体；Start 时隐藏")]
    public GameObject panelRoot;

    [Header("按钮（可空，为空时自动跳过绑定）")]
    [Tooltip("回到游戏 → gameManager.SetPaused(false)")]
    public Button resumeButton;

    [Tooltip("重新开始 → gameManager.RestartLevel()")]
    public Button restartButton;

    [Tooltip("退出关卡 → gameManager.ExitLevel()")]
    public Button exitLevelButton;

    [Tooltip("退出游戏 → gameManager.QuitGame()")]
    public Button quitButton;

    [Header("音效（可空）")]
    [Tooltip("播放 UI 音效的 AudioSource；建议勾选 playOnAwake = false")]
    public AudioSource audioSource;

    [Tooltip("暂停面板打开时播放的音效")]
    public AudioClip openClip;

    [Tooltip("按钮点击音效")]
    public AudioClip clickClip;

    [Header("输入")]
    [Tooltip("是否允许用 Esc 键开关暂停面板")]
    public bool allowEscToggle = true;

    [Tooltip("暂停开关键位（默认 Esc；只有本脚本读这个键）")]
    public KeyCode pauseKey = KeyCode.Escape;

    private bool _isOpen;

    private void Start()
    {
        _isOpen = false;

        if (panelRoot != null)
        {
            panelRoot.SetActive(false);
        }

        if (resumeButton != null)
        {
            resumeButton.onClick.AddListener(OnResumeClicked);
        }

        if (restartButton != null)
        {
            restartButton.onClick.AddListener(OnRestartClicked);
        }

        if (exitLevelButton != null)
        {
            exitLevelButton.onClick.AddListener(OnExitLevelClicked);
        }

        if (quitButton != null)
        {
            quitButton.onClick.AddListener(OnQuitClicked);
        }
    }

    private void Update()
    {
        if (!allowEscToggle)
        {
            return;
        }

        // 不依赖 Time.deltaTime：暂停时 timeScale = 0，本行依然每帧执行
        if (Input.GetKeyDown(pauseKey))
        {
            SetMenuOpen(!_isOpen);
        }
    }

    // 统一入口：面板显隐与 gameManager 的暂停状态始终同步
    private void SetMenuOpen(bool open)
    {
        _isOpen = open;

        if (panelRoot != null)
        {
            panelRoot.SetActive(open);
        }

        if (gameManager != null)
        {
            gameManager.SetPaused(open);
        }

        if (open)
        {
            PlayClip(openClip);
        }
    }

    private void OnResumeClicked()
    {
        PlayClip(clickClip);
        SetMenuOpen(false);
    }

    private void OnRestartClicked()
    {
        PlayClip(clickClip);

        // 先恢复 timeScale，再重载场景，避免新场景带着暂停状态启动
        SetMenuOpen(false);

        if (gameManager != null)
        {
            gameManager.RestartLevel();
        }
    }

    private void OnExitLevelClicked()
    {
        PlayClip(clickClip);

        // 同上：退出关卡前先解除暂停
        SetMenuOpen(false);

        if (gameManager != null)
        {
            gameManager.ExitLevel();
        }
    }

    private void OnQuitClicked()
    {
        PlayClip(clickClip);

        if (gameManager != null)
        {
            gameManager.QuitGame();
        }
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null)
        {
            return;
        }

        audioSource.PlayOneShot(clip);
    }
}
