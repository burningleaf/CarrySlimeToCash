// InventoryUI.cs —— 物品栏 UI（拉模式：只读 PlayerInventory）
// 职责：显示各槽位物品图标与中文名、当前槽高亮；携带史莱姆（IsSwitchLocked）时高亮转灰并显示 lockedOverlay。
// 实现：缓存上一次的 CurrentSlot / IsSwitchLocked / 各槽 ItemType，只在发生变化时才刷新，避免每帧 SetSprite。
// Inspector 需要拖：playerInventory、slotFrames / slotIcons / slotIndexTexts（长度建议 = 槽位数）、
//       noneIcon / whistleIcon / guideStoneIcon / slot4Icon、lockedOverlay（可空）。
// 依赖核心脚本：PlayerInventory（只读 CurrentSlot / IsSwitchLocked / GetSlot / slotCount）与顶层枚举 ItemType。
// 硬性约束：不使用事件系统；所有数组访问都做长度保护，缺引用时静默跳过。

using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class InventoryUI : MonoBehaviour
{
    [Header("核心逻辑引用")]
    [Tooltip("玩家物品栏：读取 CurrentSlot / IsSwitchLocked / GetSlot(i)")]
    public PlayerInventory playerInventory;

    [Header("槽位视图（数组长度建议一致，均按索引对应槽位 0..N-1）")]
    [Tooltip("槽位边框图（当前槽高亮 / 锁定时转灰）")]
    public Image[] slotFrames;

    [Tooltip("槽位物品图标")]
    public Image[] slotIcons;

    [Tooltip("槽位文字：优先显示 itemDisplayNames 里的物品中文名，未配置时回退为槽位序号 1..N")]
    public TMP_Text[] slotIndexTexts;

    [Header("物品图标（按 ItemType 用 switch 选择）")]
    [Tooltip("空手 None 的图标")]
    public Sprite noneIcon;

    [Tooltip("哨子 Whistle 的图标")]
    public Sprite whistleIcon;

    [Tooltip("引导石 GuideStone 的图标")]
    public Sprite guideStoneIcon;

    [Tooltip("预留槽 Slot4 的图标")]
    public Sprite slot4Icon;

    [Header("槽位边框颜色")]
    [Tooltip("普通槽位颜色")]
    public Color normalFrameColor = Color.white;

    [Tooltip("当前选中槽位的颜色")]
    public Color selectedFrameColor = Color.yellow;

    [Tooltip("携带史莱姆（物品栏锁定）时当前槽的替代颜色")]
    public Color lockedFrameColor = Color.gray;

    [Header("锁定遮罩")]
    [Tooltip("携带史莱姆（IsSwitchLocked = true）时显示的遮罩物体；可空")]
    public GameObject lockedOverlay;

    [Header("物品中文名（长度 4，索引对应 ItemType：None/Whistle/GuideStone/Slot4）")]
    public string[] itemDisplayNames = new string[] { "空手", "哨子", "引导石", "预留" };

    private int _lastViewCount = -1;
    private int _lastSlot = -1;
    private bool _lastLocked;
    private ItemType[] _lastItems;
    private bool _hasCache;

    private void Start()
    {
        // 先画一次，避免第一帧显示默认图标
        RefreshView();
    }

    private void Update()
    {
        if (playerInventory == null)
        {
            return;
        }

        int count = GetViewCount();
        if (_hasCache && !HasChanged(count))
        {
            return;
        }

        RefreshView();
    }

    // 三个视图数组中最长的长度：逐个索引做长度保护，不会越界
    private int GetViewCount()
    {
        int count = 0;

        if (slotFrames != null)
        {
            count = Mathf.Max(count, slotFrames.Length);
        }

        if (slotIcons != null)
        {
            count = Mathf.Max(count, slotIcons.Length);
        }

        if (slotIndexTexts != null)
        {
            count = Mathf.Max(count, slotIndexTexts.Length);
        }

        return count;
    }

    private void RefreshView()
    {
        int count = GetViewCount();
        int currentSlot = 0;
        bool locked = false;

        if (playerInventory != null)
        {
            currentSlot = playerInventory.CurrentSlot;
            locked = playerInventory.IsSwitchLocked;
        }

        if (_lastItems == null || _lastItems.Length != count)
        {
            _lastItems = new ItemType[count];
        }

        for (int i = 0; i < count; i++)
        {
            ItemType item = GetItemAt(i);
            _lastItems[i] = item;

            if (slotFrames != null && i < slotFrames.Length && slotFrames[i] != null)
            {
                Image frame = slotFrames[i];
                if (i == currentSlot)
                {
                    // 携带史莱姆时"高亮变灰"
                    frame.color = locked ? lockedFrameColor : selectedFrameColor;
                }
                else
                {
                    frame.color = normalFrameColor;
                }
            }

            if (slotIcons != null && i < slotIcons.Length && slotIcons[i] != null)
            {
                Sprite icon = GetIcon(item);
                if (icon != null)
                {
                    slotIcons[i].sprite = icon;
                }
            }

            if (slotIndexTexts != null && i < slotIndexTexts.Length && slotIndexTexts[i] != null)
            {
                string displayName = GetDisplayName(item);
                slotIndexTexts[i].text = string.IsNullOrEmpty(displayName) ? (i + 1).ToString() : displayName;
            }
        }

        if (lockedOverlay != null)
        {
            lockedOverlay.SetActive(locked);
        }

        _lastViewCount = count;
        _lastSlot = currentSlot;
        _lastLocked = locked;
        _hasCache = playerInventory != null;
    }

    // 只有缓存值真的变化时才返回 true
    private bool HasChanged(int count)
    {
        if (_lastViewCount != count || _lastSlot != playerInventory.CurrentSlot || _lastLocked != playerInventory.IsSwitchLocked)
        {
            return true;
        }

        if (_lastItems == null)
        {
            return true;
        }

        for (int i = 0; i < count; i++)
        {
            if (i >= _lastItems.Length || _lastItems[i] != GetItemAt(i))
            {
                return true;
            }
        }

        return false;
    }

    // 槽位越界时返回 None，避免调用到核心层的越界分支
    private ItemType GetItemAt(int index)
    {
        if (playerInventory == null || index < 0)
        {
            return ItemType.None;
        }

        if (playerInventory.slotCount > 0 && index >= playerInventory.slotCount)
        {
            return ItemType.None;
        }

        return playerInventory.GetSlot(index);
    }

    // 图标选择：ItemType + switch
    private Sprite GetIcon(ItemType type)
    {
        switch (type)
        {
            case ItemType.Whistle:
                return whistleIcon;
            case ItemType.GuideStone:
                return guideStoneIcon;
            case ItemType.Slot4:
                return slot4Icon;
            case ItemType.None:
                return noneIcon;
            default:
                return noneIcon;
        }
    }

    private string GetDisplayName(ItemType type)
    {
        if (itemDisplayNames == null)
        {
            return null;
        }

        int index = (int)type;
        if (index < 0 || index >= itemDisplayNames.Length)
        {
            return null;
        }

        return itemDisplayNames[index];
    }
}
