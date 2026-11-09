using System.Collections.Generic;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas))]
public sealed class MelControllerAnimationMenu : MonoBehaviour
{
    [SerializeField] MelAnimationControls controls;
    [SerializeField] MelAnimationMenuData animationData;
    [SerializeField] TMP_FontAsset font;
    [SerializeField, Min(0)] float blendTime = 0.5f;

    readonly List<(UnityEngine.UI.Image image, int hash, int layer)> buttons = new();
    TMP_Text blendLabel;
    int lastBaseHash, lastFaceHash;
    static readonly Color Background = new(0.025f, 0.045f, 0.075f, 0.98f);
    static readonly Color Idle = new(0.10f, 0.16f, 0.23f, 1f);
    static readonly Color Active = new(0.04f, 0.48f, 0.53f, 1f);

    public float BlendTime => blendTime;
    public MelAnimationMenuData AnimationData => animationData;

    void Awake()
    {
        if (animationData) blendTime = animationData.defaultBlendTime;
    }

    public void Configure(MelAnimationControls target, MelAnimationMenuData data, TMP_FontAsset textFont)
    {
        controls = target;
        animationData = data;
        font = textFont;
        blendTime = data.defaultBlendTime;
        Rebuild();
    }

    void OnEnable()
    {
        if (Application.isPlaying) Rebuild();
    }

    public void Rebuild()
    {
        // Only this component's generated content is replaced; the mount and Canvas stay intact.
        var previous = transform.Find("Animation Menu Content");
        if (previous)
        {
            previous.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(previous.gameObject);
            else DestroyImmediate(previous.gameObject);
        }
        buttons.Clear();
        if (!animationData || !font) return;
        int rows = Mathf.Max(animationData.baseAnimations.Length, Mathf.CeilToInt(animationData.faceAnimations.Length / 2f));
        float height = 184 + rows * 48;
        var rect = (RectTransform)transform;
        rect.sizeDelta = new Vector2(900, height);
        var content = Rect("Animation Menu Content", transform, 0, 0, 900, height);
        var background = content.gameObject.AddComponent<UnityEngine.UI.Image>();
        background.color = Background;
        background.raycastTarget = false;
        Text("Title", content, "MEL  /  ANIMATIONS", 24, 14, 852, 40, 30);
        Text("Hint", content, "Point with your right controller and press the trigger", 24, 56, 852, 28, 18,
            new Color(0.66f, 0.78f, 0.85f));
        blendLabel = Text("Blend Time", content, "", 24, 94, 620, 40, 23);
        UpdateBlendLabel();
        Button(content, "Decrease Blend", "- 0.1", 648, 94, 102, 40, () => SetBlendTime(blendTime - 0.1f));
        Button(content, "Increase Blend", "+ 0.1", 762, 94, 114, 40, () => SetBlendTime(blendTime + 0.1f));
        Text("Base Heading", content, "BASE LAYER", 24, 144, 408, 30, 21, new Color(0.34f, 0.86f, 0.88f));
        Text("Face Heading", content, "FACE", 456, 144, 420, 30, 21, new Color(0.34f, 0.86f, 0.88f));
        AddAnimations(content, animationData.baseAnimations, 0, 24, 408, 1);
        AddAnimations(content, animationData.faceAnimations, 1, 456, 420, 2);
        lastBaseHash = lastFaceHash = 0;
    }

    void AddAnimations(RectTransform parent, MelAnimationMenuData.Entry[] entries, int layer, float x, float width, int columns)
    {
        float cellWidth = (width - (columns - 1) * 8) / columns;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var image = Button(parent, entry.statePath, entry.label, x + i % columns * (cellWidth + 8),
                184 + i / columns * 48, cellWidth, 40, () => PlayAnimation(layer, entry));
            buttons.Add((image, Animator.StringToHash(entry.statePath), layer));
        }
    }

    public void SetBlendTime(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
        blendTime = Mathf.Round(Mathf.Max(seconds, 0) * 10) / 10;
        UpdateBlendLabel();
    }

    void UpdateBlendLabel()
    {
        if (blendLabel) blendLabel.text = $"Blend time   {blendTime:0.0} s";
    }

    public void PlayAnimation(int layer, MelAnimationMenuData.Entry entry)
    {
        if (!Application.isPlaying || !controls || !controls.Animator.isActiveAndEnabled) return;
        var animator = controls.Animator;
        if (layer >= animator.layerCount || !animator.HasState(layer, Animator.StringToHash(entry.statePath))) return;
        // Keep the existing Any State conditions aligned with the requested destination.
        animator.SetInteger(layer == 0 ? MelAnimationControls.BaseParameter : MelAnimationControls.FaceParameter, entry.parameterIndex);
        if (layer == 1) animator.SetLayerWeight(layer, 1f);
        animator.CrossFadeInFixedTime(entry.statePath, blendTime, layer, 0);
    }

    void LateUpdate()
    {
        if (!controls || !controls.Animator.isInitialized) return;
        var animator = controls.Animator;
        int baseHash = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0).fullPathHash : animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        int faceHash = animator.layerCount > 1 ? (animator.IsInTransition(1) ? animator.GetNextAnimatorStateInfo(1).fullPathHash : animator.GetCurrentAnimatorStateInfo(1).fullPathHash) : 0;
        if (baseHash == lastBaseHash && faceHash == lastFaceHash) return;
        lastBaseHash = baseHash;
        lastFaceHash = faceHash;
        foreach (var button in buttons)
            if (button.image) button.image.color = button.hash == (button.layer == 0 ? baseHash : faceHash) ? Active : Idle;
    }

    static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.gameObject.layer = parent.gameObject.layer;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    TMP_Text Text(string name, RectTransform parent, string value, float x, float y, float width, float height, int size, Color? color = null)
    {
        var label = Rect(name, parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.text = value;
        label.fontSize = size;
        label.color = color ?? Color.white;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;
        return label;
    }

    UnityEngine.UI.Image Button(RectTransform parent, string name, string title, float x, float y, float width, float height, UnityEngine.Events.UnityAction action)
    {
        var rect = Rect(name, parent, x, y, width, height);
        var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
        image.color = Idle;
        var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.highlightedColor = new Color(1.4f, 1.4f, 1.4f);
        colors.pressedColor = new Color(0.6f, 1f, 1f);
        colors.colorMultiplier = 1;
        button.colors = colors;
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        button.onClick.AddListener(action);
        var label = Text("Label", rect, title, 12, 0, width - 24, height, 21);
        label.enableAutoSizing = true;
        label.fontSizeMin = 14;
        label.fontSizeMax = 21;
        return image;
    }
}
