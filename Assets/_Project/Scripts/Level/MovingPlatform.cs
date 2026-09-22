using System.Collections.Generic;
using UnityEngine;

/// <summary>移动平台的路线模式。</summary>
public enum MoveMode
{
    PingPong = 0,  // 往返
    Loop = 1,      // 环形循环
    Once = 2       // 走一次就停
}

/// <summary>
/// 职责：沿固定路线移动的平台，可承载玩家与史莱姆，可被压力板开关。
/// Inspector：拖 Rigidbody2D（Kinematic）、路线点 Transform 数组（至少 2 个，建议用空物体做路点）。
/// 依赖：被 PressurePlate 调用 SetActivated(bool)。
/// 承载实现：FixedUpdate 用 MovePosition 移动，并把本帧位移补偿给站在平台上的刚体。
/// 若出现抖动，可关掉 carryRider（或改用把角色 parent 到平台的做法）。
/// </summary>
public class MovingPlatform : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public Rigidbody2D body;
    [Tooltip("路线点，至少 2 个，按顺序走")]
    public Transform[] points;

    [Header("移动")]
    public MoveMode moveMode = MoveMode.PingPong;
    public float speed = 2f;
    [Tooltip("每到一个路点的停留时间")]
    public float waitTime = 0.5f;
    public bool startActivated = true;
    [Tooltip("勾上后由压力板控制，开局保持静止")]
    public bool activatedByPlate = false;

    [Header("承载")]
    [Tooltip("把平台位移补偿给站在上面的刚体")]
    public bool carryRider = true;

    private readonly List<Rigidbody2D> _riders = new List<Rigidbody2D>();
    private int _targetIndex = 1;
    private int _direction = 1;
    private float _waitUntil;
    private bool _active;
    private bool _reachedEnd;
    private Vector2 _lastPosition;

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.bodyType = RigidbodyType2D.Kinematic;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
            _lastPosition = body.position;
        }

        _active = activatedByPlate ? false : startActivated;
        if (points != null && points.Length > 1) _targetIndex = 1;
    }

    void FixedUpdate()
    {
        if (body == null || points == null || points.Length < 2) return;

        if (!_active || _reachedEnd || Time.time < _waitUntil)
        {
            _lastPosition = body.position;
            return;
        }

        Transform target = points[Mathf.Clamp(_targetIndex, 0, points.Length - 1)];
        if (target == null) return;

        Vector2 current = _lastPosition;
        Vector2 next = Vector2.MoveTowards(current, target.position, speed * Time.fixedDeltaTime);
        Vector2 delta = next - current;

        body.MovePosition(next);
        _lastPosition = next;

        ApplyDeltaToRiders(delta);

        if (Vector2.Distance(next, target.position) <= 0.001f)
        {
            AdvanceTarget();
            _waitUntil = Time.time + waitTime;
        }
    }

    private void AdvanceTarget()
    {
        switch (moveMode)
        {
            case MoveMode.PingPong:
                if (_targetIndex >= points.Length - 1) _direction = -1;
                else if (_targetIndex <= 0) _direction = 1;
                _targetIndex = Mathf.Clamp(_targetIndex + _direction, 0, points.Length - 1);
                break;

            case MoveMode.Loop:
                _targetIndex = (_targetIndex + 1) % points.Length;
                break;

            case MoveMode.Once:
                if (_targetIndex >= points.Length - 1) _reachedEnd = true;
                else _targetIndex++;
                break;
        }
    }

    /// <summary>供压力板 / 机关调用。</summary>
    public void SetActivated(bool activated)
    {
        _active = activated;
    }

    private void ApplyDeltaToRiders(Vector2 delta)
    {
        if (!carryRider || delta == Vector2.zero) return;

        for (int i = _riders.Count - 1; i >= 0; i--)
        {
            Rigidbody2D rider = _riders[i];
            if (rider == null)
            {
                _riders.RemoveAt(i);
                continue;
            }
            rider.position += delta;
        }
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        AddRider(collision.rigidbody);
    }

    void OnCollisionExit2D(Collision2D collision)
    {
        RemoveRider(collision.rigidbody);
    }

    private void AddRider(Rigidbody2D rider)
    {
        if (!carryRider || rider == null || rider == body) return;
        if (!_riders.Contains(rider)) _riders.Add(rider);
    }

    private void RemoveRider(Rigidbody2D rider)
    {
        if (rider == null) return;
        _riders.Remove(rider);
    }

    private void OnDrawGizmosSelected()
    {
        if (points == null) return;
        Gizmos.color = Color.magenta;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            Gizmos.DrawWireSphere(points[i].position, 0.2f);
            if (i + 1 < points.Length && points[i + 1] != null)
                Gizmos.DrawLine(points[i].position, points[i + 1].position);
        }
    }
}
