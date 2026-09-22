using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 职责：全局暂停 / 重新开始 / 退出关卡 / 退出游戏。不碰任何游戏数据。
/// Inspector：无需拖引用；由 PauseMenu 的按钮反向调用它。
/// 依赖：无（只用 SceneManager 与 Application）。
/// 注意：Esc 由 PauseMenu 读取并调用 SetPaused，本脚本不读输入，避免双重切换。
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("场景名（必须与 Build Settings 中的名字一致）")]
    public string levelSelectSceneName = "LevelSelect";
    public string mainMenuSceneName = "MainMenu";

    [Header("暂停")]
    [Tooltip("暂停时的 Time.timeScale，0 表示完全冻结")]
    public float pauseTimeScale = 0f;

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused { get; private set; }

    void Start()
    {
        // 防止上一关暂停后残留的 timeScale 影响本场景
        Time.timeScale = 1f;
        IsPaused = false;
    }

    public void TogglePause()
    {
        SetPaused(!IsPaused);
    }

    /// <summary>暂停 / 恢复游戏（PauseMenu 调用）。</summary>
    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        Time.timeScale = paused ? pauseTimeScale : 1f;
    }

    /// <summary>重新开始当前关卡。</summary>
    public void RestartLevel()
    {
        Time.timeScale = 1f;
        int index = SceneManager.GetActiveScene().buildIndex;
        SceneManager.LoadScene(index);
    }

    /// <summary>退出当前关卡，回到关卡选择。</summary>
    public void ExitLevel()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(levelSelectSceneName);
    }

    /// <summary>退出游戏。</summary>
    public void QuitGame()
    {
        Time.timeScale = 1f;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
