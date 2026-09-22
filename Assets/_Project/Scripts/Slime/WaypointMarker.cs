using TMPro;
using UnityEngine;

/// <summary>
/// 职责：单个路径点标记。显示序号、提供到达半径、可跟随运动物体；本身不带 Collider，判定靠距离。
/// Inspector：拖 TextMeshPro（世界空间数字，可空）、SpriteRenderer（可空）、VisualRoot（可空）。
/// 依赖：无（纯被动，被 SlimePathFollow 持有并消费）。
/// 绑定到运动物体的两种做法：① 直接把路径点实例拖成移动平台的子物体；② 填 followTarget。
/// </summary>
public class WaypointMarker : MonoBehaviour
{
    [Header("引用（拖拽，均可空）")]
    public TextMeshPro indexText;
    public SpriteRenderer spriteRenderer;
    public Transform visualRoot;

    [Header("参数")]
    [Tooltip("序号，从 0 开始，由 SlimePathFollow 赋值")]
    public int markerIndex = 0;
    [Tooltip("史莱姆水平方向进入该半径即视为到达")]
    public float arriveRadius = 0.6f;
    [Tooltip("垂直方向容差：路径点高于该值说明史莱姆上不去，会卡住等待玩家")]
    public float arriveHeightTolerance = 1.5f;
    [Tooltip("跟随的目标（移动平台等），留空则固定在原地")]
    public Transform followTarget;
    public Vector3 followOffset = Vector3.zero;

    [Header("表现")]
    public Color normalColor = Color.white;
    public Color reachedColor = new Color(1f, 1f, 1f, 0.35f);
    public float bobAmplitude = 0.12f;
    public float bobSpeed = 2.5f;

    /// <summary>是否已被史莱姆走过。</summary>
    public bool Reached { get; private set; }

    private Vector3 _basePosition;
    private float _bobTime;

    void Awake()
    {
        _basePosition = transform.position;
    }

    void LateUpdate()
    {
        if (followTarget != null)
            _basePosition = followTarget.position + followOffset;

        _bobTime += Time.deltaTime;
        transform.position = _basePosition + Vector3.up * (Mathf.Sin(_bobTime * bobSpeed) * bobAmplitude);
    }

    /// <summary>设置序号（从 0 开始），自动刷新显示为 1/2/3。</summary>
    public void SetIndex(int index)
    {
        markerIndex = index;
        if (indexText != null) indexText.text = (index + 1).ToString();
    }

    /// <summary>标记为已走过 / 未走过，切换颜色。</summary>
    public void SetReached(bool reached)
    {
        Reached = reached;
        if (spriteRenderer != null)
            spriteRenderer.color = reached ? reachedColor : normalColor;
    }
}
