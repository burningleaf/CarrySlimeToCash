// ---------------------------------------------------------------------------
// AssetWiring.cs —— 音效资源接线（编辑器工具，只放 Editor 文件夹，不进游戏包）
//
// 用法：
//   菜单：Unity 顶部 → Tools → 呆呆史莱姆 → ♪ 音效接线（全部场景 + 预制体）
//   命令行：
//     Unity.exe -batchmode -quit -projectPath 【工程路径】 ^
//               -executeMethod AssetWiring.BatchWireAssets -logFile 【日志】
//
// 它干三件事（每一步都幂等，可以反复点、反复跑）：
//   ① 场景里 LevelBuilder 的 7 个 clip 字段（coinClip / orbClip / platePressClip /
//      plateReleaseClip / checkpointClip / enemyHitClip / goalClip）← 按映射表填资源。
//      注意：LevelBuilder【生成出来】的物件由 LevelBuilder.cs 自己挂 AudioSource
//      （BuildCoin / BuildOrb / BuildPlate / BuildCheckpoint / BuildEnemy / BuildGoal
//       里的 AttachAudio），不归本文件管。
//   ② 场景 / 预制体里【已经存在】的组件：audioSource 空着就挂一个（已有就复用，绝不重复加），
//      空的 clip 字段按映射表填上。
//   ③ 打一份报告：处理了几个文件、每个文件改了几处、哪些音频文件找不到。
//
// 三条硬规矩：
//   · 只填空字段，绝不覆盖手工拖好的值 → 跑两次结果一样
//   · 音频文件找不到【不报错、不中断】，只记进"缺失清单"，最后统一打印
//   · 清单以外的文件一律不碰（SampleScene.unity 和 Ground_A/B/Pit.prefab 不在清单里）
//
// 依赖：LevelBuilder.cs 必须先有上面那 7 个 AudioClip 字段。没有的话本文件依然能编译，
//      只在报告里列出"字段找不到"并跳过（用的是反射，不是硬引用）。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AssetWiring
{
    const string ProjectRootName = "Assets/_Project";
    const string SfxRoot = ProjectRootName + "/Audio/SFX/";
    const string ScenesRoot = ProjectRootName + "/Scenes/";

    // ======================= 精灵文件（t98 新增：终点观感） =======================
    // ⚠ 这里的路径必须与 GoalStationGenerator.StationPath 一致（那边是生成器）。
    //    本文件刻意【不硬引用】生成器：它的既定约定是"缺依赖也能编译，全靠反射"，
    //    所以路径字符串自己留一份；文件不存在时只警告、不写空值（见 WireBuilderSprites）。

    const string ArtEnvironmentRoot = ProjectRootName + "/Art/Environment/";
    const string SpriteGoalStation = ArtEnvironmentRoot + "Sprite_GoalStation.png";

    // ======================= 音频文件（改文件名只改这里） =======================

    const string ClipUiClick = SfxRoot + "UI/SFX_UI_Click.wav";
    const string ClipUiOpen = SfxRoot + "UI/SFX_UI_Open.wav";
    const string ClipUiSwitch = SfxRoot + "UI/SFX_UI_Switch.wav";
    const string ClipUiError = SfxRoot + "UI/SFX_UI_Error.wav";
    const string ClipUiLocked = SfxRoot + "UI/SFX_UI_Locked.wav";

    const string ClipPlayerJump = SfxRoot + "Player/SFX_Player_Jump.wav";
    const string ClipPlayerLand = SfxRoot + "Player/SFX_Player_Land.wav";
    const string ClipPlayerDeath = SfxRoot + "Player/SFX_Player_Death.wav";
    const string ClipPlayerGrab = SfxRoot + "Player/SFX_Player_Grab.wav";
    const string ClipPlayerPlace = SfxRoot + "Player/SFX_Player_Place.wav";
    const string ClipPlayerThrow = SfxRoot + "Player/SFX_Player_Throw.wav";
    const string ClipPlayerWhistle = SfxRoot + "Player/SFX_Player_Whistle.wav";
    const string ClipPlayerWaypoint = SfxRoot + "Player/SFX_Player_Waypoint.wav";
    const string ClipPlayerClear = SfxRoot + "Player/SFX_Player_Clear.wav";

    const string ClipSlimeHurt = SfxRoot + "Slime/SFX_Slime_Hurt.wav";
    const string ClipSlimeHeal = SfxRoot + "Slime/SFX_Slime_Heal.wav";
    const string ClipSlimeMode = SfxRoot + "Slime/SFX_Slime_Mode.wav";
    const string ClipSlimeDeath = SfxRoot + "Slime/SFX_Slime_Death.wav";

    const string ClipItemCoin = SfxRoot + "Item/SFX_Item_Coin.wav";
    const string ClipItemOrb = SfxRoot + "Item/SFX_Item_Orb.wav";

    const string ClipPlatePress = SfxRoot + "Level/SFX_Plate_Press.wav";
    const string ClipPlateRelease = SfxRoot + "Level/SFX_Plate_Release.wav";
    const string ClipLevelCheckpoint = SfxRoot + "Level/SFX_Level_Checkpoint.wav";
    const string ClipLevelGoal = SfxRoot + "Level/SFX_Level_Goal.wav";

    // ======================= 要处理的文件清单 =======================

    static readonly string[] ScenePaths =
    {
        ScenesRoot + "MainMenu.unity",
        ScenesRoot + "LevelSelect.unity",
        ScenesRoot + "Level0.unity",
        ScenesRoot + "Level1.unity",
        ScenesRoot + "Level2.unity",
        // t98 新增：第 4 关。场景还没建好时 Exists() 会只警告 + 记进"已跳过"，所以加进来是安全的
        ScenesRoot + "Level3.unity",
        ScenesRoot + "LevelMech.unity",
    };

    static readonly string[] PrefabPaths =
    {
        ScenesRoot + "Player.prefab",
        ScenesRoot + "Slime.prefab",
        ProjectRootName + "/Prefabs/Slime/WaypointMarker.prefab",
    };

    // ======================= 菜单 / 命令行入口 =======================

    [MenuItem("Tools/呆呆史莱姆/♪ 音效接线（全部场景 + 预制体）", false, 22)]
    public static void WireMenu()
    {
        BatchWireAssets();
    }

    /// <summary>
    /// 命令行入口（无参数、无返回值）：
    /// Unity.exe -batchmode -quit -projectPath 【工程路径】 -executeMethod AssetWiring.BatchWireAssets -logFile 【日志】
    /// </summary>
    public static void BatchWireAssets()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[音效接线] 当前在 Play 模式，已中止。请先退出 Play 再接线（否则改动不会被保存）。");
            return;
        }

        Debug.Log("[音效接线] ========== 开始 ==========");

        // 菜单点的时候先问一句要不要保存当前改动；批处理模式不问（和 SlimeDemoSetup 一个规矩）
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[音效接线] 用户取消了保存，已中止。");
            return;
        }

        string previousScenePath = SceneManager.GetActiveScene().path;

        Report report = new Report();

        // 先预制体、后场景：场景里的预制体实例随后直接继承预制体上的值，不必写一堆实例覆盖
        WireAllPrefabs(report);
        WireAllScenes(report);

        AssetDatabase.SaveAssets();
        report.Print();

        // 菜单路径：把用户原来开着的场景切回去（批处理模式不用切）
        if (!Application.isBatchMode && !string.IsNullOrEmpty(previousScenePath) && Exists(previousScenePath))
        {
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            Debug.Log("[音效接线] 已切回原来的场景：" + previousScenePath);
        }

        Debug.Log("[音效接线] ========== 结束 ==========");
    }

    // ======================= 预制体 =======================

    /// <summary>
    /// 文件在不在（只给场景 / 预制体用）。
    /// 先看磁盘（Unity 的工作目录就是工程根目录，和 SlimeDemoSetup 走的同一个假设），
    /// 再问 AssetDatabase —— 万一工作目录不是工程根，也不至于整份清单被当成"不存在"跳过。
    /// </summary>
    static bool Exists(string path)
    {
        if (File.Exists(path)) return true;
        return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null;
    }

    static void WireAllPrefabs(Report report)
    {
        for (int i = 0; i < PrefabPaths.Length; i++)
        {
            string path = PrefabPaths[i];
            if (!Exists(path))
            {
                Debug.LogWarning("[音效接线] 预制体不存在，跳过：" + path);
                report.Skipped.Add(path);
                continue;
            }

            // LoadPrefabContents：把预制体临时加载成一份可编辑的副本，改完再 SaveAsPrefabAsset 写回。
            // 直接改场景里的实例只会产生实例覆盖，改不到预制体本身。
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                Debug.LogWarning("[音效接线] 预制体打不开，跳过：" + path);
                report.Skipped.Add(path);
                continue;
            }

            Pass pass = new Pass();
            pass.prefabMode = true;   // 预制体内容不需要 SetDirty，SaveAsPrefabAsset 直接落盘

            try
            {
                WireHierarchy(root, pass);
                if (pass.Total > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            report.Add("预制体", Path.GetFileName(path), pass);
            Debug.Log(string.Format("[音效接线] 预制体 {0}：改了 {1} 处", Path.GetFileName(path), pass.Total));
        }
    }

    // ======================= 场景 =======================

    static void WireAllScenes(Report report)
    {
        for (int i = 0; i < ScenePaths.Length; i++)
        {
            string path = ScenePaths[i];
            if (!Exists(path))
            {
                Debug.LogWarning("[音效接线] 场景不存在，跳过：" + path);
                report.Skipped.Add(path);
                continue;
            }

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            Pass pass = new Pass();
            WireSceneContents(scene, pass);

            // 没改动就不保存 —— 别白白重写场景文件（场景是 MD5 校验 + 往返比对的对象）
            if (pass.Total > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            report.Add("场景", Path.GetFileName(path), pass);
            Debug.Log(string.Format("[音效接线] 场景 {0}：改了 {1} 处", Path.GetFileName(path), pass.Total));
        }
    }

    static void WireSceneContents(Scene scene, Pass pass)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            // ② 已经存在的组件：补 AudioSource + 空的 clip 字段
            WireHierarchy(roots[i], pass);
            // ① LevelBuilder 自己的 7 个 clip 字段（场景里的序列化值，必须写进场景再保存）
            WireBuilders(roots[i], pass);
            // ①' LevelBuilder 的精灵字段（t98：终点观感；同一套"只填空值 + 反射赋值"流程）
            WireBuilderSprites(roots[i], pass);
        }
    }

    // ======================= 接线本体 =======================

    /// <summary>遍历一棵对象树，把所有认识的组件都补一遍。每个组件只会被处理一次。</summary>
    static void WireHierarchy(GameObject root, Pass pass)
    {
        if (root == null) return;

        Component[] all = root.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Component c = all[i];
            if (c == null) continue;                       // 丢失脚本的空槽位
            Target t = FindTarget(c.GetType());
            if (t == null) continue;
            WireComponent(c, t, pass);
        }
    }

    static Target FindTarget(System.Type type)
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            if (Targets[i].type == type) return Targets[i];
        }
        return null;
    }

    static void WireComponent(Component c, Target t, Pass pass)
    {
        // ---- audioSource 字段：空就挂一个（对象上已有 AudioSource 就复用，绝不重复加）----
        AudioSource src = null;
        if (t.audioSource != null)
        {
            src = t.audioSource.GetValue(c) as AudioSource;
            if (src == null)
            {
                src = c.GetComponent<AudioSource>();
                if (src == null)
                {
                    src = c.gameObject.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.spatialBlend = 0f;      // 0 = 2D
                    pass.Sources++;
                    MarkDirty(src, pass);
                    Debug.Log("[音效接线]   " + c.gameObject.name + "：+ AudioSource（" + t.type.Name + " 用）");
                }
                t.audioSource.SetValue(c, src);
                MarkDirty(c, pass);
            }
        }

        // ---- clip 字段：只填空的，已有值一律不碰 ----
        for (int i = 0; i < t.rules.Length; i++)
        {
            Rule r = t.rules[i];
            if (r.info == null) continue;                                  // 建表时已经警告过
            if ((AudioClip)r.info.GetValue(c) != null) continue;            // 手工调过 → 不覆盖
            AudioClip clip = pass.Load(r.clip);
            if (clip == null) continue;                                    // 缺文件 → 已记进缺失清单
            r.info.SetValue(c, clip);
            MarkDirty(c, pass);
            pass.Clips++;
            Debug.Log("[音效接线]   " + c.gameObject.name + " / " + t.type.Name + "." + r.field + "  <-  " + r.clip);
        }
    }

    static void WireBuilders(GameObject root, Pass pass)
    {
        LevelBuilder[] builders = root.GetComponentsInChildren<LevelBuilder>(true);
        for (int i = 0; i < builders.Length; i++)
        {
            LevelBuilder b = builders[i];
            if (b == null) continue;
            for (int k = 0; k < BuilderRules.Length; k++)
            {
                Rule r = BuilderRules[k];
                if (r.info == null) continue;
                if ((AudioClip)r.info.GetValue(b) != null) continue;
                AudioClip clip = pass.Load(r.clip);
                if (clip == null) continue;
                r.info.SetValue(b, clip);
                MarkDirty(b, pass);
                pass.BuilderClips++;
                Debug.Log("[音效接线]   " + b.gameObject.name + " / LevelBuilder." + r.field + "  <-  " + r.clip);
            }
        }
    }

    /// <summary>
    /// 场景里 LevelBuilder 的**精灵**字段（t98 新增；目前只有终点观感 goalSprite）。
    /// 与 WireBuilders 走同一套流程（只看空值 → 反射赋值 → MarkDirty → 计数 + 打日志），区别只有一个：
    /// 资源类型是 Sprite 而不是 AudioClip，所以用 BuilderSpriteRules + pass.LoadSprite。
    ///
    /// 两条硬要求：
    ///   · **资源不存在时只警告 + 记进缺失清单，绝不写空值** —— 否则"先跑接线、后跑生成"会把字段清空；
    ///   · **字段已有值就跳过**（手工拖过的、上一次接线的都不覆盖）⇒ 幂等：反复跑结果一样，
    ///     而且没改动时 pass.Total 不变 ⇒ 场景文件不会被重写（场景是 MD5 校验 / 往返比对的对象）。
    /// </summary>
    static void WireBuilderSprites(GameObject root, Pass pass)
    {
        LevelBuilder[] builders = root.GetComponentsInChildren<LevelBuilder>(true);
        for (int i = 0; i < builders.Length; i++)
        {
            LevelBuilder b = builders[i];
            if (b == null) continue;
            for (int k = 0; k < BuilderSpriteRules.Length; k++)
            {
                SpriteRule r = BuilderSpriteRules[k];
                if (r.info == null) continue;                              // 建表时已经警告过
                if ((Sprite)r.info.GetValue(b) != null) continue;           // 手工调过 / 已接过 → 不覆盖
                Sprite spr = pass.LoadSprite(r.path);
                if (spr == null) continue;                                  // 缺文件 → 已记进缺失清单（⛔ 不写空值）
                r.info.SetValue(b, spr);
                MarkDirty(b, pass);
                pass.BuilderSprites++;
                Debug.Log("[精灵接线]   " + b.gameObject.name + " / LevelBuilder." + r.field + "  <-  " + r.path);
            }
        }
    }

    static void MarkDirty(UnityEngine.Object o, Pass pass)
    {
        if (o == null || pass.prefabMode) return;      // 预制体内容最后统一 SaveAsPrefabAsset
        EditorUtility.SetDirty(o);
        if (PrefabUtility.IsPartOfPrefabInstance(o))
            PrefabUtility.RecordPrefabInstancePropertyModifications(o);
    }

    // ======================= 映射表 =======================
    // 写法："字段名=音频路径"。加字段只加一行。
    // 注意两张表的分工：Targets = 场景/预制体里已存在的组件；BuilderRules = 场景里 LevelBuilder 的字段。

    /// <summary>清单里写了、但代码里找不到的字段（建表时填，报告里打印）。</summary>
    static readonly List<string> MissingFields = new List<string>();

    static readonly Target[] Targets =
    {
        // ---------------- 玩家 ----------------
        T(typeof(PlayerController),
            "jumpClip=" + ClipPlayerJump,
            "landClip=" + ClipPlayerLand,
            "deathClip=" + ClipPlayerDeath),
        T(typeof(PlayerGrab),
            "grabClip=" + ClipPlayerGrab,
            "placeClip=" + ClipPlayerPlace,
            "throwClip=" + ClipPlayerThrow,
            "whistleClip=" + ClipPlayerWhistle,
            "waypointClip=" + ClipPlayerWaypoint,
            "clearClip=" + ClipPlayerClear,
            "errorClip=" + ClipUiError),
        T(typeof(PlayerInventory),
            "switchClip=" + ClipUiSwitch,
            "lockedClip=" + ClipUiLocked),

        // ---------------- 史莱姆 ----------------
        T(typeof(SlimeController),
            // 故意的：史莱姆起跳复用"受伤"音，没有单独文件
            "jumpClip=" + ClipSlimeHurt,
            "hurtClip=" + ClipSlimeHurt,
            "deathClip=" + ClipSlimeDeath,
            "healClip=" + ClipSlimeHeal,
            "modeClip=" + ClipSlimeMode,
            // 2026-09-22 哨子 E/Q 重订新增：Q 冲刺用哨音；吹不响（冷却中 / 待命时）用"按不动"音
            "sprintClip=" + ClipPlayerWhistle,
            "sprintRefusedClip=" + ClipUiLocked),
        T(typeof(SlimePathFollow),
            "placeClip=" + ClipPlayerWaypoint,
            // 故意的：到达路点复用"模式切换"音，没有单独文件
            "reachedClip=" + ClipSlimeMode,
            "clearClip=" + ClipPlayerClear),

        // ---------------- 关卡物件 ----------------
        T(typeof(Checkpoint), "activateClip=" + ClipLevelCheckpoint),
        T(typeof(Hazard), "hitClip=" + ClipSlimeHurt),
        T(typeof(Enemy), "hitClip=" + ClipSlimeHurt),
        T(typeof(Coin), "pickupClip=" + ClipItemCoin),
        T(typeof(SlimeOrb), "pickupClip=" + ClipItemOrb),
        T(typeof(Goal), "arriveClip=" + ClipLevelGoal),
        T(typeof(PressurePlate),
            "pressClip=" + ClipPlatePress,
            "releaseClip=" + ClipPlateRelease),

        // ---------------- UI ----------------
        T(typeof(MainMenu), "clickClip=" + ClipUiClick),
        T(typeof(PauseMenu),
            "openClip=" + ClipUiOpen,
            "clickClip=" + ClipUiClick),
        T(typeof(LevelSelectUI), "clickClip=" + ClipUiClick),
    };

    static readonly Rule[] BuilderRules =
    {
        R(typeof(LevelBuilder), "coinClip", ClipItemCoin),
        R(typeof(LevelBuilder), "orbClip", ClipItemOrb),
        R(typeof(LevelBuilder), "platePressClip", ClipPlatePress),
        R(typeof(LevelBuilder), "plateReleaseClip", ClipPlateRelease),
        R(typeof(LevelBuilder), "checkpointClip", ClipLevelCheckpoint),
        R(typeof(LevelBuilder), "enemyHitClip", ClipSlimeHurt),
        R(typeof(LevelBuilder), "goalClip", ClipLevelGoal),
        R(typeof(LevelBuilder), "hazardHitClip", ClipSlimeHurt),
    };

    // ---- 精灵映射表（t98 新增）----
    // 与 BuilderRules 同一套"字段=资源路径"写法，只是资源类型是 Sprite（走 RSprite + pass.LoadSprite）。
    // 终点观感：LevelBuilder.goalSprite ← Art/Environment/Sprite_GoalStation.png
    // ⛔ 这个字段留空时 BuildGoal 会回退旧观感（金色圆点），所以"找不到图"不是错误，只是没接上。
    static readonly SpriteRule[] BuilderSpriteRules =
    {
        RSprite(typeof(LevelBuilder), "goalSprite", SpriteGoalStation),
    };

    static Target T(System.Type type, params string[] fieldClipPairs)
    {
        Rule[] rules = new Rule[fieldClipPairs.Length];
        for (int i = 0; i < fieldClipPairs.Length; i++)
        {
            string pair = fieldClipPairs[i];
            int eq = pair.IndexOf('=');
            if (eq <= 0 || eq >= pair.Length - 1)
            {
                Debug.LogWarning("[音效接线] 映射表写法不对（应该写成 字段=路径）：" + pair);
                rules[i] = new Rule(pair, "", null);
                continue;
            }
            rules[i] = R(type, pair.Substring(0, eq), pair.Substring(eq + 1));
        }
        return new Target(type, rules);
    }

    static Rule R(System.Type type, string field, string clipPath)
    {
        FieldInfo f = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
        if (f == null || f.FieldType != typeof(AudioClip))
        {
            Debug.LogWarning("[音效接线] 代码里没有 AudioClip 字段 " + type.Name + "." + field + "（映射表里有，跳过）");
            MissingFields.Add(type.Name + "." + field);
            return new Rule(field, clipPath, null);
        }
        return new Rule(field, clipPath, f);
    }

    /// <summary>
    /// 精灵版建表：字段必须是 Sprite。
    /// 找不到字段（例如老版 LevelBuilder 还没有 goalSprite）⇒ 只警告 + 记进"代码里找不到的字段"，不中断。
    /// </summary>
    static SpriteRule RSprite(System.Type type, string field, string spritePath)
    {
        FieldInfo f = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
        if (f == null || f.FieldType != typeof(Sprite))
        {
            Debug.LogWarning("[精灵接线] 代码里没有 Sprite 字段 " + type.Name + "." + field + "（映射表里有，跳过）");
            MissingFields.Add(type.Name + "." + field);
            return new SpriteRule(field, spritePath, null);
        }
        return new SpriteRule(field, spritePath, f);
    }

    // ======================= 数据结构 =======================

    class Rule
    {
        public readonly string field;
        public readonly string clip;
        public readonly FieldInfo info;      // 解析失败为 null

        public Rule(string field, string clip, FieldInfo info)
        {
            this.field = field;
            this.clip = clip;
            this.info = info;
        }
    }

    class SpriteRule
    {
        public readonly string field;
        public readonly string path;
        public readonly FieldInfo info;      // 解析失败为 null

        public SpriteRule(string field, string path, FieldInfo info)
        {
            this.field = field;
            this.path = path;
            this.info = info;
        }
    }

    class Target
    {
        public readonly System.Type type;
        public readonly Rule[] rules;

        public readonly FieldInfo audioSource;   // 组件没有 audioSource 字段就是 null

        public Target(System.Type type, Rule[] rules)
        {
            this.type = type;
            this.rules = rules;

            FieldInfo f = type.GetField("audioSource", BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType != typeof(AudioSource))
            {
                Debug.LogWarning("[音效接线] " + type.Name + ".audioSource 不是 AudioSource 类型，已忽略。");
                f = null;
            }
            this.audioSource = f;
        }
    }

    /// <summary>处理一个文件（一个场景或一个预制体）期间的小账本。</summary>
    class Pass
    {
        public bool prefabMode;
        public readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();
        public readonly List<string> Missing = new List<string>();        // 磁盘上没这个文件
        public readonly List<string> NotImported = new List<string>();    // 文件在，但 AssetDatabase 里没有
        public readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();
        public readonly List<string> MissingSprites = new List<string>();       // t98：磁盘上没这个精灵文件
        public readonly List<string> NotImportedSprites = new List<string>();   // t98：文件在，但没导入成 Sprite
        public int Sources;
        public int Clips;
        public int BuilderClips;
        public int BuilderSprites;      // t98 新增：LevelBuilder 的精灵字段接线数

        // ⚠ 这里必须把 BuilderSprites 算进去：WireAllScenes 用 `pass.Total > 0` 决定"要不要保存场景"，
        //    漏了它就会出现"终点精灵接上了、场景却没保存"（看着成功，其实没落盘）。
        public int Total { get { return Sources + Clips + BuilderClips + BuilderSprites; } }

        /// <summary>按路径取 clip。取不到返回 null（绝不抛异常），并记进缺失清单。</summary>
        public AudioClip Load(string path)
        {
            AudioClip cached;
            if (Cache.TryGetValue(path, out cached)) return cached;

            // 路径不存在时 LoadAssetAtPath 只是返回 null，不会报错
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            Cache[path] = clip;

            if (clip == null)
            {
                if (File.Exists(path))
                {
                    if (!NotImported.Contains(path)) NotImported.Add(path);
                }
                else
                {
                    if (!Missing.Contains(path)) Missing.Add(path);
                }
            }
            return clip;
        }

        /// <summary>
        /// 按路径取 Sprite（t98）。取不到返回 null（绝不抛异常），并记进**精灵**缺失清单。
        /// 与 Load 同一个道理：LoadAssetAtPath 对不存在的路径只返回 null；
        /// "文件在但拿不到 Sprite" 通常是还没导入 / 导入类型不是 Sprite ⇒ 单独记一条，方便排查。
        /// </summary>
        public Sprite LoadSprite(string path)
        {
            Sprite cached;
            if (SpriteCache.TryGetValue(path, out cached)) return cached;

            Sprite spr = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            SpriteCache[path] = spr;

            if (spr == null)
            {
                if (File.Exists(path))
                {
                    if (!NotImportedSprites.Contains(path)) NotImportedSprites.Add(path);
                }
                else
                {
                    if (!MissingSprites.Contains(path)) MissingSprites.Add(path);
                }
            }
            return spr;
        }
    }

    class FileStat
    {
        public string kind;      // "场景" / "预制体"
        public string label;     // 文件名
        public int sources;
        public int clips;
        public int builderClips;
        public int builderSprites;      // t98

        public int Total { get { return sources + clips + builderClips + builderSprites; } }
    }

    class Report
    {
        public readonly List<FileStat> Files = new List<FileStat>();
        public readonly List<string> Missing = new List<string>();
        public readonly List<string> NotImported = new List<string>();
        public readonly List<string> MissingSprites = new List<string>();       // t98
        public readonly List<string> NotImportedSprites = new List<string>();   // t98
        public readonly List<string> Skipped = new List<string>();
        public int Scenes;
        public int Prefabs;
        public int Sources;
        public int Clips;
        public int BuilderClips;
        public int BuilderSprites;      // t98 新增：终点精灵接线数

        public int Total { get { return Sources + Clips + BuilderClips + BuilderSprites; } }

        public void Add(string kind, string label, Pass pass)
        {
            FileStat f = new FileStat();
            f.kind = kind;
            f.label = label;
            f.sources = pass.Sources;
            f.clips = pass.Clips;
            f.builderClips = pass.BuilderClips;
            f.builderSprites = pass.BuilderSprites;
            Files.Add(f);

            if (kind == "场景") Scenes++; else Prefabs++;
            Sources += pass.Sources;
            Clips += pass.Clips;
            BuilderClips += pass.BuilderClips;
            BuilderSprites += pass.BuilderSprites;

            for (int i = 0; i < pass.Missing.Count; i++)
                if (!Missing.Contains(pass.Missing[i])) Missing.Add(pass.Missing[i]);
            for (int i = 0; i < pass.NotImported.Count; i++)
                if (!NotImported.Contains(pass.NotImported[i])) NotImported.Add(pass.NotImported[i]);
            for (int i = 0; i < pass.MissingSprites.Count; i++)
                if (!MissingSprites.Contains(pass.MissingSprites[i])) MissingSprites.Add(pass.MissingSprites[i]);
            for (int i = 0; i < pass.NotImportedSprites.Count; i++)
                if (!NotImportedSprites.Contains(pass.NotImportedSprites[i])) NotImportedSprites.Add(pass.NotImportedSprites[i]);
        }

        public void Print()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("[音效接线] ---------- 报告 ----------");
            sb.AppendLine(string.Format("[音效接线] 处理文件：场景 {0} 个，预制体 {1} 个", Scenes, Prefabs));

            for (int i = 0; i < Files.Count; i++)
            {
                FileStat f = Files[i];
                if (f.Total == 0)
                {
                    sb.AppendLine(string.Format("[音效接线]   {0} {1}：0 处（字段都已有值，或本来就没有音效字段）", f.kind, f.label));
                }
                else
                {
                    sb.AppendLine(string.Format("[音效接线]   {0} {1}：{2} 处（新挂 AudioSource {3}，组件 clip {4}，LevelBuilder clip {5}，终点精灵 {6}）",
                        f.kind, f.label, f.Total, f.sources, f.clips, f.builderClips, f.builderSprites));
                }
            }

            sb.AppendLine(string.Format("[音效接线] 合计：{0} 处（新挂 AudioSource {1}，组件 clip {2}，LevelBuilder clip {3}，终点精灵 {4}）",
                Total, Sources, Clips, BuilderClips, BuilderSprites));

            if (Missing.Count == 0 && NotImported.Count == 0)
            {
                sb.AppendLine("[音效接线] 音频文件：全部找到，无缺失。");
            }
            else
            {
                sb.AppendLine(string.Format("[音效接线] 音频文件缺失 {0} 个（这些 clip 字段这次没接上；文件到齐后再跑一次即可）：",
                    Missing.Count + NotImported.Count));
                for (int i = 0; i < Missing.Count; i++)
                    sb.AppendLine("[音效接线]   × 没这个文件  " + Missing[i]);
                for (int i = 0; i < NotImported.Count; i++)
                    sb.AppendLine("[音效接线]   ? 文件在但没导入  " + NotImported[i] + "（先 AssetDatabase.Refresh，或确认它是 AudioClip）");
            }

            // t98：终点精灵没接上时的说明。⛔ 这里只报告，不回写空值 —— 字段保持原样（留空 = 回退旧观感）。
            if (MissingSprites.Count > 0 || NotImportedSprites.Count > 0)
            {
                sb.AppendLine(string.Format("[精灵接线] 精灵文件缺失 {0} 个（这些 sprite 字段这次没接上；先跑一次生成器，再跑本工具）：",
                    MissingSprites.Count + NotImportedSprites.Count));
                for (int i = 0; i < MissingSprites.Count; i++)
                    sb.AppendLine("[精灵接线]   × 没这个文件  " + MissingSprites[i] +
                                  "（先生成：Unity -executeMethod GoalStationGenerator.BatchGenerateGoalStation）");
                for (int i = 0; i < NotImportedSprites.Count; i++)
                    sb.AppendLine("[精灵接线]   ? 文件在但没导入成 Sprite  " + NotImportedSprites[i] +
                                  "（确认它被导入为 Sprite(Single)，再跑本工具）");
            }

            if (MissingFields.Count > 0)
            {
                sb.AppendLine(string.Format("[音效接线] 代码里找不到的字段 {0} 个（映射表过时了）：", MissingFields.Count));
                for (int i = 0; i < MissingFields.Count; i++)
                    sb.AppendLine("[音效接线]   ! " + MissingFields[i]);
            }

            if (Skipped.Count > 0)
            {
                sb.AppendLine(string.Format("[音效接线] 清单里不存在的文件 {0} 个（已跳过）：", Skipped.Count));
                for (int i = 0; i < Skipped.Count; i++)
                    sb.AppendLine("[音效接线]   - " + Skipped[i]);
            }

            if (Total == 0)
            {
                sb.AppendLine("[音效接线] ⚠ 一处都没改。如果音频还没生成（Assets/_Project/Audio/SFX 下是空的），");
                sb.AppendLine("[音效接线]   先生成 WAV、等 Unity 导入完，再跑一次本工具。");
            }

            sb.Append("[音效接线] ---------- 报告结束 ----------");
            Debug.Log(sb.ToString());
        }
    }
}
