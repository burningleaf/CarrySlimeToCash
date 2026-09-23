using UnityEngine;

/// <summary>物品类型。用 enum + switch 管理，不使用接口与 ScriptableObject。</summary>
public enum ItemType
{
    None = 0,         // 空手：E 抓取/放置，Q 投掷
    Whistle = 1,      // 哨子：E 切换史莱姆模式，Q 召回 / 打断召回
    GuideStone = 2,   // 引导石：E 放路径点，Q 清除全部路径点
    CloudBottle = 3   // 跳跃云朵瓶（第 4 格）：拿着可无限次空中跳；与"举史莱姆"彻底互斥
}

/// <summary>
/// 职责：物品栏。4 个槽位，同时只能装备一个物品；数字键切换；携带史莱姆时禁止切换。
/// Inspector：拖 PlayerController、AudioSource（可空）。
/// 依赖：PlayerController（读 IsCarrying；写 SetInfiniteAirJump）。PlayerGrab 读本脚本的 CurrentItem 决定 E/Q 的行为。
/// 能力来源：空中跳不是默认能力 —— 本脚本每帧把"当前物品赋予的能力"推给 PlayerController，
///   拿着【跳跃云朵瓶】= 无限次空中跳，其它物品 / 空手 = 没有空中跳。
/// 发放来源：每关发哪些物品由关卡数据决定（LevelData.meta.itemGrants → LevelBuilder.Build → itemGrants 字段）。
/// </summary>
public class PlayerInventory : MonoBehaviour
{
    [Header("引用（拖拽）")]
    public PlayerController player;
    public AudioSource audioSource;

    [Header("槽位")]
    [Tooltip("槽位数量，默认 4，预留扩展")]
    public int slotCount = 4;
    [Tooltip("槽位内容：空手 / 哨子 / 引导石 / 跳跃云朵瓶")]
    public ItemType[] slots = new ItemType[] { ItemType.None, ItemType.Whistle, ItemType.GuideStone, ItemType.CloudBottle };
    [Tooltip("当前装备的槽位索引")]
    public int currentSlot = 0;

    [Header("本关发放哪些物品（由 LevelBuilder 按关卡数据写入）")]
    [Tooltip("按【槽位序号】排列的发放开关，索引 = ItemType 值：0=空手 / 1=哨子 / 2=引导石 / 3=跳跃云朵瓶。\n" +
             "true = 本关发放这件物品；false = 该格按不动（和「携带史莱姆时锁定」同一套反馈），也拿不到它的能力。\n" +
             "⚠ 空手（索引 0）永远可用，写 false 不生效 —— 关掉它等于把抓取 / 投掷 / 放置全砍了。\n" +
             "链路：LevelData.meta.itemGrants → LevelBuilder.Build 写进这里；导出时 LevelDataWindow 读回来。\n" +
             "数组留空 / 不等长一律按「发放」兜底（保守：宁可全给，也别把关卡玩成缺件）。demo 阶段 = 四件全给。")]
    public bool[] itemGrants = new bool[] { true, true, true, true };

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
    /// <summary>当前物品是否赋予【无限次空中跳】能力（拿着跳跃云朵瓶，且本关发放了它）。</summary>
    public bool AllowsAirJump
    {
        get { return CurrentItem == ItemType.CloudBottle && IsSlotAvailable(currentSlot); }
    }
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
        slots = new ItemType[] { ItemType.None, ItemType.Whistle, ItemType.GuideStone, ItemType.CloudBottle };
        slotKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };
    }

    void Start()
    {
        // 本关没发的物品不该"开局就选中"着（否则会留下"拿着云朵瓶但不能空中跳"的半截状态）
        if (!IsSlotAvailable(currentSlot)) currentSlot = 0;
        ApplyAbilityState();
    }

    void Update()
    {
        // 先推能力，再读按键：即使 slotKeys 没配，能力也跟得上；槽位被别处改动也能跟上。
        ApplyAbilityState();

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

    /// <summary>切换槽位。被携带状态锁定 / 本关没给这个物品时返回 false 并播放提示音。</summary>
    public bool SelectSlot(int index)
    {
        if (index < 0 || index >= slotCount) return false;

        // 本关没给这件物品 → 按不动（复用"锁定"那一声，不新增音频资源）
        if (!IsSlotAvailable(index))
        {
            PlayClip(lockedClip);
            return false;
        }

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

    /// <summary>
    /// 某个槽位在本关是否可用 = 本关发放了这件物品。
    /// 索引 = 槽位序号 = ItemType 值；开关来自关卡数据（LevelData.meta.itemGrants）。
    /// 两条兜底规则：
    ///   · 空手永远可用 —— 它不是"发放的物品"，是"没有物品"这个状态（关掉它等于砍掉抓取/投掷）
    ///   · 数组留空/越界一律按"发放"（保守，免得老场景/老 JSON 把物品全关掉）
    /// </summary>
    public bool IsSlotAvailable(int index)
    {
        if (slots == null || index < 0 || index >= slots.Length) return false;

        if (slots[index] == ItemType.None) return true;

        if (itemGrants == null || index >= itemGrants.Length) return true;

        return itemGrants[index];
    }

    /// <summary>读取某个槽位的物品，越界返回 None。</summary>
    public ItemType GetSlot(int index)
    {
        if (slots == null || index < 0 || index >= slots.Length) return ItemType.None;
        return slots[index];
    }

    /// <summary>
    /// 把"当前物品赋予的能力"推给 PlayerController（幂等，每帧推）。
    /// ⚠ 能力挂在使用者身上、由物品驱动 —— 不是给跳跃加计数器/额外跳次数：
    ///   拿着云朵瓶时玩家根本举不起史莱姆（见 PlayerGrab），而"抱着史莱姆贴脚边刷新增益"
    ///   那条路已经在 PlayerController 里用掩码分离堵死了。
    /// </summary>
    private void ApplyAbilityState()
    {
        if (player == null) return;
        player.SetInfiniteAirJump(AllowsAirJump);
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }
}
