using UnityEngine;

/// <summary>
/// 职责：唯一的动作键入口。按 PlayerInventory 当前物品用 switch 分发 E / Q 行为。
///   空手     ：E 抓取（近距离，半径 1.2，判定宽容）或放置；Q 投掷
///   哨子     ：E 切换史莱姆 Follow / Stay；Q 召回，召回中再按 Q 打断
///   引导石   ：E 放置路径点（最多 3 个）；Q 清除全部路径点
///   预留槽   ：无行为
/// Inspector：拖 PlayerController、PlayerInventory、SlimeController、SlimePathFollow、CarryPoint 空物体、AudioSource。
/// 依赖：PlayerController、PlayerInventory、SlimeController、SlimePathFollow。
/// 注意：全工程只有本脚本读 E / Q；携带 / 放置不改变史莱姆的 Follow / Stay 模式（模式只由玩家主动切换）。
/// </summary>
public class PlayerGrab : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public PlayerController player;
    public PlayerInventory inventory;
    public SlimeController slime;
    public SlimePathFollow pathFollow;
    [Tooltip("携带史莱姆时的挂点（空物体，建议挂在角色身前上方）")]
    public Transform carryPoint;
    public AudioSource audioSource;

    [Header("按键")]
    public KeyCode actionKey = KeyCode.E;
    public KeyCode secondaryKey = KeyCode.Q;

    [Header("抓取（判定要宽容）")]
    [Tooltip("抓取判定半径")]
    public float grabRadius = 1.2f;
    [Tooltip("史莱姆所在层，用于 OverlapCircle 宽容判定")]
    public LayerMask slimeLayerMask;
    [Tooltip("抓取 / 投掷后的再抓取冷却")]
    public float regrabDelay = 0.35f;

    [Header("放置")]
    public float placeOffsetX = 0.8f;
    public float placeOffsetY = 0.1f;
    [Tooltip("没有拖 CarryPoint 时，史莱姆挂在玩家上方这个高度")]
    public float carryHeightFallback = 1.0f;

    [Header("投掷")]
    public float throwOffsetX = 0.5f;
    public float throwOffsetY = 0.5f;
    public float throwForceForward = 7f;
    public float throwForceUp = 10f;

    [Header("引导石")]
    [Tooltip("路径点相对玩家的高度偏移")]
    public float waypointYOffset = 0.5f;

    [Header("音效")]
    public AudioClip grabClip;
    public AudioClip placeClip;
    public AudioClip throwClip;
    public AudioClip whistleClip;
    public AudioClip waypointClip;
    public AudioClip clearClip;
    public AudioClip errorClip;

    /// <summary>玩家当前是否抱着史莱姆。</summary>
    public bool IsCarrying { get; private set; }

    private float _regrabTimer;

    void Awake()
    {
        if (slimeLayerMask.value == 0)
        {
            slimeLayerMask = LayerMask.GetMask("Slime", "CarriedSlime");
            if (slimeLayerMask.value != 0)
                Debug.LogWarning("[PlayerGrab] slimeLayerMask 没配置，已自动兜底为 Slime|CarriedSlime");
        }

        if (carryPoint == null)
        {
            GameObject auto = new GameObject("CarryPoint_Auto");
            auto.transform.SetParent(transform, false);
            auto.transform.localPosition = new Vector3(0.25f, carryHeightFallback, 0f);
            carryPoint = auto.transform;
            Debug.LogWarning("[PlayerGrab] 没有拖 CarryPoint，已在运行时自动创建");
        }
    }

    void Update()
    {
        _regrabTimer -= Time.deltaTime;

        if (player == null || inventory == null) return;
        if (!player.ControlEnabled || player.IsDead) return;

        if (Input.GetKeyDown(actionKey)) DoAction();
        if (Input.GetKeyDown(secondaryKey)) DoSecondary();
    }

    // ---------------- E ----------------
    private void DoAction()
    {
        switch (inventory.CurrentItem)
        {
            case ItemType.None:
                if (IsCarrying) PlaceSlime();
                else TryGrabSlime();
                break;

            case ItemType.Whistle:
                if (slime != null)
                {
                    slime.ToggleMode();
                    PlayClip(whistleClip);
                }
                break;

            case ItemType.GuideStone:
                PlaceWaypoint();
                break;

            case ItemType.Slot4:
                // 预留扩展槽
                break;
        }
    }

    // ---------------- Q ----------------
    private void DoSecondary()
    {
        switch (inventory.CurrentItem)
        {
            case ItemType.None:
                if (IsCarrying) ThrowSlime();
                break;

            case ItemType.Whistle:
                if (slime != null)
                {
                    if (slime.IsRecalling) slime.CancelRecall();
                    else slime.StartRecall();
                    PlayClip(whistleClip);
                }
                break;

            case ItemType.GuideStone:
                if (pathFollow != null)
                {
                    // 撤销最后一个路径点并【返还 1 颗引导石】。反复按可以把石头全部收回来。
                    if (!pathFollow.RemoveLastWaypoint()) PlayClip(errorClip);
                }
                break;

            case ItemType.Slot4:
                break;
        }
    }

    // ---------------- 抓取 / 放置 / 投掷 ----------------
    private void TryGrabSlime()
    {
        if (_regrabTimer > 0f) return;

        SlimeController found = FindSlimeInRange();
        if (found == null)
        {
            PlayClip(errorClip);
            return;
        }

        found.EnterCarried(carryPoint != null ? carryPoint : transform);
        IsCarrying = true;
        player.SetCarrying(true);
        PlayClip(grabClip);
    }

    private SlimeController FindSlimeInRange()
    {
        // 1) 按层做宽容的圆形判定
        if (slimeLayerMask.value != 0)
        {
            Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, grabRadius, slimeLayerMask);
            for (int i = 0; i < hits.Length; i++)
            {
                SlimeController s = hits[i].GetComponentInParent<SlimeController>();
                if (s != null && s.State != SlimeState.Dead) return s;
            }
        }

        // 2) 兜底：直接对拖进来的史莱姆做距离判定（层没配好也能抓）
        if (slime != null && slime.State != SlimeState.Dead)
        {
            if (Vector2.Distance(transform.position, slime.transform.position) <= grabRadius)
                return slime;
        }

        return null;
    }

    private void PlaceSlime()
    {
        if (slime == null) return;

        Vector3 position = transform.position + new Vector3(player.FacingX * placeOffsetX, placeOffsetY, 0f);
        slime.ExitCarried(position, Vector2.zero);
        FinishCarry();
        PlayClip(placeClip);
    }

    private void ThrowSlime()
    {
        if (slime == null) return;

        Vector3 position = transform.position + new Vector3(player.FacingX * throwOffsetX, throwOffsetY, 0f);
        Vector2 velocity = new Vector2(player.FacingX * throwForceForward, throwForceUp);
        slime.ExitCarried(position, velocity);
        FinishCarry();
        PlayClip(throwClip);
    }

    private void FinishCarry()
    {
        IsCarrying = false;
        player.SetCarrying(false);
        _regrabTimer = regrabDelay;
    }

    // ---------------- 引导石 ----------------
    private void PlaceWaypoint()
    {
        if (pathFollow == null) return;

        Vector3 position = transform.position + new Vector3(0f, waypointYOffset, 0f);
        if (pathFollow.AddWaypoint(position)) PlayClip(waypointClip);
        else PlayClip(errorClip);
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, grabRadius);
    }
}
