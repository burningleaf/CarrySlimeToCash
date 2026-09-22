using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 职责：引导石的路径点管理。按放置顺序创建、推进、清空；Stay 切回 Follow 时把"玩家当前位置"当作最后一个路径点。
/// Inspector：拖 SlimeController、PlayerController、WaypointMarker 预制体、WaypointContainer 空物体、AudioSource。
/// 依赖：SlimeController（只被它询问 HasPendingPath / CurrentTarget）、PlayerController、WaypointMarker。
/// 注意：史莱姆不寻路、不绕路、不传送——本脚本只提供"下一个目标点"，怎么走由 SlimeController 的直线蠕动决定。
/// </summary>
public class SlimePathFollow : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public SlimeController slime;
    public PlayerController player;
    [Tooltip("路径点预制体（挂 WaypointMarker）")]
    public WaypointMarker waypointPrefab;
    [Tooltip("路径点实例的父物体，建议建一个空物体 WaypointContainer")]
    public Transform waypointContainer;
    public AudioSource audioSource;

    [Header("引导石")]
    [Tooltip("开局给几颗引导石。放一颗少一颗，取消（撤销）一颗返还一颗。由 LevelBuilder 按关卡数据写入，默认 3")]
    public int stoneCount = 3;
    [Tooltip("到达判定半径，与 WaypointMarker 同步")]
    public float arriveRadius = 0.6f;
    [Tooltip("从 Stay 切回 Follow 时，是否把玩家位置作为最后一个路径点")]
    public bool usePlayerAsLastWaypoint = true;

    [Header("音效")]
    public AudioClip placeClip;
    public AudioClip reachedClip;
    public AudioClip clearClip;

    [Tooltip("当前存在的路径点，按放置顺序")]
    public List<WaypointMarker> waypoints = new List<WaypointMarker>();

    private int _index;
    private bool _playerIsLast;
    private bool _playerReached;

    /// <summary>当前路径点数量。</summary>
    public int WaypointCount { get { return waypoints != null ? waypoints.Count : 0; } }
    /// <summary>还剩几颗引导石没放。放一颗少一颗，撤销一颗返还一颗。</summary>
    public int StonesRemaining { get { return Mathf.Max(0, stoneCount - WaypointCount); } }
    /// <summary>还有没有石头可用。</summary>
    public bool HasStone { get { return StonesRemaining > 0; } }
    /// <summary>正在前往的路径点索引。</summary>
    public int CurrentIndex { get { return _index; } }
    /// <summary>是否还有未走完的路径（含"最后走到玩家身边"这一段）。</summary>
    public bool HasPendingPath
    {
        get
        {
            if (waypoints == null) return false;
            return _index < waypoints.Count || (_playerIsLast && !_playerReached);
        }
    }

    /// <summary>史莱姆当前应该走向的世界坐标。</summary>
    public Vector3 CurrentTarget
    {
        get
        {
            if (waypoints != null && _index < waypoints.Count && waypoints[_index] != null)
                return waypoints[_index].transform.position;
            if (player != null) return player.transform.position;
            return transform.position;
        }
    }

    void Update()
    {
        if (waypoints == null) return;

        // 跳过被销毁的引用（路径点挂在被销毁的移动物体上时）
        while (_index < waypoints.Count && waypoints[_index] == null) _index++;

        if (_index < waypoints.Count)
        {
            WaypointMarker marker = waypoints[_index];
            Vector3 slimePos = slime != null ? slime.transform.position : transform.position;
            Vector3 delta = marker.transform.position - slimePos;

            bool horizontalOk = Mathf.Abs(delta.x) <= marker.arriveRadius;
            bool verticalOk = Mathf.Abs(delta.y) <= marker.arriveHeightTolerance;

            if (horizontalOk && verticalOk)
            {
                marker.SetReached(true);
                _index++;
                PlayClip(reachedClip);
            }
        }
        else if (_playerIsLast && !_playerReached)
        {
            if (player == null)
            {
                _playerReached = true;
            }
            else
            {
                Vector3 slimePos = slime != null ? slime.transform.position : transform.position;
                Vector3 delta = player.transform.position - slimePos;
                if (Mathf.Abs(delta.x) <= arriveRadius && Mathf.Abs(delta.y) <= 1.5f)
                {
                    _playerReached = true;
                    PlayClip(reachedClip);
                }
            }
        }
    }

    /// <summary>放置一个路径点（消耗 1 颗引导石）。没石头或没配预制体时返回 false。</summary>
    public bool AddWaypoint(Vector3 worldPosition)
    {
        if (waypointPrefab == null) return false;
        if (waypoints == null) waypoints = new List<WaypointMarker>();
        if (!HasStone) return false;

        Transform parent = waypointContainer != null ? waypointContainer : transform;
        WaypointMarker marker = Instantiate(waypointPrefab, worldPosition, Quaternion.identity, parent);
        marker.arriveRadius = arriveRadius;
        marker.SetIndex(waypoints.Count);
        marker.SetReached(false);
        waypoints.Add(marker);
        PlayClip(placeClip);
        return true;
    }

    /// <summary>
    /// 撤销最后一个路径点，**返还 1 颗引导石**（引导石 Q 键）。
    /// 反复按可以把石头全部收回来。
    /// </summary>
    public bool RemoveLastWaypoint()
    {
        if (waypoints == null || waypoints.Count == 0) return false;

        int last = waypoints.Count - 1;
        if (waypoints[last] != null) Destroy(waypoints[last].gameObject);
        waypoints.RemoveAt(last);

        // 已经走过的那些点还留着，索引不能倒退到已到达的点之前
        if (_index > waypoints.Count) _index = waypoints.Count;
        if (waypoints.Count == 0)
        {
            _index = 0;
            _playerIsLast = false;
            _playerReached = false;
        }
        PlayClip(clearClip);
        return true;
    }

    /// <summary>清除全部路径点（返还全部引导石）。</summary>
    public void ClearAllWaypoints()
    {
        if (waypoints != null)
        {
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i] != null) Destroy(waypoints[i].gameObject);
            }
            waypoints.Clear();
        }
        _index = 0;
        _playerIsLast = false;
        _playerReached = false;
    }

    /// <summary>
    /// Stay 切回 Follow 时调用：重新从第 1 个路径点走起，并把玩家所在位置作为最后一个路径点。
    /// 没有路径点时不做任何事（直接常规跟随）。
    /// </summary>
    public void BeginFollowWithPlayerAsLast()
    {
        if (waypoints == null || waypoints.Count == 0) return;

        _index = 0;
        _playerReached = false;
        _playerIsLast = usePlayerAsLastWaypoint;

        for (int i = 0; i < waypoints.Count; i++)
        {
            if (waypoints[i] != null) waypoints[i].SetReached(false);
        }
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }

    private void OnDrawGizmosSelected()
    {
        if (waypoints == null) return;
        Gizmos.color = Color.cyan;
        for (int i = 0; i < waypoints.Count; i++)
        {
            if (waypoints[i] == null) continue;
            Gizmos.DrawWireSphere(waypoints[i].transform.position, arriveRadius);
        }
    }
}
