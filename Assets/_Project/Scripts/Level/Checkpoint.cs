using UnityEngine;

/// <summary>
/// 职责：检查点。玩家或史莱姆经过即记录复活点（写入 LevelManager），只生效一次。
/// Inspector：拖 LevelManager、RespawnPoint 空物体（放在地面上方约 0.5，留空则用自身位置）、ActiveVisual 节点（可空）。
/// 依赖：LevelManager、PlayerController、SlimeController。
/// 说明：玩家碰怪死亡 → 从最近检查点复活并保留金币；史莱姆死亡 → 整关失败重开，与本脚本无关。
/// </summary>
public class Checkpoint : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public LevelManager levelManager;
    [Tooltip("复活位置，留空则用检查点自身位置")]
    public Transform respawnPoint;
    [Tooltip("激活后的视觉节点，可空")]
    public Transform activeVisual;
    public AudioSource audioSource;

    [Header("规则")]
    public bool forPlayer = true;
    public bool forSlime = true;

    [Header("表现")]
    public AudioClip activateClip;

    private bool _activated;

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_activated || levelManager == null) return;

        bool hitPlayer = forPlayer && other.GetComponentInParent<PlayerController>() != null;
        bool hitSlime = forSlime && other.GetComponentInParent<SlimeController>() != null;
        if (!hitPlayer && !hitSlime) return;

        _activated = true;
        Vector3 position = respawnPoint != null ? respawnPoint.position : transform.position;
        levelManager.SetCheckpoint(position);

        if (activeVisual != null) activeVisual.gameObject.SetActive(true);
        if (audioSource != null && activateClip != null) audioSource.PlayOneShot(activateClip);
    }
}
