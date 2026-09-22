using UnityEngine;

/// <summary>危险区类型。</summary>
public enum HazardType
{
    Spike = 0,   // 尖刺
    Water = 1,   // 水
    Fire = 2     // 火
}

/// <summary>
/// 职责：危险区。史莱姆碰到按 damage 扣血（进入 Scared + 无敌帧）；玩家碰到按 killPlayer 决定是否致死。
/// Inspector：拖 LevelManager、AudioSource（可空）、粒子（可空）。Collider2D 必须勾 Is Trigger，Layer = Hazard。
/// 依赖：SlimeController（TakeDamage）、LevelManager（玩家死亡）、PlayerController。
/// 注意：对怪物与收集物不生效（靠碰撞矩阵排除）；史莱姆被携带时切到 CarriedSlime 层，也不会触发本脚本。
/// </summary>
public class Hazard : MonoBehaviour
{
    [Header("危险类型与伤害")]
    public HazardType hazardType = HazardType.Spike;
    [Tooltip("史莱姆碰到掉多少血")]
    public int damage = 15;
    [Tooltip("玩家碰到是否致死")]
    public bool killPlayer = true;
    [Tooltip("玩家碰到时是【直接关卡失败】还是【从最近检查点复活】。\n" +
             "深坑底部的死亡判定块填 true —— 掉进深坑就是失败，不给复活。\n" +
             "留给以后的尖刺 / 怪物用 false（碰到扣一条命，回检查点继续）。")]
    public bool fatalToPlayer = false;
    [Tooltip("是否只生效一次（落石等一次性危险）")]
    public bool damageOnce = false;
    [Tooltip("同一个危险区的重复伤害间隔，0 = 只依赖史莱姆自身的无敌帧")]
    public float repeatInterval = 0f;

    [Header("引用（拖拽）")]
    public LevelManager levelManager;
    public AudioSource audioSource;
    public ParticleSystem hitEffect;

    [Header("音效")]
    public AudioClip hitClip;

    private bool _used;
    private float _lastDamageTime = -999f;

    void OnTriggerEnter2D(Collider2D other)
    {
        Handle(other);
    }

    void OnTriggerStay2D(Collider2D other)
    {
        Handle(other);
    }

    private void Handle(Collider2D other)
    {
        if (_used) return;
        if (repeatInterval > 0f && Time.time - _lastDamageTime < repeatInterval) return;

        // 史莱姆受伤
        SlimeController slime = other.GetComponentInParent<SlimeController>();
        if (slime != null)
        {
            if (slime.State == SlimeState.Dead) return;
            if (slime.CurrentHealth <= 0) return;
            if (slime.IsCarried) return;   // 被抱着时免伤（不依赖碰撞矩阵也能成立）

            slime.TakeDamage(damage, this);
            _lastDamageTime = Time.time;
            PlayFeedback();
            if (damageOnce) _used = true;
            return;
        }

        // 玩家致死
        if (!killPlayer) return;
        PlayerController player = other.GetComponentInParent<PlayerController>();
        if (player == null || player.IsDead) return;

        if (levelManager != null)
        {
            if (fatalToPlayer) levelManager.OnPlayerFellIntoPit();
            else levelManager.OnPlayerDied();
        }
        _lastDamageTime = Time.time;
        PlayFeedback();
        if (damageOnce) _used = true;
    }

    private void PlayFeedback()
    {
        if (audioSource != null && hitClip != null) audioSource.PlayOneShot(hitClip);
        if (hitEffect != null) hitEffect.Play();
    }
}
