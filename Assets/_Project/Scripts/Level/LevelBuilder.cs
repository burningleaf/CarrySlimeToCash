// ---------------------------------------------------------------------------
// LevelBuilder.cs —— LevelData（数据）→ Unity 场景（对象）
//
// 运行时脚本：不引用 UnityEditor。Editor 面板与游戏内地图生成器共用这一份，
// 所以"从数据生成关卡"这件事永远只有一处实现。
//
// 配套：LevelData.cs（数据格式） / LevelDataWindow.cs（Editor 面板）
//
// 场景模板边界：
//   本脚本只生成【地形 + 物件 + 终点】。
//   玩家、史莱姆、相机、Canvas、各 Manager 属于"场景模板"，不归它管，
//   重建关卡时这些对象原封不动（所以它们的引用不会被重建打乱）。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class LevelBuilder : MonoBehaviour
{
    /// <summary>某一类物件的外观。数值都放在字段里，不写死在方法里。</summary>
    [Serializable]
    public class KindStyle
    {
        public Color color = Color.white;
        public float size = 0.5f;
        public int sortingOrder = 6;
        public bool trigger = true;

        public KindStyle() { }

        public KindStyle(Color color, float size, int sortingOrder, bool trigger)
        {
            this.color = color; this.size = size; this.sortingOrder = sortingOrder; this.trigger = trigger;
        }
    }

    // ======================= 外观 =======================

    [Header("精灵（拖进来；留空则用纯色方块/圆）")]
    public Sprite blockSprite;        // 地形方块
    public Sprite circleSprite;       // 圆形物件（金币/引导石/终点…）
    public Sprite[] muralSprites = new Sprite[0];

    [Header("地形外观")]
    public KindStyle ground = new KindStyle(new Color(0.42f, 0.44f, 0.50f), 1f, 0, false);
    public KindStyle oneWayPlatform = new KindStyle(new Color(0.62f, 0.52f, 0.40f), 1f, 1, false);
    public KindStyle water = new KindStyle(new Color(0.28f, 0.55f, 0.95f, 0.75f), 1f, 3, true);
    public KindStyle hazardTerrain = new KindStyle(new Color(0.90f, 0.28f, 0.30f), 1f, 4, true);
    public KindStyle decor = new KindStyle(new Color(0.30f, 0.32f, 0.38f, 0.55f), 1f, -2, false);

    [Header("物件外观")]
    public KindStyle coin = new KindStyle(new Color(1f, 0.84f, 0.25f), 0.6f, 6, true);
    public KindStyle slimeOrb = new KindStyle(new Color(0.35f, 0.90f, 0.45f), 0.6f, 6, true);
    public KindStyle waypoint = new KindStyle(new Color(0.30f, 1f, 1f), 0.35f, 8, true);
    public KindStyle goal = new KindStyle(new Color(1f, 0.85f, 0.30f), 2f, 5, true);
    public KindStyle plate = new KindStyle(new Color(0.85f, 0.75f, 0.35f), 1f, 4, true);
    public KindStyle movingPlatform = new KindStyle(new Color(0.55f, 0.70f, 0.95f), 2f, 2, false);
    public KindStyle gate = new KindStyle(new Color(0.70f, 0.60f, 0.45f), 1.6f, 4, false);
    public KindStyle checkpoint = new KindStyle(new Color(0.45f, 0.95f, 0.85f), 0.8f, 5, true);
    public KindStyle enemy = new KindStyle(new Color(0.90f, 0.35f, 0.75f), 0.9f, 5, false);

    // ======================= 引用（拖） =======================

    [Header("引用（拖场景里的对象）")]
    public LevelManager levelManager;
    public SlimeController slime;
    public SlimePathFollow pathFollow;
    public CameraFollow cameraFollow;
    public Transform player;
    [Tooltip("玩家物品栏：把本关的「发放哪些物品」（meta.itemGrants）写进去。\n" +
             "留空 = 这一步跳过（物品栏保持它自己的默认值），不会报错")]
    public PlayerInventory inventory;

    [Header("音效（拖 AudioClip；留空则该物件静音，不会报错）")]
    [Tooltip("金币被捡起时播放。生成金币时会挂一个 AudioSource 并把 clip 接到 Coin.pickupClip")]
    public AudioClip coinClip;
    [Tooltip("回血球被捡起时播放")]
    public AudioClip orbClip;
    [Tooltip("压力板被压下时播放")]
    public AudioClip platePressClip;
    [Tooltip("压力板弹回时播放")]
    public AudioClip plateReleaseClip;
    [Tooltip("检查点被激活时播放")]
    public AudioClip checkpointClip;
    [Tooltip("巡逻怪被击中时播放")]
    public AudioClip enemyHitClip;
    [Tooltip("史莱姆到达终点时播放")]
    public AudioClip goalClip;
    [Tooltip("史莱姆被水/尖刺伤到时播放（生成 Hazard 地形时会挂 AudioSource）")]
    public AudioClip hazardHitClip;

    [Header("物理（留空会自动取默认层）")]
    public LayerMask enemyGroundLayer;
    public LayerMask plateTriggerMask;
    public float enemyGravityScale = 3f;
    public float enemyMass = 1f;

    [Header("数值默认值（物件数据里没写时用）")]
    public int defaultCoinValue = 10;
    // ⚠ 下面三个伤害 / 回血量的口径 = **满条百分比**（不是血条点数）：
    //   血条总长是每关算出来的（LevelManager.BarPointsFor = 2 × parTime 点），
    //   所以 JSON 里的数字必须与"本条有多长"无关 —— 15 = 掉满条的 15%，25 = 回满条的 25%。
    //   换算只发生在 SlimeController.TakeDamage / Heal 里（PointsFromBarPercent），数值本身不许动。
    public int defaultOrbHeal = 25;
    public int defaultSpikeDamage = 15;
    public int defaultEnemyDamage = 15;

    [Header("壁画路牌（占位阶段）")]
    [Tooltip("勾选后：在牌面上写出这块牌子以后该画什么。美术做完后关掉即可，不用改数据")]
    public bool showMuralLabels = true;
    [Tooltip("占位文字用的字体（拖中文 SDF 字体：工程内 Art/UI 下的 `SourceHanSansSC-Medium SDF` —— " +
             "开源思源黑体，OFL 1.1）。资源名由 Editor 侧 SlimeDemoSetup.chineseSdfAssetName 决定，" +
             "换字体只改那边几个字段；本脚本是运行时脚本，所以这里只写说明、不引用那个 Editor 类。")]
    public TMP_FontAsset muralFont;
    public float muralFontSize = 0.42f;
    public Color muralBoardColor = new Color(0.16f, 0.14f, 0.25f, 0.92f);
    public Color muralPostColor = new Color(0.12f, 0.11f, 0.18f, 1f);
    public Color muralTextColor = new Color(1f, 1f, 1f, 0.95f);
    public int muralSortingOrder = -10;

    [Header("生成")]
    public string rootName = "__LevelRoot";
    public Transform levelParent;          // 留空 = 放在场景根
    public bool applyTags = true;          // Tag 不存在时会跳过，不会抛异常

    List<GameObject> _spawned = new List<GameObject>();
    Dictionary<string, GameObject> _byId = new Dictionary<string, GameObject>();

    public GameObject Root { get; private set; }
    public IList<GameObject> Spawned { get { return _spawned; } }

    // 注意：LayerMask.GetMask / LayerMask.NameToLayer 不能在字段初始化器或构造函数里调用
    // （Unity 会抛 "NameToLayer is not allowed to be called from a MonoBehaviour constructor"）。
    // 所以层掩码只能在这里补。Reset 在编辑器里 AddComponent 时自动调用；Awake 兜住运行时的 AddComponent。
    void Reset() { EnsureMasks(); }
    void Awake() { EnsureMasks(); EnsureCollections(); }

    void EnsureMasks()
    {
        if (enemyGroundLayer.value == 0) enemyGroundLayer = LayerMask.GetMask("Ground", "Platform", "MovingPlatform");
        // ⚠ 必须带上 MovingPlatform 和 Enemy：
        //   PressurePlate 的说明写着"玩家 / 史莱姆 / 怪物 / 移动平台压住即激活"，
        //   但碰撞矩阵里 MovingPlatform × TriggerZone 是开的，会被这个掩码在这里挡掉 ——
        //   于是"移动平台周期性压住压力板"这种经典时序机关**永远触发不了**（第 4 步实测踩到）。
        //   现有三关一个压力板都没有，所以改这里对它们是零影响。
        if (plateTriggerMask.value == 0)
            plateTriggerMask = LayerMask.GetMask("Player", "Slime", "CarriedSlime", "MovingPlatform", "Enemy");
    }

    void EnsureCollections()
    {
        if (_spawned == null) _spawned = new List<GameObject>();
        if (_byId == null) _byId = new Dictionary<string, GameObject>();
    }

    // ======================= 清空 =======================

    /// <summary>删除上次生成的整棵关卡树。场景模板对象不受影响。</summary>
    public void ClearLevel()
    {
        EnsureCollections();
        if (Root != null)
        {
            DestroySmart(Root);
            Root = null;
        }
        else
        {
            Transform existing = FindRoot(rootName);
            if (existing != null) DestroySmart(existing.gameObject);
        }
        _spawned.Clear();
        _byId.Clear();
    }

    Transform FindRoot(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (levelParent != null) return levelParent.Find(name);
        GameObject go = GameObject.Find(name);
        return go != null ? go.transform : null;
    }

    static void DestroySmart(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(o);
        else UnityEngine.Object.DestroyImmediate(o);
    }

    // ======================= 构建 =======================

    public GameObject Build(LevelData data)
    {
        if (data == null) { Debug.LogError("[LevelBuilder] data 为空"); return null; }
        data.EnsureNoNulls();
        EnsureMasks();
        EnsureCollections();
        ClearLevel();

        GameObject root = new GameObject(string.IsNullOrEmpty(rootName) ? "__LevelRoot" : rootName);
        if (levelParent != null) root.transform.SetParent(levelParent, false);
        Root = root;
        _byId.Clear();

        // 1) 地形
        for (int i = 0; i < data.terrain.Length; i++) BuildTerrain(data.terrain[i], i, root.transform);

        // 2) 物件（先把对象都建出来，再接线，避免顺序依赖）
        for (int i = 0; i < data.objects.Length; i++) BuildObject(data.objects[i], i, root.transform);

        // 3) 机关联动
        for (int i = 0; i < data.links.Length; i++) WireLink(data.links[i]);

        // 4) 壁画
        for (int i = 0; i < data.murals.Length; i++) BuildMural(data.murals[i], i, root.transform);

        // 5) 把关卡参数写进 LevelManager。
        //    结算公式的真相源在【数据】里而不是组件上 —— MonoBehaviour 的序列化字段会盖掉
        //    代码默认值，改代码对已存在的场景毫无影响（这个坑踩过两次）。
        //    ⚠ 每加一个这里写入的值，都要在 LevelDataWindow.ExportScene 里读回来，
        //      否则下一次导出会把它重置成默认值。
        defaultCoinValue = data.meta.coinValue;
        if (levelManager != null)
        {
            levelManager.parTime = data.meta.parTime;
            levelManager.barMaxOverride = data.meta.barMaxOverride;
            if (data.meta.starRatios != null && data.meta.starRatios.Length >= 3)
                levelManager.starRatios = (float[])data.meta.starRatios.Clone();
            // 注意：售价条满格 / 掉血速率 / 金币目标 都是【算出来的】，不写进组件，
            // 免得和全局常数（每秒掉 5 元）不同步。

            int total = 0;
            for (int i = 0; i < data.objects.Length; i++)
            {
                LevelObject o = data.objects[i];
                if (o == null || o.kind != LevelObjectKind.Coin) continue;
                total += Mathf.RoundToInt(o.Param(0, defaultCoinValue));
            }
            levelManager.coinTotalInLevel = total;
        }

        // 5.5) 史莱姆的血条点数 = LevelManager.BarPointsFor(parTime)（= 2 × parTime 点）。
        //      史莱姆得先拿得到 levelManager 才推得出点数（点数的唯一真相源在 LevelManager，不在
        //      场景/预制体上序列化的 maxHealth）⇒ 这里补一次引用。幂等：已经拖好就不动。
        //      注意 AutoWire（SlimeDemoSetup）也会按类型接这一格，这里只是"接线晚一步"的兜底。
        if (slime != null && levelManager != null && slime.levelManager == null)
            slime.levelManager = levelManager;

        // 6) 引导石数量：数据说了算（各关数据现在都是 3，所以表现就是"每关开局三颗"）
        //    ⚠ pathFollow 在关卡场景里是 Slime.prefab 的【实例组件】：这里只写内存里的值，
        //      必须由编辑器侧的 LevelDataWindow.RebuildScene 登记 prefab 覆盖才会落盘
        //      （内部问题跟踪里记过的 stoneCount 坑就是漏了这一步，表现是"改 JSON 不生效"）。
        if (pathFollow != null)
            pathFollow.stoneCount = Mathf.Max(0, data.meta.startStones);

        // 7) 关卡编号与存档开关（教程关 levelIndex = 0、saveProgress = false）
        if (levelManager != null)
        {
            levelManager.levelIndex = data.meta.levelIndex;
            levelManager.saveProgress = data.meta.saveProgress;
        }

        // 8) 相机取景（这是关卡数据，不该留在场景模板里）
        //    ⚠ 空判据必须单独起一行：原来这行写成「注释……  if (cameraFollow != null)」，
        //      if 被注释吞掉 ⇒ cameraFollow 为空时下面整块照样执行，第一行就 NRE。
        if (cameraFollow != null)
        {
            cameraFollow.useBounds = data.meta.useCameraBounds;
            cameraFollow.boundsMin = new Vector2(data.meta.camMinX, data.meta.camMinY);
            cameraFollow.boundsMax = new Vector2(data.meta.camMaxX, data.meta.camMaxY);
            Camera cam = cameraFollow.GetComponent<Camera>();
            if (cam != null && data.meta.cameraSize > 0.1f)
            {
                cam.orthographic = true;
                cam.orthographicSize = data.meta.cameraSize;
            }

            // 自动缩放：把史莱姆接成相机的【第二目标】—— 于是"两人分开时自动拉远"是【每一关】都有的普适机制，
            // 不用逐场景手动拖。（CameraFollow 在场景里是普通对象、不是 prefab 实例 ⇒ 写它的字段不会遇到
            // "prefab override 未记录被丢弃"的问题；这里赋的值是场景里那个史莱姆对象的 Transform。）
            // 场景模板没拖史莱姆（理论上不存在）就保持原样 = 自动缩放关闭，行为与以前完全一样。
            if (slime != null) cameraFollow.secondTarget = slime.transform;
        }

        // 9) 本关发放哪些物品：数据说了算（索引 = 槽位序号 = ItemType 值）。
        //    ⚠ 这里写进组件的值，LevelDataWindow.ExportScene 必须读回来（铁律：写进去的要读回来），
        //      否则下一次导出会把它重置成默认值 = "四件全给"。
        //    ⚠ 而且关卡场景里的 PlayerInventory 是 Player.prefab 的【实例组件】（stripped）：
        //      它的落盘值 = prefab 资产值 + 该实例的 m_Modifications 覆盖表。代码直接改字段绕过了
        //      Inspector 的 SerializedObject 通道，不进覆盖表 ⇒ SaveScene 时被静默丢弃
        //      （这就是 stoneCount 那类同款坑，也早有预警："以后再加一条实例写入就会踩"）。
        //      登记覆盖那一步必须调 PrefabUtility.RecordPrefabInstancePropertyModifications，
        //      而本文件是【运行时脚本、不引用 UnityEditor】⇒ 放在编辑器侧的
        //      LevelDataWindow.RebuildScene（那里已经按类型补好了 inventory 引用）。
        //    用 Clone：别把数据里的数组和组件共享同一个引用（面板一改数据，场景组件跟着变）。
        if (inventory != null && data.meta.itemGrants != null)
            inventory.itemGrants = (bool[])data.meta.itemGrants.Clone();

        return root;
    }

    /// <summary>把场景模板里的玩家/史莱姆搬到出生点。数据里没标 hasSpawn 就不动。</summary>
    public void PlaceSpawns(LevelData data)
    {
        if (data == null || data.meta == null || !data.meta.hasSpawn) return;
        Vector2 p = data.meta.PlayerSpawn;
        Vector2 s = data.meta.SlimeSpawn;
        if (player != null) player.position = new Vector3(p.x, p.y, player.position.z);
        if (slime != null) slime.transform.position = new Vector3(s.x, s.y, slime.transform.position.z);
    }

    // ======================= 地形 =======================

    void BuildTerrain(TerrainBlock b, int index, Transform parent)
    {
        if (b == null) return;
        KindStyle style = StyleFor(b.kind);

        GameObject go = new GameObject(string.IsNullOrEmpty(b.name) ? "Terrain_" + index : b.name);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(b.CenterX, b.CenterY, 0f);
        go.transform.localScale = new Vector3(b.Width, b.Height, 1f);
        go.layer = LayerId(LayerForTerrain(b.kind));

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = blockSprite;
        sr.color = style.color;
        sr.sortingOrder = style.sortingOrder;

        if (b.kind == TerrainKind.Decor) { Track(go); return; }

        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;     // 跟着 localScale 放大，正好覆盖整块
        box.isTrigger = style.trigger;

        if (b.kind == TerrainKind.Water || b.kind == TerrainKind.Hazard)
        {
            Hazard h = go.AddComponent<Hazard>();
            h.hazardType = (b.kind == TerrainKind.Water) ? HazardType.Water : HazardType.Spike;
            h.damage = Mathf.RoundToInt(b.Param(0, b.kind == TerrainKind.Water ? 0f : defaultSpikeDamage));
            h.killPlayer = b.kind == TerrainKind.Hazard && b.Param(1, 1f) > 0.5f;
            h.damageOnce = b.Param(2, 0f) > 0.5f;
            h.fatalToPlayer = b.Param(3, 0f) > 0.5f;   // 深坑底部的死亡判定块用 true
            h.levelManager = levelManager;
            // 水和尖刺也要能出声 —— 不接的话"从 JSON 重建关卡"之后这批 Hazard 会静音
            // （场景里现存的 Hazard 会被 AssetWiring 接上，但重建出来的是新对象）
            h.audioSource = AttachAudio(go);
            h.hitClip = hazardHitClip;
        }
        else if (b.kind == TerrainKind.OneWayPlatform)
        {
            PlatformEffector2D eff = go.AddComponent<PlatformEffector2D>();
            eff.useOneWay = true;
            eff.useColliderMask = false;
            box.usedByEffector = true;
        }

        Track(go);
    }

    static KindStyle StyleOf(LevelBuilder b, TerrainKind k)
    {
        switch (k)
        {
            case TerrainKind.OneWayPlatform: return b.oneWayPlatform;
            case TerrainKind.Water: return b.water;
            case TerrainKind.Hazard: return b.hazardTerrain;
            case TerrainKind.Decor: return b.decor;
            default: return b.ground;
        }
    }

    KindStyle StyleFor(TerrainKind k) { return StyleOf(this, k); }

    static string LayerForTerrain(TerrainKind k)
    {
        switch (k)
        {
            case TerrainKind.OneWayPlatform: return "Platform";
            case TerrainKind.Water:
            case TerrainKind.Hazard: return "Hazard";
            case TerrainKind.Decor: return "Default";
            default: return "Ground";
        }
    }

    // ======================= 物件 =======================

    void BuildObject(LevelObject o, int index, Transform parent)
    {
        if (o == null) return;
        string goName = !string.IsNullOrEmpty(o.name) ? o.name
                      : (!string.IsNullOrEmpty(o.id) ? o.id : o.kind + "_" + index);

        GameObject go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(o.Center.x, o.Center.y, 0f);

        switch (o.kind)
        {
            case LevelObjectKind.Coin: BuildCoin(o, go); break;
            case LevelObjectKind.SlimeOrb: BuildOrb(o, go); break;
            case LevelObjectKind.Waypoint: BuildWaypoint(o, go); break;
            case LevelObjectKind.Goal: BuildGoal(o, go); break;
            case LevelObjectKind.PressurePlate: BuildPlate(o, go); break;
            case LevelObjectKind.MovingPlatform: BuildMovingPlatform(o, go); break;
            case LevelObjectKind.Gate: BuildGate(o, go); break;
            case LevelObjectKind.Checkpoint: BuildCheckpoint(o, go); break;
            case LevelObjectKind.Enemy: BuildEnemy(o, go); break;
            default:
                Debug.LogWarning("[LevelBuilder] 未知物件种类：" + o.kind + "（" + goName + "）");
                break;
        }

        Track(go);
        if (!string.IsNullOrEmpty(o.id)) _byId[o.id] = go;
    }

    Vector2 ObjectSize(LevelObject o, KindStyle style)
    {
        if (o.sizeX > 0.001f && o.sizeY > 0.001f) return new Vector2(o.sizeX, o.sizeY);
        return new Vector2(style.size, style.size);
    }

    void AddSprite(GameObject go, KindStyle style, Sprite sprite, string childName = null, int orderOverride = int.MinValue)
    {
        GameObject target = go;
        if (!string.IsNullOrEmpty(childName))
        {
            target = new GameObject(childName);
            target.transform.SetParent(go.transform, false);
            target.transform.localPosition = Vector3.zero;
        }
        SpriteRenderer sr = target.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = style.color;
        sr.sortingOrder = orderOverride == int.MinValue ? style.sortingOrder : orderOverride;
    }

    void BuildCoin(LevelObject o, GameObject go)
    {
        go.layer = LayerId("Pickup");
        Vector2 size = ObjectSize(o, coin);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, coin, circleSprite);
        CircleCollider2D col = go.AddComponent<CircleCollider2D>();
        col.radius = 0.5f;
        col.isTrigger = coin.trigger;

        Coin c = go.AddComponent<Coin>();
        c.value = Mathf.RoundToInt(o.Param(0, defaultCoinValue));
        c.levelManager = levelManager;
        c.audioSource = AttachAudio(go);
        c.pickupClip = coinClip;
        SetTag(go, "Coin");
    }

    void BuildOrb(LevelObject o, GameObject go)
    {
        go.layer = LayerId("Pickup");
        Vector2 size = ObjectSize(o, slimeOrb);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, slimeOrb, circleSprite);
        CircleCollider2D col = go.AddComponent<CircleCollider2D>();
        col.radius = 0.5f;
        col.isTrigger = slimeOrb.trigger;

        SlimeOrb orb = go.AddComponent<SlimeOrb>();
        orb.healAmount = Mathf.RoundToInt(o.Param(0, defaultOrbHeal));
        orb.allowOverheal = o.Param(1, 0f) > 0.5f;
        orb.slime = slime;
        orb.audioSource = AttachAudio(go);
        orb.pickupClip = orbClip;
        SetTag(go, "SlimeOrb");
    }

    void BuildWaypoint(LevelObject o, GameObject go)
    {
        go.layer = LayerId("Default");
        WaypointMarker m = go.AddComponent<WaypointMarker>();
        GameObject vis = new GameObject("Visual");
        vis.transform.SetParent(go.transform, false);
        vis.transform.localPosition = Vector3.zero;
        vis.transform.localScale = new Vector3(waypoint.size, waypoint.size, 1f);
        SpriteRenderer sr = vis.AddComponent<SpriteRenderer>();
        sr.sprite = circleSprite;
        sr.color = waypoint.color;
        sr.sortingOrder = waypoint.sortingOrder;
        m.visualRoot = vis.transform;
        m.spriteRenderer = sr;
    }

    void BuildGoal(LevelObject o, GameObject go)
    {
        go.layer = LayerId("TriggerZone");
        Vector2 size = ObjectSize(o, goal);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);

        // 精灵放在子对象上：Goal.pulseTarget / visualRoot 要一个可以被缩放的目标，
        // 直接缩放根对象会把碰撞体一起放大。
        GameObject vis = new GameObject("Visual");
        vis.transform.SetParent(go.transform, false);
        SpriteRenderer sr = vis.AddComponent<SpriteRenderer>();
        sr.sprite = circleSprite;
        sr.color = goal.color;
        sr.sortingOrder = goal.sortingOrder;

        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = goal.trigger;

        Goal g = go.AddComponent<Goal>();
        if (levelManager != null) g.levelManager = levelManager;
        g.onlySlime = o.Param(0, 1f) > 0.5f;
        g.requireSlimeNotCarried = o.Param(1, 0f) > 0.5f;
        g.cutsceneDuration = o.Param(2, 0f);
        g.visualRoot = vis.transform;
        g.pulseTarget = vis.transform;
        g.audioSource = AttachAudio(go);
        g.arriveClip = goalClip;
        Track(vis);
        SetTag(go, "Goal");
    }

    void BuildPlate(LevelObject o, GameObject go)
    {
        go.layer = LayerId("TriggerZone");
        Vector2 size = ObjectSize(o, plate);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, plate, blockSprite);
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = plate.trigger;

        PressurePlate p = go.AddComponent<PressurePlate>();
        p.platforms = new MovingPlatform[0];
        p.toggleObjects = new GameObject[0];
        p.triggerMask = plateTriggerMask;
        p.stayPressed = o.Param(0, 0f) > 0.5f;
        p.oneShot = o.Param(1, 0f) > 0.5f;
        p.objectsActiveWhenPressed = o.Param(2, 0f) > 0.5f;
        p.pressDepth = o.Param(3, 0.08f);
        GameObject vis = new GameObject("PlateVisual");
        vis.transform.SetParent(go.transform, false);
        vis.transform.localPosition = Vector3.zero;
        vis.transform.localScale = Vector3.one;
        SpriteRenderer sr = vis.AddComponent<SpriteRenderer>();
        sr.sprite = blockSprite;
        sr.color = plate.color;
        sr.sortingOrder = plate.sortingOrder;
        p.plateVisual = vis.transform;
        p.audioSource = AttachAudio(go);
        p.pressClip = platePressClip;
        p.releaseClip = plateReleaseClip;
        SetTag(go, "PressurePlate");
    }

    void BuildMovingPlatform(LevelObject o, GameObject go)
    {
        go.layer = LayerId("MovingPlatform");
        Vector2 size = ObjectSize(o, movingPlatform);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, movingPlatform, blockSprite);
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = movingPlatform.trigger;

        Rigidbody2D body = go.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Kinematic;
        body.gravityScale = 0f;
        body.freezeRotation = true;

        MovingPlatform mp = go.AddComponent<MovingPlatform>();
        mp.body = body;
        mp.moveMode = MoveMode.PingPong;
        mp.speed = o.Param(0, 2f);
        mp.waitTime = o.Param(1, 0.5f);
        mp.startActivated = o.Param(2, 1f) > 0.5f;
        mp.activatedByPlate = o.Param(3, 0f) > 0.5f;
        mp.carryRider = o.Param(4, 1f) > 0.5f;

        List<Transform> points = new List<Transform>();
        for (int i = 0; i < o.PointCount; i++)
        {
            GameObject pt = new GameObject("Point_" + i);
            // ⚠ 路点**不能**挂在平台底下！
            //   挂在平台下面，路点就跟着平台一起跑 —— 平台永远追不上自己的目标点，
            //   它会一路飞出去停不下来（第 4 步的机关测试实测踩到：本该停在 x33，实际飞到 x43 还在跑）。
            //   所以路点挂在平台的**父节点**上，位置固定在世界坐标里。
            pt.transform.SetParent(go.transform.parent, true);
            pt.transform.position = go.transform.position + (Vector3)o.PointOffset(i);
            pt.transform.localScale = Vector3.one;
            points.Add(pt.transform);
            Track(pt);
        }
        mp.points = points.ToArray();
        SetTag(go, "MovingPlatform");
    }

    void BuildGate(LevelObject o, GameObject go)
    {
        go.layer = LayerId("Gate");
        Vector2 size = ObjectSize(o, gate);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, gate, blockSprite);
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = false;
        SetTag(go, "Gate");
    }

    void BuildCheckpoint(LevelObject o, GameObject go)
    {
        go.layer = LayerId("TriggerZone");
        Vector2 size = ObjectSize(o, checkpoint);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, checkpoint, circleSprite);
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = checkpoint.trigger;

        Checkpoint cp = go.AddComponent<Checkpoint>();
        cp.levelManager = levelManager;
        cp.forPlayer = o.Param(0, 1f) > 0.5f;
        cp.forSlime = o.Param(1, 1f) > 0.5f;
        Transform respawn = new GameObject("RespawnPoint").transform;
        respawn.SetParent(go.transform, false);
        respawn.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
        cp.respawnPoint = respawn;
        cp.audioSource = AttachAudio(go);
        cp.activateClip = checkpointClip;
        Track(respawn.gameObject);
        SetTag(go, "Checkpoint");
    }

    void BuildEnemy(LevelObject o, GameObject go)
    {
        go.layer = LayerId("Enemy");
        Vector2 size = ObjectSize(o, enemy);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        AddSprite(go, enemy, circleSprite, "Visual");
        BoxCollider2D box = go.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.isTrigger = false;

        Rigidbody2D body = go.AddComponent<Rigidbody2D>();
        body.gravityScale = enemyGravityScale;
        body.freezeRotation = true;
        body.mass = enemyMass;

        GameObject checkGo = new GameObject("GroundCheck");
        checkGo.transform.SetParent(go.transform, false);
        checkGo.transform.localPosition = new Vector3(0f, -0.5f, 0f);

        Enemy e = go.AddComponent<Enemy>();
        e.body = body;
        e.bodyCollider = box;
        e.spriteRoot = go.transform.Find("Visual");
        e.groundCheck = checkGo.transform;
        e.levelManager = levelManager;
        e.groundLayer = enemyGroundLayer;
        e.patrolMode = PatrolMode.Distance;
        e.moveSpeed = o.Param(0, 2.5f);
        e.patrolDistance = o.Param(1, 3f);
        e.waitAtTurnTime = o.Param(2, 0.5f);
        e.startDirection = o.Param(3, 1f);
        e.contactDamage = Mathf.RoundToInt(o.Param(4, defaultEnemyDamage));
        e.killPlayer = o.Param(5, 1f) > 0.5f;
        e.audioSource = AttachAudio(go);
        e.hitClip = enemyHitClip;
        Track(checkGo);
        SetTag(go, "Enemy");
    }

    void BuildMural(Mural m, int index, Transform parent)
    {
        if (m == null) return;
        string code = string.IsNullOrEmpty(m.muralId) ? ("M" + index) : m.muralId;

        GameObject go = new GameObject("Mural_" + code);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(m.Center.x, m.Center.y, 0f);
        go.layer = LayerId("Default");

        // 支柱
        GameObject post = new GameObject("Post");
        post.transform.SetParent(go.transform, false);
        post.transform.localPosition = new Vector3(0f, -(m.sizeY * 0.5f + 0.6f), 0f);
        post.transform.localScale = new Vector3(0.12f, 1.2f, 1f);
        SpriteRenderer psr = post.AddComponent<SpriteRenderer>();
        psr.sprite = blockSprite;
        psr.color = muralPostColor;
        psr.sortingOrder = muralSortingOrder;

        // 牌面
        GameObject board = new GameObject("Board");
        board.transform.SetParent(go.transform, false);
        board.transform.localScale = new Vector3(Mathf.Max(0.2f, m.sizeX), Mathf.Max(0.2f, m.sizeY), 1f);
        SpriteRenderer bsr = board.AddComponent<SpriteRenderer>();
        bsr.sprite = blockSprite;
        bsr.color = muralBoardColor;
        bsr.sortingOrder = muralSortingOrder + 1;

        // 正式版：象形图；占位阶段：直接把"该画什么"写在牌面上
        if (muralSprites != null && m.sprIndex >= 0 && m.sprIndex < muralSprites.Length && muralSprites[m.sprIndex] != null)
        {
            GameObject icon = new GameObject("Icon");
            icon.transform.SetParent(go.transform, false);
            SpriteRenderer isr = icon.AddComponent<SpriteRenderer>();
            isr.sprite = muralSprites[m.sprIndex];
            isr.color = Color.white;
            isr.sortingOrder = muralSortingOrder + 2;
        }
        else if (showMuralLabels)
        {
            GameObject txt = new GameObject("Label");
            txt.transform.SetParent(go.transform, false);
            txt.transform.localPosition = Vector3.zero;
            txt.transform.localScale = Vector3.one;
            TextMeshPro t = txt.AddComponent<TextMeshPro>();
            if (muralFont != null) t.font = muralFont;
            t.text = string.IsNullOrEmpty(m.label) ? code : m.label;
            t.fontSize = muralFontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.color = muralTextColor;
            t.sortingOrder = muralSortingOrder + 2;
            Track(txt);
        }

        Track(go);
        Track(post);
        Track(board);
    }

    // ======================= 机关联动 =======================

    void WireLink(LevelLink link)
    {
        if (link == null) return;
        GameObject srcGo, dstGo;
        if (!_byId.TryGetValue(link.sourceId ?? "", out srcGo)) { Debug.LogWarning("[LevelBuilder] 联动源找不到：" + link.sourceId); return; }
        if (!_byId.TryGetValue(link.targetId ?? "", out dstGo)) { Debug.LogWarning("[LevelBuilder] 联动目标找不到：" + link.targetId); return; }

        PressurePlate plate = srcGo.GetComponent<PressurePlate>();
        if (plate == null) { Debug.LogWarning("[LevelBuilder] 联动源不是压力板：" + link.sourceId); return; }

        MovingPlatform mp = dstGo.GetComponent<MovingPlatform>();
        if (mp != null)
        {
            List<MovingPlatform> list = new List<MovingPlatform>(plate.platforms ?? new MovingPlatform[0]);
            if (!list.Contains(mp)) list.Add(mp);
            plate.platforms = list.ToArray();
            mp.activatedByPlate = true;
        }
        else
        {
            List<GameObject> list = new List<GameObject>(plate.toggleObjects ?? new GameObject[0]);
            if (!list.Contains(dstGo)) list.Add(dstGo);
            plate.toggleObjects = list.ToArray();
        }

        ApplyLinkMode(plate, link.mode);

        if (link.delay > 0.001f)
            Debug.LogWarning("[LevelBuilder] 联动延时 " + link.delay + "s 暂未实现（PressurePlate 没有对应字段），已忽略：" +
                             link.sourceId + " → " + link.targetId);
    }

    static void ApplyLinkMode(PressurePlate plate, LinkMode mode)
    {
        switch (mode)
        {
            case LinkMode.OpenOnce:
                plate.oneShot = true; plate.stayPressed = false; plate.objectsActiveWhenPressed = true;
                break;
            case LinkMode.CloseOnce:
                plate.oneShot = true; plate.stayPressed = false; plate.objectsActiveWhenPressed = false;
                break;
            case LinkMode.Reverse:
                plate.oneShot = false; plate.stayPressed = false; plate.objectsActiveWhenPressed = true;
                break;
            default: // Toggle
                plate.oneShot = false; plate.stayPressed = false; plate.objectsActiveWhenPressed = false;
                break;
        }
    }

    // ======================= 小工具 =======================

    void Track(GameObject go)
    {
        if (go != null && !_spawned.Contains(go)) _spawned.Add(go);
    }

    int LayerId(string layerName)
    {
        int id = LayerMask.NameToLayer(layerName);
        return id < 0 ? 0 : id;
    }

    void SetTag(GameObject go, string tag)
    {
        if (!applyTags || go == null || string.IsNullOrEmpty(tag)) return;
        try { go.tag = tag; }
        catch (UnityException) { /* 工程里没这个 Tag，跳过 */ }
    }

    /// <summary>
    /// 给生成出来的物件挂一个纯 2D 音效播放器。已经有了就复用（绝不重复加）。
    /// clip 不写进 AudioSource.clip —— 统一由各脚本的 PlayClip(clip) 走 PlayOneShot，
    /// 这样压力板那种"一个源两个 clip"（按下 / 弹回）的物件才不会被互相打断。
    /// 音效资源由 AssetWiring（Editor）按路径写进上面的 clip 字段。
    /// </summary>
    static AudioSource AttachAudio(GameObject go)
    {
        if (go == null) return null;
        AudioSource src = go.GetComponent<AudioSource>();
        if (src == null) src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;      // 0 = 2D，横版游戏不需要衰减
        return src;
    }
}
