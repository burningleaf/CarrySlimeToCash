using UnityEngine;

/// <summary>
/// 职责：玩家水平移动、跳跃、地面检测、携带状态、死亡与复活、动画参数与音效。
/// Inspector：拖 Rigidbody2D、GroundCheck 空物体、Animator（可空）、AudioSource（可空）。
/// 依赖：无逻辑依赖（死亡由 Enemy / LevelManager 判定后调用 Kill/Respawn）。史莱姆只读本脚本的 FacingX / LastJumpTime / CurrentJumpHeight。
/// 禁止：不实现攀爬、爬墙、抓边、翻越；所有垂直移动只能靠跳跃、史莱姆垫脚、投掷、机关、斜坡。
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

    [Header("调试")]
    [Tooltip("按了跳跃却跳不起来时，在 Console 打印原因。排查用，定稿前关掉")]
    public bool logJumpFailures = true;

    [Header("地面检测")]
    [Tooltip("哪些层算地面。留空会自动兜底为 Ground|Platform|MovingPlatform")]
    public LayerMask groundLayer;
    [Tooltip("把史莱姆两层也强制算作可站立地面。\n" +
             "默认开：万一碰撞矩阵被改成玩家能踩在史莱姆身上，不加这个就会出现\n" +
             "「明明站着却判定不在地面 → 完全跳不起来」。当前矩阵下玩家与史莱姆不碰撞，平时用不到。")]
    public bool treatSlimeAsGround = true;
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
    public bool IsGrounded { get; private set; }
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

        // 注意：这里是【无条件 OR】，不能写成"掩码为 0 才兜底"——
        // 场景里 groundLayer 早就配好了，那种写法永远不会执行（上一版就是这么白改的）。
        if (treatSlimeAsGround)
            groundLayer |= LayerMask.GetMask("Slime", "CarriedSlime");

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
        if (!IsGrounded) accel *= airControl;

        float newX = Mathf.MoveTowards(body.velocity.x, targetSpeed, accel * Time.fixedDeltaTime);

        // 腾空时如果正前方贴着墙：不再往墙里推。
        // 但【只停止加速，不清零速度】—— 清零的话，跳跃时擦到任何台阶侧面都会瞬间停住，
        // 玩家感觉就是"一顿一顿"（这是最明显的一个手感问题）。保留惯性，滑过去就好。
        if (!IsGrounded && Mathf.Abs(_moveInput) > 0.01f && CheckWallAhead(Mathf.Sign(_moveInput)))
            newX = body.velocity.x;

        float newY = Mathf.Max(body.velocity.y, -maxFallSpeed);
        body.velocity = new Vector2(newX, newY);

        // 跳跃
        if (_controlEnabled && !IsDead && _jumpBufferCounter > 0f)
        {
            bool canJump = IsGrounded || (Time.time - _lastGroundedTime) <= coyoteTime;
            if (canJump)
            {
                float gravity = Mathf.Abs(Physics2D.gravity.y * body.gravityScale);
                float velocity = Mathf.Sqrt(2f * gravity * CurrentJumpHeight);
                body.velocity = new Vector2(body.velocity.x, velocity);

                _jumpBufferCounter = 0f;
                _lastGroundedTime = -999f;
                IsGrounded = false;
                LastJumpTime = Time.time;
                PlayClip(jumpClip);
            }
            else if (logJumpFailures)
            {
                // 排查用：按了跳跃却跳不起来时，把"为什么"打出来。
                // 光看现象分不清是暂停(timeScale=0)、操控被关、还是地面检测失效。
                Debug.LogWarning(string.Format(
                    "[跳跃失败] 贴地={0} 距上次着地={1:F2}s(宽限{2:F2}) 可操控={3} 死亡={4} " +
                    "timeScale={5:F2} 速度=({6:F2},{7:F2}) 位置=({8:F2},{9:F2})",
                    IsGrounded, Time.time - _lastGroundedTime, coyoteTime, _controlEnabled, IsDead,
                    Time.timeScale, body.velocity.x, body.velocity.y,
                    transform.position.x, transform.position.y));
                _jumpBufferCounter = 0f;   // 清掉，免得每帧刷屏
            }
        }
    }

    private void UpdateGrounded()
    {
        bool wasGrounded = IsGrounded;

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
        IsGrounded = Physics2D.OverlapCircle(origin, groundCheckRadius, groundLayer);

        if (IsGrounded)
        {
            _lastGroundedTime = Time.time;
            if (!wasGrounded && !IsDead) PlayClip(landClip);
        }
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
        animator.SetBool("IsGrounded", IsGrounded);
        animator.SetBool("IsCarrying", IsCarrying);
        animator.SetBool("IsDead", IsDead);
    }

    // ---------------- 对外状态接口 ----------------
    /// <summary>由 PlayerGrab 在抓取 / 放下 / 投掷时调用。</summary>
    public void SetCarrying(bool carrying)
    {
        IsCarrying = carrying;
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
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
        }

        // 贴墙检测射线（腾空时才生效）
        Gizmos.color = Color.red;
        float footY = _collider != null ? _collider.bounds.min.y : transform.position.y;
        Gizmos.DrawLine(new Vector3(transform.position.x + _facing * (HalfWidth + 0.02f), footY + wallCheckHeight, 0f),
                        new Vector3(transform.position.x + _facing * (HalfWidth + 0.02f + wallCheckDistance), footY + wallCheckHeight, 0f));
    }
}
