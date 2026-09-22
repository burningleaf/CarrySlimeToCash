using UnityEngine;

/// <summary>物品类型。用 enum + switch 管理，不使用接口与 ScriptableObject。</summary>
public enum ItemType
{
    None = 0,        // 空手：E 抓取/放置，Q 投掷
    Whistle = 1,     // 哨子：E 切换史莱姆模式，Q 召回 / 打断召回
    GuideStone = 2,  // 引导石：E 放路径点，Q 清除全部路径点
    Slot4 = 3        // 预留扩展槽
}

/// <summary>
/// 职责：物品栏。4 个槽位，同时只能装备一个物品；数字键切换；携带史莱姆时禁止切换。
/// Inspector：拖 PlayerController、AudioSource（可空）。
/// 依赖：PlayerController（读 IsCarrying）。PlayerGrab 读本脚本的 CurrentItem 决定 E/Q 的行为。
/// </summary>
public class PlayerInventory : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public PlayerController player;
    public AudioSource audioSource;

    [Header("槽位")]
    [Tooltip("槽位数量，默认 4，预留扩展")]
    public int slotCount = 4;
    [Tooltip("槽位内容：空手 / 哨子 / 引导石 / 预留")]
    public ItemType[] slots = new ItemType[] { ItemType.None, ItemType.Whistle, ItemType.GuideStone, ItemType.Slot4 };
    [Tooltip("当前装备的槽位索引")]
    public int currentSlot = 0;

    [Header("按键")]
    public KeyCode[] slotKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

    [Header("限制")]
    [Tooltip("携带史莱姆时是否允许切换物品（按规则默认关闭）")]
    public bool allowSwitchWhileCarrying = false;

    [Header("音效")]
    public AudioClip switchClip;
    public AudioClip lockedClip;

    /// <summary>当前装备的物品。</summary>
    public ItemType CurrentItem { get { return GetSlot(currentSlot); } }
    /// <summary>当前槽位索引。</summary>
    public int CurrentSlot { get { return currentSlot; } }
    /// <summary>是否因携带史莱姆而锁定切换。</summary>
    public bool IsSwitchLocked
    {
        get
        {
            if (allowSwitchWhileCarrying) return false;
            return player != null && player.IsCarrying;
        }
    }

    void Reset()
    {
        // 组件刚挂上时保证数组长度正确（Inspector 里可继续改）
        slotCount = 4;
        slots = new ItemType[] { ItemType.None, ItemType.Whistle, ItemType.GuideStone, ItemType.Slot4 };
        slotKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };
    }

    void Update()
    {
        if (slotKeys == null) return;
        for (int i = 0; i < slotKeys.Length && i < slotCount; i++)
        {
            if (Input.GetKeyDown(slotKeys[i]))
            {
                SelectSlot(i);
                break;
            }
        }
    }

    /// <summary>切换槽位。被携带状态锁定时返回 false 并播放提示音。</summary>
    public bool SelectSlot(int index)
    {
        if (index < 0 || index >= slotCount) return false;
        if (index == currentSlot) return true;

        if (IsSwitchLocked)
        {
            PlayClip(lockedClip);
            return false;
        }

        currentSlot = index;
        PlayClip(switchClip);
        return true;
    }

    /// <summary>读取某个槽位的物品，越界返回 None。</summary>
    public ItemType GetSlot(int index)
    {
        if (slots == null || index < 0 || index >= slots.Length) return ItemType.None;
        return slots[index];
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }
}
