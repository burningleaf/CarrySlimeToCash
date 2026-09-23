using UnityEngine;

/// <summary>
/// 职责：2D 相机跟随。平滑跟随目标，把视野限制在关卡边界内，并带垂直死区（跳一下不会把画面带飞）。
/// Inspector：挂在 Main Camera 上，把 Player 拖进 target；边界由关卡生成器自动填写。
/// 依赖：无。
/// 注意：这是为长关卡新增的第 23 个脚本（原设计 22 个脚本里没有相机跟随）。
/// 新增：两人（玩家 + 史莱姆）分开时按分离程度把视野自动拉远，保证两个人都留在画面里。
///       · Inspector 上把史莱姆拖进 secondTarget；留空 = 自动拉远关闭（Level0/1/2 不拖它，手感一模一样）。
///       · baseSize 是【关卡基准视野】（= 关卡数据 meta.cameraSize）。自动拉远只在此基础上放大，绝不改它。
///         导出器读的就是 baseSize —— 不能读 Camera.orthographicSize：导出那一刻相机可能正在拉远，
///         读当下值会把"拉远后的尺寸"永久写进 JSON，而 META 比对行含 cameraSize（LevelData.cs:388-391），
///         ⓪-3 往返就会报「不一致 ❌」。
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [Header("引用（拖拽）")]
    [Tooltip("跟随目标，一般是 Player")]
    public Transform target;
    [Tooltip("第二个跟随目标，一般是史莱姆。留空 = 关闭自动拉远（老关卡零影响）")]
    public Transform secondTarget;

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

    [Header("自动拉远（两人分开时把两个人都框进视野）")]
    [Tooltip("总开关。关掉 = 只跟随不缩放")]
    public bool autoZoomOut = true;
    [Tooltip("关卡【基准】视野大小（= 关卡数据 meta.cameraSize）。自动拉远只在这个基础上放大，永远不会改它。" +
             "填 0 或负数 = 启动时从相机当前尺寸取一次")]
    public float baseSize = 6.5f;
    [Tooltip("拉远上限倍数（相对 baseSize）。1 = 不放大；6 = 最多拉到 6 倍大（base 6.5 → 39），到顶就不再拉远")]
    public float maxZoomOutMultiplier = 6f;
    [Tooltip("把两人框进视野时四周多留的边距（世界单位）。也用来吸收相机跟随的滞后")]
    public float framingMargin = 1f;
    [Tooltip("拉远 / 回缩的平滑时间（秒），0 = 瞬间切换（会跳，不推荐）")]
    public float zoomSmoothTime = 0.35f;

    private Camera _camera;
    private Vector3 _velocity;
    private float _lockedY;
    private bool _hasLockedY;
    private bool _forceIgnoreBounds;

    private float _currentSize;           // 当前视野（平滑后的值）
    private float _sizeVelocity;          // SmoothDamp 的缓存
    private float _lastAppliedSize = -1f; // 我们最后一次写进相机的尺寸（用来分辨"相机尺寸被别人改了"）
    private bool _sizeLockedByFocus;      // 通关演出接管了镜头尺寸 → 自动拉远让位

    void Awake()
    {
        _camera = GetComponent<Camera>();
        if (_camera == null) _camera = Camera.main;

        // baseSize 填 0/负数 = 从相机当前尺寸取一次；否则用关卡数据写进来的值
        if (_camera != null && baseSize <= 0f) baseSize = _camera.orthographicSize;
        _currentSize = baseSize;
    }

    void Start()
    {
        SnapToTarget();
    }

    void LateUpdate()
    {
        if (target == null) return;

        if (_camera == null)
        {
            _camera = GetComponent<Camera>();
            if (_camera == null) _camera = Camera.main;
        }

        // 先维护"基准尺寸"，再按两人分离程度自动拉远。
        // 演出接管了镜头尺寸时整段让位（否则两人分开时会把演出的镜头拉回去）。
        if (!_sizeLockedByFocus)
        {
            AdoptExternalSizeAsBase();
            UpdateAutoZoom();
        }

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

    /// <summary>
    /// 自动拉远是否生效：总开关 + 拖了第二目标（史莱姆）+ 基准尺寸有效 + 演出没接管镜头。
    /// 任一不满足 → 一个尺寸都不写相机（老关卡零影响）。
    /// </summary>
    bool AutoZoomActive()
    {
        return autoZoomOut && secondTarget != null && target != null
               && _camera != null && baseSize > 0f && !_sizeLockedByFocus;
    }

    /// <summary>
    /// 别人（LevelBuilder 按 meta.cameraSize 写、演示脚本、手动改）动了相机尺寸 → 那就是新的基准尺寸。
    /// 自动拉远自己写进去的值会同步记到 _lastAppliedSize，所以不会被误当成"外部改动"。
    /// 这里只改 baseSize、不动相机 —— 所以没开自动拉远的关卡依然是零影响，但导出器读到的 base 是对的。
    /// </summary>
    void AdoptExternalSizeAsBase()
    {
        if (_camera == null) return;

        float camSize = _camera.orthographicSize;
        if (_lastAppliedSize < 0f || !Mathf.Approximately(_lastAppliedSize, camSize))
            baseSize = camSize;
        _lastAppliedSize = camSize;
    }

    /// <summary>每帧把视野平滑地推向"需要的值"。没有第二目标时直接返回，不碰相机。</summary>
    void UpdateAutoZoom()
    {
        if (!AutoZoomActive()) return;

        float desired = ComputeDesiredSize();
        _currentSize = zoomSmoothTime > 0f
            ? Mathf.SmoothDamp(_currentSize, desired, ref _sizeVelocity, zoomSmoothTime)
            : desired;
        ApplySize(_currentSize);
    }

    /// <summary>把视野写进相机，并记下"这个值是我们写的"。</summary>
    void ApplySize(float size)
    {
        if (_camera == null || !_camera.orthographic) return;

        _camera.orthographicSize = size;
        _lastAppliedSize = size;
    }

    /// <summary>
    /// 算出"两人都留在视野里"需要的视野大小（半高，世界单位）。
    /// 相机中心 ≈ target + offset（水平钉在目标上、垂直钉在死区内），
    /// 所以要求 = 从相机中心到两人里更远的那个的距离 + 留白；
    /// 再夹到 [baseSize, baseSize × maxZoomOutMultiplier] —— 分离为 0 时正好是 baseSize。
    /// </summary>
    float ComputeDesiredSize()
    {
        if (!AutoZoomActive()) return baseSize;

        Vector3 a = target.position;
        Vector3 b = secondTarget.position;
        float dx = b.x - a.x;
        float dy = b.y - a.y;

        // 水平：相机中心在 a.x + offset.x 附近；垂直：在 a.y + offset.y 附近，死区还会再多一个 verticalDeadZone 的余量
        float needHalfWidth = Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(dx - offset.x)) + framingMargin;
        float needHalfHeight = Mathf.Max(Mathf.Abs(offset.y), Mathf.Abs(dy - offset.y))
                             + verticalDeadZone + framingMargin;

        float aspect = _camera.aspect;
        float sizeFromWidth = aspect > 0f ? needHalfWidth / aspect : needHalfWidth;

        float minSize = baseSize;
        float maxSize = Mathf.Max(minSize, minSize * maxZoomOutMultiplier);   // 上限 = base × 倍数（6.5 → 39）
        return Mathf.Clamp(Mathf.Max(needHalfHeight, sizeFromWidth), minSize, maxSize);
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
    /// 演出期间镜头尺寸归演出管：自动拉远从此让位（否则两人分开时会把演出镜头拉回去）。
    /// </summary>
    public void FocusOn(Transform focusTarget, float size, bool ignoreBounds)
    {
        if (focusTarget == null) return;

        target = focusTarget;
        _forceIgnoreBounds = ignoreBounds;
        _hasLockedY = false;      // 让垂直死区重新锁定到新目标
        _sizeLockedByFocus = true; // 自动拉远让位（否则两人分开时会把演出的镜头拉回去）

        if (size > 0f)
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            _lastAppliedSize = size;   // 记成"我们写的"，免得下一帧被当成外部改动收进 baseSize
            if (_camera != null && _camera.orthographic) _camera.orthographicSize = size;
        }
    }

    /// <summary>立刻把相机对齐到目标（进关卡 / 复活时用）。</summary>
    /// <remarks>视野也一起定到"当前应有的值"，不能从错误尺寸弹一下。</remarks>
    public void SnapToTarget()
    {
        if (target == null) return;

        _lockedY = target.position.y;
        _hasLockedY = true;
        _velocity = Vector3.zero;

        if (_camera == null)
        {
            _camera = GetComponent<Camera>();
            if (_camera == null) _camera = Camera.main;
        }

        if (!_sizeLockedByFocus)
        {
            AdoptExternalSizeAsBase();
            if (AutoZoomActive())
            {
                _currentSize = ComputeDesiredSize();
                _sizeVelocity = 0f;
                ApplySize(_currentSize);
            }
        }

        Vector3 desired = ClampToBounds(target.position + offset);
        transform.position = desired;
    }
}
