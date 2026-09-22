using UnityEngine;

/// <summary>巡逻方式。</summary>
public enum PatrolMode
{
    Distance = 0,    // 左右往返固定距离
    EdgeDetect = 1   // 悬崖 / 墙前自动转身
}

/// <summary>
/// 职责：巡逻怪。碰到小孩 → 玩家死亡（从检查点复活）；碰到史莱姆 → 扣史莱姆的血。
/// Inspector：拖 Rigidbody2D、Collider2D、GroundCheck 空物体、SpriteRoot（翻转用）、LevelManager、Animator、AudioSource。
/// 依赖：LevelManager（玩家死亡）、SlimeController（TakeDamage）。
/// 禁止：不寻路、不追击、不可被击杀（本 Demo 无战斗系统）。
/// </summary>
public class Enemy : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public Rigidbody2D body;
    public Collider2D bodyCollider;
    public Transform spriteRoot;
    public Transform groundCheck;
    public LevelManager levelManager;
    public Animator animator;
    public AudioSource audioSource;

    [Header("巡逻")]
    public PatrolMode patrolMode = PatrolMode.Distance;
    public float moveSpeed = 2.5f;
    [Tooltip("Distance 模式下的往返半径")]
    public float patrolDistance = 3f;
    [Tooltip("转身时的停顿时间")]
    public float waitAtTurnTime = 0.5f;
    public bool turnAtWall = true;
    [Tooltip("初始朝向：1 右，-1 左")]
    public float startDirection = 1f;

    [Header("检测")]
    public LayerMask groundLayer;
    public float edgeCheckForward = 0.45f;
    public float edgeCheckDistance = 1f;
    public float wallCheckDistance = 0.45f;
    public float wallCheckHeight = 0.4f;

    [Header("伤害")]
    [Tooltip("碰到史莱姆扣多少血")]
    public int contactDamage = 15;
    [Tooltip("碰到玩家是否致死")]
    public bool killPlayer = true;
    [Tooltip("两次伤害的最小间隔")]
    public float repeatInterval = 0.5f;

    [Header("音效")]
    public AudioClip hitClip;

    private float _startX;
    private float _dir = 1f;
    private float _turnUntil;
    private float _lastDamageTime = -999f;

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody2D>();
        if (bodyCollider == null) bodyCollider = GetComponent<Collider2D>();
        if (body != null) body.freezeRotation = true;

        if (groundLayer.value == 0)
        {
            groundLayer = LayerMask.GetMask("Ground", "Platform", "MovingPlatform");
            Debug.LogWarning("[Enemy] groundLayer 没配置，已自动兜底为 Ground|Platform|MovingPlatform");
        }
    }

    void Start()
    {
        _startX = transform.position.x;
        _dir = startDirection >= 0f ? 1f : -1f;
        UpdateFlip();
    }

    void FixedUpdate()
    {
        if (body == null) return;

        if (Time.time < _turnUntil)
        {
            body.velocity = new Vector2(0f, body.velocity.y);
            return;
        }

        if (NeedTurn())
        {
            _dir = -_dir;
            _turnUntil = Time.time + waitAtTurnTime;
            body.velocity = new Vector2(0f, body.velocity.y);
            UpdateFlip();
            return;
        }

        body.velocity = new Vector2(_dir * moveSpeed, body.velocity.y);
        UpdateFlip();
    }

    private bool NeedTurn()
    {
        if (patrolMode == PatrolMode.Distance)
        {
            if (_dir > 0f && transform.position.x >= _startX + patrolDistance) return true;
            if (_dir < 0f && transform.position.x <= _startX - patrolDistance) return true;
        }
        else
        {
            // 悬崖检测：前方没有地面就转身
            Vector2 edgeOrigin = groundCheck != null
                ? new Vector2(groundCheck.position.x + _dir * edgeCheckForward, FootY + 0.15f)
                : new Vector2(transform.position.x + _dir * edgeCheckForward, FootY + 0.15f);
            RaycastHit2D groundHit = Physics2D.Raycast(edgeOrigin, Vector2.down, edgeCheckDistance, groundLayer);
            if (groundHit.collider == null) return true;
        }

        if (turnAtWall)
        {
            Vector2 wallOrigin = new Vector2(transform.position.x, FootY + wallCheckHeight);
            RaycastHit2D wallHit = Physics2D.Raycast(wallOrigin, new Vector2(_dir, 0f), wallCheckDistance, groundLayer);
            if (wallHit.collider != null) return true;
        }

        return false;
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        HandleContact(collision.collider);
    }

    void OnCollisionStay2D(Collision2D collision)
    {
        HandleContact(collision.collider);
    }

    private void HandleContact(Collider2D other)
    {
        if (other == null) return;
        if (Time.time - _lastDamageTime < repeatInterval) return;

        if (killPlayer)
        {
            PlayerController player = other.GetComponentInParent<PlayerController>();
            if (player != null && !player.IsDead)
            {
                if (levelManager != null) levelManager.OnPlayerDied();
                _lastDamageTime = Time.time;
                PlayHit();
                return;
            }
        }

        SlimeController slime = other.GetComponentInParent<SlimeController>();
        if (slime != null && slime.State != SlimeState.Dead)
        {
            if (slime.IsCarried) return;   // 被抱着时免伤（不依赖碰撞矩阵也能成立）
            slime.TakeDamage(contactDamage);
            _lastDamageTime = Time.time;
            PlayHit();
        }
    }

    private void PlayHit()
    {
        if (audioSource != null && hitClip != null) audioSource.PlayOneShot(hitClip);
    }

    private void UpdateFlip()
    {
        if (spriteRoot == null) return;
        Vector3 scale = spriteRoot.localScale;
        scale.x = Mathf.Abs(scale.x) * _dir;
        spriteRoot.localScale = scale;
    }

    private float FootY
    {
        get { return bodyCollider != null ? bodyCollider.bounds.min.y : transform.position.y; }
    }

    private void OnDrawGizmosSelected()
    {
        float footY = bodyCollider != null ? bodyCollider.bounds.min.y : transform.position.y;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(new Vector3(transform.position.x + _dir * edgeCheckForward, footY + 0.15f, 0f),
                        new Vector3(transform.position.x + _dir * edgeCheckForward, footY + 0.15f - edgeCheckDistance, 0f));
    }
}
