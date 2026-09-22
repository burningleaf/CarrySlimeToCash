using UnityEngine;

/// <summary>
/// 职责：2D 相机跟随。平滑跟随目标，把视野限制在关卡边界内，并带垂直死区（跳一下不会把画面带飞）。
/// Inspector：挂在 Main Camera 上，把 Player 拖进 target；边界由关卡生成器自动填写。
/// 依赖：无。
/// 注意：这是为长关卡新增的第 23 个脚本（原设计 22 个脚本里没有相机跟随）。
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [Header("引用（拖拽）")]
    [Tooltip("跟随目标，一般是 Player")]
    public Transform target;

    [Header("跟随")]
    [Tooltip("相机相对目标的偏移（z 必须是负值）")]
    public Vector3 offset = new Vector3(0f, 1.5f, -10f);
    [Tooltip("平滑时间，0 = 硬跟随")]
    public float smoothTime = 0.12f;
    [Tooltip("垂直死区：目标上下移动不超过这个范围时相机不动")]
    public float verticalDeadZone = 1.5f;

    [Header("边界限制（世界坐标）")]
    public bool useBounds = true;
    public Vector2 boundsMin = new Vector2(-100f, -20f);
    public Vector2 boundsMax = new Vector2(100f, 20f);

    private Camera _camera;
    private Vector3 _velocity;
    private float _lockedY;
    private bool _hasLockedY;
    private bool _forceIgnoreBounds;

    void Awake()
    {
        _camera = GetComponent<Camera>();
        if (_camera == null) _camera = Camera.main;
    }

    void Start()
    {
        SnapToTarget();
    }

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desired = target.position + offset;

        // 垂直死区：只在目标明显上下移动时才让相机跟着走
        if (!_hasLockedY || Mathf.Abs(target.position.y - _lockedY) > verticalDeadZone)
        {
            _lockedY = target.position.y;
            _hasLockedY = true;
        }
        desired.y = _lockedY + offset.y;

        desired = ClampToBounds(desired);

        transform.position = smoothTime > 0f
            ? Vector3.SmoothDamp(transform.position, desired, ref _velocity, smoothTime)
            : desired;
    }

    /// <summary>把相机位置夹在关卡边界内（保证不会拍到地图外的空白）。</summary>
    private Vector3 ClampToBounds(Vector3 position)
    {
        if (!useBounds || _forceIgnoreBounds) return position;

        float halfHeight = 5f;
        float halfWidth = 9f;
        if (_camera != null)
        {
            halfHeight = _camera.orthographic ? _camera.orthographicSize : 5f;
            halfWidth = halfHeight * Mathf.Max(0.1f, _camera.aspect);
        }

        float minX = boundsMin.x + halfWidth;
        float maxX = boundsMax.x - halfWidth;
        float minY = boundsMin.y + halfHeight;
        float maxY = boundsMax.y - halfHeight;

        // 关卡比视野还窄时居中对齐，避免抖动
        position.x = minX <= maxX ? Mathf.Clamp(position.x, minX, maxX) : (boundsMin.x + boundsMax.x) * 0.5f;
        position.y = minY <= maxY ? Mathf.Clamp(position.y, minY, maxY) : (boundsMin.y + boundsMax.y) * 0.5f;
        return position;
    }

    /// <summary>
    /// 演出用：把镜头切到某个目标上，并改变视野大小。
    /// 通关动画想让镜头推到收购站时调它。size <= 0 表示不改视野。
    /// ignoreBounds = true 时忽略关卡边界（收购站靠边时相机会真正居中过去）。
    /// </summary>
    public void FocusOn(Transform focusTarget, float size, bool ignoreBounds)
    {
        if (focusTarget == null) return;

        target = focusTarget;
        _forceIgnoreBounds = ignoreBounds;
        _hasLockedY = false;      // 让垂直死区重新锁定到新目标

        if (size > 0f)
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            if (_camera != null && _camera.orthographic) _camera.orthographicSize = size;
        }
    }

    /// <summary>立刻把相机对齐到目标（进关卡 / 复活时用）。</summary>
    public void SnapToTarget()
    {
        if (target == null) return;

        _lockedY = target.position.y;
        _hasLockedY = true;
        _velocity = Vector3.zero;

        Vector3 desired = ClampToBounds(target.position + offset);
        transform.position = desired;
    }
}
