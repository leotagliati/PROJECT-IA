using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Prompt "etiqueta": no objeto, um rótulo escuro com "[E] Abrir" e um sublinhado que enche no Hold. De longe, só um
/// ponto. Em teste contra o <see cref="InteractionWidget"/> — ver <see cref="InteractionPromptBase"/>.
/// </summary>
[DefaultExecutionOrder(200)]
public class InteractionLabelWidget : InteractionPromptBase
{
    [Header("Etiqueta")]
    [SerializeField] private float fontSize = 16f;
    [SerializeField] private Vector2 padding = new(6f, 3f);

    [Tooltip("Distância do objeto até o começo da etiqueta.")]
    [SerializeField] private float offset = 16f;

    [Tooltip("Quanto a etiqueta desliza ao aparecer.")]
    [SerializeField] private float slide = 12f;

    [SerializeField] private float underlineHeight = 3f;
    [SerializeField] private Color panelColor = new(0f, 0f, 0f, 0.67f);
    [SerializeField] private Color trackColor = new(0.24f, 0.23f, 0.21f, 1f);
    [SerializeField, Min(0.01f)] private float appearTime = 0.12f;

    private RectTransform dot;
    private CanvasGroup dotGroup;
    private RectTransform panel;
    private CanvasGroup panelGroup;
    private Image keyIcon;
    private TMP_Text text;
    private RectTransform track;
    private RectTransform fill;
    private Image fillImage;
    private TMP_Text reason;

    private string keyLabel = "E";
    private Sprite keySprite;
    private float visibility;
    private float nearBlend;

    protected override void Build()
    {
        dot = NewRect("Dot", root, Vector2.one * 4f);
        dotGroup = dot.gameObject.AddComponent<CanvasGroup>();
        NewImage(dot, null, WithAlpha(textColor, 0.8f));

        panel = NewRect("Panel", root, Vector2.zero);
        panel.pivot = new Vector2(0f, 0.5f);
        panelGroup = panel.gameObject.AddComponent<CanvasGroup>();
        NewImage(panel, null, panelColor);

        keyIcon = NewImage(NewRect("KeyIcon", panel, Vector2.one * fontSize), null, Color.white);
        keyIcon.preserveAspect = true;
        keyIcon.rectTransform.anchorMin = keyIcon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        keyIcon.rectTransform.pivot = new Vector2(0f, 0.5f);

        text = NewText("Text", panel, fontSize, TextAlignmentOptions.MidlineLeft);
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        text.rectTransform.pivot = new Vector2(0f, 0.5f);

        // Sem sprite o Image ignora o Filled, então o sublinhado enche pela largura.
        track = NewRect("Track", panel, Vector2.zero);
        track.anchorMin = new Vector2(0f, 0f);
        track.anchorMax = new Vector2(1f, 0f);
        track.pivot = new Vector2(0f, 1f);
        track.sizeDelta = new Vector2(0f, underlineHeight);
        NewImage(track, null, trackColor);

        fill = NewRect("Fill", track, Vector2.zero);
        fill.anchorMin = fill.anchorMax = fill.pivot = new Vector2(0f, 0.5f);
        fillImage = NewImage(fill, null, progressColor);

        reason = NewText("Reason", panel, fontSize * 0.8f, TextAlignmentOptions.TopLeft);
        reason.rectTransform.anchorMin = reason.rectTransform.anchorMax = new Vector2(0f, 0f);
        reason.rectTransform.pivot = new Vector2(0f, 1f);
        reason.rectTransform.anchoredPosition = new Vector2(0f, -underlineHeight - 6f);
        reason.color = failureColor;
        reason.alpha = 0f;
    }

    protected override void RefreshKey()
    {
        keySprite = InputIcons.GetActionIcon("Interact");
        keyLabel = keySprite == null ? KeyLabel() : null;
    }

    private void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        Vector2 at = default;
        bool visible = controller.HasPrompt && !controller.IsDragging && TryWorldToCanvas(controller.TargetAnchor, out at);
        bool near = visible && controller.IsInRange;

        visibility = Mathf.MoveTowards(visibility, visible ? 1f : 0f, dt / appearTime);
        nearBlend = Mathf.MoveTowards(nearBlend, near ? 1f : 0f, dt / appearTime);
        rootGroup.alpha = visibility;
        dotGroup.alpha = 1f - nearBlend;
        panelGroup.alpha = nearBlend;

        if (!visible)
            return;

        root.localPosition = at;

        InteractionPrompt prompt = controller.CurrentPrompt;
        bool blocked = !prompt.Available || Denying;
        Layout(prompt.Label, blocked);

        float eased = 1f - (1f - nearBlend) * (1f - nearBlend);
        panel.anchoredPosition = new Vector2(offset - slide * (1f - eased) + Shake, 0f);

        UpdateProgress(dt);

        fill.sizeDelta = new Vector2(track.rect.width * progress, underlineHeight);
        fillImage.color = CompletionFlash ? textColor : progressColor;

        reason.text = reasonText ?? string.Empty;
        reason.alpha = Denying ? Mathf.Clamp01((reasonUntil - Time.unscaledTime) / 0.25f) : 0f;
    }

    private void Layout(string label, bool blocked)
    {
        string keyHex = ColorUtility.ToHtmlStringRGB(keyColor);
        text.text = keySprite != null ? label : $"<color=#{keyHex}>[{keyLabel}]</color> {label}";
        text.alpha = blocked ? 0.5f : 1f;

        keyIcon.sprite = keySprite;
        keyIcon.enabled = keySprite != null;
        keyIcon.color = WithAlpha(Color.white, blocked ? 0.5f : 1f);

        float iconWidth = keySprite != null ? fontSize + padding.x * 0.5f : 0f;
        keyIcon.rectTransform.anchoredPosition = new Vector2(padding.x, 0f);
        text.rectTransform.anchoredPosition = new Vector2(padding.x + iconWidth, 0f);

        Vector2 size = text.GetPreferredValues();
        text.rectTransform.sizeDelta = size;
        panel.sizeDelta = new Vector2(padding.x * 2f + iconWidth + size.x, Mathf.Max(size.y, fontSize) + padding.y * 2f);
    }
}
