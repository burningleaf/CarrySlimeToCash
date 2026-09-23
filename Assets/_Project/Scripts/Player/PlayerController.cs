using UnityEngine;

/// <summary>
/// 职责：玩家水平移动、跳跃、地面检测、携带状态、死亡与复活、动画参数与音效。
/// Inspector：拖 Rigidbody2D、GroundCheck 空物体、Animator（可空）、AudioSource（可空）。
/// 依赖：无逻辑依赖（死亡由 Enemy / LevelManager 判定后调用 Kill/Respawn）。史莱姆只读本脚本的 FacingX / LastJumpTime / CurrentJumpHeight。
/// 禁止：不实现攀爬、爬墙、抓边、翻越；所有垂直移动只能靠跳跃、史莱姆垫脚、投掷、机关、斜坡。
/// 能力开关：空中跳不是默认能力，由物品【跳跃云朵瓶】赋予 —— PlayerInventory 每帧调 SetInfiniteAirJump()。
/// 掩码语义（2026 修 bug）：groundLayer = **起跳判据**（只有 Ground|Platform|MovingPlatform）；
///   史莱姆那两层只进【可站立面】掩码 slimeStandLayer（表现层用），绝不再并进 groundLayer。
/// 动画参数名（Animator 里建好，缺了只会打警告）：Speed(float)、IsGrounded(bool)、IsCarrying(bool)、IsDead(bool)。
/// </summary>
public class PlayerController : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public Rigidbody2D body;
    [Tooltip("脚底的地面检测点（空物体，挂在角色脚下）")]
    public Transform groundCheck;
    [Tooltip("角色贴图节点，用于左右翻转，可空")]
    public Transform spriteRoot;
    public Animator animator;
    public AudioSource audioSource;

    [Header("移动")]
    public float moveSpeed = 6.5f;
    public float acceleration = 60f;
    public float deceleration = 70f;
    [Tooltip("空中操控系数")]
    public float airControl = 0.8f;
    [Tooltip("携带史莱姆时的移速（绝对值，不再是倍率）")]
    public float carryMoveSpeed = 3.5f;

    [Header("跳跃（高度单位 = 格）")]
    [Tooltip("空手跳跃高度，约 3 格")]
    public float jumpHeight = 3f;
    [Tooltip("携带史莱姆时跳跃高度，1.5~2 格")]
    public float carryJumpHeight = 1.8f;
    public float gravityScale = 3f;
    [Tooltip("最大下落速度。原来 25 太快：物理步进 0.5 格容易穿模，相机也跟不上（显得一顿一顿）")]
    public float maxFallSpeed = 18f;
    [Tooltip("离开地面后仍可起跳的宽限时间")]
    public float coyoteTime = 0.1f;
    [Tooltip("落地前提前按跳跃的缓冲时间")]
    public float jumpBufferTime = 0.12f;
    [Tooltip("松开跳跃键时的上升速度截断比例。0.5 太狠（轻点只能跳 0.75 格，和按满差 4 倍），0.65 更稳")]
    [Range(0f, 1f)] public float jumpCutMultiplier = 0.65f;
    [Tooltip("起跳后这段时间内【不】截断上升速度。这样轻点也能拿到像样的高度，跳跃手感才一致")]
    public float minJumpHoldTime = 0.08f;
    [Tooltip("高速时用连续碰撞检测，避免穿模和落点漂移")]
    public bool useContinuousCollision = true;

    [Header("空中跳（由物品赋予，不是默认能力）")]
    [Tooltip("是否允许【无限次】空中跳。由 PlayerInventory 按当前选中的物品每帧开关：\n" +
             "拿着「跳跃云朵瓶」= true（无限次，不做次数限制），其它物品 = false。\n" +
             "⚠ 刻意【不】做「额外跳次数」计数器：那种写法在抱着史莱姆（它贴在脚边）时会被反复刷新，\n" +
             "等于又变成无限跳。根治点是上面把 groundLayer / slimeStandLayer 两个语义分开。")]
    public bool infiniteAirJump = false;

    [Header("调试")]
    [Tooltip("按了跳跃却跳不起来时，在 Console 打印原因。排查用，定稿前关掉")]
    public bool logJumpFailures = true;

    [Header("地面检测")]
    [Tooltip("哪些层算【地面】。这是【起跳判据】—— 起跳、土狼时间、空中操控、贴墙检测都只看这个掩码。\n" +
             "留空会自动兜底为 Ground|Platform|MovingPlatform")]
    public LayerMask groundLayer;
    [Tooltip("把史莱姆两层也算作可【站立】面（Slime / CarriedSlime）。\n" +
             "⚠ 只影响表现层：IsSupported / 落地音效 / 动画 IsGrounded / 空中操控；【绝不】参与起跳判据。\n" +
             "以前这行是无条件把 Slime|CarriedSlime OR 进 groundLayer（起跳判据）⇒ 史莱姆跟在脚边时\n" +
             "空中也被判成 Grounded、抱着它更是永远 Grounded ⇒ 举着史莱姆就能无限空中跳（bug）。\n" +
             "现在两个语义分开了：起跳只认 groundLayer，站立表现才看 slimeStandLayer。\n" +
             "当前碰撞矩阵下玩家与史莱姆不碰撞，所以这个站立判定平时用不到；它的作用是万一矩阵改成\n" +
             "「玩家能踩在史莱姆身上」时，不会出现「明明站着却判定不在地面」。")]
    public bool treatSlimeAsGround = true;
    [Tooltip("史莱姆所在层（Slime / CarriedSlime），【只用于可站立面判定】，不给跳跃用。留空自动兜底")]
    public LayerMask slimeStandLayer;
    public float groundCheckRadius = 0.18f;
    [Tooltip("没有拖 GroundCheck 子物体时，用碰撞体底部 + 这个偏移做地面检测")]
    public float groundProbeYOffset = 0.05f;

    [Header("贴墙检测（腾空时防止被墙粘住）")]
    [Tooltip("腾空时前方多近算作贴墙")]
    public float wallCheckDistance = 0.45f;
    [Tooltip("贴墙检测射线离脚底的高度")]
    public float wallCheckHeight = 0.4f;

    [Header("音效")]
    public AudioClip jumpClip;
    public AudioClip landClip;
    public AudioClip deathClip;

    // ---------------- 只读状态（史莱姆 / UI / 机关来读） ----------------
    /// <summary>是否踩着【真地面】（Ground / Platform / MovingPlatform）。⚠ 这是唯一的起跳判据。</summary>
    public bool IsGrounded { get; private set; }
    /// <summary>是否站在【可站立面】上 = 真地面 ∪ 史莱姆（treatSlimeAsGround 时）。
    ///  只给表现层用（落地音效 / 动画 / 空中操控），【绝不】参与起跳判据。</summary>
    public bool IsSupported { get; private set; }
    /// <summary>拿着「跳跃云朵瓶」时为 true：允许无限次空中跳。由 PlayerInventory 每帧写入。</summary>
    public bool InfiniteAirJump { get { return infiniteAirJump; } }
    public bool IsCarrying { get; private set; }
    public bool IsDead { get; private set; }
    public bool ControlEnabled { get { return _controlEnabled; } }
    /// <summary>最近一次起跳的时间戳，史莱姆靠它做 0.15~0.25s 延迟跟跳。</summary>
    public float LastJumpTime { get; private set; }
    /// <summary>当前状态下的跳跃高度（格），携带时取 carryJumpHeight。</summary>
    public float CurrentJumpHeight { get { return IsCarrying ? carryJumpHeight : jumpHeight; } }
    /// <summary>朝向：1 右，-1 左。</summary>
    public float FacingX { get { return _facing; } }

    private float _moveInput;
    private float _facing = 1f;
    private float _jumpBufferCounter;
    private float _lastGroundedTime = -999f;
    private bool _controlEnabled = true;
    private Collider2D _collider;
    /// <summary>可站立面掩码 = groundLayer ∪ slimeStandLayer（treatSlimeAsGround 时才并）。
    ///  只在 UpdateGrounded 里用，而且【只写 IsSupported】，永远不参与起跳。</summary>
    private LayerMask _standableLayer;

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody2D>();
        _collider = GetComponent<Collider2D>();

        // 和史莱姆一样：预制体里 spriteRoot 是空的，UpdateFlip 里有 null 早退，
        // 结果角色转身一直没有视觉表现（而且不报错）。这里自己兜底找。
        if (spriteRoot == null)
        {
            SpriteRenderer sr = GetComponentInChildren<SpriteRenderer>(true);
            if (sr != null && sr.transform != transform) spriteRoot = sr.transform;
        }

        if (groundLayer.value == 0)
        {
            groundLayer = LayerMask.GetMask("Ground", "Platform", "MovingPlatform");
            Debug.LogWarning("[PlayerController] groundLayer 没配置，已自动兜底为 Ground|Platform|MovingPlatform");
        }

        if (slimeStandLayer.value == 0)
            slimeStandLayer = LayerMask.GetMask("Slime", "CarriedSlime");

        // ★ 修 bug（2026）：以前这里是 `groundLayer |= LayerMask.GetMask("Slime", "CarriedSlime")`，
        //   把 Slime(7) / CarriedSlime(8) 并进了【起跳判据】。史莱姆跟随时贴在脚边 ⇒ OverlapCircle
        //   永远命中 ⇒ 空中也算 IsGrounded；抱着它更是永远 Grounded ⇒ 举着史莱姆就能无限空中跳。
        //   现在改成两个掩码：起跳只用 groundLayer（真地面），史莱姆只进 _standableLayer（站立表现）。
        _standableLayer = groundLayer;
        if (treatSlimeAsGround)
            _standableLayer |= slimeStandLayer;

        if (body != null)
        {
            body.gravityScale = gravityScale;
            body.freezeRotation = true;
            // 起跳速度约 13 格/秒。离散碰撞在 50Hz 下每步走 0.27 格，起跳顶点会随帧漂移
            body.collisionDetectionMode = useContinuousCollision
                ? CollisionDetectionMode2D.Continuous
                : CollisionDetectionMode2D.Discrete;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
        }
        LastJumpTime = -999f;
    }

    void Update()
    {
        if (!_controlEnabled || IsDead)
        {
            _moveInput = 0f;
            _jumpBufferCounter = 0f;
            UpdateAnimator();
            return;
        }

        _moveInput = Input.GetAxisRaw("Horizontal");

        if (Input.GetButtonDown("Jump"))
            _jumpBufferCounter = jumpBufferTime;
        else
            _jumpBufferCounter -= Time.deltaTime;

        // 提前松手 → 截断上升速度（手感）。
        // 但起跳后 minJumpHoldTime 秒内不截断：否则"轻点"只能跳 0.75 格、"按满"能跳 3 格，
        // 同一个键高度差 4 倍，玩家会觉得跳跃忽大忽小、一顿一顿。
        if (Input.GetButtonUp("Jump") && body != null && body.velocity.y > 0f &&
            Time.time - LastJumpTime >= minJumpHoldTime)
            body.velocity = new Vector2(body.velocity.x, body.velocity.y * jumpCutMultiplier);

        UpdateAnimator();
        UpdateFlip();
    }

    void FixedUpdate()
    {
        UpdateGrounded();

        if (body == null) return;

        // 水平移动
        float targetSpeed = 0f;
        if (_controlEnabled && !IsDead)
            targetSpeed = _moveInput * (IsCarrying ? carryMoveSpeed : moveSpeed);

        float accel = Mathf.Abs(targetSpeed) > 0.01f ? acceleration : deceleration;
        // 空中操控用【站立判定】（IsSupported）：站起来（含万一能踩在史莱姆上）就不算空中。
        // 这里刻意不用 IsGrounded —— 这不是起跳判据，不影响"能不能跳"。
        if (!IsSupported) accel *= airControl;

        float newX = Mathf.MoveTowards(body.velocity.x, targetSpeed, accel * Time.fixedDeltaTime);

        // 腾空时如果正前方贴着墙：不再往墙里推。
        // 但【只停止加速，不清零速度】—— 清零的话，跳跃时擦到任何台阶侧面都会瞬间停住，
        // 玩家感觉就是"一顿一顿"（这是最明显的一个手感问题）。保留惯性，滑过去就好。
        if (!IsSupported && Mathf.Abs(_moveInput) > 0.01f && CheckWallAhead(Mathf.Sign(_moveInput)))
            newX = body.velocity.x;

        float newY = Mathf.Max(body.velocity.y, -maxFallSpeed);
        body.velocity = new Vector2(newX, newY);

        // 跳跃：① 贴着真地面（或土狼时间内）→ 常规跳；② 拿着【跳跃云朵瓶】→ 无限次空中跳。
        if (_controlEnabled && !IsDead && _jumpBufferCounter > 0f)
        {
            bool walkingOnGround = IsGrounded || (Time.time - _lastGroundedTime) <= coyoteTime;
            bool canJump = walkingOnGround || infiniteAirJump;
            if (canJump)
            {
                // 常规跳用当前状态高度（空手 3 格 / 携带 1.8 格，与以前完全一致）；
                // 空中跳（只有拿着云朵瓶才可能）也用同一套高度，所以"空手跳高/跨距"一点没变。
                float gravity = Mathf.Abs(Physics2D.gravity.y * body.gravityScale);
                float velocity = Mathf.Sqrt(2f * gravity * CurrentJumpHeight);
                body.velocity = new Vector2(body.velocity.x, velocity);

                _jumpBufferCounter = 0f;
                _lastGroundedTime = -999f;
                IsGrounded = false;
                IsSupported = false;
                LastJumpTime = Time.time;
                PlayClip(jumpClip);
            }
            else if (logJumpFailures)
            {
                // 排查用：按了跳跃却跳不起来时，把"为什么"打出来。
                // 光看现象分不清是暂停(timeScale=0)、操控被关、还是地面检测失效。
                Debug.LogWarning(string.Format(
                    "[跳跃失败] 贴地={0} 距上次着地={1:F2}s(宽限{2:F2}) 可操控={3} 死亡={4} " +
                    "timeScale={5:F2} 速度=({6:F2},{7:F2}) 位置=({8:F2},{9:F2}) 云朵瓶空中跳={10}",
                    IsGrounded, Time.time - _lastGroundedTime, coyoteTime, _controlEnabled, IsDead,
                    Time.timeScale, body.velocity.x, body.velocity.y,
                    transform.position.x, transform.position.y, infiniteAirJump));
                _jumpBufferCounter = 0f;   // 清掉，免得每帧刷屏
            }
        }
    }

    private void UpdateGrounded()
    {
        bool wasSupported = IsSupported;

        Vector2 origin;
        if (groundCheck != null)
        {
            origin = groundCheck.position;
        }
        else
        {
            // 没拖 GroundCheck 就退化用碰撞体底部，避免"什么都没配就永远跳不起来"
            float footY = _collider != null ? _collider.bounds.min.y : transform.position.y;
            origin = new Vector2(transform.position.x, footY + groundProbeYOffset);
        }
        // ① 起跳判据：只认【真地面】。史莱姆不算 —— 这就是"举着史莱姆无限空中跳"的根治点。
        IsGrounded = Physics2D.OverlapCircle(origin, groundCheckRadius, groundLayer);

        // ② 站立表现：真地面 ∪ 史莱姆（可站立面）。⚠ 只写 IsSupported，绝不给起跳判据用。
        IsSupported = IsGrounded;
        if (!IsSupported && treatSlimeAsGround)
            IsSupported = Physics2D.OverlapCircle(origin, groundCheckRadius, _standableLayer) != null;

        if (IsGrounded)
            _lastGroundedTime = Time.time;

        if (IsSupported && !wasSupported && !IsDead) PlayClip(landClip);
    }

    private void UpdateFlip()
    {
        if (Mathf.Abs(_moveInput) > 0.01f)
            _facing = Mathf.Sign(_moveInput);

        if (spriteRoot != null)
        {
            Vector3 scale = spriteRoot.localScale;
            scale.x = Mathf.Abs(scale.x) * _facing;
            spriteRoot.localScale = scale;
        }
    }

    private void UpdateAnimator()
    {
        if (animator == null) return;
        float speed = body != null ? Mathf.Abs(body.velocity.x) : 0f;
        animator.SetFloat("Speed", speed);
        // 动画看【站立表现】（IsSupported）：站着就是站着，不管站着的是地面还是史莱姆。
        // 起跳判据另有其人（IsGrounded），别把两者又混回去。
        animator.SetBool("IsGrounded", IsSupported);
        animator.SetBool("IsCarrying", IsCarrying);
        animator.SetBool("IsDead", IsDead);
    }

    // ---------------- 对外状态接口 ----------------
    /// <summary>由 PlayerGrab 在抓取 / 放下 / 投掷时调用。</summary>
    public void SetCarrying(bool carrying)
    {
        IsCarrying = carrying;
    }

    /// <summary>
    /// 由 PlayerInventory 按【当前选中的物品】开关无限空中跳：
    /// 拿着「跳跃云朵瓶」= true，其它物品 / 空手 = false。
    /// 每帧推一次（幂等），所以换物品、本关不发放云朵瓶（meta.itemGrants[3] = false）都能立刻跟上。
    /// </summary>
    public void SetInfiniteAirJump(bool enabled)
    {
        infiniteAirJump = enabled;
    }

    /// <summary>开启 / 关闭操控（死亡、结算、失败时关闭）。</summary>
    public void SetControlEnabled(bool enabled)
    {
        _controlEnabled = enabled;
        if (!enabled)
        {
            _moveInput = 0f;
            _jumpBufferCounter = 0f;
            if (body != null) body.velocity = new Vector2(0f, body.velocity.y);
        }
    }

    /// <summary>玩家死亡表现（碰怪 / 掉出地图）。复活由 LevelManager 负责。</summary>
    public void Kill()
    {
        if (IsDead) return;
        IsDead = true;
        _controlEnabled = false;
        if (body != null) body.velocity = Vector2.zero;
        if (animator != null) animator.SetBool("IsDead", true);
        PlayClip(deathClip);
    }

    /// <summary>从检查点复活，金币与物品保留。</summary>
    public void Respawn(Vector3 worldPosition)
    {
        transform.position = worldPosition;
        if (body != null) body.velocity = Vector2.zero;
        IsDead = false;
        _controlEnabled = true;
        _lastGroundedTime = -999f;
        if (animator != null) animator.SetBool("IsDead", false);
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }

    private float HalfWidth
    {
        get { return _collider != null ? _collider.bounds.extents.x : 0.4f; }
    }

    /// <summary>
    /// 正前方（脚底往上 wallCheckHeight 的高度）是否贴着墙。
    /// 腾空时用它避免每帧把速度怼进墙里，被摩擦力粘在墙上。
    /// 用的还是 groundLayer（真地面）：史莱姆不再是"墙"，贴着它跳过时不会再被按住。
    /// </summary>
    private bool CheckWallAhead(float dir)
    {
        float footY = _collider != null ? _collider.bounds.min.y : transform.position.y;
        Vector2 origin = new Vector2(transform.position.x + dir * (HalfWidth + 0.02f), footY + wallCheckHeight);
        RaycastHit2D hit = Physics2D.Raycast(origin, new Vector2(dir, 0f), wallCheckDistance, groundLayer);
        return hit.collider != null;
    }

    private void OnDrawGizmosSelected()
    {
        if (groundCheck != null)
        {
            // 绿圈 = 起跳判据（真地面 groundLayer）
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);

            // 青圈 = 可站立面（真地面 ∪ 史莱姆）。它【不】决定能不能跳，只决定"看起来站没站住"。
            if (treatSlimeAsGround)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius + 0.03f);
            }
        }

        // 贴墙检测射线（腾空时才生效）
        Gizmos.color = Color.red;
        float footY = _collider != null ? _collider.bounds.min.y : transform.position.y;
        Gizmos.DrawLine(new Vector3(transform.position.x + _facing * (HalfWidth + 0.02f), footY + wallCheckHeight, 0f),
                        new Vector3(transform.position.x + _facing * (HalfWidth + 0.02f + wallCheckDistance), footY + wallCheckHeight, 0f));
    }
}
