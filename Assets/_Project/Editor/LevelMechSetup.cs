using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 第 4 步：机关测试关「LevelMech」。
///
/// 为什么单独做一关，而不是把机关塞进 Level0/1/2：
///   那三关刚刚确认好（第 2 步重做 + 求解器体检），往里加机关会同时动关卡和风险。
///   单独一关可以放开手做，也正好当"五类机关各用一次"的验收现场。
///
/// 这一关里有：
///   ① 压力板 A（踩一下就锁定）→ 门 1 永久打开        —— 联动 + 一次性
///   ② 悬空移动平台往返，载玩家 + 史莱姆过 6 格深坑    —— 载人
///   ③ 敌人巡逻（碰到玩家致死）+ 检查点复活            —— 危险与复活
///   ④ 压力板 B（**压住才开**）由移动平台周期性压住 → 门 2 按时开合 —— 时序机关
///      （玩家自己也能跳上高台压住板，但一离开门就关 —— 这就是"必须靠平台"的原因）
/// </summary>
public static class LevelMechSetup
{
    public const string LevelName = "LevelMech";
    public const string ScenePath = "Assets/_Project/Scenes/" + LevelName + ".unity";
    public const string TemplateScene = "Assets/_Project/Scenes/Level2.unity";
    public const string JsonPath = "Assets/_Project/Levels/" + LevelName + ".json";

    static int Q(float world) { return Mathf.RoundToInt(world / LevelUnits.Q); }

    // ======================= 关卡数据 =======================

    public static LevelData BuildMechData()
    {
        LevelData d = new LevelData();
        d.meta.levelId = LevelName;
        d.meta.displayName = "机关测试关";
        d.meta.parTime = 40f;
        d.meta.coinValue = 10;
        d.meta.startStones = 3;
        d.meta.levelIndex = 0;
        d.meta.saveProgress = false;          // 测试关不给星星，也不解锁下一关
        d.meta.useCameraBounds = true;
        // 视野矩形（ClampToBounds 会用 orthographicSize 内缩，所以这里是"取景框"不是"相机中心范围"）
        d.meta.camMinX = -2f; d.meta.camMaxX = 58f;
        d.meta.camMinY = -7f; d.meta.camMaxY = 6f;   // → 相机中心锁在 y = -0.5
        d.meta.cameraSize = 6.5f;
        d.meta.hasSpawn = true;
        d.meta.playerX = 2f; d.meta.playerY = 0.5f;
        d.meta.slimeX = 0.5f; d.meta.slimeY = 0.5f;

        // ---------------- 地形（qx,qy = 左下角；qw,qh = 尺寸；全部 1/4 格）----------------
        List<TerrainBlock> ts = new List<TerrainBlock>();
        AddGround(ts, "G1", 0f, 14f, 0f, 2f);            // 出生区
        AddGround(ts, "G_PitBed", 14f, 20f, -2.5f, 2f);  // 坑底（掉下去能跳出来）
        AddGround(ts, "G2", 20f, 56f, 0f, 2f);           // 主区
        AddGround(ts, "G_High", 27f, 33f, 2.5f, 1f);     // 压板 B 的高台（悬空 1.5，跳得上去）
        d.terrain = ts.ToArray();

        // ---------------- 物件 ----------------
        List<LevelObject> os = new List<LevelObject>();

        // ① 压力板 A（踩一次锁定）→ 门 1
        os.Add(Obj("plate_A", LevelObjectKind.PressurePlate, 6f, 0.25f, 2f, 0.3f,
            new float[] { 0f, 1f, 0f, 0.08f }));      // stayPressed, oneShot, activeWhenPressed, pressDepth
        os.Add(Obj("gate_1", LevelObjectKind.Gate, 12f, 2.25f, 1f, 4.5f));

        // ② 移动平台 1：往返跨坑（坑 14..20，宽 6 > 空手跳 5.87，跳不过去）
        //    平台顶面 = y0（与地面齐平）→ 中心 -0.25，厚 0.5
        LevelObject p1 = Obj("plat_1", LevelObjectKind.MovingPlatform, 17f, -0.25f, 2.5f, 0.5f,
            new float[] { 3f, 0.4f, 1f, 0f, 1f });     // speed, waitTime, startActivated, activatedByPlate, carryRider
        p1.pts = new int[] { Q(-3f), 0, Q(3f), 0 };    // 相对中心 ±3 格 → 世界 x 14 与 20
        os.Add(p1);

        // ④ 压力板 B（压住才开）+ 移动平台 2（在高台上方来回扫，周期性压住板 B）+ 门 2
        os.Add(Obj("plate_B", LevelObjectKind.PressurePlate, 30f, 2.75f, 2f, 0.5f,
            new float[] { 0f, 0f, 0f, 0.08f }));
        LevelObject p2 = Obj("plat_2", LevelObjectKind.MovingPlatform, 30f, 3f, 1.5f, 0.4f,
            new float[] { 2f, 0.5f, 1f, 0f, 1f });
        p2.pts = new int[] { Q(-3f), 0, Q(3f), 0 };    // 世界 x 27 与 33，正好扫过高台
        os.Add(p2);
        os.Add(Obj("gate_2", LevelObjectKind.Gate, 36f, 2.25f, 1f, 4.5f));

        // ③ 检查点 + 敌人
        os.Add(Obj("checkpoint_1", LevelObjectKind.Checkpoint, 39f, 1f, 1.5f, 2f,
            new float[] { 1f, 1f }));                  // forPlayer, forSlime
        os.Add(Obj("enemy_1", LevelObjectKind.Enemy, 46f, 0.5f, 0.9f, 0.9f,
            new float[] { 2f, 3f, 0.5f, 1f, 15f, 1f })); // speed, patrol, waitAtTurn, dir, damage, killPlayer

        // 终点（只有史莱姆算数 —— 碰撞矩阵里 Pickup × Player 是关的，金币同理）
        os.Add(Obj("goal_0", LevelObjectKind.Goal, 52f, 1f, 2f, 2f,
            new float[] { 1f, 0f, 1.2f }));            // onlySlime, requireNotCarried, cutscene

        // 几枚金币，让收益表有意义
        os.Add(Coin("coin_1", 8f, 0.5f));
        os.Add(Coin("coin_2", 24f, 0.5f));
        os.Add(Coin("coin_3", 43f, 0.5f));
        os.Add(Coin("coin_4", 50f, 0.5f));

        d.objects = os.ToArray();

        // ---------------- 联动 ----------------
        // CloseOnce = 触发一次后把目标【关掉】= 门永久打开
        // Toggle    = 压住时把目标关掉、松开还原   = 门压住才开
        d.links = new LevelLink[]
        {
            new LevelLink("plate_A", "gate_1", LinkMode.CloseOnce, 0f),
            new LevelLink("plate_B", "gate_2", LinkMode.Toggle, 0f),
        };
        d.murals = new Mural[0];
        return d;
    }

    static LevelObject Coin(string id, float x, float y)
    {
        LevelObject o = Obj(id, LevelObjectKind.Coin, x, y, 0.6f, 0.6f);
        o.p = new float[] { 10f };
        return o;
    }

    static LevelObject Obj(string id, LevelObjectKind kind, float x, float y, float sx, float sy, float[] p = null)
    {
        LevelObject o = new LevelObject();
        o.id = id; o.name = id; o.kind = kind;
        o.qx = Q(x); o.qy = Q(y);
        o.sizeX = sx; o.sizeY = sy;
        o.p = p ?? new float[0];
        o.pts = new int[0];
        return o;
    }

    static void AddGround(List<TerrainBlock> list, string name, float x0, float x1, float top, float thickness)
    {
        list.Add(new TerrainBlock(name, Q(x0), Q(top - thickness), Q(x1 - x0), Q(thickness), TerrainKind.Ground));
    }

    // ======================= 建场景 =======================

    /// <summary>
    /// 命令行入口（不要加 -quit 由它自己结束也无所谓，这个方法是同步的）：
    ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod LevelMechSetup.BatchCreateMechLevel
    /// </summary>
    [MenuItem("Tools/呆呆史莱姆/▷ 建机关测试关 LevelMech", false, 18)]
    public static void BatchCreateMechLevel()
    {
        // 1) 先写 JSON —— 数据是唯一真相源
        LevelData d = BuildMechData();
        d.EnsureNoNulls();
        Directory.CreateDirectory(Path.GetDirectoryName(JsonPath));
        File.WriteAllText(JsonPath, d.ToJson(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log("[机关关] 已写出 " + JsonPath);

        // 2) 场景模板：从 Level2 复制（它带着完整的运行时骨架：LevelManager / 玩家 / 史莱姆 / 相机 / Canvas）
        //    必须用 AssetDatabase.CopyAsset，直接拷文件会复制 GUID，Unity 会报资源冲突。
        if (!File.Exists(ScenePath))
        {
            if (!File.Exists(TemplateScene)) { Debug.LogError("[机关关] 找不到模板场景 " + TemplateScene); return; }
            if (!AssetDatabase.CopyAsset(TemplateScene, ScenePath))
            {
                Debug.LogError("[机关关] 复制模板场景失败：" + TemplateScene + " → " + ScenePath);
                return;
            }
            AssetDatabase.Refresh();
            Debug.Log("[机关关] 已从 " + TemplateScene + " 复制出场景模板 " + ScenePath);
        }

        // 3) 打开 → 修压力板掩码 → 用 JSON 重建内容 → 保存
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        LevelBuilder[] builders = Object.FindObjectsOfType<LevelBuilder>(true);
        foreach (LevelBuilder b in builders)
        {
            // 压力板必须能被 MovingPlatform 压到 —— 否则"平台扫过压板"永远不触发
            b.plateTriggerMask = LayerMask.GetMask("Player", "Slime", "CarriedSlime", "MovingPlatform", "Enemy");
            b.enemyGroundLayer = LayerMask.GetMask("Ground", "Platform", "MovingPlatform");
        }

        if (!LevelDataWindow.RebuildScene(scene, out string msg))
        {
            Debug.LogError("[机关关] 重建失败：" + msg);
            return;
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[机关关] ✅ 建好了：" + msg);
    }
}
