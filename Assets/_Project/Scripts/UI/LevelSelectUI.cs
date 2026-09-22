// LevelSelectUI.cs —— 关卡选择界面
// 职责：Start 时按 PlayerPrefs 读取每关解锁状态与成绩：已解锁 → 按钮可点 + 显示金币/星级；
//       未解锁 → interactable = false + 文字与图标置灰 + 星星全灭；点击关卡先 Time.timeScale = 1 再加载场景。
// 星级显示方式：**选用 Image 星星**（levelStarImages 一维扁平数组，第 i 关第 s 颗星的下标 = i * starsPerLevel + s），
//       配套 starOnSprite / starOffSprite；未使用文本形式的星级显示。
// Inspector 需要拖：levelButtons、levelSceneNames（默认 Level1/2/3）、levelCoinTexts、levelStarImages、
//       starOnSprite / starOffSprite、backButton，audioSource 与 clickClip（可空）。
// 依赖核心脚本：无（只读 PlayerPrefs，键名与核心层 LevelManager 的约定一致）。
// 说明：数组长度不一致时统一用 Mathf.Min 保护，任何越界访问都会先做长度判断。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class LevelSelectUI : MonoBehaviour
{
    [Header("关卡按钮与场景名（索引 0 对应第 1 关）")]
    [Tooltip("关卡按钮数组；未解锁时 interactable = false；多出来的按钮会按 hideUnusedButtons 处理")]
    public Button[] levelButtons;

    [Tooltip("关卡场景名。勾选 autoDetectFromBuildSettings 时这里会被自动覆盖，不用手填")]
    public string[] levelSceneNames = new string[] { "Level1", "Level2" };

    [Header("关卡列表自动识别")]
    [Tooltip("勾选后：从 Build Settings 里自动取出所有关卡场景，排除 nonLevelSceneNames 里的名字。" +
             "好处是加一关只要把场景加进 Build Settings 就行，不用回来改这个界面")]
    public bool autoDetectFromBuildSettings = true;

    [Tooltip("自动识别时要排除的非关卡场景（主菜单 / 选关界面 / 以后可能的教程或测试场景）")]
    public string[] nonLevelSceneNames = new string[] { "MainMenu", "LevelSelect", "Test_Auto", "SampleScene" };

    [Tooltip("勾选后：没有对应场景的多余按钮会被隐藏，避免出现一个点了没反应的假关卡")]
    public bool hideUnusedButtons = true;

    [Header("成绩显示")]
    [Tooltip("每关卡片标题数组（自动写成「教学关」/「第 N 关」）")]
    public TMP_Text[] levelLabels;

    [Tooltip("每关金币文本数组，显示 PlayerPrefs 的 LevelCoins_i")]
    public TMP_Text[] levelCoinTexts;

    [Tooltip("星级图标（扁平数组）：第 i 关第 s 颗星的下标 = i * starsPerLevel + s")]
    public Image[] levelStarImages;

    [Tooltip("每关星星数量（默认 3），用于计算 levelStarImages 的下标")]
    public int starsPerLevel = 3;

    [Tooltip("点亮星星的 sprite")]
    public Sprite starOnSprite;

    [Tooltip("未点亮星星的 sprite")]
    public Sprite starOffSprite;

    [Tooltip("这些场景不显示星级（教程关不算成绩）。按场景名匹配")]
    public string[] sceneNamesWithoutStars = new string[] { "Level0" };

    [Tooltip("教程关卡片（编号 0）的标题")]
    public string tutorialCardTitle = "教学关";

    [Tooltip("普通关卡卡片标题格式，{0} 会替换成关卡编号")]
    public string levelCardTitleFormat = "第 {0} 关";

    [Header("关卡编号与解锁")]
    [Tooltip("关卡编号从【场景名】里解析（Level2 → 2），而不是用数组下标。" +
             "这样插入教程关 Level0 也不会打乱已有的 LevelUnlocked_N 存档")]
    public bool parseLevelNumberFromSceneName = true;

    [Tooltip("编号 <= 这个值的关卡默认解锁（0 = 教程关，1 = 第一关）")]
    public int defaultUnlockedUpTo = 1;

    [Header("卡片自动排布（只排显示出来的那些，整体居中）")]
    [Tooltip("勾选后：把实际显示的卡片按网格重新排布并居中，没关卡的空槽位不会留洞")]
    public bool autoArrangeVisibleSlots = true;

    [Tooltip("网格最多几列")]
    public int gridColumns = 4;

    [Tooltip("格子间距（像素）")]
    public Vector2 gridCellSize = new Vector2(390f, 260f);

    [Tooltip("整个网格的中心相对画布中心的偏移")]
    public Vector2 gridCenterOffset = new Vector2(0f, 10f);

    [Header("颜色")]
    [Tooltip("未解锁时文字与图标的颜色（置灰）")]
    public Color lockedTextColor = Color.gray;

    [Tooltip("已解锁时文字与图标的颜色")]
    public Color unlockedTextColor = Color.white;

    [Header("返回主菜单")]
    [Tooltip("返回主菜单按钮")]
    public Button backButton;

    [Tooltip("主菜单场景名")]
    public string mainMenuSceneName = "MainMenu";

    [Header("调试与音效")]
    [Tooltip("调试用：勾选后无视 PlayerPrefs，全部关卡解锁")]
    public bool unlockAllForDebug = false;

    [Tooltip("播放 UI 音效的 AudioSource；可空")]
    public AudioSource audioSource;

    [Tooltip("按钮点击音效；可空")]
    public AudioClip clickClip;

    [Header("PlayerPrefs 键名（与核心层 LevelManager 的约定一致，勿随意修改）")]
    [Tooltip("解锁键前缀，实际键为 前缀 + 关卡序号，例如 LevelUnlocked_1（默认只有第 1 关解锁）")]
    public string unlockedKeyPrefix = "LevelUnlocked_";

    [Tooltip("金币记录键前缀，例如 LevelCoins_1")]
    public string coinsKeyPrefix = "LevelCoins_";

    [Tooltip("星级记录键前缀，例如 LevelStars_1")]
    public string starsKeyPrefix = "LevelStars_";

    private void Start()
    {
        // 场景进入状态复位：防止上一关暂停残留
        Time.timeScale = 1f;

        if (backButton != null)
        {
            backButton.onClick.AddListener(OnBackClicked);
        }

        // 关卡列表以 Build Settings 为准：
        // 这样"加一关只要把场景拖进 Build Settings"，不用回来改 UI（也不会再出现点了报错的假关卡）
        if (autoDetectFromBuildSettings)
        {
            levelSceneNames = DetectLevelScenes();
        }

        if (levelButtons == null || levelSceneNames == null)
        {
            return;
        }

        // 两个数组长度不一致时取较小值，保证不越界
        int count = Mathf.Min(levelButtons.Length, levelSceneNames.Length);

        // 没有对应场景的多余按钮：连监听都不挂，并且隐藏掉，免得玩家点了没反应
        if (hideUnusedButtons && levelButtons.Length > count)
        {
            for (int i = count; i < levelButtons.Length; i++)
            {
                HideSlot(i);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Button button = levelButtons[i];
            if (button == null)
            {
                continue;
            }

            string sceneName = levelSceneNames[i];
            int levelNumber = parseLevelNumberFromSceneName ? ParseLevelNumber(sceneName) : (i + 1);
            bool showStars = !IsSceneWithoutStars(sceneName);

            bool unlocked = unlockAllForDebug
                || levelNumber <= defaultUnlockedUpTo
                || PlayerPrefs.GetInt(unlockedKeyPrefix + levelNumber, 0) == 1;

            button.interactable = unlocked;

            // 按钮自身的文字与图标一起着色（已解锁恢复亮色，未解锁置灰）
            ApplyButtonColor(button, unlocked ? unlockedTextColor : lockedTextColor);

            // 卡片标题：教程关（编号 0）写「教学关」，其余写「第 N 关」
            if (levelLabels != null && i < levelLabels.Length && levelLabels[i] != null)
            {
                levelLabels[i].text = levelNumber <= 0
                    ? tutorialCardTitle
                    : string.Format(levelCardTitleFormat, levelNumber);
                levelLabels[i].color = unlocked ? unlockedTextColor : lockedTextColor;
            }

            int coins = PlayerPrefs.GetInt(coinsKeyPrefix + levelNumber, 0);
            int stars = PlayerPrefs.GetInt(starsKeyPrefix + levelNumber, 0);

            if (levelCoinTexts != null && i < levelCoinTexts.Length && levelCoinTexts[i] != null)
            {
                // 不计成绩的关卡（教程关）不显示金币记录，免得永远是 0 让人以为没打赢
                levelCoinTexts[i].text = showStars ? coins.ToString() : "";
                levelCoinTexts[i].color = unlocked ? unlockedTextColor : lockedTextColor;
            }

            // 未解锁的关卡星星全灭；不计成绩的关卡直接不显示星星
            RefreshStars(i, (unlocked && showStars) ? stars : 0, showStars);

            if (unlocked)
            {
                // 复制循环变量，避免所有按钮共享同一个 i
                int levelIndex = i;
                button.onClick.AddListener(() => OnLevelClicked(levelIndex));
            }
        }

        if (autoArrangeVisibleSlots)
        {
            ArrangeVisibleSlots(count);
        }
    }

    /// <summary>
    /// 把实际显示的 count 张卡片按网格重新排布并整体居中。
    /// 这样 3 关就居中排一行、12 关就是 4×3，不会因为空槽位留下洞。
    /// </summary>
    private void ArrangeVisibleSlots(int count)
    {
        if (levelButtons == null || count <= 0)
        {
            return;
        }

        int cols = Mathf.Clamp(gridColumns, 1, count);
        int rows = Mathf.CeilToInt(count / (float)cols);

        for (int i = 0; i < count; i++)
        {
            Button button = levelButtons[i];
            if (button == null)
            {
                continue;
            }

            RectTransform rt = button.transform as RectTransform;
            if (rt == null)
            {
                continue;
            }

            int row = i / cols;
            int col = i % cols;
            int colsInThisRow = Mathf.Min(cols, count - row * cols);

            // 每行内部也居中（最后一行不满时不会靠左）
            float x = (col - (colsInThisRow - 1) * 0.5f) * gridCellSize.x + gridCenterOffset.x;
            float y = ((rows - 1) * 0.5f - row) * gridCellSize.y + gridCenterOffset.y;
            rt.anchoredPosition = new Vector2(x, y);
        }
    }

    /// <summary>从场景名里解析关卡编号："Level2" → 2；解析不出来返回 0。</summary>
    public static int ParseLevelNumber(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return 0;
        }

        string digits = "";
        for (int i = 0; i < sceneName.Length; i++)
        {
            if (sceneName[i] >= '0' && sceneName[i] <= '9')
            {
                digits += sceneName[i];
            }
            else if (digits.Length > 0)
            {
                break;   // 数字已经结束
            }
        }

        int value;
        return int.TryParse(digits, out value) ? value : 0;
    }

    private bool IsSceneWithoutStars(string sceneName)
    {
        if (sceneNamesWithoutStars == null || string.IsNullOrEmpty(sceneName))
        {
            return false;
        }

        for (int i = 0; i < sceneNamesWithoutStars.Length; i++)
        {
            if (!string.IsNullOrEmpty(sceneNamesWithoutStars[i]) &&
                string.Equals(sceneNamesWithoutStars[i], sceneName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从 Build Settings 里取出所有关卡场景名（按 Build Settings 的顺序），
    /// 排除 nonLevelSceneNames 里列出的非关卡场景。
    /// </summary>
    private string[] DetectLevelScenes()
    {
        List<string> list = new List<string>();
        int total = SceneManager.sceneCountInBuildSettings;

        for (int i = 0; i < total; i++)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name) || IsNonLevelScene(name))
            {
                continue;
            }

            list.Add(name);
        }

        return list.ToArray();
    }

    private bool IsNonLevelScene(string sceneName)
    {
        if (nonLevelSceneNames == null)
        {
            return false;
        }

        for (int i = 0; i < nonLevelSceneNames.Length; i++)
        {
            if (!string.IsNullOrEmpty(nonLevelSceneNames[i]) &&
                string.Equals(nonLevelSceneNames[i], sceneName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>隐藏第 i 个关卡槽位（按钮 + 它的金币文本 + 它的星星）。用于"这个关卡还不存在"。</summary>
    private void HideSlot(int i)
    {
        Button button = levelButtons != null && i < levelButtons.Length ? levelButtons[i] : null;
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.gameObject.SetActive(false);
        }

        if (levelCoinTexts != null && i < levelCoinTexts.Length && levelCoinTexts[i] != null)
        {
            if (button == null || !levelCoinTexts[i].transform.IsChildOf(button.transform))
            {
                levelCoinTexts[i].gameObject.SetActive(false);
            }
        }

        if (levelStarImages == null)
        {
            return;
        }

        int perLevel = Mathf.Max(1, starsPerLevel);
        for (int s = 0; s < perLevel; s++)
        {
            int flatIndex = i * perLevel + s;
            if (flatIndex < 0 || flatIndex >= levelStarImages.Length || levelStarImages[flatIndex] == null)
            {
                continue;
            }
            if (button == null || !levelStarImages[flatIndex].transform.IsChildOf(button.transform))
            {
                levelStarImages[flatIndex].gameObject.SetActive(false);
            }
        }
    }

    private void OnLevelClicked(int levelIndex)
    {
        PlayClick();

        // 进入关卡前必须复位 timeScale（上一关可能是暂停状态退出的）
        Time.timeScale = 1f;

        if (levelSceneNames == null || levelIndex < 0 || levelIndex >= levelSceneNames.Length)
        {
            return;
        }

        string sceneName = levelSceneNames[levelIndex];
        if (string.IsNullOrEmpty(sceneName))
        {
            return;
        }

        // 兜底：场景不在 Build Settings 里时给一条人话警告，而不是抛 Unity 的 SceneManager 错误
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning("[选关] 场景 '" + sceneName + "' 不在 Build Settings 里，无法进入。" +
                             "把该场景加进 File → Build Settings，或改 LevelSelectUI.levelSceneNames。");
            return;
        }

        SceneManager.LoadScene(sceneName);
    }

    private void OnBackClicked()
    {
        PlayClick();

        if (string.IsNullOrEmpty(mainMenuSceneName))
        {
            return;
        }

        SceneManager.LoadScene(mainMenuSceneName);
    }

    // 把按钮下所有 Graphic（文字 / 图标 / 底板）统一着色，保留各自原有透明度
    private void ApplyButtonColor(Button button, Color color)
    {
        if (button == null)
        {
            return;
        }

        Graphic[] graphics = button.GetComponentsInChildren<Graphic>(true);
        if (graphics == null)
        {
            return;
        }

        for (int i = 0; i < graphics.Length; i++)
        {
            if (graphics[i] == null)
            {
                continue;
            }

            Color target = color;
            target.a = graphics[i].color.a;
            graphics[i].color = target;
        }
    }

    // 第 levelIndex（0 起）关的星级显示：扁平数组下标 = levelIndex * starsPerLevel + s
    // visible = false 时整排星星隐藏（教程关不计成绩）
    private void RefreshStars(int levelIndex, int stars, bool visible)
    {
        if (levelStarImages == null)
        {
            return;
        }

        int perLevel = Mathf.Max(1, starsPerLevel);
        int baseIndex = levelIndex * perLevel;
        stars = Mathf.Clamp(stars, 0, perLevel);

        for (int s = 0; s < perLevel; s++)
        {
            int flatIndex = baseIndex + s;
            if (flatIndex < 0 || flatIndex >= levelStarImages.Length)
            {
                continue;
            }

            Image star = levelStarImages[flatIndex];
            if (star == null)
            {
                continue;
            }

            star.gameObject.SetActive(visible);
            if (!visible)
            {
                continue;
            }

            Sprite sprite = s < stars ? starOnSprite : starOffSprite;
            if (sprite != null)
            {
                star.sprite = sprite;
            }
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
