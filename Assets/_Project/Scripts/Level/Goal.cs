using UnityEngine;

/// <summary>
/// 职责：收购站。史莱姆进入触发区即通关（玩家单独站上去不算），触发一次后关闭自己。
/// Inspector：拖 LevelManager、AudioSource（可空）、粒子（可空）。Collider2D 勾 Is Trigger，Layer = TriggerZone。
/// 依赖：SlimeController、LevelManager。
/// 注意：默认允许"抱着史莱姆到达"，因为搬运是手段之一。
/// </summary>
public class Goal : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public LevelManager levelManager;
    public AudioSource audioSource;
    public ParticleSystem arriveEffect;

    [Header("规则")]
    [Tooltip("只有史莱姆到达才算通关；关掉则玩家到达也算（调试用）")]
    public bool onlySlime = true;
    [Tooltip("是否要求史莱姆没有被抱着")]
    public bool requireSlimeNotCarried = false;

    [Header("表现")]
    public AudioClip arriveClip;
    [Tooltip("呼吸缩放的目标节点，可空")]
    public Transform pulseTarget;
    public float pulseSpeed = 2f;
    public float pulseAmount = 0.06f;

    [Header("通关演出（美术 / 动画挂在 Visual 子物体上，逻辑物体保持干净）")]
    [Tooltip("美术节点：贴图 / Animator / 粒子都放这个子物体上，本物体只留碰撞体和脚本")]
    public Transform visualRoot;
    [Tooltip("通关时触发的 Animator，可空")]
    public Animator animator;
    [Tooltip("通关时发给 Animator 的 Trigger 名")]
    public string arriveTrigger = "Arrive";
    [Tooltip("演出时长（秒）。>0 时镜头会推近收购站，结算面板也会等这么久再弹；0 = 立即结算")]
    public float cutsceneDuration = 0f;
    [Tooltip("演出时镜头对准的点，留空 = 对准收购站自己")]
    public Transform cameraFocusPoint;
    [Tooltip("演出时的相机视野大小（越小越近），<=0 表示不改")]
    public float cutsceneCameraSize = 4.5f;
    [Tooltip("场景里的 CameraFollow，演出时接管它（可空）")]
    public CameraFollow cameraFollow;
    [Tooltip("演出时忽略相机边界限制（收购站贴着关卡边缘时建议开）")]
    public bool cutsceneIgnoreCameraBounds = true;

    private bool _triggered;
    private Vector3 _baseScale = Vector3.one;

    void Awake()
    {
        if (pulseTarget != null) _baseScale = pulseTarget.localScale;
    }

    void Update()
    {
        if (_triggered || pulseTarget == null) return;
        float t = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        pulseTarget.localScale = _baseScale * t;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        TryFinish(other);
    }

    void OnTriggerStay2D(Collider2D other)
    {
        TryFinish(other);
    }

    private void TryFinish(Collider2D other)
    {
        if (_triggered || levelManager == null) return;

        SlimeController slime = other.GetComponentInParent<SlimeController>();
        if (slime != null)
        {
            if (slime.State == SlimeState.Dead) return;
            if (requireSlimeNotCarried && slime.IsCarried) return;
        }
        else
        {
            if (onlySlime) return;
            PlayerController player = other.GetComponentInParent<PlayerController>();
            if (player == null || player.IsDead) return;
        }

        _triggered = true;

        // ---- 通关演出：先播动画 / 特效，再把镜头推近 ----
        if (visualRoot != null) visualRoot.gameObject.SetActive(true);
        if (animator != null && !string.IsNullOrEmpty(arriveTrigger)) animator.SetTrigger(arriveTrigger);
        if (audioSource != null && arriveClip != null) audioSource.PlayOneShot(arriveClip);
        if (arriveEffect != null) arriveEffect.Play();

        // 把演出时长告诉 LevelManager，UI 会据此推迟结算面板
        levelManager.SetGoalCutsceneDuration(cutsceneDuration);

        if (cutsceneDuration > 0f && cameraFollow != null)
        {
            Transform focus = cameraFocusPoint != null ? cameraFocusPoint : transform;
            cameraFollow.FocusOn(focus, cutsceneCameraSize, cutsceneIgnoreCameraBounds);
        }

        levelManager.OnSlimeReachedGoal();
    }
}
