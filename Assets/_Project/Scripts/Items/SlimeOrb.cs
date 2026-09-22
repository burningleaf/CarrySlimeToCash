using UnityEngine;

/// <summary>
/// 职责：史莱姆球。只对史莱姆生效——史莱姆碰到后回血、播音效、播粒子，然后消失（可选延时重生）。
/// Inspector：拖 SlimeController、AudioSource（可空）、粒子（可空）。Collider2D 勾 Is Trigger，Layer = Pickup。
/// 依赖：SlimeController.Heal(int)。
/// 注意：小孩碰到不触发（碰撞矩阵 Pickup × Player = 关闭 + 组件判定兜底）。
/// </summary>
public class SlimeOrb : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public SlimeController slime;
    public AudioSource audioSource;
    public ParticleSystem pickupEffect;

    [Header("数值")]
    [Tooltip("回复的血量")]
    public int healAmount = 25;
    [Tooltip("血量满了还能不能吃（吃了就浪费）")]
    public bool allowOverheal = false;
    [Tooltip("大于 0 时被吃掉后隔多久重新出现")]
    public float respawnTime = 0f;

    [Header("表现")]
    public AudioClip pickupClip;
    public float bobAmplitude = 0.12f;
    public float bobSpeed = 2f;

    private bool _collected;
    private Vector3 _basePosition;
    private float _bobTime;
    private SpriteRenderer _spriteRenderer;
    private Collider2D _collider;

    void Awake()
    {
        _basePosition = transform.position;
        _spriteRenderer = GetComponent<SpriteRenderer>();
        _collider = GetComponent<Collider2D>();
    }

    void Update()
    {
        if (_collected || bobAmplitude <= 0f) return;
        _bobTime += Time.deltaTime;
        transform.position = _basePosition + Vector3.up * (Mathf.Sin(_bobTime * bobSpeed) * bobAmplitude);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_collected) return;

        SlimeController target = other.GetComponentInParent<SlimeController>();
        if (target == null) target = slime;
        if (target == null) return;
        if (target.State == SlimeState.Dead) return;
        if (!allowOverheal && target.CurrentHealth >= target.MaxHealth) return;

        _collected = true;

        target.Heal(healAmount);
        if (audioSource != null && pickupClip != null) audioSource.PlayOneShot(pickupClip);

        if (pickupEffect != null)
        {
            pickupEffect.transform.SetParent(null, true);
            pickupEffect.Play();
            Destroy(pickupEffect.gameObject, pickupEffect.main.duration + pickupEffect.main.startLifetime.constantMax);
        }

        if (_collider != null) _collider.enabled = false;
        if (_spriteRenderer != null) _spriteRenderer.enabled = false;

        if (respawnTime > 0f) Invoke("Respawn", respawnTime);
        else Destroy(gameObject);
    }

    private void Respawn()
    {
        transform.position = _basePosition;
        if (_collider != null) _collider.enabled = true;
        if (_spriteRenderer != null) _spriteRenderer.enabled = true;
        _collected = false;
    }
}
