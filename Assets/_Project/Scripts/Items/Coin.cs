using UnityEngine;

/// <summary>
/// 职责：金币。只对史莱姆生效——史莱姆碰到后加金币、播音效、播粒子，然后消失。
/// Inspector：拖 LevelManager、AudioSource（可空）、粒子（可空）。Collider2D 勾 Is Trigger，Layer = Pickup。
/// 依赖：SlimeController、LevelManager。
/// 注意：小孩碰到不触发。这一条由碰撞矩阵（Pickup × Player = 关闭）保证，本脚本再做一次组件判定兜底。
/// </summary>
public class Coin : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public LevelManager levelManager;
    public AudioSource audioSource;
    public ParticleSystem pickupEffect;

    [Header("数值")]
    public int value = 10;

    [Header("表现")]
    public AudioClip pickupClip;
    [Tooltip("吃掉后延迟多久销毁，用于让粒子播完")]
    public float destroyDelay = 0f;
    [Tooltip("上下浮动的幅度，0 = 不浮动")]
    public float bobAmplitude = 0.1f;
    public float bobSpeed = 2f;
    public float spinSpeed = 0f;

    private bool _collected;
    private Vector3 _basePosition;
    private float _bobTime;

    void Awake()
    {
        _basePosition = transform.position;
    }

    void Update()
    {
        if (_collected) return;

        if (bobAmplitude > 0f)
        {
            _bobTime += Time.deltaTime;
            transform.position = _basePosition + Vector3.up * (Mathf.Sin(_bobTime * bobSpeed) * bobAmplitude);
        }

        if (spinSpeed > 0f)
            transform.Rotate(0f, 0f, spinSpeed * Time.deltaTime);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_collected) return;

        SlimeController slime = other.GetComponentInParent<SlimeController>();
        if (slime == null) return;
        if (slime.State == SlimeState.Dead) return;

        _collected = true;

        if (levelManager != null) levelManager.AddCoin(value);
        if (audioSource != null && pickupClip != null) audioSource.PlayOneShot(pickupClip);

        if (pickupEffect != null)
        {
            pickupEffect.transform.SetParent(null, true);
            pickupEffect.Play();
            Destroy(pickupEffect.gameObject, pickupEffect.main.duration + pickupEffect.main.startLifetime.constantMax);
        }

        GetComponent<Collider2D>().enabled = false;
        if (GetComponent<SpriteRenderer>() != null) GetComponent<SpriteRenderer>().enabled = false;
        Destroy(gameObject, destroyDelay);
    }
}
