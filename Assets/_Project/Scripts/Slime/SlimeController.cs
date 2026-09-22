using UnityEngine;

/// <summary>史莱姆状态。Follow / Stay 是玩家可切换的模式，其余是过程状态。</summary>
public enum SlimeState
{
    Follow = 0,   // 常规跟随（有路径点时沿路径点走）
    Stay = 1,     // 静止等待
    Scared = 2,   // 受击后的短暂惊吓
    Carried = 3,  // 被玩家抱着
    Thrown = 4,   // 被投掷中
    Dead = 5,     // 死亡（关卡失败）
    Resting = 6   // 投掷落地后的短暂静止（给玩家时间用哨子切换模式）
}

/// <summary>
/// 职责：史莱姆核心。enum + switch 状态机、血量与无敌帧、贴地蠕动、0.1~0.2s 延迟跟随、0.15~0.25s 跟跳、障碍卡住检测。
/// Inspector：拖 Rigidbody2D、Collider2D、PlayerController、SlimePathFollow、LevelManager、GroundCheck 空物体、SpriteRenderer、Animator、AudioSource。
/// 依赖：PlayerController（只读位置 / 在地面 / 起跳时间戳 / 跳跃高度）、SlimePathFollow、LevelManager（死亡汇报）。
/// 禁止：不自主跳跃（只跟跳）、不寻路、不绕路、不传送、不攀爬。
/// 动画参数名：Speed(float)、IsGrounded(bool)、IsCarried(bool)、IsScared(bool)、IsDead(bool)。
/// </summary>
public class SlimeController : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public Rigidbody2D body;
    public Collider2D bodyCollider;
    public PlayerController player;
    public SlimePathFollow pathFollow;
    public LevelManager levelManager;
    [Tooltip("脚底的地面检测点，留空则用碰撞体底部")]
    public Transform groundCheck;
    [Tooltip("贴图节点，用于蠕动挤压表现，可空")]
    public Transform spriteRoot;
    public SpriteRenderer spriteRenderer;
    public Animator animator;
    public AudioSource audioSource;

    [Header("血量（= 售价，会随时间下降）")]
    // 必须用 float：掉血速率可能是 0.6 血/秒这种小数，用 int 会永远掉不动。
    public float maxHealth = 100f;
    public float currentHealth = 100f;
    [Tooltip("受击后的无敌帧时长，避免连续掉血")]
    public float invincibleTime = 1f;
    [Tooltip("惊吓状态持续时间，结束后回到之前的模式")]
    public float scaredDuration = 0.8f;

    [Header("跟随")]
    [Tooltip("常规跟随速度，接近玩家移速")]
    public float followSpeed = 6f;
    [Tooltip("跟随时保持的距离")]
    public float followDistance = 1.4f;
    [Tooltip("移动延迟，0.1~0.2 秒")]
    public float followDelay = 0.15f;
    [Tooltip("哨子召回时的速度倍率（仍然是直线蠕动，不寻路）")]
    public float recallSpeedMultiplier = 1.4f;
    [Tooltip("位置历史缓冲长度")]
    public int historySize = 128;

    [Header("跟跳（延迟 0.15~0.25 秒）")]
    public float jumpFollowDelay = 0.2f;
    [Tooltip("超过这个时间就不再跟那次跳跃")]
    public float jumpFollowWindow = 0.6f;
    [Tooltip("跟跳高度 = 玩家当前跳跃高度 × 该比例，仅在无法取到玩家重力时作为兜底")]
    public float jumpHeightRatio = 1f;
    [Tooltip("跟跳滞空时间倍率：1 = 和玩家跳一样久，所以能跳过同样的坑。<=0 视为 1")]
    public float jumpAirTimeMultiplier = 1f;

    [Header("障碍判定")]
    [Tooltip("能自己迈过去的最大台阶高度，超过就卡住等玩家")]
    public float maxStepHeight = 0.35f;
    [Tooltip("迈小台阶时的向上微调速度（不是攀爬）")]
    public float stepAssistSpeed = 3f;
    public float wallCheckDistance = 0.45f;
    public float ledgeCheckForward = 0.45f;
    public float ledgeCheckDistance = 0.9f;
    [Tooltip("是否启用边缘判定。开启后：下方有地面就走下去，只有无底坑才停下")]
    public bool stopAtLedge = true;
    [Tooltip("边缘下方多深以内算\"可以走下去\"。调大 = 见边就下（跟着人物下悬崖）；调小 = 只在矮台阶下")]
    public float maxDropHeight = 8f;
    [Tooltip("水平加速度：越大越干脆，越小越丝滑")]
    public float acceleration = 45f;
    public LayerMask groundLayer;
    public float groundCheckRadius = 0.18f;

    [Header("掉出世界的处理（防卡死）")]
    [Tooltip("低于这个高度就认为掉出了地图")]
    public float fallDeathY = -25f;
    [Tooltip("true = 送回最后一次站稳的位置；false = 判定死亡（关卡失败并自动重开）")]
    public bool recoverFromFall = true;

    [Header("投掷落地静止")]
    [Tooltip("投掷落地后保持静止的时长，给玩家时间用哨子切换模式；期间按哨子可立即接管")]
    public float thrownRestTime = 1.5f;

    [Header("层级（携带时切层，实现免伤）")]
    public int slimeLayer = 7;
    public int carriedLayer = 8;

    [Header("携带表现")]
    public float carryFollowSharpness = 25f;
    [Tooltip("距离挂点超过该值直接吸附（复活等瞬移场景）")]
    public float carrySnapDistance = 2.5f;

    [Header("蠕动表现")]
    public float squashAmount = 0.15f;
    public float wobbleSpeed = 6f;
    [Tooltip("被玩家举着时，Sprite 上下晃动的幅度")]
    public float carriedSwayAmplitude = 0.08f;
    [Tooltip("被玩家举着时，Sprite 上下晃动的速度")]
    public float carriedSwaySpeed = 3f;
    public bool flashOnDamage = true;
    public Color hurtFlashColor = new Color(1f, 0.4f, 0.4f);
    public float flashSpeed = 12f;

    [Header("卡住时的挣扎表现")]
    // 场景：史莱姆跟跳没跳上台子、落在台子侧面 → CheckBlocked 判定"过不去" → 停住不动。
    // 它只在玩家跳的时候才跳，所以玩家站在台上一动不动时它会一直卡着 ——
    // 与其当成 bug 消掉，不如让它"看得出来是在使劲，但爬不上去"，这正好是"呆呆"的人设。
    // 救援方式：玩家【再跳一次】就会触发 CheckJumpFollow，它跟着蹦上来。
    [Tooltip("连续多久没动才算卡住（秒）。要比跟跳的正常反应时间长，否则每次跟跳都会闪一下")]
    public float struggleDelay = 0.8f;
    [Tooltip("脱离卡住后多久退出挣扎（秒）。取值要比 struggleDelay 大 —— \n" +
             "玩家在头顶跳来跳去时判定会一帧真一帧假，清零式写法永远攒不满，挣扎就永远不触发（踩过）")]
    public float struggleReleaseDelay = 1.0f;
    [Tooltip("2D 距离超过 followDistance + 这个值 才判定；已经跟到玩家身边就不算卡住")]
    public float stuckDistanceSlack = 0.6f;
    [Tooltip("一个物理步移动少于这么多就算没动（正常跟随一步约 0.13 格）")]
    public float stuckMoveEpsilon = 0.03f;
    [Tooltip("卡住时在 Console 打印诊断。排查用，定稿前关掉")]
    public bool logStruggleDiagnostics = true;
    [Tooltip("挣扎时贴图左右颤动的幅度（格）。只动贴图，不动碰撞体，不影响物理")]
    public float struggleShakeAmplitude = 0.12f;
    [Tooltip("颤动频率。越大抖得越快")]
    public float struggleShakeSpeed = 42f;
    [Tooltip("使劲的节奏速度（顶一下、松一下）")]
    public float strugglePushSpeed = 5f;
    [Tooltip("使劲时横向压扁 / 纵向压低的比例（贴着墙推的感觉）")]
    public float struggleSquashX = 0.22f;
    public float struggleSquashY = 0.18f;
    [Tooltip("试着往上蹦的小幅上顶幅度（格）")]
    public float struggleHopHeight = 0.14f;
    public float struggleHopSpeed = 7f;
    [Tooltip("挣扎—喘气的循环周期；最后 strugglePantDuration 秒是没劲了的喘气")]
    public float strugglePantPeriod = 2.6f;
    public float strugglePantDuration = 0.5f;
    [Tooltip("喘气时额外缩小多少，像泄了气")]
    public float strugglePantShrink = 0.08f;
    [Tooltip("挣扎时贴图染成的颜色。远看也能一眼发现它卡住了")]
    public Color struggleColor = new Color(1f, 0.5f, 0.3f);
    [Tooltip("挣扎变色的脉冲速度")]
    public float struggleColorSpeed = 7f;
    [Tooltip("有 Animator 且控制器里有 IsStruggling 参数时才打开。现在没有动画资源，默认关")]
    public bool writeAnimatorStruggleParam = false;

    [Header("音效")]
    public AudioClip jumpClip;
    public AudioClip hurtClip;
    public AudioClip deathClip;
    public AudioClip healClip;
    public AudioClip modeClip;

    // ---------------- 只读状态（UI / PlayerGrab / 关卡件来读） ----------------
    public SlimeState State { get { return _state; } }
    /// <summary>玩家可切换的模式，只会是 Follow 或 Stay。</summary>
    public SlimeState Mode { get { return _mode; } }
    // UI 与结算面板读整数版本；公式一律读 HealthRatio（浮点、平滑）
    public int CurrentHealth { get { return Mathf.RoundToInt(currentHealth); } }
    public int MaxHealth { get { return Mathf.RoundToInt(maxHealth); } }
    public float CurrentHealthF { get { return currentHealth; } }
    public float HealthRatio { get { return maxHealth > 0f ? Mathf.Clamp01(currentHealth / maxHealth) : 0f; } }
    public bool IsCarried { get { return _state == SlimeState.Carried; } }
    public bool IsRecalling { get { return _recalling; } }
    public bool IsGrounded { get; private set; }
    public float FacingX { get { return _facing; } }

    private SlimeState _state = SlimeState.Follow;
    private SlimeState _mode = SlimeState.Follow;
    private float _facing = 1f;
    private float _invincibleUntil = -999f;
    private float _scaredUntil;
    private float _thrownTimer;
    private float _restUntil;
    private float _followedJumpTime = -999f;
    private bool _recalling;
    private Transform _carryPoint;

    private Vector3[] _historyPos;
    private float[] _historyTime;
    private int _historyHead;
    private int _historyCount;

    private Vector3 _baseScale = Vector3.one;
    private Vector3 _baseSpriteLocalPos = Vector3.zero;
    private Vector3 _lastSafePosition;
    private float _wobbleTime;

    // 挣扎状态：MoveTowards 里被 CheckBlocked 挡住时打标记，FixedUpdate 末尾统一累计
    private bool _blockedThisFrame;
    private Vector2 _prevStepPos;
    private float _stuckness;
    private float _struggleTime;
    private bool _struggling;
    private float _lastStruggleLog = -999f;

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody2D>();
        if (bodyCollider == null) bodyCollider = GetComponent<Collider2D>();

        // ★ spriteRoot / spriteRenderer 在预制体里是空的（AutoWire 没填上），而 UpdateWobble 和
        //   UpdateFlash 开头就是 null 早退 —— 结果【蠕动、挣扎抖动、受伤闪红、被举着时的晃动】
        //   一个都没生效，而且不会有任何报错。这里照 body 的写法自己兜底找。
        if (spriteRenderer == null) spriteRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (spriteRoot == null && spriteRenderer != null && spriteRenderer.transform != transform)
            spriteRoot = spriteRenderer.transform;   // 用 != transform 兜底：绝不能把碰撞体也一起晃

        _prevStepPos = transform.position;            // 卡住判定要拿它算"这一步移动了多少"

        if (body != null)
        {
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
        }

        if (groundLayer.value == 0)
        {
            groundLayer = LayerMask.GetMask("Ground", "Platform", "MovingPlatform");
            Debug.LogWarning("[SlimeController] groundLayer 没配置，已自动兜底为 Ground|Platform|MovingPlatform");
        }

        // 跟跳时间窗太短会漏跳（史莱姆落地稍微晚一点就再也跟不上），兜一个下限
        jumpFollowWindow = Mathf.Max(jumpFollowWindow, 0.9f);

        // 边缘/加速度兜底：老预制体里没有 maxDropHeight / acceleration 这两个新字段时，
        // 它们可能被反序列化成 0 —— 那样"深探射线"长度为 0，永远探不到地面，
        // 就会退回"见边就停"的老毛病。这里兜一个下限。
        if (maxDropHeight < ledgeCheckDistance) maxDropHeight = ledgeCheckDistance + 4f;
        if (acceleration <= 0f) acceleration = followSpeed * 10f;

        // 血量字段从 int 改成 float 时，老场景里存的值可能被重置成 0，兜一下
        if (maxHealth <= 0f) maxHealth = 100f;
        currentHealth = Mathf.Clamp(currentHealth <= 0f ? maxHealth : currentHealth, 0f, maxHealth);
        _state = SlimeState.Follow;
        _mode = SlimeState.Follow;
        gameObject.layer = slimeLayer;
        _lastSafePosition = transform.position;

        if (spriteRoot != null)
        {
            _baseScale = spriteRoot.localScale;
            _baseSpriteLocalPos = spriteRoot.localPosition;
        }

        int size = Mathf.Max(8, historySize);
        historySize = size;
        _historyPos = new Vector3[size];
        _historyTime = new float[size];
        for (int i = 0; i < size; i++)
        {
            _historyPos[i] = transform.position;
            _historyTime[i] = -999f;
        }
    }

    void Update()
    {
        _wobbleTime += Time.deltaTime;

        if (_state == SlimeState.Scared && Time.time >= _scaredUntil)
            ReturnToMode();

        TickPriceDrain();
        UpdateFlash();
        UpdateWobble();
        UpdateAnimator();
    }

    /// <summary>
    /// 售价随时间下降：史莱姆是货物，运久了会蔫。
    /// 掉到 maxHealth × LevelManager.barFloor 就【停住】—— 时间不会饿死史莱姆，危险会。
    /// 掉血速率不在这里手填，而是由 LevelManager 按本关标准时间算出来
    /// （全局统一"每秒掉 5 元"，所以关卡越长、血条掉得越慢）。
    /// </summary>
    private void TickPriceDrain()
    {
        if (_state == SlimeState.Dead) return;
        if (levelManager == null) return;

        float rate = levelManager.DrainRate;
        if (rate <= 0f) return;

        float floor = maxHealth * Mathf.Clamp01(LevelManager.barFloor);
        if (currentHealth <= floor) return;

        currentHealth = Mathf.Max(floor, currentHealth - rate * Time.deltaTime);
        if (currentHealth <= 0f) Die();   // 只有把 barFloor 配成 0 时才会走到这里
    }

    void FixedUpdate()
    {
        // 卡住判定：不关心"为什么卡"，只看"想过去却没动"。
        // 上一次 FixedUpdate 到这里之间物理走了一步，位置差就是这一步实际移动的距离
        // （正常跟随一步约 0.13 格；卡住时≈0）。
        float movedThisStep = Vector2.Distance(transform.position, _prevStepPos);
        _prevStepPos = transform.position;
        bool stuckNow = IsStuckNow(movedThisStep);
        _blockedThisFrame = false;

        // 充能表：卡住时按 struggleDelay 涨满，脱离时按 struggleReleaseDelay 落回 0。
        // ⚠ 不能用"卡住就累加、否则立刻清零"——玩家在头顶跳来跳去时判定会一帧真一帧假，
        //   清零式写法永远攒不满，挣扎就永远不触发（日志实测：stranded=True 但 struggling=False）。
        float dt = Time.fixedDeltaTime;
        float rise = Mathf.Max(0.05f, struggleDelay);
        float fall = Mathf.Max(0.05f, struggleReleaseDelay);
        _stuckness = Mathf.MoveTowards(_stuckness, stuckNow ? 1f : 0f, dt / (stuckNow ? rise : fall));

        bool wasStruggling = _struggling;
        _struggling = _stuckness >= 1f;
        if (_struggling && !wasStruggling) _struggleTime = 0f;   // 重新开始一轮"使劲 → 喘气"
        if (_stuckness > 0f) _struggleTime += dt;

        // 只在【真的进入挣扎】时打日志，避免正常跟跳时刷屏
        if (logStruggleDiagnostics && _struggling && Time.time - _lastStruggleLog > 0.5f)
        {
            _lastStruggleLog = Time.time;
            Debug.LogWarning(string.Format(
                "[史莱姆卡住] 状态={0} 贴地={1} 挪动={2:F3} 充能={3:F2} " +
                "我=({4:F1},{5:F1}) 玩家=({6:F1},{7:F1}) 距离={8:F2} timeScale={9:F2}",
                _state, IsGrounded, movedThisStep, _stuckness,
                transform.position.x, transform.position.y,
                player != null ? player.transform.position.x : 0f,
                player != null ? player.transform.position.y : 0f,
                player != null ? Vector2.Distance(transform.position, player.transform.position) : -1f,
                Time.timeScale));
        }

        UpdateGrounded();
        RecordPlayerHistory();

        // 掉出世界：要么送回最后站稳的位置，要么判定死亡。
        // 绝不允许"永远往下掉" —— 那会让关卡既无法完成也无法失败，直接卡死。
        if (transform.position.y < fallDeathY)
        {
            HandleOutOfWorld();
            return;
        }

        if (body == null) return;

        // 记录最后一次站稳的位置（掉出世界后的回收点）
        if (IsGrounded && _state != SlimeState.Carried && _state != SlimeState.Thrown)
            _lastSafePosition = transform.position;

        // 召回优先于模式：仍然只是朝玩家直线蠕动，被挡住就卡住
        if (_recalling && _state != SlimeState.Dead && _state != SlimeState.Carried && _state != SlimeState.Thrown)
        {
            TickRecall();
            return;
        }

        switch (_state)
        {
            case SlimeState.Follow:
                TickFollow();
                break;

            case SlimeState.Stay:
            case SlimeState.Scared:
            case SlimeState.Dead:
                StopHorizontal();
                break;

            case SlimeState.Thrown:
                TickThrown();
                break;

            case SlimeState.Resting:
                // 投掷落地后的静止：不动、不跟随，等玩家用哨子决定它接下来干什么
                StopHorizontal();
                if (Time.time >= _restUntil) ReturnToMode();
                break;

            case SlimeState.Carried:
                // 位置由 LateUpdate 跟随挂点，物理已设为 Kinematic
                break;
        }
    }

    void LateUpdate()
    {
        if (_state != SlimeState.Carried || _carryPoint == null) return;

        float distance = Vector3.Distance(transform.position, _carryPoint.position);
        if (distance > carrySnapDistance)
            transform.position = _carryPoint.position;
        else
            transform.position = Vector3.Lerp(transform.position, _carryPoint.position, carryFollowSharpness * Time.deltaTime);
    }

    // ---------------- Follow ----------------
    private void TickFollow()
    {
        if (player == null)
        {
            StopHorizontal();
            return;
        }

        Vector3 target;
        float stopDistance;

        if (pathFollow != null && pathFollow.HasPendingPath)
        {
            // 沿路径点走：一直走到点上，由 SlimePathFollow 判定到达
            target = pathFollow.CurrentTarget;
            stopDistance = 0.1f;
        }
        else
        {
            // 常规跟随：跟着"延迟后的玩家位置"，形成慢半拍
            target = GetDelayedPlayerPosition();
            stopDistance = followDistance;
        }

        MoveTowards(target.x, stopDistance, 1f);
    }

    // ---------------- 召回 ----------------
    private void TickRecall()
    {
        if (player == null)
        {
            _recalling = false;
            StopHorizontal();
            return;
        }

        MoveTowards(player.transform.position.x, followDistance * 0.7f, recallSpeedMultiplier);
    }

    /// <summary>朝目标 x 直线蠕动；被高台 / 深坑挡住就停下等玩家处理。</summary>
    private void MoveTowards(float targetX, float stopDistance, float speedMultiplier)
    {
        float dx = targetX - transform.position.x;
        float distance = Mathf.Abs(dx);

        // 腾空时不要"到点就刹车"：否则跳出深坑的途中水平速度会被清零，垂直掉回坑里
        if (!IsGrounded) stopDistance = Mathf.Min(stopDistance, 0.05f);

        if (distance <= stopDistance)
        {
            StopHorizontal();
            CheckJumpFollow();
            return;
        }

        float dir = dx > 0f ? 1f : -1f;
        _facing = dir;

        // 贴地时做完整障碍判定（高台 / 深坑 / 小台阶）→ 卡住等玩家处理。
        // 这里打上"被挡住"标记，让 FixedUpdate 累计成挣扎状态（表现成"使劲爬但爬不上去"）。
        // 救援方式：玩家再跳一次 → CheckJumpFollow 会让它跟着蹦上来。
        if (IsGrounded && CheckBlocked(dir))
        {
            _blockedThisFrame = true;
            StopHorizontal();
            CheckJumpFollow();
            return;
        }

        // 腾空时如果正前方贴着墙：停止横向推挤，让它顺着墙滑到地面。
        // 否则每帧把水平速度往墙里赋值，接触摩擦力会把竖直方向也锁死 ——
        // 表现就是"粘在墙上，既不前进也不下落"。
        if (!IsGrounded && CheckWallAhead(dir))
        {
            StopHorizontal();
            CheckJumpFollow();
            return;
        }

        // 用加速度平滑过渡，而不是瞬间把速度打满 —— 走起来更丝滑
        float targetSpeed = dir * followSpeed * speedMultiplier;
        float newX = Mathf.MoveTowards(body.velocity.x, targetSpeed, acceleration * Time.fixedDeltaTime);
        body.velocity = new Vector2(newX, body.velocity.y);
        CheckJumpFollow();
    }

    private bool CheckBlocked(float dir)
    {
        float footY = FootY;
        float halfWidth = HalfWidth;

        // 高墙：站上去也过不去 → 卡住
        Vector2 highOrigin = new Vector2(transform.position.x + dir * (halfWidth + 0.02f), footY + maxStepHeight + 0.06f);
        RaycastHit2D highHit = Physics2D.Raycast(highOrigin, new Vector2(dir, 0f), wallCheckDistance, groundLayer);
        if (highHit.collider != null) return true;

        // 小台阶：允许向上微调迈过去（不是攀爬，高度受 maxStepHeight 限制）
        Vector2 lowOrigin = new Vector2(transform.position.x + dir * (halfWidth + 0.02f), footY + 0.06f);
        RaycastHit2D lowHit = Physics2D.Raycast(lowOrigin, new Vector2(dir, 0f), wallCheckDistance, groundLayer);
        if (lowHit.collider != null)
        {
            body.position += Vector2.up * (stepAssistSpeed * Time.fixedDeltaTime);
        }

        // 边缘判定：前方地面缺失时，先往下探一探再决定
        //   下方 maxDropHeight 以内有地面 → 允许走下去（跟着人物下悬崖 / 下台阶）
        //   下方什么都没有（无底坑）        → 停在边缘等玩家处理
        if (stopAtLedge)
        {
            Vector2 ledgeOrigin = new Vector2(transform.position.x + dir * ledgeCheckForward, footY + 0.15f);
            RaycastHit2D nearHit = Physics2D.Raycast(ledgeOrigin, Vector2.down, ledgeCheckDistance, groundLayer);
            if (nearHit.collider == null)
            {
                RaycastHit2D deepHit = Physics2D.Raycast(ledgeOrigin, Vector2.down, maxDropHeight, groundLayer);
                if (deepHit.collider == null) return true;   // 无底坑才停
            }
        }

        return false;
    }

    /// <summary>
    /// 正前方（脚底往上一点点的高度）是否贴着墙。
    /// 腾空时用它判断"撞墙了"，避免每帧把速度怼进墙里把自己粘住。
    /// </summary>
    private bool CheckWallAhead(float dir)
    {
        float footY = FootY;
        Vector2 origin = new Vector2(transform.position.x + dir * (HalfWidth + 0.02f), footY + maxStepHeight + 0.06f);
        RaycastHit2D hit = Physics2D.Raycast(origin, new Vector2(dir, 0f), wallCheckDistance, groundLayer);
        return hit.collider != null;
    }

    // ---------------- 跟跳 ----------------
    private void CheckJumpFollow()
    {
        if (player == null || !IsGrounded) return;

        float jumpTime = player.LastJumpTime;
        if (jumpTime <= 0f) return;
        if (jumpTime <= _followedJumpTime) return;

        float elapsed = Time.time - jumpTime;
        if (elapsed < jumpFollowDelay) return;

        if (elapsed > jumpFollowWindow)
        {
            _followedJumpTime = jumpTime;
            return;
        }

        // 规则：只要在跟随状态，玩家跳史莱姆就跟着跳 —— 不管前面是平地、高台还是深坑。
        // 注意：这里以前有一句"正前方是高墙就不跳"，会导致史莱姆掉进坑里后面对坑壁永远不肯起跳，被困死。
        _followedJumpTime = jumpTime;
        JumpFollow(player.CurrentJumpHeight * jumpHeightRatio);
    }

    /// <summary>
    /// 跟跳：让史莱姆的滞空时间和玩家一致（高度按重力比例反算），
    /// 这样玩家能跳过去的坑，史莱姆也能跳过去，不会半路掉进坑里。
    /// </summary>
    private void JumpFollow(float fallbackHeight)
    {
        float height = fallbackHeight;

        if (player != null && body != null)
        {
            float gravity = Mathf.Abs(Physics2D.gravity.y);
            float playerG = player.gravityScale > 0f ? player.gravityScale : 1f;
            float slimeG = body.gravityScale > 0f ? body.gravityScale : 1f;
            float jumpH = Mathf.Max(0.1f, player.CurrentJumpHeight);

            // 玩家的滞空时间：t = 2*sqrt(2h/g)
            float airTime = 2f * Mathf.Sqrt(2f * jumpH / (gravity * playerG));
            float m = jumpAirTimeMultiplier <= 0f ? 1f : jumpAirTimeMultiplier;

            // 用同样的滞空时间反算史莱姆需要跳多高：h = g*t^2/8
            height = gravity * slimeG * (airTime * m) * (airTime * m) / 8f;
            height = Mathf.Clamp(height, 0.3f, 8f);
        }

        Jump(height);
    }

    private void Jump(float height)
    {
        if (body == null) return;
        float gravity = Mathf.Abs(Physics2D.gravity.y * body.gravityScale);
        float velocity = Mathf.Sqrt(2f * gravity * Mathf.Max(0.1f, height));
        body.velocity = new Vector2(body.velocity.x, velocity);
        IsGrounded = false;
        PlayClip(jumpClip);
    }

    // ---------------- 投掷落地 ----------------
    private void TickThrown()
    {
        _thrownTimer += Time.fixedDeltaTime;
        if (!IsGrounded || _thrownTimer <= 0.1f) return;

        // 落地后先静止一段时间，让玩家来得及用哨子切换模式。
        // 这期间按哨子会走 SetMode，直接接管并取消自动恢复。
        _state = SlimeState.Resting;
        _restUntil = Time.time + thrownRestTime;
        StopHorizontal();
    }

    // ---------------- 携带 ----------------
    /// <summary>被玩家抓起：物理转 Kinematic、切到 CarriedSlime 层（免伤、不推挤玩家）。模式保持不变。</summary>
    public void EnterCarried(Transform carryPoint)
    {
        if (_state == SlimeState.Dead) return;

        _carryPoint = carryPoint != null ? carryPoint : transform;
        _state = SlimeState.Carried;
        _recalling = false;

        if (body != null)
        {
            body.velocity = Vector2.zero;
            body.bodyType = RigidbodyType2D.Kinematic;
        }
        gameObject.layer = carriedLayer;
        PlayClip(modeClip);
    }

    /// <summary>被放下或投掷出去。放置不改变 Follow / Stay 模式；投掷落地后回到投掷前的模式。</summary>
    public void ExitCarried(Vector3 worldPosition, Vector2 velocity)
    {
        if (_state == SlimeState.Dead) return;

        transform.position = worldPosition;
        gameObject.layer = slimeLayer;

        if (body != null)
        {
            body.bodyType = RigidbodyType2D.Dynamic;
            body.velocity = velocity;
        }

        _thrownTimer = 0f;
        if (velocity.sqrMagnitude > 0.25f)
            _state = SlimeState.Thrown;
        else
            ReturnToMode();

        IsGrounded = false;
    }

    // ---------------- 模式切换（哨子 / 规则） ----------------
    /// <summary>切换模式。只接受 Follow 与 Stay。</summary>
    public void SetMode(SlimeState mode)
    {
        if (mode != SlimeState.Follow && mode != SlimeState.Stay) return;

        // 投掷落地静止期间，即使切到"当前已经是"的模式也要接管（用来取消自动恢复）
        bool wasResting = _state == SlimeState.Resting;
        if (_mode == mode && !wasResting) return;

        SlimeState old = _mode;
        _mode = mode;

        if (_state == SlimeState.Dead) return;

        if (mode == SlimeState.Stay)
        {
            _recalling = false;
            if (_state != SlimeState.Carried && _state != SlimeState.Thrown)
            {
                _state = SlimeState.Stay;
                StopHorizontal();
            }
        }
        else if (old == SlimeState.Stay && mode == SlimeState.Follow)
        {
            // Stay → Follow：把玩家所在位置作为最后一个路径点，按放置顺序依次走完
            if (pathFollow != null) pathFollow.BeginFollowWithPlayerAsLast();
            if (_state != SlimeState.Carried && _state != SlimeState.Thrown) _state = SlimeState.Follow;
        }
        else
        {
            if (_state != SlimeState.Carried && _state != SlimeState.Thrown) _state = SlimeState.Follow;
        }

        PlayClip(modeClip);
    }

    public void ToggleMode()
    {
        SetMode(_mode == SlimeState.Follow ? SlimeState.Stay : SlimeState.Follow);
    }

    // ---------------- 哨子召回 ----------------
    /// <summary>召回：以更高速度朝玩家方向直线蠕动。不寻路、不绕路、不传送，被挡住照样卡住。</summary>
    public void StartRecall()
    {
        if (_state == SlimeState.Dead || _state == SlimeState.Carried) return;
        _recalling = true;
    }

    /// <summary>打断召回。</summary>
    public void CancelRecall()
    {
        _recalling = false;
    }

    // ---------------- 血量 ----------------
    /// <summary>受到危险区 / 怪物的伤害。无敌帧内会被忽略。</summary>
    public void TakeDamage(int amount, Hazard source = null)
    {
        if (_state == SlimeState.Dead) return;
        if (amount <= 0) return;
        if (Time.time < _invincibleUntil) return;

        currentHealth = Mathf.Max(0, currentHealth - amount);
        _invincibleUntil = Time.time + invincibleTime;
        PlayClip(hurtClip);

        if (currentHealth <= 0) Die();
        else EnterScared();
    }

    /// <summary>吃掉史莱姆球回血。</summary>
    public void Heal(int amount)
    {
        if (_state == SlimeState.Dead) return;
        if (amount <= 0) return;

        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        PlayClip(healClip);
    }

    private void EnterScared()
    {
        _state = SlimeState.Scared;
        _scaredUntil = Time.time + scaredDuration;
        StopHorizontal();
    }

    private void Die()
    {
        _state = SlimeState.Dead;
        _recalling = false;
        StopHorizontal();
        if (body != null) body.velocity = Vector2.zero;
        if (animator != null) animator.SetBool("IsDead", true);
        PlayClip(deathClip);
        if (levelManager != null) levelManager.OnSlimeDied();
    }

    private void ReturnToMode()
    {
        _state = (_mode == SlimeState.Stay) ? SlimeState.Stay : SlimeState.Follow;
    }

    // ---------------- 工具 ----------------
    private float FootY
    {
        get { return bodyCollider != null ? bodyCollider.bounds.min.y : transform.position.y; }
    }

    private float HalfWidth
    {
        get { return bodyCollider != null ? bodyCollider.bounds.extents.x : 0.4f; }
    }

    private void UpdateGrounded()
    {
        Vector2 origin = groundCheck != null
            ? (Vector2)groundCheck.position
            : new Vector2(transform.position.x, FootY + 0.02f);

        IsGrounded = Physics2D.OverlapCircle(origin, groundCheckRadius, groundLayer);
    }

    /// <summary>掉出地图后的处理：默认送回最后一次站稳的位置，避免关卡被永久卡死。</summary>
    private void HandleOutOfWorld()
    {
        if (!recoverFromFall)
        {
            Die();
            return;
        }

        transform.position = _lastSafePosition;
        gameObject.layer = slimeLayer;
        _carryPoint = null;
        _recalling = false;
        ReturnToMode();

        if (body != null)
        {
            if (body.bodyType == RigidbodyType2D.Kinematic) body.bodyType = RigidbodyType2D.Dynamic;
            body.velocity = Vector2.zero;
        }

        Debug.LogWarning("[SlimeController] 史莱姆掉出了地图，已送回最后一次站稳的位置 " + _lastSafePosition);
    }

    private void StopHorizontal()
    {
        if (body == null) return;
        body.velocity = new Vector2(0f, body.velocity.y);
    }

    private void RecordPlayerHistory()
    {
        if (_historyPos == null) return;
        Vector3 pos = player != null ? player.transform.position : transform.position;
        _historyPos[_historyHead] = pos;
        _historyTime[_historyHead] = Time.time;
        _historyHead = (_historyHead + 1) % historySize;
        if (_historyCount < historySize) _historyCount++;
    }

    /// <summary>取 followDelay 秒之前的玩家位置，实现 0.1~0.2 秒的跟随延迟。</summary>
    private Vector3 GetDelayedPlayerPosition()
    {
        if (player == null) return transform.position;
        if (followDelay <= 0f || _historyCount == 0) return player.transform.position;

        float targetTime = Time.time - followDelay;
        int best = -1;
        for (int i = 1; i <= _historyCount; i++)
        {
            int index = (_historyHead - i + historySize) % historySize;
            if (index < 0) index += historySize;
            if (_historyTime[index] <= targetTime)
            {
                best = index;
                break;
            }
        }
        if (best < 0) best = (_historyHead - _historyCount + historySize) % historySize;
        return _historyPos[best];
    }

    private void UpdateFlash()
    {
        if (spriteRenderer == null) return;

        // 受伤闪红优先
        if (flashOnDamage && Time.time < _invincibleUntil)
        {
            float t = Mathf.PingPong(Time.time * flashSpeed, 1f);
            spriteRenderer.color = Color.Lerp(Color.white, hurtFlashColor, t);
            return;
        }

        // 其次：挣扎变色。使劲时颜色最深、喘气时淡回去 —— 光靠抖动远看不够明显，变色才看得见
        if (_struggling)
        {
            float period = Mathf.Max(0.3f, strugglePantPeriod);
            float cycle = Mathf.Repeat(_struggleTime, period);
            bool panting = cycle > period - Mathf.Clamp(strugglePantDuration, 0f, period * 0.8f);

            float t = panting
                ? 0.15f
                : 0.2f + 0.8f * Mathf.PingPong(_struggleTime * struggleColorSpeed, 1f);
            spriteRenderer.color = Color.Lerp(Color.white, struggleColor, t);
            return;
        }

        if (spriteRenderer.color != Color.white)
        {
            spriteRenderer.color = Color.white;
        }
    }

    /// <summary>
    /// 判定"卡住"：处于跟随/召回、人还离得远（按 2D 距离算）、但一个物理步几乎没移动。
    ///
    /// 这个判法**不关心"为什么卡"**（高墙 / 台子角 / 无底坑 / 悬在边上），
    /// 只要是"想过去却过不去"就算，所以不会漏。
    ///
    /// 之前用"玩家在上方 + 水平已到位 + 贴地"来判断，两个方向都错过：
    ///   · 误报：正常跟跳（跳上柱子 / 台子）时那三个条件也成立
    ///   · 漏报：史莱姆悬在台子角上时 groundCheck 探不到地面，IsGrounded=false 直接被排除
    /// </summary>
    private bool IsStuckNow(float movedThisStep)
    {
        if (player == null) return false;
        if (_state == SlimeState.Dead || _state == SlimeState.Carried) return false;
        if (_state != SlimeState.Follow && !_recalling) return false;

        // 已经跟到玩家身边了，站着不动是正常的
        float d = Vector2.Distance(transform.position, player.transform.position);
        if (d <= followDistance + stuckDistanceSlack) return false;

        // 想过去却几乎没动 → 卡住
        return movedThisStep < stuckMoveEpsilon;
    }

    private void UpdateWobble()    {
        if (spriteRoot == null || body == null) return;

        // 贴地蠕动时的挤压
        float speedRatio = Mathf.Clamp01(Mathf.Abs(body.velocity.x) / Mathf.Max(0.01f, followSpeed));
        float squash = Mathf.Sin(_wobbleTime * wobbleSpeed) * squashAmount * speedRatio;

        float sx = _baseScale.x * (1f - squash);
        float sy = _baseScale.y * (1f + squash);

        Vector3 spriteLocalPos = _baseSpriteLocalPos;

        if (_struggling)
        {
            // 卡在台子边上的挣扎：顶一下 → 颤动 → 没劲了喘口气 → 再来。
            // 全程只动 spriteRoot（贴图），碰撞体和物理位置一点不动，所以绝对不会影响玩法。
            float period = Mathf.Max(0.3f, strugglePantPeriod);
            float cycle = Mathf.Repeat(_struggleTime, period);
            bool panting = cycle > period - Mathf.Clamp(strugglePantDuration, 0f, period * 0.8f);

            // 使劲的程度：不喘气时在 0.45~1 之间起伏，像一下一下地顶
            float effort = 0f;
            if (!panting)
                effort = 0.45f + 0.55f * (Mathf.Sin(_struggleTime * strugglePushSpeed) * 0.5f + 0.5f);

            // 高频左右颤动 —— "用力到发抖"
            float shake = Mathf.Sin(_struggleTime * struggleShakeSpeed) * struggleShakeAmplitude;
            spriteLocalPos.x += panting ? shake * 0.15f : shake;

            // 试着往上蹦的小幅上顶
            if (!panting)
            {
                float hop = Mathf.Max(0f, Mathf.Sin(_struggleTime * struggleHopSpeed));
                spriteLocalPos.y += hop * struggleHopHeight;
            }

            // 使劲时横向压扁、纵向压低（贴着台子推的感觉）
            sx *= 1f + struggleSquashX * effort;
            sy *= 1f - struggleSquashY * effort;

            // 喘气时整体缩一点，像泄了气
            if (panting)
            {
                sx *= 1f - strugglePantShrink;
                sy *= 1f - strugglePantShrink;
            }
        }
        else if (_state == SlimeState.Carried)
        {
            // 被玩家举着时，Sprite 轻微上下晃动（只动贴图，不动碰撞体，不影响物理）
            spriteLocalPos.y += Mathf.Sin(_wobbleTime * carriedSwaySpeed) * carriedSwayAmplitude;
        }

        spriteRoot.localScale = new Vector3(sx, sy, _baseScale.z);
        spriteRoot.localPosition = spriteLocalPos;
    }

    private void UpdateAnimator()
    {
        if (animator == null) return;
        float speed = body != null ? Mathf.Abs(body.velocity.x) : 0f;
        animator.SetFloat("Speed", speed);
        animator.SetBool("IsGrounded", IsGrounded);
        animator.SetBool("IsCarried", _state == SlimeState.Carried);
        animator.SetBool("IsScared", _state == SlimeState.Scared);
        animator.SetBool("IsDead", _state == SlimeState.Dead);
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.4f, 1f, 0.6f, 0.6f);
        float footY = bodyCollider != null ? bodyCollider.bounds.min.y : transform.position.y;
        Gizmos.DrawLine(new Vector3(transform.position.x, footY + maxStepHeight, 0f),
                        new Vector3(transform.position.x + _facing * wallCheckDistance, footY + maxStepHeight, 0f));
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(new Vector3(transform.position.x + _facing * ledgeCheckForward, footY + 0.15f, 0f),
                        new Vector3(transform.position.x + _facing * ledgeCheckForward, footY + 0.15f - ledgeCheckDistance, 0f));
    }
}
