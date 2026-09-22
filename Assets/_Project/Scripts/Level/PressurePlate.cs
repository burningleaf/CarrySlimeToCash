using UnityEngine;

/// <summary>
/// 职责：压力板。玩家 / 史莱姆 / 怪物 / 移动平台压住即激活目标（移动平台、门、机关），离开则复位。
/// Inspector：拖 MovingPlatform 数组、被控制的物体数组、PlateVisual 节点、AudioSource（可空）。
///   Collider2D 勾 Is Trigger，Layer = TriggerZone。
/// 依赖：MovingPlatform.SetActivated(bool)。不使用 UnityEvent。
/// 表现：压住时 PlateVisual 下沉 pressDepth。
/// </summary>
public class PressurePlate : MonoBehaviour
{
    [Header("引用（拖拽）")]
    [Tooltip("压住时开始移动的平台")]
    public MovingPlatform[] platforms;
    [Tooltip("压住时要切换开关状态的物体（门、桥、机关）")]
    public GameObject[] toggleObjects;
    [Tooltip("压力板的下压视觉节点，可空")]
    public Transform plateVisual;
    public AudioSource audioSource;

    [Header("触发规则")]
    [Tooltip("哪些层可以压住它；留空则任何层都算")]
    public LayerMask triggerMask;
    [Tooltip("压过一次后即使离开也保持激活")]
    public bool stayPressed = false;
    [Tooltip("只生效一次，之后永久保持激活")]
    public bool oneShot = false;
    [Tooltip("判定容差：物理帧间隔大于该值才算离开")]
    public float contactGrace = 0.1f;
    [Tooltip("压住时视觉下沉的距离")]
    public float pressDepth = 0.08f;
    [Tooltip("压住时 toggleObjects 是否为激活状态；默认 false = 压住时把门关掉（打开通路）")]
    public bool objectsActiveWhenPressed = false;

    [Header("音效")]
    public AudioClip pressClip;
    public AudioClip releaseClip;

    private float _lastContactTime = -999f;
    private bool _pressed;
    private Vector3 _visualBasePosition;

    /// <summary>当前是否处于压下状态。</summary>
    public bool IsPressed { get { return _pressed; } }

    void Awake()
    {
        if (plateVisual != null) _visualBasePosition = plateVisual.localPosition;
    }

    void Start()
    {
        ApplyState(false, false);
    }

    void Update()
    {
        bool contact = (Time.time - _lastContactTime) <= contactGrace;
        bool shouldPress = contact || ((stayPressed || oneShot) && _pressed);

        if (shouldPress != _pressed) ApplyState(shouldPress, true);

        if (plateVisual != null)
        {
            Vector3 target = _pressed ? _visualBasePosition + Vector3.down * pressDepth : _visualBasePosition;
            plateVisual.localPosition = Vector3.Lerp(plateVisual.localPosition, target, 12f * Time.deltaTime);
        }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!IsValid(other.gameObject.layer)) return;
        _lastContactTime = Time.time;
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (!IsValid(other.gameObject.layer)) return;
        _lastContactTime = Time.time;
    }

    private bool IsValid(int layer)
    {
        if (triggerMask.value == 0) return true;   // 没配掩码就接受任何层
        return (triggerMask.value & (1 << layer)) != 0;
    }

    private void ApplyState(bool pressed, bool withFeedback)
    {
        _pressed = pressed;

        if (platforms != null)
        {
            for (int i = 0; i < platforms.Length; i++)
            {
                if (platforms[i] != null) platforms[i].SetActivated(pressed);
            }
        }

        if (toggleObjects != null)
        {
            for (int i = 0; i < toggleObjects.Length; i++)
            {
                if (toggleObjects[i] != null) toggleObjects[i].SetActive(pressed == objectsActiveWhenPressed);
            }
        }

        if (withFeedback && audioSource != null)
        {
            AudioClip clip = pressed ? pressClip : releaseClip;
            if (clip != null) audioSource.PlayOneShot(clip);
        }
    }
}
