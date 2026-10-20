using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Prompt "anel na mira": fica no centro da tela, não no objeto. Um anel de quadradinhos abre em volta da mira ao
/// chegar perto e acende no sentido horário durante o Hold; a tecla e o verbo ficam embaixo. Em teste contra o
/// <see cref="InteractionWidget"/> — ver <see cref="InteractionPromptBase"/>.
/// </summary>
[DefaultExecutionOrder(200)]
public class InteractionRingWidget : InteractionPromptBase
{
    [Header("Anel")]
    [SerializeField, Min(3)] private int ticks = 12;
    [SerializeField] private float tickSize = 4f;

    [Tooltip("Raio com o objeto ao alcance de vista e ao alcance da mão.")]
    [SerializeField] private Vector2 radius = new(10f, 22f);

    [SerializeField] private Color idleTickColor = new(0.55f, 0.54f, 0.5f, 1f);

    [Tooltip("Opacidade do anel quando o objeto ainda está longe.")]
    [SerializeField, Range(0f, 1f)] private float farAlpha = 0.35f;

    [Header("Tecla e verbo")]
    [SerializeField] private float fontSize = 16f;
    [SerializeField] private float keySize = 28f;
    [SerializeField] private float labelGap = 14f;
    [SerializeField, Min(0.01f)] private float appearTime = 0.12f;

    private Image[] tickImages;
    private RectTransform ring;
    private RectTransform caption;
    private CanvasGroup captionGroup;
    private Image keyCap;
    private TMP_Text keyText;
    private Image keyIcon;
    private TMP_Text verb;
    private TMP_Text reason;

    private Sprite keySprite;
    private float visibility;
    private float nearBlend;
    private float shown;

    protected override void Build()
    {
        ring = NewRect("Ring", root, Vector2.zero);
        tickImages = new Image[ticks];
        for (int i = 0; i < ticks; i++)
            tickImages[i] = NewImage(NewRect($"Tick {i + 1}", ring, Vector2.one * tickSize), null, idleTickColor);

        caption = NewRect("Caption", root, Vector2.zero);
        captionGroup = caption.gameObject.AddComponent<CanvasGroup>();

        keyCap = NewImage(NewRect("KeyCap", caption, Vector2.one * keySize), keyCapSprite, Color.white);
        keyCap.enabled = keyCapSprite != null;
        // Borda do 9-slice em 2x: no tamanho nativo (3px) o bisel some numa tecla pequena.
        keyCap.pixelsPerUnitMultiplier = 0.5f;
        keyText = NewText("Key", keyCap.transform, fontSize, TextAlignmentOptions.Center);
        keyText.rectTransform.sizeDelta = Vector2.one * keySize;
        // A base da tecla é a "altura" dela: o texto fica no tampo, um pouco acima do centro.
        keyText.rectTransform.anchoredPosition = new Vector2(0f, keySize * 0.06f);

        keyIcon = NewImage(NewRect("KeyIcon", caption, Vector2.one * keySize), null, Color.white);
        keyIcon.preserveAspect = true;

        verb = NewText("Verb", caption, fontSize, TextAlignmentOptions.MidlineLeft);
        verb.rectTransform.pivot = new Vector2(0f, 0.5f);

        reason = NewText("Reason", caption, fontSize * 0.8f, TextAlignmentOptions.Top);
        reason.rectTransform.pivot = new Vector2(0.5f, 1f);
        reason.rectTransform.anchoredPosition = new Vector2(0f, -keySize * 0.5f - 8f);
        reason.color = failureColor;
        reason.alpha = 0f;
    }

    protected override void RefreshKey()
    {
        keySprite = InputIcons.GetActionIcon("Interact");
        if (keyText != null)
            keyText.text = keySprite == null ? KeyLabel() : string.Empty;
    }

    private void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        bool visible = controller.HasPrompt && !controller.IsDragging;
        bool near = visible && controller.IsInRange;

        visibility = Mathf.MoveTowards(visibility, visible ? 1f : 0f, dt / appearTime);
        nearBlend = Mathf.MoveTowards(nearBlend, near ? 1f : 0f, dt / appearTime);
        rootGroup.alpha = visibility;
        captionGroup.alpha = nearBlend;

        if (visibility <= 0f)
            return;

        // Centro da tela: é onde a mira está, e é para onde o jogador já está olhando.
        root.localPosition = new Vector3(Shake, 0f, 0f);

        InteractionPrompt prompt = controller.CurrentPrompt;
        bool blocked = !prompt.Available || Denying;

        if (controller.IsHolding)
            shown = controller.HoldProgress;
        else if (Time.unscaledTime - completedAt > 0.3f)
            shown = Mathf.MoveTowards(shown, 0f, dt * 4f);
        else
            shown = 1f;

        float eased = 1f - (1f - nearBlend) * (1f - nearBlend);
        float r = Mathf.Lerp(radius.x, radius.y, eased);
        int lit = Mathf.FloorToInt(shown * ticks + 1e-4f);
        Color litColor = CompletionFlash ? textColor : progressColor;

        for (int i = 0; i < ticks; i++)
        {
            // Começa no topo e anda no sentido horário, como um relógio.
            float angle = Mathf.PI * 0.5f - i * 2f * Mathf.PI / ticks;
            Vector2 p = new(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r);
            // Arredonda para o tamanho do quadradinho: em escala de pixel, meia posição treme.
            tickImages[i].rectTransform.anchoredPosition = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));

            Color color = Denying ? failureColor : i < lit ? litColor : idleTickColor;
            float alpha = Mathf.Lerp(farAlpha, 1f, nearBlend) * (blocked && !Denying ? 0.5f : 1f);
            tickImages[i].color = WithAlpha(color, alpha);
        }

        LayoutCaption(prompt.Label, blocked, r);

        reason.text = reasonText;
        reason.alpha = Denying ? Mathf.Clamp01((reasonUntil - Time.unscaledTime) / 0.25f) : 0f;
    }

    private void LayoutCaption(string label, bool blocked, float r)
    {
        verb.text = label;
        verb.alpha = blocked ? 0.5f : 1f;
        Vector2 verbSize = verb.GetPreferredValues();
        verb.rectTransform.sizeDelta = verbSize;

        bool icon = keySprite != null;
        keyIcon.sprite = keySprite;
        keyIcon.enabled = icon;
        keyCap.enabled = !icon && keyCapSprite != null;
        keyCap.color = WithAlpha(Color.white, blocked ? 0.5f : 1f);
        keyText.alpha = blocked ? 0.5f : 1f;

        // Tecla + verbo centralizados juntos embaixo do anel.
        float gap = keySize * 0.3f;
        float total = keySize + gap + verbSize.x;
        float left = -total * 0.5f;
        caption.anchoredPosition = new Vector2(0f, -r - labelGap - keySize * 0.5f);
        keyCap.rectTransform.anchoredPosition = keyIcon.rectTransform.anchoredPosition = new Vector2(left + keySize * 0.5f, 0f);
        verb.rectTransform.anchoredPosition = new Vector2(left + keySize + gap, 0f);
    }
}
