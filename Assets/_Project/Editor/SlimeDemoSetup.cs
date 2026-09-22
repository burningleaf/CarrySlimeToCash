// ---------------------------------------------------------------------------
// SlimeDemoSetup.cs —— 编辑器自动化工具（只放在 Editor 文件夹里，不进游戏包）
//
// 用法：Unity 顶部菜单 → Tools → 呆呆史莱姆 → ⑤ 一键全做
//
// 它能替你做完这些事（每一步都幂等，可以反复点）：
//   ① 初始化工程设置：11 个 Layer、11 个 Tag、Physics2D 碰撞矩阵
//   ② 自动连接当前场景里所有空的引用字段（不会覆盖你已经拖好的）
//   ③ 生成占位美术（1 张 16x16 白色方块，用颜色区分物体）
//   ④ 生成一个全新的测试场景 Test_Auto.unity（不会动你现有的 Test.unity）
//   ⑥ 体检报告：列出场景里还没连上的引用
//
// 注意：本文件属于编辑器工具，允许做场景内的批量扫描；游戏运行时脚本依然遵守
//      "禁止 FindObjectOfType" 的约定 —— 这里用的是 scene.GetRootGameObjects()。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.EventSystems;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class SlimeDemoSetup
{
    const string Root = "Assets/_Project";
    const int FirstUserLayer = 6;
    const string TestScenePath = Root + "/Scenes/Test_Auto.unity";

    static readonly string[] LayerNames =
    {
        "Player", "Slime", "CarriedSlime", "Ground", "Platform",
        "MovingPlatform", "Hazard", "Enemy", "Pickup", "TriggerZone", "Gate"
    };

    static readonly string[] TagNames =
    {
        "Player", "Slime", "Enemy", "MovingPlatform", "Coin", "SlimeOrb",
        "Hazard", "Goal", "Checkpoint", "PressurePlate", "Gate"
    };

    // ======================= 菜单 =======================

    [MenuItem("Tools/呆呆史莱姆/⑤ 一键全做（推荐）", false, 100)]
    public static void RunAll()
    {
        Debug.Log("========== 呆呆史莱姆：一键搭建开始 ==========");
        EnsureFolders();
        SetupLayersAndTags();
        SetupCollisionMatrix();
        Sprite square = CreateSquareSprite();
        BuildTestScene(square);
        AutoWireActiveScene();
        ReportEmptyReferences();

        string msg = "搭建完成！\n\n" +
                     "已经生成场景：Assets/_Project/Scenes/Test_Auto.unity\n" +
                     "Layer / Tag / 碰撞矩阵已配好，引用已自动连接。\n\n" +
                     "现在直接按 ▶ Play 就能玩：\n" +
                     "  A/D 移动，空格跳，E 抓取/放下，Q 投掷\n" +
                     "  1/2/3 切换物品，2 号哨子按 E 切换史莱姆模式\n\n" +
                     "详细信息看 Console 窗口（Window → General → Console）。";
        Debug.Log("========== 呆呆史莱姆：一键搭建结束 ==========");
        if (!Application.isBatchMode) EditorUtility.DisplayDialog("呆呆史莱姆", msg, "好");
    }

    [MenuItem("Tools/呆呆史莱姆/① 初始化工程设置（Layer/Tag/碰撞矩阵）", false, 101)]
    public static void InitProjectSettings()
    {
        EnsureFolders();
        SetupLayersAndTags();
        SetupCollisionMatrix();
        Debug.Log("[呆呆史莱姆] ① 工程设置完成。");
    }

    [MenuItem("Tools/呆呆史莱姆/② 自动连接当前场景的引用", false, 102)]
    public static void AutoWireMenu()
    {
        int n = AutoWireActiveScene();
        ReportEmptyReferences();
        Debug.Log("[呆呆史莱姆] ② 自动连接完成，填补了 " + n + " 个空引用。");
    }

    [MenuItem("Tools/呆呆史莱姆/③ 生成占位美术", false, 103)]
    public static void MakeArt()
    {
        Sprite s = CreateSquareSprite();
        Debug.Log("[呆呆史莱姆] ③ 占位美术：" + (s != null ? "已就绪" : "生成失败"));
    }

    [MenuItem("Tools/呆呆史莱姆/④ 生成全新测试场景 Test_Auto", false, 104)]
    public static void BuildSceneMenu()
    {
        Sprite s = CreateSquareSprite();
        BuildTestScene(s);
        AutoWireActiveScene();
        ReportEmptyReferences();
    }

    [MenuItem("Tools/呆呆史莱姆/⑥ 体检报告（列出没连上的引用）", false, 106)]
    public static void ReportMenu()
    {
        ReportEmptyReferences();
    }

    [MenuItem("Tools/呆呆史莱姆/⑧ 数值对齐：史莱姆重力 = 玩家重力", false, 108)]
    public static void AlignNumbersMenu()
    {
        AlignSlimeNumbers();
    }

    // ======================= ① 文件夹 / Layer / Tag / 矩阵 =======================

    static void EnsureFolders()
    {
        string[] dirs =
        {
            "Art/Characters", "Art/Environment", "Art/Items", "Art/Props", "Art/UI", "Art/VFX",
            "Audio/BGM", "Audio/SFX",
            "Animations/Player", "Animations/Slime", "Animations/Controllers",
            "Prefabs/Player", "Prefabs/Slime", "Prefabs/Items", "Prefabs/Level", "Prefabs/UI",
            "Scenes", "Settings/Physics", "Settings/Rendering",
            "Tiles/Palettes", "Tiles/Rules", "Editor"
        };

        foreach (string d in dirs)
        {
            string full = Path.Combine(Root, d);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
        }
        AssetDatabase.Refresh();
    }

    static void SetupLayersAndTags()
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0)
        {
            Debug.LogError("[呆呆史莱姆] 找不到 TagManager.asset，Layer/Tag 需要手动配置（见手册阶段 1）");
            return;
        }

        SerializedObject so = new SerializedObject(assets[0]);

        // ---- Layers ----
        SerializedProperty layers = so.FindProperty("layers");
        if (layers != null && layers.isArray)
        {
            for (int i = 0; i < LayerNames.Length; i++)
            {
                int index = FirstUserLayer + i;
                if (index >= layers.arraySize) break;

                SerializedProperty sp = layers.GetArrayElementAtIndex(index);
                if (string.IsNullOrEmpty(sp.stringValue))
                {
                    sp.stringValue = LayerNames[i];
                    Debug.Log("[呆呆史莱姆] Layer " + index + " = " + LayerNames[i]);
                }
                else if (sp.stringValue != LayerNames[i])
                {
                    Debug.LogWarning("[呆呆史莱姆] Layer " + index + " 已经是 '" + sp.stringValue +
                                     "'，不是期望的 '" + LayerNames[i] + "'，已跳过。请手动确认！");
                }
            }
        }

        // ---- Tags ----
        SerializedProperty tags = so.FindProperty("tags");
        if (tags != null && tags.isArray)
        {
            foreach (string tag in TagNames)
            {
                bool exists = false;
                for (int i = 0; i < tags.arraySize; i++)
                {
                    if (tags.GetArrayElementAtIndex(i).stringValue == tag) { exists = true; break; }
                }
                if (exists) continue;

                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
                Debug.Log("[呆呆史莱姆] Tag = " + tag);
            }
        }

        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
    }

    /// <summary>索引 0..10 对应 Layer 6..16。true = 这两层会碰撞。</summary>
    static bool[,] DesiredMatrix()
    {
        bool[,] m = new bool[11, 11];
        On(m, 0, 3); On(m, 0, 4); On(m, 0, 5); On(m, 0, 6); On(m, 0, 7); On(m, 0, 9); On(m, 0, 10);
        On(m, 1, 3); On(m, 1, 4); On(m, 1, 5); On(m, 1, 6); On(m, 1, 7); On(m, 1, 8); On(m, 1, 9); On(m, 1, 10);
        On(m, 2, 9);
        On(m, 3, 7);
        On(m, 4, 7);
        On(m, 5, 7); On(m, 5, 9);
        On(m, 7, 9); On(m, 7, 10);
        return m;
    }

    static void On(bool[,] m, int a, int b)
    {
        m[a, b] = true;
        m[b, a] = true;
    }

    static void SetupCollisionMatrix()
    {
        bool[,] want = DesiredMatrix();
        bool assetOk = false;

        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/Physics2DSettings.asset");
        if (assets != null && assets.Length > 0)
        {
            SerializedObject so = new SerializedObject(assets[0]);
            SerializedProperty matrix = so.FindProperty("m_LayerCollisionMatrix");

            if (matrix != null && matrix.isArray && matrix.arraySize >= 32)
            {
                int[] mask = new int[32];
                for (int i = 0; i < 32; i++) mask[i] = matrix.GetArrayElementAtIndex(i).intValue;

                for (int i = 0; i < 11; i++)
                {
                    for (int j = 0; j < 11; j++)
                    {
                        int li = FirstUserLayer + i;
                        int lj = FirstUserLayer + j;
                        bool on = want[i, j];       // 含 i == j（自碰撞一律关闭）
                        SetBit(ref mask[li], lj, on);
                        SetBit(ref mask[lj], li, on);
                    }
                }

                for (int i = 0; i < 32; i++) matrix.GetArrayElementAtIndex(i).intValue = mask[i];
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                assetOk = true;
            }
        }

        // 双保险：同时用运行时 API 设一遍（部分 Unity 版本只认这个）
        for (int i = 0; i < 11; i++)
        {
            for (int j = 0; j < 11; j++)
            {
                Physics2D.IgnoreLayerCollision(FirstUserLayer + i, FirstUserLayer + j, !want[i, j]);
            }
        }

        Debug.Log(assetOk
            ? "[呆呆史莱姆] 碰撞矩阵已写入 Physics2DSettings.asset"
            : "[呆呆史莱姆] 警告：没能写入 Physics2DSettings.asset，矩阵可能需要手动配（见手册阶段 4）");
    }

    static void SetBit(ref int mask, int bit, bool value)
    {
        if (value) mask |= (1 << bit);
        else mask &= ~(1 << bit);
    }

    // ======================= ③ 占位美术 =======================

    static Sprite CreateSquareSprite()
    {
        string dir = Root + "/Art/Environment";
        string path = dir + "/Square_White.png";

        if (!File.Exists(path))
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Texture2D tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[16 * 16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null)
        {
            bool dirty = false;
            if (ti.textureType != TextureImporterType.Sprite) { ti.textureType = TextureImporterType.Sprite; dirty = true; }
            if (ti.spriteImportMode != SpriteImportMode.Single) { ti.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (!Mathf.Approximately(ti.spritePixelsPerUnit, 16f)) { ti.spritePixelsPerUnit = 16f; dirty = true; }
            if (ti.filterMode != FilterMode.Point) { ti.filterMode = FilterMode.Point; dirty = true; }
            if (ti.mipmapEnabled) { ti.mipmapEnabled = false; dirty = true; }
            if (!ti.alphaIsTransparency) { ti.alphaIsTransparency = true; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null) Debug.LogError("[呆呆史莱姆] 占位美术导入失败：" + path);
        return sprite;
    }

    // ======================= ④ 生成测试场景 =======================

    static void BuildTestScene(Sprite sprite)
    {
        if (sprite == null)
        {
            Debug.LogError("[呆呆史莱姆] 没有占位图，无法建场景。请先点 ③ 生成占位美术。");
            return;
        }

        if (!Application.isBatchMode)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        int LAYER(string name)
        {
            int l = LayerMask.NameToLayer(name);
            if (l < 0) Debug.LogError("[呆呆史莱姆] 找不到 Layer：" + name + "，请先点 ① 初始化工程设置");
            return l < 0 ? 0 : l;
        }

        Color cGround = new Color(0.36f, 0.32f, 0.44f);
        Color cPlayer = new Color(0.33f, 0.62f, 1f);
        Color cSlime = new Color(0.45f, 0.92f, 0.55f);
        Color cDanger = new Color(0.95f, 0.30f, 0.30f);
        Color cCoin = new Color(1f, 0.85f, 0.25f);
        Color cOrb = new Color(0.60f, 0.88f, 1f);
        Color cGoal = new Color(0.30f, 0.95f, 0.90f);

        // ---------- 地面 ----------
        MakeSpriteBox("Ground_A", new Vector3(-6f, -1f, 0f), new Vector2(16f, 2f), LAYER("Ground"), cGround, 0, sprite, null, true);
        MakeSpriteBox("Ground_Pit", new Vector3(3f, -3f, 0f), new Vector2(4f, 2f), LAYER("Ground"), cGround, 0, sprite, null, true);
        MakeSpriteBox("Ground_B", new Vector3(10f, -1f, 0f), new Vector2(12f, 2f), LAYER("Ground"), cGround, 0, sprite, null, true);

        // ---------- 玩家 ----------
        GameObject playerGo = new GameObject("Player");
        playerGo.transform.position = new Vector3(-8f, 0.6f, 0f);
        playerGo.layer = LAYER("Player");
        MakeSpriteChild(playerGo.transform, "Sprite", Vector2.zero, new Vector2(0.8f, 1.2f), cPlayer, 10, sprite);
        BoxCollider2D playerCol = playerGo.AddComponent<BoxCollider2D>();
        playerCol.size = new Vector2(0.8f, 1.2f);
        Rigidbody2D playerRb = playerGo.AddComponent<Rigidbody2D>();
        playerRb.gravityScale = 3f;
        playerRb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        playerRb.interpolation = RigidbodyInterpolation2D.Interpolate;
        playerRb.freezeRotation = true;
        PlayerController pc = playerGo.AddComponent<PlayerController>();
        PlayerInventory pi = playerGo.AddComponent<PlayerInventory>();
        PlayerGrab pg = playerGo.AddComponent<PlayerGrab>();
        playerGo.AddComponent<AudioSource>();
        GameObject gcChild = MakeEmptyChild(playerGo.transform, "GroundCheck", new Vector3(0f, -0.6f, 0f));
        GameObject cpChild = MakeEmptyChild(playerGo.transform, "CarryPoint", new Vector3(0.3f, 0.9f, 0f));

        // ---------- 史莱姆 ----------
        GameObject slimeGo = new GameObject("Slime");
        slimeGo.transform.position = new Vector3(-10f, 0.5f, 0f);
        slimeGo.layer = LAYER("Slime");
        Transform slimeSprite = MakeSpriteChild(slimeGo.transform, "Sprite", Vector2.zero, new Vector2(0.9f, 0.9f), cSlime, 9, sprite);
        CircleCollider2D slimeCol = slimeGo.AddComponent<CircleCollider2D>();
        slimeCol.radius = 0.45f;
        Rigidbody2D slimeRb = slimeGo.AddComponent<Rigidbody2D>();
        slimeRb.gravityScale = 4f;
        slimeRb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        slimeRb.interpolation = RigidbodyInterpolation2D.Interpolate;
        slimeRb.freezeRotation = true;
        SlimeController sc = slimeGo.AddComponent<SlimeController>();
        SlimePathFollow spf = slimeGo.AddComponent<SlimePathFollow>();
        slimeGo.AddComponent<AudioSource>();
        GameObject slimeGcChild = MakeEmptyChild(slimeGo.transform, "GroundCheck", new Vector3(0f, -0.45f, 0f));

        // ---------- 管理器 ----------
        GameObject gmGo = new GameObject("GameManager");
        GameManager gm = gmGo.AddComponent<GameManager>();
        GameObject lmGo = new GameObject("LevelManager");
        LevelManager lm = lmGo.AddComponent<LevelManager>();

        // ---------- 相机 ----------
        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.position = new Vector3(1f, 0f, -10f);
        Camera cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 9f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.11f, 0.12f, 0.17f);
        camGo.AddComponent<AudioListener>();

        // ---------- 关卡元素 ----------
        GameObject spike = MakeSpriteBox("Hazard_Spike", new Vector3(7f, 0.25f, 0f), new Vector2(1.5f, 0.5f), LAYER("Hazard"), cDanger, 5, sprite, null, false);
        BoxCollider2D spikeCol = spike.AddComponent<BoxCollider2D>();
        spikeCol.isTrigger = true;
        Hazard hazard = spike.AddComponent<Hazard>();
        hazard.damage = 20;

        GameObject coin1 = MakePickup("Coin_1", new Vector3(9f, 0.5f, 0f), cCoin, sprite, LAYER("Pickup"));
        Coin coinComp = coin1.AddComponent<Coin>();
        GameObject coin2 = MakePickup("Coin_2", new Vector3(11.5f, 0.5f, 0f), cCoin, sprite, LAYER("Pickup"));
        coin2.AddComponent<Coin>();

        GameObject orb1 = MakePickup("Orb_1", new Vector3(5f, 0.5f, 0f), cOrb, sprite, LAYER("Pickup"));
        SlimeOrb orbComp = orb1.AddComponent<SlimeOrb>();

        GameObject goal = MakeSpriteBox("Goal", new Vector3(14f, 1f, 0f), new Vector2(2f, 2f), LAYER("TriggerZone"), cGoal, 2, sprite, null, false);
        BoxCollider2D goalCol = goal.AddComponent<BoxCollider2D>();
        goalCol.isTrigger = true;
        Goal goalComp = goal.AddComponent<Goal>();

        // ---------- 路径点容器 + 预制体 ----------
        GameObject container = new GameObject("WaypointContainer");
        WaypointMarker waypointPrefab = CreateWaypointPrefab(sprite);

        // ---------- 连线 ----------
        pc.groundCheck = gcChild.transform;
        pc.groundLayer = MakeMask("Ground", "Platform", "MovingPlatform");

        pi.player = pc;

        pg.player = pc;
        pg.inventory = pi;
        pg.slime = sc;
        pg.pathFollow = spf;
        pg.carryPoint = cpChild.transform;
        pg.slimeLayerMask = MakeMask("Slime", "CarriedSlime");

        sc.player = pc;
        sc.pathFollow = spf;
        sc.levelManager = lm;
        sc.groundCheck = slimeGcChild.transform;
        sc.groundLayer = MakeMask("Ground", "Platform", "MovingPlatform");
        sc.spriteRoot = slimeSprite;
        sc.spriteRenderer = slimeSprite.GetComponent<SpriteRenderer>();
        sc.slimeLayer = LAYER("Slime");
        sc.carriedLayer = LAYER("CarriedSlime");

        spf.slime = sc;
        spf.player = pc;
        spf.waypointPrefab = waypointPrefab;
        spf.waypointContainer = container.transform;

        lm.slime = sc;
        lm.player = pc;
        lm.startPoint = playerGo.transform;
        lm.levelIndex = 1;

        hazard.levelManager = lm;
        coinComp.levelManager = lm;
        coin2.GetComponent<Coin>().levelManager = lm;
        orbComp.slime = sc;
        goalComp.levelManager = lm;

        // ---------- 保存 ----------
        string dir = Root + "/Scenes";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        bool saved = EditorSceneManager.SaveScene(scene, TestScenePath);
        AssetDatabase.Refresh();
        AddSceneToBuildSettings(TestScenePath);

        Debug.Log(saved
            ? "[呆呆史莱姆] 已生成并保存场景：" + TestScenePath
            : "[呆呆史莱姆] 场景保存失败：" + TestScenePath);
    }

    static void AddSceneToBuildSettings(string path)
    {
        List<EditorBuildSettingsScene> list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (EditorBuildSettingsScene s in list)
        {
            if (s.path == path) return;
        }
        list.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // ---------- 造物体的辅助方法 ----------

    static GameObject MakeEmptyChild(Transform parent, string name, Vector3 localPos)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go;
    }

    static Transform MakeSpriteChild(Transform parent, string name, Vector3 localPos, Vector2 scale, Color color, int order, Sprite sprite)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = order;
        return go.transform;
    }

    static GameObject MakeSpriteBox(string name, Vector3 pos, Vector2 size, int layer, Color color, int order,
                                    Sprite sprite, Transform parent, bool addCollider)
    {
        GameObject go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.layer = layer;

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = order;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);

        if (addCollider)
        {
            // 碰撞体放在本体上：默认 size(1,1) 会跟着 scale 一起放大，正好覆盖整块地面
            go.AddComponent<BoxCollider2D>();
        }
        return go;
    }

    static GameObject MakePickup(string name, Vector3 pos, Color color, Sprite sprite, int layer)
    {
        GameObject go = new GameObject(name);
        go.transform.position = pos;
        go.layer = layer;
        go.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = 6;
        CircleCollider2D col = go.AddComponent<CircleCollider2D>();
        col.isTrigger = true;
        col.radius = 0.45f;
        return go;
    }

    static WaypointMarker CreateWaypointPrefab(Sprite sprite)
    {
        string dir = Root + "/Prefabs/Slime";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        GameObject go = new GameObject("WaypointMarker");
        WaypointMarker marker = go.AddComponent<WaypointMarker>();
        Transform vis = MakeSpriteChild(go.transform, "Visual", Vector3.zero, new Vector2(0.35f, 0.35f), new Color(0.3f, 1f, 1f), 8, sprite);
        marker.visualRoot = vis;
        marker.spriteRenderer = vis.GetComponent<SpriteRenderer>();

        string path = dir + "/WaypointMarker.prefab";
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        AssetDatabase.Refresh();

        if (prefab == null) { Debug.LogWarning("[呆呆史莱姆] 路径点预制体生成失败"); return null; }
        return prefab.GetComponent<WaypointMarker>();
    }

    static LayerMask MakeMask(params string[] layerNames)
    {
        int mask = 0;
        foreach (string n in layerNames)
        {
            int l = LayerMask.NameToLayer(n);
            if (l >= 0) mask |= (1 << l);
        }
        return mask;
    }

    // ======================= ◉ 关卡可视性检查 =======================

    [MenuItem("Tools/呆呆史莱姆/◉ 关卡可视性检查（有没有东西在画面外）", false, 116)]
    public static void CheckLevelVisibility()
    {
        Scene scene = SceneManager.GetActiveScene();

        Camera cam = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            cam = root.GetComponentInChildren<Camera>(true);
            if (cam != null) break;
        }
        if (cam == null)
        {
            Debug.LogError("[呆呆史莱姆] 场景里没有相机");
            return;
        }

        float halfH = cam.orthographic ? cam.orthographicSize : 5f;
        float halfW = halfH * Mathf.Max(0.1f, cam.aspect);

        Vector2 min, max;
        CameraFollow follow = cam.GetComponent<CameraFollow>();
        if (follow != null && follow.target != null && follow.useBounds)
        {
            min = follow.boundsMin;
            max = follow.boundsMax;
            Debug.Log(string.Format("[呆呆史莱姆] 相机【跟随】玩家，可达范围 x[{0:F1}, {1:F1}]  y[{2:F1}, {3:F1}]",
                      min.x, max.x, min.y, max.y));
        }
        else
        {
            min = new Vector2(cam.transform.position.x - halfW, cam.transform.position.y - halfH);
            max = new Vector2(cam.transform.position.x + halfW, cam.transform.position.y + halfH);
            Debug.Log(string.Format("[呆呆史莱姆] 相机【固定】在 ({0:F1}, {1:F1})，只能看到 {2:F1} x {3:F1} 的范围：x[{4:F1}, {5:F1}]  y[{6:F1}, {7:F1}]",
                      cam.transform.position.x, cam.transform.position.y, halfW * 2f, halfH * 2f,
                      min.x, max.x, min.y, max.y));
        }

        int bad = 0;
        string report = "";
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Collider2D[] cols = root.GetComponentsInChildren<Collider2D>(true);
            foreach (Collider2D c in cols)
            {
                if (c == null) continue;
                if (c.gameObject.name.StartsWith("Wall_")) continue;

                Bounds b = c.bounds;
                bool inside = b.max.x >= min.x && b.min.x <= max.x && b.max.y >= min.y && b.min.y <= max.y;
                if (inside) continue;

                bad++;
                report += string.Format("{0}    {1}  在 ({2:F1}, {3:F1})", "\n", c.gameObject.name, b.center.x, b.center.y);
            }
        }

        if (bad == 0) Debug.Log("[呆呆史莱姆] OK：所有碰撞体都在相机够得到的范围内");
        else Debug.LogWarning("[呆呆史莱姆] 有 " + bad + " 个碰撞体在相机够不到的地方（关卡变长时请用跟随相机）：" + report);
    }
    // ======================= ✦ 视觉美化 =======================

    // ---- 扁平简洁风调色板 ----
    static readonly Color ColBG        = new Color(0.075f, 0.070f, 0.110f);   // 深蓝紫背景
    static readonly Color ColPanel     = new Color(0.145f, 0.125f, 0.205f, 0.95f);
    static readonly Color ColTextMain  = new Color(0.950f, 0.940f, 0.980f);
    static readonly Color ColTextDim   = new Color(0.660f, 0.630f, 0.770f);
    static readonly Color ColGold      = new Color(1.000f, 0.780f, 0.240f);
    static readonly Color ColGreen     = new Color(0.290f, 0.870f, 0.500f);
    static readonly Color ColRed       = new Color(1.000f, 0.300f, 0.300f);
    static readonly Color ColBtnFill   = new Color(0.180f, 0.161f, 0.259f);

    static Sprite _uiPanelSprite;

    /// <summary>UI 用的圆角九宫格图（白填充 + 深色描边，缩放不变形）。</summary>
    static Sprite UIPanelSprite()
    {
        if (_uiPanelSprite == null)
            _uiPanelSprite = CreateRoundedPanelSprite("UI_Panel", 128, 30, 6, 44);
        return _uiPanelSprite;
    }

    [MenuItem("Tools/呆呆史莱姆/✦ 一键美化画面（抗锯齿 + 相机 + 描边美术 + UI）", false, 98)]
    public static void ApplyVisualPolishMenu()
    {
        ApplyVisualPolish();
    }

    public static void ApplyVisualPolish()
    {
        Debug.Log("[呆呆史莱姆] ========== 开始视觉美化 ==========");

        PolishGraphicsSettings();

        Sprite box = CreateRoundedBoxSprite("Sprite_Box", 128, 26, 6);
        Sprite circle = CreateCircleSprite("Sprite_Circle", 128, 6);
        Sprite ground = CreateGroundSprite("Sprite_Ground", 128);
        UIPanelSprite();

        string[] paths =
        {
            Root + "/Scenes/MainMenu.unity",
            Root + "/Scenes/LevelSelect.unity",
            Root + "/Scenes/Level1.unity",
            Root + "/Scenes/Level2.unity"
        };

        foreach (string path in paths)
        {
            if (!File.Exists(path)) { Debug.LogWarning("[呆呆史莱姆] 跳过：" + path); continue; }

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            int sprites = SwapSceneSprites(scene, box, circle, ground);
            int cameras = PolishCameras(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(string.Format("[呆呆史莱姆] {0}：换了 {1} 个贴图，调了 {2} 个相机",
                      Path.GetFileName(path), sprites, cameras));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] ========== 视觉美化完成（UI 需要再点一次 ⑭ 重建）==========");
    }

    [MenuItem("Tools/呆呆史莱姆/✦✦ 一键美化全部（画面 + 重建所有 UI）", false, 97)]
    public static void ApplyFullPolish()
    {
        ApplyVisualPolish();

        // 关卡 UI 重建：圆角按钮 / HUD 底板 / 统一配色
        string[] levels = { Root + "/Scenes/Level1.unity", Root + "/Scenes/Level2.unity" };
        TMP_FontAsset font = EnsureChineseFont();

        foreach (string path in levels)
        {
            if (!File.Exists(path)) continue;

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            BuildHud();
            BuildPanels();
            AutoWireActiveScene();
            ForceChineseFont(scene, font);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[呆呆史莱姆] 已重建 UI：" + Path.GetFileName(path));
        }

        // 主菜单 / 关卡选择（顺带注册 Build Settings）
        BuildMenus();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ReportBuildScenes();
        Debug.Log("[呆呆史莱姆] ========== 全量美化完成 ==========");
    }
    /// <summary>URP 设置：开 4x MSAA、关 HDR（2D 用不到，省性能也更干净）。</summary>
    static void PolishGraphicsSettings()
    {
        string[] guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
        if (guids == null || guids.Length == 0)
        {
            Debug.LogWarning("[呆呆史莱姆] 没找到 URP 资产，跳过抗锯齿设置");
            return;
        }

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Object asset = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (asset == null) continue;

            SerializedObject so = new SerializedObject(asset);
            bool changed = false;

            SerializedProperty msaa = so.FindProperty("m_MSAA");
            if (msaa != null && msaa.intValue != 4) { msaa.intValue = 4; changed = true; }

            SerializedProperty hdr = so.FindProperty("m_SupportsHDR");
            if (hdr != null && hdr.boolValue) { hdr.boolValue = false; changed = true; }

            if (changed)
            {
                so.ApplyModifiedProperties();
                Debug.Log("[呆呆史莱姆] URP 已设置 4x MSAA / 关闭 HDR：" + path);
            }
        }
        AssetDatabase.SaveAssets();
    }

    /// <summary>把场景里的 SpriteRenderer 换成新的描边贴图（地面用横向均匀的渐变贴图）。</summary>
    static int SwapSceneSprites(Scene scene, Sprite box, Sprite circle, Sprite ground)
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        int count = 0;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            SpriteRenderer[] renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (SpriteRenderer sr in renderers)
            {
                if (sr == null || sr.sprite == null) continue;

                Sprite want = box;
                if (sr.gameObject.layer == groundLayer)
                {
                    want = ground;                                   // 地面/平台：横向均匀，拉伸不变形
                }
                else if (sr.GetComponentInParent<SlimeController>() != null
                      || sr.GetComponentInParent<Coin>() != null
                      || sr.GetComponentInParent<SlimeOrb>() != null
                      || sr.GetComponentInParent<WaypointMarker>() != null)
                {
                    want = circle;                                   // 圆形的：史莱姆 / 金币 / 回血球 / 路径点
                }

                if (want == null || sr.sprite == want) continue;
                sr.sprite = want;
                EditorUtility.SetDirty(sr);
                count++;
            }
        }
        return count;
    }

    /// <summary>相机：拉近视野、换深色背景；有玩家的场景自动挂上跟随。</summary>
    static int PolishCameras(Scene scene)
    {
        int count = 0;
        PlayerController player = FindOne<PlayerController>(CollectBehaviours(scene));

        Vector2 bMin, bMax;
        ComputeSceneBounds(scene, out bMin, out bMax);

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Camera cam = root.GetComponentInChildren<Camera>(true);
            if (cam == null) continue;

            cam.orthographicSize = 6.5f;
            cam.backgroundColor = ColBG;
            EditorUtility.SetDirty(cam);
            count++;

            if (player == null) continue;

            CameraFollow follow = cam.GetComponent<CameraFollow>();
            if (follow == null) follow = cam.gameObject.AddComponent<CameraFollow>();

            follow.target = player.transform;
            follow.offset = new Vector3(0f, 1.5f, -10f);
            follow.smoothTime = 0.12f;
            follow.verticalDeadZone = 1.5f;
            follow.useBounds = true;
            follow.boundsMin = bMin;
            follow.boundsMax = bMax;
            EditorUtility.SetDirty(follow);
        }
        return count;
    }

    /// <summary>从 Ground 层的碰撞体算出关卡边界，交给相机跟随限制视野。</summary>
    static void ComputeSceneBounds(Scene scene, out Vector2 min, out Vector2 max)
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        Bounds total = new Bounds();
        bool has = false;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Collider2D[] cols = root.GetComponentsInChildren<Collider2D>(true);
            foreach (Collider2D c in cols)
            {
                if (c == null || c.gameObject.layer != groundLayer) continue;
                if (c.gameObject.name.StartsWith("Wall_")) continue;
                if (!has) { total = c.bounds; has = true; }
                else total.Encapsulate(c.bounds);
            }
        }

        if (!has)
        {
            min = new Vector2(-60f, -12f);
            max = new Vector2(60f, 14f);
            return;
        }
        min = new Vector2(total.min.x - 1f, total.min.y - 3f);
        max = new Vector2(total.max.x + 1f, total.max.y + 8f);
    }

    // ---------------- 程序化生成贴图 ----------------

    static Texture2D NewTexture(int w, int h)
    {
        return new Texture2D(w, h, TextureFormat.RGBA32, false);
    }

    /// <summary>点到圆角矩形边界的距离：<0 在内部，>0 在外部。</summary>
    static float RoundedRectDistance(float x, float y, int w, int h, float radius)
    {
        float hw = w * 0.5f, hh = h * 0.5f;
        float qx = Mathf.Abs(x - hw + 0.5f) - (hw - radius);
        float qy = Mathf.Abs(y - hh + 0.5f) - (hh - radius);
        float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
        float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
        return outside + inside - radius;
    }

    static float CircleDistance(float x, float y, int size, float radius)
    {
        float c = size * 0.5f - 0.5f;
        float dx = x - c, dy = y - c;
        return Mathf.Sqrt(dx * dx + dy * dy) - radius;
    }

    /// <summary>圆角矩形 + 深色描边（用于玩家 / 危险区 / 收购站等方形物体）。</summary>
    static Sprite CreateRoundedBoxSprite(string fileName, int size, int radius, int borderWidth)
    {
        Texture2D tex = NewTexture(size, size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = RoundedRectDistance(x, y, size, size, radius);
                float a = Mathf.Clamp01(0.5f - d);
                Color32 c;
                if (d > -borderWidth) c = new Color32(16, 13, 26, (byte)(240f * a));
                else c = new Color32(255, 255, 255, (byte)(255f * a));
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return SaveTextureAsSprite(tex, Root + "/Art/Environment/" + fileName + ".png", size, Vector4.zero);
    }

    /// <summary>圆形 + 深色描边（用于史莱姆 / 金币 / 回血球 / 路径点）。</summary>
    static Sprite CreateCircleSprite(string fileName, int size, int borderWidth)
    {
        Texture2D tex = NewTexture(size, size);
        float radius = size * 0.5f - 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = CircleDistance(x, y, size, radius);
                float a = Mathf.Clamp01(0.5f - d);
                Color32 c;
                if (d > -borderWidth) c = new Color32(16, 13, 26, (byte)(240f * a));
                else c = new Color32(255, 255, 255, (byte)(255f * a));
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return SaveTextureAsSprite(tex, Root + "/Art/Environment/" + fileName + ".png", size, Vector4.zero);
    }

    /// <summary>
    /// 地面/平台专用：**只做纵向渐变，横向完全均匀**。
    /// 这样一块 16×2 的地面把贴图横向拉伸 16 倍也不会变形（圆角描边拉伸就毁了）。
    /// 上亮下暗，让地形有立体感。
    /// </summary>
    static Sprite CreateGroundSprite(string fileName, int size)
    {
        Texture2D tex = NewTexture(size, size);
        int topBand = Mathf.RoundToInt(size * 0.10f);
        int bottomBand = Mathf.RoundToInt(size * 0.22f);

        for (int y = 0; y < size; y++)
        {
            float v;
            if (y >= size - topBand) v = 1.00f;                        // 顶部亮边
            else if (y < bottomBand) v = 0.62f;                        // 底部暗边
            else v = 0.86f;                                            // 主体
            byte b = (byte)(v * 255f);
            for (int x = 0; x < size; x++) tex.SetPixel(x, y, new Color32(b, b, b, 255));
        }
        tex.Apply();
        return SaveTextureAsSprite(tex, Root + "/Art/Environment/" + fileName + ".png", size, Vector4.zero);
    }

    /// <summary>UI 用的圆角九宫格图（白填充 + 深色描边，带 spriteBorder，缩放不变形）。</summary>
    static Sprite CreateRoundedPanelSprite(string fileName, int size, int radius, int borderWidth, int sliceBorder)
    {
        Texture2D tex = NewTexture(size, size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = RoundedRectDistance(x, y, size, size, radius);
                float a = Mathf.Clamp01(0.5f - d);
                Color32 c;
                if (d > -borderWidth) c = new Color32(16, 13, 26, (byte)(235f * a));
                else c = new Color32(255, 255, 255, (byte)(255f * a));
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return SaveTextureAsSprite(tex, Root + "/Art/UI/" + fileName + ".png", size,
                                   new Vector4(sliceBorder, sliceBorder, sliceBorder, sliceBorder));
    }

    /// <summary>把一张 Image 变成"圆角九宫格面板"（缩放不变形）。贴图生成失败时退回纯色矩形。</summary>
    static void ApplyPanelStyle(Image img, Color color)
    {
        if (img == null) return;

        Sprite panel = UIPanelSprite();
        if (panel != null)
        {
            img.sprite = panel;
            img.type = Image.Type.Sliced;
        }
        img.color = color;
    }

    static Sprite SaveTextureAsSprite(Texture2D tex, string path, int pixelsPerUnit, Vector4 border)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null)
        {
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.spritePixelsPerUnit = pixelsPerUnit;
            ti.filterMode = FilterMode.Bilinear;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 2048;
            if (border != Vector4.zero) ti.spriteBorder = border;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
    // ======================= ✚ 修复：中文字体 + 缺失引用 =======================

    /// <summary>
    /// 把场景里所有 TMP 文字的字体强制换成中文字体。
    /// 为什么需要这个：生成 UI 时如果字体参数是空的，TMP 会退回默认的 LiberationSans（纯英文），
    /// 中文没有字形 → 界面上就是"一片空白没有文字"。这个函数保证无论生成路径如何，字体一定对。
    /// </summary>
    static int ForceChineseFont(Scene scene, TMP_FontAsset font)
    {
        if (font == null)
        {
            // 不能静默返回：那会建出一个"全部用无中文字体"的场景，而且日志里毫无痕迹。
            Debug.LogError("[呆呆史莱姆] ForceChineseFont 收到空的字体资源 —— " +
                           "这个场景里的中文会全部显示成方块。请检查 EnsureChineseFont() 为什么返回了 null。");
            return 0;
        }

        int fixedCount = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            TMP_Text[] texts = root.GetComponentsInChildren<TMP_Text>(true);
            foreach (TMP_Text text in texts)
            {
                if (text == null) continue;
                if (text.font == font) continue;

                text.font = font;
                EditorUtility.SetDirty(text);
                fixedCount++;
            }
        }
        return fixedCount;
    }

    /// <summary>
    /// 把工程的 TMP 默认字体设成中文字体。
    /// 最后一道保险：万一哪里新建文本时忘了指定字体，也不会变成一屏方块。
    /// </summary>
    static void EnsureDefaultTmpFont(TMP_FontAsset font)
    {
        if (font == null) return;

        string[] guids = AssetDatabase.FindAssets("t:TMP_Settings");
        if (guids == null || guids.Length == 0)
        {
            Debug.LogWarning("[呆呆史莱姆] 找不到 TMP Settings，无法设置默认中文字体");
            return;
        }

        UnityEngine.Object settings = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guids[0]));
        if (settings == null) return;

        SerializedObject so = new SerializedObject(settings);
        SerializedProperty prop = so.FindProperty("m_defaultFontAsset");
        if (prop == null) return;

        if (prop.objectReferenceValue != font)
        {
            prop.objectReferenceValue = font;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("[呆呆史莱姆] 已把工程默认 TMP 字体设为：" + font.name);
        }
    }

    [MenuItem("Tools/呆呆史莱姆/✚ 修复全部场景（补缺失引用 + 修正中文字体）", false, 115)]
    public static void RepairAllScenesMenu()
    {
        RepairAllScenes();
    }

    /// <summary>
    /// 非破坏性修复：把 4 个场景逐个打开，补上空的引用、把 TMP 字体换成中文字体，然后存盘。
    /// 不会重新生成地形或关卡内容，所以可以放心对已经在手工调整的场景执行。
    /// </summary>
    public static void RepairAllScenes()
    {
        EnsureFolders();
        EnsureTMPEssentials();
        TMP_FontAsset font = EnsureChineseFont();
        if (font == null)
        {
            Debug.LogError("[呆呆史莱姆] 找不到/生成不了中文字体，修复中止");
            return;
        }

        // 场景列表以 Build Settings 为准 —— 加新场景（例如教程关 Level0）不用回来改这里。
        // 之前是硬编码 4 个场景，结果新建的 Level0 直接被漏掉。
        List<string> paths = new List<string>();
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (s != null && !string.IsNullOrEmpty(s.path)) paths.Add(s.path);
        }
        if (paths.Count == 0)
        {
            paths.Add(Root + "/Scenes/MainMenu.unity");
            paths.Add(Root + "/Scenes/LevelSelect.unity");
            paths.Add(Root + "/Scenes/Level0.unity");
            paths.Add(Root + "/Scenes/Level1.unity");
            paths.Add(Root + "/Scenes/Level2.unity");
        }

        Debug.Log("[呆呆史莱姆] ========== 开始修复全部场景 ==========");

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning("[呆呆史莱姆] 跳过（不存在）：" + path);
                continue;
            }

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            int wired = AutoWireActiveScene();
            int fonts = ForceChineseFont(scene, font);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(string.Format("[呆呆史莱姆] {0}：补了 {1} 个引用，修了 {2} 处字体",
                      Path.GetFileName(path), wired, fonts));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ReportBuildScenes();
        Debug.Log("[呆呆史莱姆] ========== 修复完成 ==========");
    }
    // ======================= ★ 一键全部构建 =======================

    /// <summary>
    /// 把"重命名 Test→Level1、清理重复 UI、生成第二关、生成主菜单与关卡选择、注册 Build Settings"
    /// 合成一次调用。既可以在菜单里点，也可以用命令行跑：
    ///   Unity.exe -batchmode -quit -projectPath &lt;工程路径&gt; -executeMethod SlimeDemoSetup.BuildAll -logFile &lt;日志&gt;
    /// </summary>
    [MenuItem("Tools/呆呆史莱姆/★ 一键全部构建（重命名 + 清UI + 第二关 + 主菜单选关）", false, 99)]
    public static void BuildAll()
    {
        Debug.Log("[呆呆史莱姆] ========== 一键全部构建 开始 ==========");
        string level1 = Root + "/Scenes/Level1.unity";
        string level2 = Root + "/Scenes/Level2.unity";

        // ---- 1) 主菜单 / 关卡选择 / Build Settings（顺带把 Test 重命名为 Level1）----
        BuildMenus();
        Debug.Log("[呆呆史莱姆] 步骤 1/4 完成：主菜单 + 关卡选择 + Build Settings");

        // ---- 2) Level1 的 UI 重建，清掉重复的 HUD ----
        if (File.Exists(level1))
        {
            EditorSceneManager.OpenScene(level1, OpenSceneMode.Single);
            BuildHud();
            BuildPanels();
            AutoWireActiveScene();
            ReportEmptyReferences();
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("[呆呆史莱姆] 步骤 2/4 完成：Level1 的 UI 已重建（重复的已清理）");
        }
        else
        {
            Debug.LogWarning("[呆呆史莱姆] 步骤 2/4：找不到 Level1.unity，跳过");
        }

        // ---- 3) 第二关 ----
        BuildLevel2();
        Debug.Log("[呆呆史莱姆] 步骤 3/4 完成：第二关");

        // ---- 4) 重配"下一关"链路（此时 Level2 才存在）----
        ConfigureNextScene(level1, File.Exists(level2) ? "Level2" : "");
        ConfigureNextScene(level2, "");

        ReportBuildScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] ========== 一键全部构建 完成 ==========");
    }

    /// <summary>把 Build Settings 的现状打印出来，方便核对关卡流程顺序。</summary>
    static void ReportBuildScenes()
    {
        string joined = "";
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            joined += "\n    [" + (s.enabled ? "OK" : "--") + "] " + s.path;

        Debug.Log("[呆呆史莱姆] Build Settings 现状（顺序 = 关卡流程）：" + joined +
                  "\n    （应该是 MainMenu → LevelSelect → Level1 → Level2）");
    }
    // ======================= ⑬ 主菜单 + 关卡选择（C 阶段）=======================

    [MenuItem("Tools/呆呆史莱姆/⑭ 重建当前场景的 UI（清理重复的 HUD）", false, 114)]
    public static void RebuildUiMenu()
    {
        BuildHud();
        BuildPanels();
        AutoWireActiveScene();
        ReportEmptyReferences();
        Debug.Log("[呆呆史莱姆] ⑭ 当前场景的 UI 已重建（重复的会先被清掉）");
    }

    [MenuItem("Tools/呆呆史莱姆/⑬ 生成主菜单与关卡选择（并注册 Build Settings）", false, 113)]
    public static void BuildMenusMenu()
    {
        BuildMenus();
    }

    /// <summary>
    /// C 阶段：把 Test 重命名为 Level1，生成 MainMenu / LevelSelect，并把 4 个场景按顺序注册进 Build Settings。
    /// 这样结算面板的"返回关卡选择"、暂停菜单的"退出关卡"就不会再报错。
    /// </summary>
    public static void BuildMenus()
    {
        EnsureFolders();
        EnsureTMPEssentials();
        TMP_FontAsset font = EnsureChineseFont();
        Sprite white = CreateSquareSprite();
        Sprite starOn = CreateColorSprite("Icon_StarOn", new Color(1f, 0.85f, 0.20f));
        Sprite starOff = CreateColorSprite("Icon_StarOff", new Color(0.25f, 0.25f, 0.30f));

        if (white == null)
        {
            Debug.LogError("[呆呆史莱姆] 没有占位图，请先点 ③ 生成占位美术");
            return;
        }

        if (font == null)
        {
            // 宁可不建，也不要建出一屏方块：菜单场景几乎全是中文。
            Debug.LogError("[呆呆史莱姆] 中文 TMP 字体不可用，已中止生成菜单场景（否则中文会全是方块）。" +
                           "先把 Assets/_Project/Art/UI/SimHei SDF.asset 修好再来。");
            return;
        }

        // ---- 1) Test.unity → Level1.unity（MoveAsset 保持 GUID，引用不丢）----
        string testPath = Root + "/Scenes/Test.unity";
        string level1Path = Root + "/Scenes/Level1.unity";
        string level2Path = Root + "/Scenes/Level2.unity";

        if (File.Exists(testPath) && !File.Exists(level1Path))
        {
            string err = AssetDatabase.MoveAsset(testPath, level1Path);
            if (string.IsNullOrEmpty(err)) Debug.Log("[呆呆史莱姆] Test.unity 已重命名为 Level1.unity");
            else Debug.LogWarning("[呆呆史莱姆] 重命名 Test → Level1 失败：" + err);
        }
        else if (File.Exists(level1Path))
        {
            Debug.Log("[呆呆史莱姆] Level1.unity 已存在，跳过重命名");
        }

        if (!File.Exists(level2Path))
            Debug.LogWarning("[呆呆史莱姆] 还没生成 Level2.unity，先点一次 ⑫ 再跑本项效果最好");

        if (!Application.isBatchMode)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        }

        // ---- 2) 生成主菜单场景 ----
        BuildMainMenuScene(font, white);

        // ---- 3) 生成关卡选择场景 ----
        BuildLevelSelectScene(font, white, starOn, starOff);

        // ---- 4) 注册 Build Settings（顺序：MainMenu → LevelSelect → Level0 → Level1 → Level2）----
        // 注意：关卡选择界面在运行时是【按 Build Settings 里实际存在的场景】生成卡片的，
        // 所以这里漏掉哪个场景，选关界面就会少一张卡。
        string level0Path = Root + "/Scenes/Level0.unity";
        List<string> buildOrder = new List<string>
        {
            Root + "/Scenes/MainMenu.unity",
            Root + "/Scenes/LevelSelect.unity"
        };
        if (File.Exists(level0Path)) buildOrder.Add(level0Path);   // 教程关（还没生成就跳过）
        buildOrder.Add(level1Path);
        buildOrder.Add(level2Path);
        SetBuildScenes(buildOrder.ToArray());

        // ---- 5) 配置"下一关"跳转 ----
        ConfigureNextScene(level0Path, "Level1");
        ConfigureNextScene(level1Path, File.Exists(level2Path) ? "Level2" : "");
        ConfigureNextScene(level2Path, "");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] ⑬ 完成：主菜单 + 关卡选择已生成，Build Settings 已注册");
    }

    static void BuildMainMenuScene(TMP_FontAsset font, Sprite white)
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Vector2 cc = new Vector2(0.5f, 0.5f);

        MakeStaticCamera(new Vector3(0f, 0f, -10f), 5.4f);
        MakeEventSystemObject();

        GameObject canvasGo = new GameObject("MainMenuCanvas");
        SetupCanvasComponent(canvasGo);

        TMP_Text title = MakeText(canvasGo.transform, "Title", cc, new Vector2(0f, 240f),
                                  new Vector2(1700f, 130f), 78f, TextAlignmentOptions.Center, font,
                                  new Color(1f, 0.95f, 0.72f));
        title.text = "带呆呆史莱姆越障换钱";

        TMP_Text subtitle = MakeText(canvasGo.transform, "Subtitle", cc, new Vector2(0f, 150f),
                                     new Vector2(1700f, 60f), 30f, TextAlignmentOptions.Center, font,
                                     new Color(0.78f, 0.84f, 0.95f));
        subtitle.text = "用哨子、引导石、地形和机关，把这只会跟跳的呆史莱姆安全送进收购站";

        Button startBtn = MakeButton(canvasGo.transform, "Btn_Start", cc, new Vector2(0f, -10f),
                                     new Vector2(440f, 88f), "开始游戏", font, white);
        Button quitBtn = MakeButton(canvasGo.transform, "Btn_Quit", cc, new Vector2(0f, -130f),
                                    new Vector2(440f, 88f), "退出游戏", font, white);

        MainMenu menu = EnsureComponent<MainMenu>(canvasGo);
        menu.startButton = startBtn;
        menu.quitButton = quitBtn;
        menu.levelSelectSceneName = "LevelSelect";

        ForceChineseFont(scene, font);

        string path = Root + "/Scenes/MainMenu.unity";
        EditorSceneManager.SaveScene(scene, path);
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] 主菜单场景已生成：" + path);
    }

    static void BuildLevelSelectScene(TMP_FontAsset font, Sprite white, Sprite starOn, Sprite starOff)
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Vector2 cc = new Vector2(0.5f, 0.5f);
        Vector2 topCenter = new Vector2(0.5f, 1f);
        Vector2 bottomCenter = new Vector2(0.5f, 0f);

        MakeStaticCamera(new Vector3(0f, 0f, -10f), 5.4f);
        MakeEventSystemObject();

        GameObject canvasGo = new GameObject("LevelSelectCanvas");
        SetupCanvasComponent(canvasGo);

        TMP_Text title = MakeText(canvasGo.transform, "Title", topCenter, new Vector2(0f, -70f),
                                  new Vector2(1700f, 110f), 64f, TextAlignmentOptions.Top, font,
                                  new Color(1f, 0.95f, 0.72f));
        title.text = "选择关卡";

        // 12 个槽位（4 列 × 3 行）：以后加关卡不用重做这个场景。
        // 没有对应场景的槽位会被 LevelSelectUI 在运行时隐藏，并把剩下的卡片整体居中。
        const int levelCount = 12;
        const int gridCols = 4;
        const float cellW = 380f;
        const float cellH = 260f;
        const float cardW = 340f;
        const float cardH = 230f;

        string[] sceneNames = { "Level0", "Level1", "Level2" };
        Button[] buttons = new Button[levelCount];
        TMP_Text[] labels = new TMP_Text[levelCount];
        TMP_Text[] coinTexts = new TMP_Text[levelCount];
        Image[] starImages = new Image[levelCount * 3];

        for (int i = 0; i < levelCount; i++)
        {
            int row = i / gridCols;
            int col = i % gridCols;
            float x = (col - (gridCols - 1) * 0.5f) * cellW;
            float y = 230f - row * cellH;

            Button card = MakeButton(canvasGo.transform, "LevelCard" + (i + 1), cc,
                                     new Vector2(x, y), new Vector2(cardW, cardH), "", font, white);
            buttons[i] = card;

            // 卡片标题（复用 MakeButton 生成的 Label 子物体）
            TMP_Text label = card.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                label.text = "第 " + (i + 1) + " 关";
                label.fontSize = 34f;
                label.color = new Color(0.12f, 0.12f, 0.15f);
                RectTransform lrt = label.rectTransform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f);
                lrt.pivot = new Vector2(0.5f, 1f);
                lrt.anchoredPosition = new Vector2(0f, -16f);
                lrt.sizeDelta = new Vector2(cardW - 30f, 46f);
                labels[i] = label;
            }

            coinTexts[i] = MakeText(card.transform, "CoinText", cc, new Vector2(0f, -14f),
                                    new Vector2(cardW - 30f, 36f), 24f, TextAlignmentOptions.Center, font,
                                    new Color(0.18f, 0.18f, 0.22f));

            for (int s = 0; s < 3; s++)
            {
                starImages[i * 3 + s] = MakeImage(card.transform, "Star" + (s + 1), bottomCenter,
                                                  new Vector2((s - 1) * 62f, 32f), new Vector2(52f, 52f),
                                                  Color.white, starOff, false);
            }
        }

        Button backBtn = MakeButton(canvasGo.transform, "Btn_Back", bottomCenter, new Vector2(0f, 90f),
                                    new Vector2(400f, 84f), "返回主菜单", font, white);

        LevelSelectUI ui = EnsureComponent<LevelSelectUI>(canvasGo);
        ui.levelButtons = buttons;
        ui.levelSceneNames = sceneNames;
        ui.levelLabels = labels;
        ui.levelCoinTexts = coinTexts;
        ui.levelStarImages = starImages;
        ui.starsPerLevel = 3;
        ui.starOnSprite = starOn;
        ui.starOffSprite = starOff;
        ui.backButton = backBtn;
        ui.mainMenuSceneName = "MainMenu";
        ui.autoDetectFromBuildSettings = true;
        ui.hideUnusedButtons = true;
        ui.autoArrangeVisibleSlots = true;
        ui.gridColumns = gridCols;
        ui.gridCellSize = new Vector2(cellW, cellH);

        ForceChineseFont(scene, font);

        string path = Root + "/Scenes/LevelSelect.unity";
        EditorSceneManager.SaveScene(scene, path);
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] 关卡选择场景已生成：" + path +
                  "（12 个槽位，运行时按 Build Settings 里实际存在的关卡显示，剩下的隐藏并整体居中）");
    }

    static void SetBuildScenes(string[] paths)
    {
        List<EditorBuildSettingsScene> list = new List<EditorBuildSettingsScene>();
        foreach (string p in paths)
        {
            if (File.Exists(p)) list.Add(new EditorBuildSettingsScene(p, true));
            else Debug.LogWarning("[呆呆史莱姆] 场景不存在，跳过注册：" + p);
        }
        EditorBuildSettings.scenes = list.ToArray();

        string joined = "";
        foreach (EditorBuildSettingsScene s in list) joined += "\n    " + s.path;
        Debug.Log("[呆呆史莱姆] Build Settings 已注册 " + list.Count + " 个场景：" + joined);
    }

    static void ConfigureNextScene(string scenePath, string nextSceneName)
    {
        if (!File.Exists(scenePath)) return;

        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        LevelResult result = FindOne<LevelResult>(CollectBehaviours(scene));
        if (result == null)
        {
            Debug.LogWarning("[呆呆史莱姆] " + Path.GetFileName(scenePath) + " 里没有 LevelResult，跳过");
            return;
        }

        result.nextSceneName = nextSceneName;
        EditorUtility.SetDirty(result);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[呆呆史莱姆] " + Path.GetFileName(scenePath) + " 的下一关 = " +
                  (string.IsNullOrEmpty(nextSceneName) ? "(无，末关)" : nextSceneName));
    }

    // ---------- 菜单场景用的公共小工具 ----------

    static GameObject MakeStaticCamera(Vector3 position, float size)
    {
        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.position = position;

        Camera cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = size;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.10f, 0.11f, 0.16f);
        camGo.AddComponent<AudioListener>();
        return camGo;
    }

    static void MakeEventSystemObject()
    {
        GameObject esGo = new GameObject("EventSystem");
        esGo.AddComponent<EventSystem>();
        esGo.AddComponent<StandaloneInputModule>();
    }

    static void SetupCanvasComponent(GameObject canvasGo)
    {
        Canvas canvas = EnsureComponent<Canvas>(canvasGo);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = EnsureComponent<CanvasScaler>(canvasGo);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        EnsureComponent<GraphicRaycaster>(canvasGo);
    }
    // ======================= ⑫ 第二关：多层平台 + 水道 + 路径石 + 投掷深坑 =======================

    [MenuItem("Tools/呆呆史莱姆/⑫ 生成第二关 Level2（长关卡，带相机跟随）", false, 112)]
    public static void BuildLevel2Menu()
    {
        BuildLevel2();
    }

    /// <summary>
    /// 生成第二关场景 Assets/_Project/Scenes/Level2.unity。
    /// 关卡总长 96 格（x -26 → 70），所以带相机跟随。
    /// 段落：起点 → 多层平台 → 长水道 → 路径石通道 → 投掷深坑 → 收购站。
    /// </summary>
    public static void BuildLevel2()
    {
        EnsureFolders();
        SetupLayersAndTags();
        SetupCollisionMatrix();
        EnsureTMPEssentials();
        TMP_FontAsset font = EnsureChineseFont();
        Sprite square = CreateSquareSprite();
        if (square == null)
        {
            Debug.LogError("[呆呆史莱姆] 没有占位图，请先点 ③ 生成占位美术");
            return;
        }

        if (!Application.isBatchMode)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Color cGround = new Color(0.36f, 0.32f, 0.44f);
        Color cPlat = new Color(0.48f, 0.43f, 0.58f);
        Color cWater = new Color(0.25f, 0.55f, 0.95f, 0.55f);
        Color cCoin = new Color(1f, 0.85f, 0.25f);
        Color cOrb = new Color(0.60f, 0.88f, 1f);
        Color cGoal = new Color(0.30f, 0.95f, 0.90f);

        int L_Ground = LayerMask.NameToLayer("Ground");
        int L_Hazard = LayerMask.NameToLayer("Hazard");
        int L_Pickup = LayerMask.NameToLayer("Pickup");
        int L_Trigger = LayerMask.NameToLayer("TriggerZone");

        Transform content = new GameObject("LevelContent").transform;

        // ---------------- 地形（1 格 = 1 单位，地面顶面统一 y = 0）----------------
        MakeBlock(content, "Ground_Start", new Vector3(-19f, -1f, 0f), new Vector2(14f, 2f), L_Ground, cGround, 0, square, false);
        MakeBlock(content, "Ground_Mid", new Vector3(-4f, -1f, 0f), new Vector2(16f, 2f), L_Ground, cGround, 0, square, false);
        MakeBlock(content, "Ground_WaterBed", new Vector3(12f, -2.5f, 0f), new Vector2(16f, 2f), L_Ground, cGround, 0, square, false);
        MakeBlock(content, "Ground_Right", new Vector3(34f, -1f, 0f), new Vector2(28f, 2f), L_Ground, cGround, 0, square, false);
        MakeBlock(content, "Ground_PitBed", new Vector3(50f, -2.5f, 0f), new Vector2(4f, 2f), L_Ground, cGround, 0, square, false);
        MakeBlock(content, "Ground_End", new Vector3(61f, -1f, 0f), new Vector2(18f, 2f), L_Ground, cGround, 0, square, false);

        // ---------------- 段落 1：多层平台（每级高 1.5 格、间隙 1.5 格 → 抱着史莱姆也能一级级跳）----------------
        MakeBlock(content, "Plat_1", new Vector3(-9.5f, 0.75f, 0f), new Vector2(3f, 1.5f), L_Ground, cPlat, 1, square, false);
        MakeBlock(content, "Plat_2", new Vector3(-5f, 1.5f, 0f), new Vector2(3f, 3f), L_Ground, cPlat, 1, square, false);
        MakeBlock(content, "Plat_3", new Vector3(-0.5f, 2.25f, 0f), new Vector2(3f, 4.5f), L_Ground, cPlat, 1, square, false);

        // ---------------- 段落 2：长水道（16 格，深 1.5 格。史莱姆自己走 ≈ 掉 80 血 → 必须抱着）----------------
        Hazard waterChannel = MakeWater(content, "Water_Channel", new Vector3(12f, -0.75f, 0f), new Vector2(16f, 1.5f), 20, square, cWater);

        // ---------------- 段落 3：路径石段落（史莱姆钻玩家钻不过去的矮通道，玩家从上面绕过去）----------------
        // 障碍块：底 y = 1.1、顶 y = 2.5
        //   下方矮通道净高 1.1 格 → 史莱姆（碰撞体高 0.9）钻得过去；玩家（碰撞体高 1.2）钻不过去
        //   顶面 2.5 格 → 玩家空手跳 3 格上得去；抱着史莱姆只有 1.8 格，上不去 → 必须先把它放下
        //   所以"玩家（带着史莱姆时）无法通过的地方"在这里是物理成立的，不是靠数值硬凑
        MakeBlock(content, "Wall_Guide", new Vector3(28f, 1.8f, 0f), new Vector2(8f, 1.4f), L_Ground, cPlat, 2, square, false);

        // ---------------- 段落 4：投掷深坑（4 格宽 × 1.5 格深，坑底是水）----------------
        Hazard waterPit = MakeWater(content, "Water_Pit", new Vector3(50f, -0.75f, 0f), new Vector2(4f, 1.5f), 15, square, cWater);

        // ---------------- 金币与回血球 ----------------
        // 多层平台顶部
        MakePickupBlock(content, "Coin_P1", new Vector3(-9.5f, 2.2f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_P2", new Vector3(-5f, 3.7f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_P3", new Vector3(-0.5f, 5.2f, 0f), square, cCoin, false);
        // 水道之后补给
        MakePickupBlock(content, "Orb_AfterWater", new Vector3(22f, 0.5f, 0f), square, cOrb, true);
        // 路径石通道里的额外奖励（只有走通道的史莱姆吃得到）
        MakePickupBlock(content, "Coin_Guide1", new Vector3(26f, 0.5f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_Guide2", new Vector3(28f, 0.5f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_Guide3", new Vector3(30f, 0.5f, 0f), square, cCoin, false);
        // 障碍顶上的金币（玩家绕路时自己拿，和通道里的互不干扰）
        MakePickupBlock(content, "Coin_Ledge1", new Vector3(26f, 3.2f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_Ledge2", new Vector3(30f, 3.2f, 0f), square, cCoin, false);
        // 投掷坑之前补给
        MakePickupBlock(content, "Orb_BeforePit", new Vector3(45f, 0.5f, 0f), square, cOrb, true);
        // 终点前
        MakePickupBlock(content, "Coin_End1", new Vector3(56f, 0.5f, 0f), square, cCoin, false);
        MakePickupBlock(content, "Coin_End2", new Vector3(60f, 0.5f, 0f), square, cCoin, false);

        // ---------------- 收购站 ----------------
        GameObject goal = MakeBlock(content, "Goal", new Vector3(65f, 1f, 0f), new Vector2(2f, 2f), L_Trigger, cGoal, 4, square, true);
        Goal goalComp = EnsureComponent<Goal>(goal);
        goalComp.onlySlime = true;
        goalComp.requireSlimeNotCarried = false;

        // ---------------- 管理器 ----------------
        GameObject gmGo = new GameObject("GameManager");
        gmGo.AddComponent<GameManager>();
        GameObject lmGo = new GameObject("LevelManager");
        LevelManager lm = lmGo.AddComponent<LevelManager>();
        lm.levelIndex = 2;

        // ---------------- 玩家 / 史莱姆（从预制体实例化，保持预制体连接）----------------
        GameObject playerGo = InstantiatePrefab(Root + "/Scenes/Player.prefab", new Vector3(-22f, 0.6f, 0f));
        GameObject slimeGo = InstantiatePrefab(Root + "/Scenes/Slime.prefab", new Vector3(-24f, 0.5f, 0f));

        if (playerGo == null || slimeGo == null)
        {
            Debug.LogError("[呆呆史莱姆] 找不到 Player.prefab / Slime.prefab（应在 Assets/_Project/Scenes/ 下），关卡不完整");
        }
        else
        {
            // 路径石预制体 + 路径点容器（引导石功能要用）
            SlimePathFollow spf = slimeGo.GetComponent<SlimePathFollow>();
            WaypointMarker wpPrefab = AssetDatabase.LoadAssetAtPath<WaypointMarker>(Root + "/Prefabs/Slime/WaypointMarker.prefab");
            if (spf != null)
            {
                if (wpPrefab != null) spf.waypointPrefab = wpPrefab;
                else Debug.LogWarning("[呆呆史莱姆] 找不到 WaypointMarker.prefab，引导石会失效（先点一次 ⑤ 一键全做）");

                if (spf.waypointContainer == null)
                {
                    GameObject container = new GameObject("WaypointContainer");
                    spf.waypointContainer = container.transform;
                }
            }
        }

        // ---------------- 相机（长关卡必须跟随）----------------
        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.position = new Vector3(-22f, 2f, -10f);
        Camera cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 7.5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.11f, 0.12f, 0.17f);
        camGo.AddComponent<AudioListener>();

        CameraFollow follow = camGo.AddComponent<CameraFollow>();
        follow.target = playerGo != null ? playerGo.transform : null;
        follow.offset = new Vector3(0f, 1.5f, -10f);
        follow.smoothTime = 0.12f;
        follow.verticalDeadZone = 1.5f;
        follow.useBounds = true;
        follow.boundsMin = new Vector2(-26f, -6f);
        follow.boundsMax = new Vector2(72f, 12f);

        // ---------------- 边界墙（防止跑出地图）----------------
        EnsureBoundaryWalls(scene);

        // ---------------- 注册 ----------------
        string path = Root + "/Scenes/Level2.unity";
        AddSceneToBuildSettings(path);

        // ---------------- UI + 自动连线（复用第一关那套）----------------
        BuildHud();
        BuildPanels();
        AutoWireActiveScene();
        ReportEmptyReferences();

        // ---------------- 最后统一存盘 ----------------
        // 关键：必须放在 AutoWireActiveScene 之后！
        // 否则 Player / Slime 预制体实例上的引用（player / levelManager / groundLayer...）
        // 不会被写进场景文件 —— 表现就是"进了第二关史莱姆不跟随"。
        ForceChineseFont(scene, font);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, path);
        AssetDatabase.Refresh();

        if (waterChannel == null || waterPit == null)
            Debug.LogWarning("[呆呆史莱姆] 水道生成异常，请检查 Hazard 层是否存在");

        Debug.Log("[呆呆史莱姆] ⑫ 第二关已生成：" + path + "（96 格长，相机跟随已开启）");
    }

    // ---------------- 第二关用的辅助方法 ----------------

    static GameObject MakeBlock(Transform parent, string name, Vector3 position, Vector2 size, int layer,
                                Color color, int sortingOrder, Sprite sprite, bool isTrigger)
    {
        GameObject go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        PlaceBox(go, position, size, layer, color, sortingOrder, sprite, isTrigger);
        return go;
    }

    static Hazard MakeWater(Transform parent, string name, Vector3 position, Vector2 size, int damage,
                            Sprite sprite, Color color)
    {
        GameObject go = MakeBlock(parent, name, position, size, LayerMask.NameToLayer("Hazard"), color, 3, sprite, true);
        Hazard h = EnsureComponent<Hazard>(go);
        h.hazardType = HazardType.Water;
        h.damage = damage;
        h.killPlayer = false;      // 水不杀玩家，只是让他跳出来费点劲
        return h;
    }

    static GameObject MakePickupBlock(Transform parent, string name, Vector3 position, Sprite sprite,
                                      Color color, bool isOrb)
    {
        GameObject go = MakeBlock(parent, name, position, new Vector2(0.6f, 0.6f),
                                  LayerMask.NameToLayer("Pickup"), color, 6, sprite, true);
        if (isOrb) EnsureComponent<SlimeOrb>(go);
        else EnsureComponent<Coin>(go);
        return go;
    }

    static GameObject InstantiatePrefab(string prefabPath, Vector3 position)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogWarning("[呆呆史莱姆] 找不到预制体：" + prefabPath);
            return null;
        }
        GameObject go = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (go != null) go.transform.position = position;
        return go;
    }
    // ======================= ⑪ 暂停菜单 + 结算面板 =======================

    [MenuItem("Tools/呆呆史莱姆/⑪ 搭暂停菜单与结算面板", false, 111)]
    public static void BuildPanelsMenu()
    {
        int n = BuildPanels();
        AutoWireActiveScene();
        ReportEmptyReferences();
        Debug.Log("[呆呆史莱姆] ⑪ 面板搭建完成，处理了 " + n + " 个面板。");
    }

    /// <summary>搭出暂停菜单与结算面板，并创建 EventSystem（按钮点击必需）。可重复执行。</summary>
    public static int BuildPanels()
    {
        EnsureTMPEssentials();
        TMP_FontAsset font = EnsureChineseFont();

        Scene scene = SceneManager.GetActiveScene();

        GameObject canvasGo = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "UICanvas") { canvasGo = root; break; }
        }
        if (canvasGo == null)
        {
            Debug.LogError("[呆呆史莱姆] 场景里没有 UICanvas，请先点 ⑩ 搭 HUD 与物品栏 UI");
            return 0;
        }

        List<MonoBehaviour> all = CollectBehaviours(scene);
        LevelManager levelManager = FindOne<LevelManager>(all);
        GameManager gameManager = FindOne<GameManager>(all);

        Sprite white = CreateSquareSprite();
        Sprite starOn = CreateColorSprite("Icon_StarOn", new Color(1f, 0.85f, 0.20f));
        Sprite starOff = CreateColorSprite("Icon_StarOff", new Color(0.25f, 0.25f, 0.30f));

        Vector2 cc = new Vector2(0.5f, 0.5f);

        // ---------- EventSystem（按钮点击必需）----------
        GameObject esGo = FindOrCreateRoot(scene, "EventSystem");
        if (esGo.GetComponent<EventSystem>() == null) esGo.AddComponent<EventSystem>();
        if (esGo.GetComponent<StandaloneInputModule>() == null) esGo.AddComponent<StandaloneInputModule>();

        // ---------- 暂停面板 ----------
        RectTransform pausePanel = FindOrCreateUIRoot(scene, "PausePanel", canvasGo.transform);
        Image pauseDim = pausePanel.gameObject.GetComponent<Image>();
        if (pauseDim == null) pauseDim = pausePanel.gameObject.AddComponent<Image>();
        pauseDim.sprite = white;
        pauseDim.color = new Color(0.04f, 0.035f, 0.06f, 0.86f);
        pauseDim.raycastTarget = true;

        Image pauseCard = MakeImage(pausePanel, "Card", cc, new Vector2(0f, 25f), new Vector2(660f, 640f), ColPanel, null, false);
        ApplyPanelStyle(pauseCard, ColPanel);

        MakeText(pausePanel, "Title", cc, new Vector2(0f, 250f), new Vector2(800f, 90f), 60f, TextAlignmentOptions.Center, font, Color.white).text = "已暂停";

        Button resumeBtn = MakeButton(pausePanel, "Btn_Resume", cc, new Vector2(0f, 90f), new Vector2(380f, 76f), "回到游戏", font, white);
        Button restartBtn = MakeButton(pausePanel, "Btn_Restart", cc, new Vector2(0f, -5f), new Vector2(380f, 76f), "重新开始", font, white);
        Button exitBtn = MakeButton(pausePanel, "Btn_ExitLevel", cc, new Vector2(0f, -100f), new Vector2(380f, 76f), "退出关卡", font, white);
        Button quitBtn = MakeButton(pausePanel, "Btn_Quit", cc, new Vector2(0f, -195f), new Vector2(380f, 76f), "退出游戏", font, white);

        PauseMenu pause = EnsureComponent<PauseMenu>(canvasGo);
        pause.gameManager = gameManager;
        pause.panelRoot = pausePanel.gameObject;
        pause.resumeButton = resumeBtn;
        pause.restartButton = restartBtn;
        pause.exitLevelButton = exitBtn;
        pause.quitButton = quitBtn;
        EditorUtility.SetDirty(pause);
        pausePanel.gameObject.SetActive(false);

        // ---------- 结算面板 ----------
        RectTransform resultPanel = FindOrCreateUIRoot(scene, "ResultPanel", canvasGo.transform);
        Image resultDim = resultPanel.gameObject.GetComponent<Image>();
        if (resultDim == null) resultDim = resultPanel.gameObject.AddComponent<Image>();
        resultDim.sprite = white;
        resultDim.color = new Color(0.04f, 0.035f, 0.06f, 0.90f);
        resultDim.raycastTarget = true;

        Image resultCard = MakeImage(resultPanel, "Card", cc, new Vector2(0f, 10f), new Vector2(820f, 900f), ColPanel, null, false);
        ApplyPanelStyle(resultCard, ColPanel);

        TMP_Text stampText = MakeText(resultPanel, "StampText", cc, new Vector2(0f, 330f), new Vector2(900f, 90f), 58f, TextAlignmentOptions.Center, font, new Color(1f, 0.9f, 0.4f));
        TMP_Text coinText = MakeText(resultPanel, "ResultCoinText", cc, new Vector2(0f, 215f), new Vector2(900f, 70f), 46f, TextAlignmentOptions.Center, font, new Color(1f, 0.85f, 0.25f));
        TMP_Text healthText = MakeText(resultPanel, "ResultHealthText", cc, new Vector2(0f, 140f), new Vector2(900f, 56f), 34f, TextAlignmentOptions.Center, font, Color.white);
        TMP_Text timeText = MakeText(resultPanel, "ResultTimeText", cc, new Vector2(0f, 78f), new Vector2(900f, 56f), 34f, TextAlignmentOptions.Center, font, Color.white);

        Image[] stars = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            stars[i] = MakeImage(resultPanel, "Star" + (i + 1), cc, new Vector2((i - 1) * 115f, -30f),
                                 new Vector2(95f, 95f), Color.white, starOff, false);
        }

        Button replayBtn = MakeButton(resultPanel, "Btn_Replay", cc, new Vector2(0f, -155f), new Vector2(380f, 76f), "重玩", font, white);
        Button nextBtn = MakeButton(resultPanel, "Btn_Next", cc, new Vector2(0f, -250f), new Vector2(380f, 76f), "下一关", font, white);
        Button selectBtn = MakeButton(resultPanel, "Btn_LevelSelect", cc, new Vector2(0f, -345f), new Vector2(380f, 76f), "返回关卡选择", font, white);

        LevelResult result = EnsureComponent<LevelResult>(canvasGo);
        result.levelManager = levelManager;
        result.gameManager = gameManager;
        result.panelRoot = resultPanel.gameObject;
        result.stampText = stampText;
        result.coinText = coinText;
        result.healthText = healthText;
        result.timeText = timeText;
        result.starImages = stars;
        result.starOnSprite = starOn;
        result.starOffSprite = starOff;
        result.replayButton = replayBtn;
        result.nextButton = nextBtn;
        result.levelSelectButton = selectBtn;
        EditorUtility.SetDirty(result);
        resultPanel.gameObject.SetActive(false);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[呆呆史莱姆] ⑪ 暂停菜单 + 结算面板 + EventSystem 已生成到「" + scene.name + "」");
        return 2;
    }

    static Button MakeButton(Transform parent, string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size,
                             string label, TMP_FontAsset font, Sprite sprite)
    {
        RectTransform rt = MakeRect(parent, name, anchor, anchoredPos, size);

        Image img = rt.gameObject.AddComponent<Image>();
        Sprite panel = UIPanelSprite();
        if (panel != null)
        {
            img.sprite = panel;
            img.type = Image.Type.Sliced;
        }
        else
        {
            img.sprite = sprite;
            img.type = Image.Type.Simple;
        }
        img.color = Color.white;
        img.raycastTarget = true;

        Button btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        // 悬停 / 按下的整块高亮；描边是烘进贴图的深色，不会被染掉
        ColorBlock colors = btn.colors;
        colors.normalColor = new Color(0.24f, 0.21f, 0.34f);
        colors.highlightedColor = new Color(0.35f, 0.30f, 0.48f);
        colors.pressedColor = new Color(0.16f, 0.14f, 0.24f);
        colors.selectedColor = new Color(0.24f, 0.21f, 0.34f);
        colors.disabledColor = new Color(0.14f, 0.13f, 0.18f, 0.65f);
        colors.fadeDuration = 0.06f;
        btn.colors = colors;

        TMP_Text txt = MakeText(rt, "Label", anchor, Vector2.zero,
                                new Vector2(Mathf.Max(10f, size.x - 24f), Mathf.Max(10f, size.y - 12f)),
                                32f, TextAlignmentOptions.Center, font, ColTextMain);
        txt.text = label;

        return btn;
    }
    // ======================= ⑩ HUD 与物品栏 =======================

    [MenuItem("Tools/呆呆史莱姆/⑩ 搭 HUD 与物品栏 UI", false, 110)]
    public static void BuildHudMenu()
    {
        int n = BuildHud();
        AutoWireActiveScene();
        ReportEmptyReferences();
        Debug.Log("[呆呆史莱姆] ⑩ UI 搭建完成，处理了 " + n + " 组 UI。");
    }

    /// <summary>在当前场景里搭出 HUD（金币/计时/模式/路径点/血条/提示）与 4 格物品栏。可重复执行。</summary>
    public static int BuildHud()
    {
        EnsureTMPEssentials();
        TMP_FontAsset font = EnsureChineseFont();

        Scene scene = SceneManager.GetActiveScene();

        // 先删掉旧的 UICanvas 再重建：
        // 早期版本的生成代码每次都 new 新物体，反复执行会把 HUD 叠成好几份（文字发虚、互相遮挡）。
        GameObject oldCanvas = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "UICanvas") { oldCanvas = root; break; }
        }
        if (oldCanvas != null) Object.DestroyImmediate(oldCanvas);

        List<MonoBehaviour> all = CollectBehaviours(scene);
        LevelManager levelManager = FindOne<LevelManager>(all);
        SlimeController slime = FindOne<SlimeController>(all);
        PlayerInventory inventory = FindOne<PlayerInventory>(all);
        PlayerController player = FindOne<PlayerController>(all);
        SlimePathFollow pathFollow = FindOne<SlimePathFollow>(all);

        Sprite white = CreateSquareSprite();
        Sprite iconNone = CreateColorSprite("Icon_None", new Color(0.55f, 0.55f, 0.55f));
        Sprite iconWhistle = CreateColorSprite("Icon_Whistle", new Color(1f, 0.85f, 0.25f));
        Sprite iconStone = CreateColorSprite("Icon_GuideStone", new Color(0.30f, 0.95f, 0.95f));
        Sprite iconSlot4 = CreateColorSprite("Icon_Slot4", new Color(0.65f, 0.45f, 0.95f));

        // ---------- Canvas ----------
        GameObject canvasGo = FindOrCreateRoot(scene, "UICanvas");
        Canvas canvas = EnsureComponent<Canvas>(canvasGo);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = EnsureComponent<CanvasScaler>(canvasGo);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        EnsureComponent<GraphicRaycaster>(canvasGo);

        Vector2 tl = new Vector2(0f, 1f);
        Vector2 bl = new Vector2(0f, 0f);
        Vector2 tc = new Vector2(0.5f, 1f);
        Vector2 cc = new Vector2(0.5f, 0.5f);

        // ---------- HUD 底板（半透明圆角面板，信息不再直接飘在画面上）----------
        Image hudPanel = MakeImage(canvasGo.transform, "HudPanel", tl, new Vector2(12f, -12f), new Vector2(700f, 336f), ColPanel, null, false);
        ApplyPanelStyle(hudPanel, ColPanel);

        // ---------- HUD 文字 ----------
        TMP_Text coinText = MakeText(canvasGo.transform, "CoinText", tl, new Vector2(36f, -26f), new Vector2(560f, 46f), 34f, TextAlignmentOptions.TopLeft, font, ColGold);
        TMP_Text timeText = MakeText(canvasGo.transform, "TimeText", tl, new Vector2(36f, -76f), new Vector2(300f, 42f), 30f, TextAlignmentOptions.TopLeft, font, ColTextDim);
        TMP_Text modeText = MakeText(canvasGo.transform, "ModeText", tl, new Vector2(36f, -124f), new Vector2(560f, 42f), 30f, TextAlignmentOptions.TopLeft, font, ColGreen);
        TMP_Text waypointText = MakeText(canvasGo.transform, "WaypointText", tl, new Vector2(36f, -172f), new Vector2(560f, 42f), 28f, TextAlignmentOptions.TopLeft, font, new Color(0.30f, 0.90f, 0.88f));
        TMP_Text carryText = MakeText(canvasGo.transform, "CarryText", tl, new Vector2(36f, -218f), new Vector2(640f, 42f), 28f, TextAlignmentOptions.TopLeft, font, ColGold);
        TMP_Text hintText = MakeText(canvasGo.transform, "HintText", tc, new Vector2(0f, -150f), new Vector2(1300f, 70f), 38f, TextAlignmentOptions.Top, font, ColTextMain);
        hintText.enableWordWrapping = true;

        // ---------- 血条 ----------
        Image healthBg = MakeImage(canvasGo.transform, "HealthBarBg", tl, new Vector2(36f, -274f), new Vector2(320f, 26f), new Color(0.06f, 0.05f, 0.10f), null, false);
        ApplyPanelStyle(healthBg, new Color(0.06f, 0.05f, 0.10f));
        Image healthFill = MakeImage(canvasGo.transform, "HealthBarFill", tl, new Vector2(42f, -279f), new Vector2(308f, 16f), ColGreen, white, false);
        healthFill.type = Image.Type.Filled;
        healthFill.fillMethod = Image.FillMethod.Horizontal;
        healthFill.fillOrigin = 0;
        healthFill.fillAmount = 1f;
        TMP_Text healthText = MakeText(canvasGo.transform, "HealthText", tl, new Vector2(368f, -271f), new Vector2(320f, 40f), 26f, TextAlignmentOptions.TopLeft, font, ColTextMain);

        // ---------- 售价条（小黄条）----------
        Image priceBg = MakeImage(canvasGo.transform, "PriceBarBg", tl, new Vector2(36f, -308f), new Vector2(320f, 22f), new Color(0.06f, 0.05f, 0.10f), null, false);
        ApplyPanelStyle(priceBg, new Color(0.06f, 0.05f, 0.10f));
        Image priceFill = MakeImage(canvasGo.transform, "PriceBarFill", tl, new Vector2(42f, -312f), new Vector2(308f, 14f), ColGold, white, false);
        priceFill.type = Image.Type.Filled;
        priceFill.fillMethod = Image.FillMethod.Horizontal;
        priceFill.fillOrigin = 0;
        priceFill.fillAmount = 1f;
        TMP_Text priceText = MakeText(canvasGo.transform, "PriceText", tl, new Vector2(368f, -305f), new Vector2(440f, 40f), 24f, TextAlignmentOptions.TopLeft, font, ColGold);

        UIManager uiManager = EnsureComponent<UIManager>(canvasGo);
        // 7 项对应 SlimeState 的 7 个值（新增了 Resting = 投掷落地）
        uiManager.modeDisplayNames = new string[] { "跟随", "待命", "惊吓", "被携带", "投掷中", "阵亡", "投掷落地" };
        uiManager.levelManager = levelManager;
        uiManager.slimeController = slime;
        uiManager.playerInventory = inventory;
        uiManager.playerController = player;
        uiManager.pathFollow = pathFollow;
        uiManager.coinText = coinText;
        uiManager.timeText = timeText;
        uiManager.modeText = modeText;
        uiManager.waypointText = waypointText;
        uiManager.hintText = hintText;
        uiManager.carryText = carryText;
        uiManager.healthFill = healthFill;
        uiManager.healthText = healthText;
        uiManager.priceFill = priceFill;
        uiManager.priceText = priceText;
        EditorUtility.SetDirty(uiManager);

        // ---------- 物品栏 ----------
        RectTransform barRt = FindOrCreateUIRoot(scene, "InventoryBar", canvasGo.transform);
        barRt.anchorMin = bl;
        barRt.anchorMax = bl;
        barRt.pivot = bl;
        barRt.anchoredPosition = new Vector2(30f, 30f);
        barRt.sizeDelta = new Vector2(440f, 104f);

        const int slotCount = 4;
        Image[] frames = new Image[slotCount];
        Image[] icons = new Image[slotCount];
        TMP_Text[] indexTexts = new TMP_Text[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            RectTransform slot = MakeRect(barRt, "Slot" + (i + 1), bl, new Vector2(i * 110f, 4f), new Vector2(100f, 100f));
            frames[i] = MakeImage(slot, "Frame", bl, Vector2.zero, new Vector2(100f, 100f), Color.white, white, false);
            icons[i] = MakeImage(slot, "Icon", cc, Vector2.zero, new Vector2(74f, 74f), Color.white, null, false);
            indexTexts[i] = MakeText(slot, "Index", bl, new Vector2(8f, 2f), new Vector2(60f, 34f), 24f, TextAlignmentOptions.BottomLeft, font, new Color(0.15f, 0.15f, 0.15f));
        }

        Image lockedOverlay = MakeImage(barRt, "LockedOverlay", bl, new Vector2(-6f, -6f), new Vector2(452f, 116f), new Color(0.05f, 0.05f, 0.05f, 0.65f), white, false);
        lockedOverlay.transform.SetAsLastSibling();
        lockedOverlay.gameObject.SetActive(false);

        InventoryUI invUi = EnsureComponent<InventoryUI>(canvasGo);
        invUi.playerInventory = inventory;
        invUi.slotFrames = frames;
        invUi.slotIcons = icons;
        invUi.slotIndexTexts = indexTexts;
        invUi.noneIcon = iconNone;
        invUi.whistleIcon = iconWhistle;
        invUi.guideStoneIcon = iconStone;
        invUi.slot4Icon = iconSlot4;
        invUi.lockedOverlay = lockedOverlay.gameObject;
        EditorUtility.SetDirty(invUi);

        ForceChineseFont(scene, font);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[呆呆史莱姆] ⑩ HUD 与物品栏已生成到「" + scene.name + "」");
        return 2;
    }

    // ---------- UI 基础设施 ----------

    /// <summary>导入 TextMeshPro Essential Resources（只做一次）。</summary>
    static void EnsureTMPEssentials()
    {
        if (Directory.Exists("Assets/TextMesh Pro")) return;

        string pkg = null;
        string cache = Path.GetFullPath("Library/PackageCache");
        if (Directory.Exists(cache))
        {
            foreach (string dir in Directory.GetDirectories(cache, "com.unity.textmeshpro*"))
            {
                string candidate = Path.Combine(dir, "Package Resources/TMP Essential Resources.unitypackage");
                if (File.Exists(candidate)) { pkg = candidate; break; }
            }
        }

        if (pkg == null)
        {
            Debug.LogWarning("[呆呆史莱姆] 找不到 TMP Essential Resources，先手动点 Window → TextMeshPro → Import TMP Essential Resources");
            return;
        }

        AssetDatabase.ImportPackage(pkg, false);
        AssetDatabase.Refresh();
        Debug.Log("[呆呆史莱姆] 已导入 TextMeshPro Essential Resources");
    }

    /// <summary>从系统字体生成一个支持中文的 TMP 动态字体资源（只做一次）。失败则返回 null，UI 用默认字体。</summary>
    static TMP_FontAsset EnsureChineseFont()
    {
        string assetPath = Root + "/Art/UI/SimHei SDF.asset";

        // ⚠ 必须先 Refresh 再加载：批处理会话里紧跟 ImportPackage / 建资源之后，
        //   AssetDatabase 可能还没就绪，LoadAssetAtPath 会【静默返回 null】，
        //   于是整个 UI 用默认字体（LiberationSans，无中文）建出来 ——
        //   日志里一条警告都没有，直到运行时才刷出一屏"字符找不到"（踩过）。
        AssetDatabase.Refresh();

        TMP_FontAsset cached = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        if (cached == null)
        {
            // 再兜一层：按"文件名 + 类型"搜索，防止资源被挪了位置
            string[] guids = AssetDatabase.FindAssets("SimHei t:TMP_FontAsset");
            for (int i = 0; i < guids.Length; i++)
            {
                TMP_FontAsset found = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (found != null) { cached = found; break; }
            }
        }

        if (cached != null)
        {
            EnsureDefaultTmpFont(cached);
            return cached;
        }

        try
        {
            string[] candidates = { "C:/Windows/Fonts/simhei.ttf", "C:/Windows/Fonts/Deng.ttf", "C:/Windows/Fonts/msyh.ttc" };
            string source = null;
            foreach (string c in candidates) { if (File.Exists(c)) { source = c; break; } }
            if (source == null)
            {
                Debug.LogWarning("[呆呆史莱姆] 系统里没找到中文字体，UI 里的中文会显示成方块");
                return null;
            }

            string uiDir = Root + "/Art/UI";
            if (!Directory.Exists(uiDir)) Directory.CreateDirectory(uiDir);

            string fontPath = uiDir + "/SimHei.ttf";
            if (!File.Exists(fontPath)) File.Copy(source, fontPath, true);
            AssetDatabase.ImportAsset(fontPath, ImportAssetOptions.ForceUpdate);

            Font font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null)
            {
                Debug.LogWarning("[呆呆史莱姆] 中文字体导入失败：" + fontPath);
                return null;
            }

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(font);
            if (fontAsset == null)
            {
                Debug.LogWarning("[呆呆史莱姆] 创建 TMP 字体资源失败");
                return null;
            }

            fontAsset.name = "SimHei SDF";
            AssetDatabase.CreateAsset(fontAsset, assetPath);

            if (fontAsset.atlasTextures != null && fontAsset.atlasTextures.Length > 0 && fontAsset.atlasTextures[0] != null)
            {
                fontAsset.atlasTextures[0].name = "SimHei Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
            }
            if (fontAsset.material != null)
            {
                fontAsset.material.name = "SimHei Atlas Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            Debug.Log("[呆呆史莱姆] 已生成中文 TMP 字体资源：" + assetPath);

            TMP_FontAsset created = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            EnsureDefaultTmpFont(created);
            return created;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[呆呆史莱姆] 生成中文字体出错，UI 会退回默认字体（中文可能是方块）：" + e.Message);
            return null;
        }
    }

    /// <summary>生成一张纯色占位图并导入成 Sprite（用于 UI 图标）。</summary>
    static Sprite CreateColorSprite(string fileName, Color color)
    {
        string dir = Root + "/Art/UI";
        string path = dir + "/" + fileName + ".png";

        if (!File.Exists(path))
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Texture2D tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[16 * 16];
            Color32 c = color;
            for (int i = 0; i < px.Length; i++) px[i] = c;
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null && ti.textureType != TextureImporterType.Sprite)
        {
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.spritePixelsPerUnit = 16f;
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static RectTransform FindOrCreateUIRoot(Scene scene, string name, Transform parent)
    {
        GameObject go = null;

        // 先找父物体下面（面板都是 Canvas 的子物体）
        if (parent != null)
        {
            Transform under = parent.Find(name);
            if (under != null) go = under.gameObject;
        }
        if (go == null)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) { go = root; break; }
            }
        }

        // 已存在的普通物体（没有 RectTransform）不能改成 UI，直接删掉重建
        if (go != null && go.GetComponent<RectTransform>() == null)
        {
            Object.DestroyImmediate(go);
            go = null;
        }
        if (go == null) go = new GameObject(name, typeof(RectTransform));

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    static RectTransform MakeRect(Transform parent, string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
    {
        // 父物体下已有同名 UI 子物体就复用，避免反复执行时重复生成
        GameObject go = null;
        Transform existing = parent != null ? parent.Find(name) : null;
        if (existing != null && existing.GetComponent<RectTransform>() != null)
        {
            go = existing.gameObject;
        }
        else
        {
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
        }

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return rt;
    }

    static TMP_Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size,
                             float fontSize, TextAlignmentOptions align, TMP_FontAsset font, Color color)
    {
        RectTransform rt = MakeRect(parent, name, anchor, anchoredPos, size);
        TextMeshProUGUI tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.fontSize = fontSize;
        tmp.alignment = align;
        tmp.color = color;
        tmp.text = name;
        tmp.raycastTarget = false;
        return tmp;
    }

    static Image MakeImage(Transform parent, string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size,
                           Color color, Sprite sprite, bool raycastTarget)
    {
        RectTransform rt = MakeRect(parent, name, anchor, anchoredPos, size);
        Image img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = raycastTarget;
        return img;
    }
    // ======================= ⑨ 往当前场景补关卡元素 =======================

    [MenuItem("Tools/呆呆史莱姆/⑨ 往当前场景补关卡元素（尖刺/金币/回血球/收购站）", false, 109)]
    public static void AddLevelElementsMenu()
    {
        int n = AddLevelElements();
        AutoWireActiveScene();
        ReportEmptyReferences();
        Debug.Log("[呆呆史莱姆] ⑨ 关卡元素补齐完成，处理了 " + n + " 个物体。");
    }

    /// <summary>
    /// 在当前场景里补齐 尖刺 / 回血球 / 金币 / 收购站，并按地面碰撞体的实际包围盒自动摆放。
    /// 可重复执行：已存在的同名物体会被复用并重新摆放，不会重复创建。
    /// </summary>
    public static int AddLevelElements()
    {
        Scene scene = SceneManager.GetActiveScene();
        Sprite square = CreateSquareSprite();
        if (square == null)
        {
            Debug.LogError("[呆呆史莱姆] 没有占位图，请先点 ③ 生成占位美术");
            return 0;
        }

        List<MonoBehaviour> all = CollectBehaviours(scene);
        LevelManager levelManager = FindOne<LevelManager>(all);
        SlimeController slime = FindOne<SlimeController>(all);

        Collider2D groundA = FindGroundCollider(scene, "Ground_A");
        Collider2D groundB = FindGroundCollider(scene, "Ground_B");
        Collider2D widest = FindWidestGround(scene);

        if (groundA == null) groundA = widest;
        if (groundB == null) groundB = widest;
        if (groundB == null)
        {
            Debug.LogError("[呆呆史莱姆] 场景里找不到地面（Layer = Ground 的碰撞体），没法自动摆放。请先铺好地面。");
            return 0;
        }
        if (groundA == null) groundA = groundB;

        // ---- 尖刺：放在左半地面右侧（玩家出生点前方，史莱姆跟着走过来时会撞上）----
        GameObject spike = FindOrCreateRoot(scene, "Hazard_Spike");
        PlaceBox(spike, GroundSpot(groundA, 0.72f, 0.25f), new Vector2(1.5f, 0.5f),
                 LayerMask.NameToLayer("Hazard"), new Color(0.95f, 0.30f, 0.30f), 5, square, true);
        Hazard hazard = EnsureComponent<Hazard>(spike);
        hazard.hazardType = HazardType.Spike;
        hazard.damage = 20;
        hazard.killPlayer = true;
        hazard.levelManager = levelManager;

        // ---- 回血球：紧跟尖刺之后，掉血后能立刻补回来 ----
        MakeOrUpdatePickup(scene, "Orb_1", GroundSpot(groundA, 0.80f, 0.5f),
                           new Color(0.60f, 0.88f, 1f), square, levelManager, slime, true);

        // ---- 金币 x2：放在右半地面（需要抱着史莱姆跳过坑才能吃到）----
        MakeOrUpdatePickup(scene, "Coin_1", GroundSpot(groundB, 0.35f, 0.5f),
                           new Color(1f, 0.85f, 0.25f), square, levelManager, slime, false);
        MakeOrUpdatePickup(scene, "Coin_2", GroundSpot(groundB, 0.60f, 0.5f),
                           new Color(1f, 0.85f, 0.25f), square, levelManager, slime, false);

        // ---- 收购站：右半地面尽头 ----
        GameObject goal = FindOrCreateRoot(scene, "Goal");
        PlaceBox(goal, GroundSpot(groundB, 0.90f, 1f), new Vector2(2f, 2f),
                 LayerMask.NameToLayer("TriggerZone"), new Color(0.30f, 0.95f, 0.90f), 2, square, true);
        Goal goalComp = EnsureComponent<Goal>(goal);
        goalComp.levelManager = levelManager;
        goalComp.onlySlime = true;
        goalComp.requireSlimeNotCarried = false;

        // ---- 关卡边界墙：防止跑出地图掉进虚空（掉出去会导致关卡永远无法完成）----
        EnsureBoundaryWalls(scene);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.Refresh();

        Debug.Log("[呆呆史莱姆] ⑨ 已在「" + scene.name + "」补齐：尖刺 1、回血球 1、金币 2、收购站 1 + 左右边界墙");
        return 5;
    }

    /// <summary>
    /// 在地图左右两端各加一堵看不见的墙，防止玩家/史莱姆跑出边界掉进虚空。
    /// 掉出边界会导致关卡既完不成也失败不了 —— 这是"跑出地图卡死"的治本方案。
    /// </summary>
    static int EnsureBoundaryWalls(Scene scene)
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        if (groundLayer < 0)
        {
            Debug.LogWarning("[呆呆史莱姆] 找不到 Ground 层，跳过边界墙");
            return 0;
        }

        Bounds total = new Bounds();
        bool has = false;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Collider2D[] cols = root.GetComponentsInChildren<Collider2D>(true);
            foreach (Collider2D c in cols)
            {
                if (c.gameObject.layer != groundLayer) continue;
                if (c.gameObject.name.StartsWith("Wall_")) continue;
                if (!has) { total = c.bounds; has = true; }
                else total.Encapsulate(c.bounds);
            }
        }

        if (!has)
        {
            Debug.LogWarning("[呆呆史莱姆] 场景里找不到地面，跳过边界墙");
            return 0;
        }

        float thickness = 2f;
        float height = 60f;
        float centerY = total.min.y + height * 0.5f - 10f;

        MakeWall(scene, "Wall_Left", new Vector3(total.min.x - thickness * 0.5f, centerY, 0f),
                 new Vector2(thickness, height), groundLayer);
        MakeWall(scene, "Wall_Right", new Vector3(total.max.x + thickness * 0.5f, centerY, 0f),
                 new Vector2(thickness, height), groundLayer);

        Debug.Log(string.Format("[呆呆史莱姆] 边界墙已就位：x < {0:F1} 与 x > {1:F1} 会被挡住", total.min.x, total.max.x));
        return 2;
    }

    static void MakeWall(Scene scene, string name, Vector3 position, Vector2 size, int layer)
    {
        GameObject go = FindOrCreateRoot(scene, name);
        go.transform.position = position;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        go.layer = layer;

        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        if (sr != null) sr.enabled = false;   // 看不见，但 Hierarchy 里能找到（方便调试）

        BoxCollider2D box = EnsureComponent<BoxCollider2D>(go);
        box.size = Vector2.one;
        box.isTrigger = false;
    }

    /// <summary>在地面碰撞体上按横向比例 t 取落点，halfHeight 是物体半高，让它正好坐在地面上。</summary>
    static Vector3 GroundSpot(Collider2D ground, float t, float halfHeight)
    {
        Bounds b = ground.bounds;
        return new Vector3(Mathf.Lerp(b.min.x, b.max.x, t), b.max.y + halfHeight, 0f);
    }

    static GameObject FindOrCreateRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
            Transform child = root.transform.Find(name);
            if (child != null) return child.gameObject;
        }
        return new GameObject(name);
    }

    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        if (c == null) c = go.AddComponent<T>();
        return c;
    }

    static Collider2D FindGroundCollider(Scene scene, string objectName)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all)
            {
                if (t.name != objectName) continue;
                Collider2D c = t.GetComponent<Collider2D>();
                if (c != null) return c;
            }
        }
        return null;
    }

    static Collider2D FindWidestGround(Scene scene)
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        Collider2D best = null;
        float bestWidth = 0f;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Collider2D[] cols = root.GetComponentsInChildren<Collider2D>(true);
            foreach (Collider2D c in cols)
            {
                if (groundLayer >= 0 && c.gameObject.layer != groundLayer) continue;
                if (c.bounds.size.x > bestWidth) { bestWidth = c.bounds.size.x; best = c; }
            }
        }
        return best;
    }

    static void PlaceBox(GameObject go, Vector3 position, Vector2 size, int layer, Color color,
                         int sortingOrder, Sprite sprite, bool isTrigger)
    {
        go.transform.position = position;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        go.layer = layer < 0 ? 0 : layer;

        SpriteRenderer sr = EnsureComponent<SpriteRenderer>(go);
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = sortingOrder;

        BoxCollider2D box = EnsureComponent<BoxCollider2D>(go);
        box.size = Vector2.one;
        box.isTrigger = isTrigger;
    }

    static void MakeOrUpdatePickup(Scene scene, string name, Vector3 position, Color color, Sprite sprite,
                                   LevelManager levelManager, SlimeController slime, bool isOrb)
    {
        GameObject go = FindOrCreateRoot(scene, name);
        go.transform.position = position;
        go.transform.localScale = new Vector3(0.6f, 0.6f, 1f);

        int pickupLayer = LayerMask.NameToLayer("Pickup");
        go.layer = pickupLayer < 0 ? 0 : pickupLayer;

        SpriteRenderer sr = EnsureComponent<SpriteRenderer>(go);
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = 6;

        CircleCollider2D col = EnsureComponent<CircleCollider2D>(go);
        col.radius = 0.5f;
        col.isTrigger = true;

        if (isOrb)
        {
            SlimeOrb orb = EnsureComponent<SlimeOrb>(go);
            orb.slime = slime;
            orb.healAmount = 25;
        }
        else
        {
            Coin coin = EnsureComponent<Coin>(go);
            coin.levelManager = levelManager;
            coin.value = 10;
        }
    }
    // ======================= ② 自动连接引用 =======================

    class SceneIndex
    {
        public PlayerController player;
        public PlayerInventory inventory;
        public PlayerGrab grab;
        public SlimeController slime;
        public SlimePathFollow pathFollow;
        public LevelManager levelManager;
        public GameManager gameManager;
        public WaypointMarker waypointPrefab;
        public Transform waypointContainer;
    }

    static List<MonoBehaviour> CollectBehaviours(Scene scene)
    {
        List<MonoBehaviour> list = new List<MonoBehaviour>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            MonoBehaviour[] comps = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour mb in comps)
            {
                // 只处理我们自己写的脚本（Assembly-CSharp），绝不碰 Unity / TMP / Cinemachine 的组件
                if (mb != null && mb.GetType().Assembly.GetName().Name == "Assembly-CSharp") list.Add(mb);
            }
        }
        return list;
    }

    static Transform FindTransformByNameInScene(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all)
            {
                if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        return null;
    }

    static T FindOne<T>(List<MonoBehaviour> all) where T : MonoBehaviour
    {
        T found = null;
        foreach (MonoBehaviour mb in all)
        {
            T t = mb as T;
            if (t == null) continue;
            if (found != null && found != t)
                Debug.LogWarning("[呆呆史莱姆] 场景里有多个 " + typeof(T).Name + "，只取第一个：" + found.name);
            if (found == null) found = t;
        }
        return found;
    }

    static Transform FindChildByName(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
        }
        return null;
    }

    /// <summary>把当前场景里所有空的引用字段尽最大努力填上。只填空的，绝不覆盖已有值。</summary>
    static int AutoWireActiveScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        List<MonoBehaviour> all = CollectBehaviours(scene);

        SceneIndex idx = new SceneIndex();
        idx.player = FindOne<PlayerController>(all);
        idx.inventory = FindOne<PlayerInventory>(all);
        idx.grab = FindOne<PlayerGrab>(all);
        idx.slime = FindOne<SlimeController>(all);
        idx.pathFollow = FindOne<SlimePathFollow>(all);
        idx.levelManager = FindOne<LevelManager>(all);
        idx.gameManager = FindOne<GameManager>(all);

        foreach (MonoBehaviour mb in all)
        {
            WaypointMarker wm = mb as WaypointMarker;
            if (wm != null) { idx.waypointPrefab = wm; break; }
        }
        idx.waypointContainer = FindTransformByNameInScene(scene, "WaypointContainer");

        int filled = 0;
        foreach (MonoBehaviour mb in all)
        {
            FieldInfo[] fields = mb.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo f in fields)
            {
                if (f.IsInitOnly) continue;
                if (!IsEmpty(f, mb)) continue;

                object value = SuggestValue(f, mb, idx);
                if (value == null) continue;

                f.SetValue(mb, value);
                EditorUtility.SetDirty(mb);
                if (PrefabUtility.IsPartOfPrefabInstance(mb))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(mb);
                filled++;
                Debug.Log("[呆呆史莱姆] " + mb.GetType().Name + "." + f.Name + "  <-  " + Describe(value));
            }
        }

        if (filled > 0 && !Application.isBatchMode) EditorSceneManager.MarkSceneDirty(scene);
        return filled;
    }

    static bool IsEmpty(FieldInfo f, MonoBehaviour owner)
    {
        object v = f.GetValue(owner);
        System.Type t = f.FieldType;

        if (t == typeof(LayerMask)) return ((LayerMask)v).value == 0;
        if (t == typeof(int)) return IsRepairableInt(f.Name.ToLowerInvariant()) && (int)v == 0;
        if (t.IsValueType) return false;             // 其它数值类型一律不碰
        if (t.IsArray || t.IsGenericType) return false;

        UnityEngine.Object uo = v as UnityEngine.Object;
        return uo == null;
    }

    /// <summary>只对这几个"配错了就直接玩不了"的整数做兜底，其它数值（伤害、速度等）绝不覆盖。</summary>
    static bool IsRepairableInt(string n)
    {
        // 只保留"0 真的等于没配置"的层号。
        // 曾经这里还有 levelindex / baseprice / maxwaypoints / currenthealth，都是错的：
        //   levelindex  —— 教程关合法值就是 0，被当成"没配置"改成了 1（踩过）
        //   baseprice   —— 字段已删除
        //   maxwaypoints—— 已改名 stoneCount，而且 0 = 一颗石头都不给，也是合法配置
        //   currenthealth —— 已改成 float，0 表示死亡，同样不该自动补
        return n == "slimelayer" || n == "carriedlayer";
    }

    static object SuggestValue(FieldInfo f, MonoBehaviour owner, SceneIndex idx)
    {
        System.Type t = f.FieldType;
        string n = f.Name.ToLowerInvariant();

        // ---- 组件引用 ----
        if (t == typeof(LevelManager)) return idx.levelManager;
        if (t == typeof(GameManager)) return idx.gameManager;
        if (t == typeof(PlayerController)) return idx.player;
        if (t == typeof(PlayerInventory)) return idx.inventory;
        if (t == typeof(PlayerGrab)) return idx.grab;
        if (t == typeof(SlimeController)) return idx.slime;
        if (t == typeof(SlimePathFollow)) return idx.pathFollow;
        if (t == typeof(WaypointMarker)) return idx.waypointPrefab;
        if (t == typeof(AudioSource)) return owner.GetComponent<AudioSource>();
        if (t == typeof(SpriteRenderer)) return owner.GetComponentInChildren<SpriteRenderer>(true);
        if (t == typeof(Rigidbody2D)) return owner.GetComponent<Rigidbody2D>();
        if (t == typeof(Collider2D)) return owner.GetComponent<Collider2D>();

        // ---- LayerMask ----
        if (t == typeof(LayerMask))
        {
            if (n.Contains("ground")) return MakeMask("Ground", "Platform", "MovingPlatform");
            if (n.Contains("wall")) return MakeMask("Ground");
            if (n.Contains("slime")) return MakeMask("Slime", "CarriedSlime");
            if (n.Contains("trigger")) return MakeMask("Player", "Slime", "CarriedSlime", "Enemy", "MovingPlatform");
            return null;
        }

        // ---- Transform ----
        if (t == typeof(Transform))
        {
            if (n.Contains("groundcheck"))
            {
                Transform c = FindChildByName(owner.transform, "GroundCheck");
                if (c != null) return c;
                if (idx.slime != null) return FindChildByName(idx.slime.transform, "GroundCheck");
                return null;
            }
            if (n.Contains("carrypoint"))
            {
                Transform c = FindChildByName(owner.transform, "CarryPoint");
                if (c != null) return c;
                if (idx.player != null) return FindChildByName(idx.player.transform, "CarryPoint");
                return null;
            }
            if (n.Contains("spriteroot")) return FindChildByName(owner.transform, "Sprite");
            if (n.Contains("startpoint")) return idx.player != null ? idx.player.transform : null;
            if (n.Contains("waypointcontainer")) return idx.waypointContainer;
            return null;   // respawnPoint / plateVisual / pulseTarget 等交给手动
        }

        // ---- 少数数值兜底 ----
        // ⚠ 这里的每一条都必须和 IsRepairableInt 的名单一致：IsEmpty 会先把不在名单里的 int 筛掉，
        //    所以不在这两个名字里的分支全是死代码。曾经这里还留着 maxwaypoints / levelindex /
        //    baseprice / currenthealth —— 其中 levelindex 会把教程关合法的 0 改成 1（踩过），
        //    已全部删除，避免以后有人把名字加回 IsRepairableInt 时又把 bug 带回来。
        if (t == typeof(int))
        {
            int v = (int)f.GetValue(owner);
            if (n == "slimelayer" && v == 0) return LayerMask.NameToLayer("Slime");
            if (n == "carriedlayer" && v == 0) return LayerMask.NameToLayer("CarriedSlime");
            return null;
        }

        return null;
    }

    static string Describe(object v)
    {
        UnityEngine.Object uo = v as UnityEngine.Object;
        if (uo != null) return uo.name + " (" + uo.GetType().Name + ")";
        return v.ToString();
    }

    // ======================= ⑥ 体检报告 =======================

    /// <summary>当前场景里有没有我们的玩家对象。</summary>
    public static bool ActiveSceneHasPlayer()
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<PlayerController>(true) != null) return true;
        }
        return false;
    }

    /// <summary>生成占位美术 + 路径点预制体，并挂到场景里所有 SlimePathFollow 上。</summary>
    public static void CreateWaypointPrefabAndAssign()
    {
        Sprite square = CreateSquareSprite();
        if (square == null) return;

        WaypointMarker prefab = CreateWaypointPrefab(square);
        if (prefab == null) return;

        Scene scene = SceneManager.GetActiveScene();
        int count = 0;
        foreach (MonoBehaviour mb in CollectBehaviours(scene))
        {
            SlimePathFollow spf = mb as SlimePathFollow;
            if (spf == null) continue;
            if (spf.waypointPrefab != null) continue;

            spf.waypointPrefab = prefab;
            EditorUtility.SetDirty(spf);
            if (PrefabUtility.IsPartOfPrefabInstance(spf))
                PrefabUtility.RecordPrefabInstancePropertyModifications(spf);
            count++;
        }
        Debug.Log("[呆呆史莱姆] 占位美术与路径点预制体已就绪，挂到了 " + count + " 个 SlimePathFollow 上");
    }

    /// <summary>
    /// 数值对齐：把史莱姆预制体的重力改成和玩家一致（这样它跳多高由玩家决定），
    /// 跟随速度也对齐到玩家移速，保证"玩家能跳过去的坑它也能跳过去"。
    /// 直接改预制体资产，Inspector 里看得见、能继续调。
    /// </summary>
    public static void AlignSlimeNumbers()
    {
        string[] candidates = { Root + "/Scenes/Slime.prefab", Root + "/Prefabs/Slime/Slime.prefab" };
        int done = 0;

        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Rigidbody2D rb = root.GetComponent<Rigidbody2D>();
                if (rb != null) rb.gravityScale = 3f;

                SlimeController sc = root.GetComponent<SlimeController>();
                if (sc != null) sc.followSpeed = 6.5f;

                PrefabUtility.SaveAsPrefabAsset(root, path);
                done++;
                Debug.Log("[呆呆史莱姆] 数值已对齐（重力 3 / 跟随速度 6.5）：" + path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        if (done == 0)
            Debug.LogWarning("[呆呆史莱姆] 没找到 Slime.prefab，跳过数值对齐");

        // ---- 玩家预制体：投掷力度。为了让"必须按 Q 投掷过坑"成立（坑宽 4 格）：
        //      抱着跳 2.45 格（过不去） < 坑宽 4 格 < 投掷 4.76 格（过得去） < 空手跳 5.87 格
        string[] playerPrefabs = { Root + "/Scenes/Player.prefab", Root + "/Prefabs/Player/Player.prefab" };
        foreach (string path in playerPrefabs)
        {
            if (!File.Exists(path)) continue;

            GameObject proot = PrefabUtility.LoadPrefabContents(path);
            try
            {
                PlayerGrab grab = proot.GetComponent<PlayerGrab>();
                if (grab != null)
                {
                    grab.throwForceForward = 7f;
                    grab.throwForceUp = 10f;
                }
                PrefabUtility.SaveAsPrefabAsset(proot, path);
                Debug.Log("[呆呆史莱姆] 投掷力度已对齐（前 7 / 上 10 → 投掷距离约 4.76 格）：" + path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(proot);
            }
        }
    }

    static void ReportEmptyReferences()
    {
        Scene scene = SceneManager.GetActiveScene();
        List<MonoBehaviour> all = CollectBehaviours(scene);
        List<string> problems = new List<string>();

        foreach (MonoBehaviour mb in all)
        {
            if (mb == null) continue;

            FieldInfo[] fields = mb.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo f in fields)
            {
                if (f.IsInitOnly) continue;
                System.Type t = f.FieldType;
                if (t.IsValueType || t.IsArray || t.IsGenericType) continue;

                bool isComponentRef = typeof(Component).IsAssignableFrom(t) || t == typeof(GameObject);
                if (!isComponentRef) continue;

                if (f.GetValue(mb) == null)
                    problems.Add("  · " + mb.gameObject.name + " → " + mb.GetType().Name + "." + f.Name + "  (" + t.Name + ")");
            }
        }

        if (problems.Count == 0)
        {
            Debug.Log("[呆呆史莱姆] 体检报告：场景「" + scene.name + "」的所有引用都连上了 ✓");
        }
        else
        {
            Debug.LogWarning("[呆呆史莱姆] 体检报告：场景「" + scene.name + "」还有 " + problems.Count +
                             " 个引用没连上（如果是不需要的功能可以忽略）：\n" + string.Join("\n", problems.ToArray()));
        }
    }
}
