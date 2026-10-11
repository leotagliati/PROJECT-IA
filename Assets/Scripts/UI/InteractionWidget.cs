using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DefaultExecutionOrder(200)]
[RequireComponent(typeof(CanvasGroup))]
public class InteractionWidget : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private InteractionController controller;

    [Tooltip("Label do prompt antigo. Só empresta a fonte e é escondido.")]
    [SerializeField] private TMP_Text label;

    [Header("Aparência")]
    [SerializeField] private float buttonSize = 28f;

    [SerializeField] private Color discColor = new Color(0.08f, 0.08f, 0.08f, 0.85f);

    [SerializeField] private Color glyphColor = new Color(0.93f, 0.9f, 0.84f, 1f);

    [SerializeField] private Color ringColor = new Color(1f, 0.85f, 0.55f, 1f);

    [SerializeField] private Color failureColor = new Color(1f, 0.45f, 0.4f);

    [SerializeField] private float fadeTime = 0.08f;

    [Header("Recusa")]
    [SerializeField] private float failureDuration = 1.5f;

    [SerializeField] private float shakeDuration = 0.4f;

    [SerializeField] private float shakeAmplitude = 5f;

    private Canvas canvas;
    private RectTransform canvasRect;
    private CanvasGroup rootGroup;

    private RectTransform root;
    private RectTransform button;
    private CanvasGroup buttonGroup;
    private CanvasGroup dotGroup;
    private Image disc;
    private Image icon;
    private Image ringTrack;
    private Image ringFill;
    private Image slash;
    private TMP_Text glyph;
    private TMP_Text verb;
    private TMP_Text reason;

    private float fadeVelocity;
    private float nearBlend;
    private float ringValue;
    private float scale = 1f;
    private float scaleVelocity;
    private float shakeStart = -10f;
    private float reasonUntil;
    private float pulse;
    private bool wasNear;

    private void Awake()
    {
        GetComponent<CanvasGroup>().alpha = 0f;

        Canvas parentCanvas = GetComponentInParent<Canvas>();
        canvas = parentCanvas != null ? parentCanvas.rootCanvas : null;

        if (label == null)
            label = GetComponentInChildren<TMP_Text>();

        if (controller == null)
            controller = FindFirstObjectByType<InteractionController>();

        if (controller == null || canvas == null)
        {
            Debug.LogError($"{nameof(InteractionWidget)}: controller ou canvas não encontrados.", this);
            enabled = false;
            return;
        }

        TMP_FontAsset font = label != null ? label.font : TMP_Settings.defaultFontAsset;
        if (label != null)
            label.enabled = false;

        canvasRect = (RectTransform)canvas.transform;
        Build(font);
    }

    private void OnDestroy()
    {
        if (root != null)
            Destroy(root.gameObject);
    }

    private void OnEnable()
    {
        controller.InteractionDenied += HandleDenied;
        controller.InteractionCompleted += HandleCompleted;

        InputIcons.SchemeChanged += RefreshGlyph;

        RefreshGlyph();
    }

    private void OnDisable()
    {
        controller.InteractionDenied -= HandleDenied;
        controller.InteractionCompleted -= HandleCompleted;

        InputIcons.SchemeChanged -= RefreshGlyph;

        rootGroup.alpha = 0f;
    }

    private void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        bool visible = controller.HasPrompt && !controller.IsDragging && TryPlace(controller.TargetAnchor);
        bool near = visible && controller.IsInRange;

        rootGroup.alpha = Mathf.SmoothDamp(rootGroup.alpha, visible ? 1f : 0f, ref fadeVelocity, fadeTime, Mathf.Infinity, dt);

        if (near && !wasNear)
            scale = 0.75f;
        wasNear = near;

        nearBlend = Mathf.MoveTowards(nearBlend, near ? 1f : 0f, dt / 0.12f);
        buttonGroup.alpha = nearBlend;
        dotGroup.alpha = 1f - nearBlend;

        if (!visible)
            return;

        InteractionPrompt prompt = controller.CurrentPrompt;
        bool denying = Time.unscaledTime < reasonUntil;
        bool blocked = !prompt.Available || denying;

        verb.text = prompt.Label;
        verb.alpha = blocked ? 0.5f : 1f;
        glyph.alpha = blocked ? 0.4f : 1f;
        icon.color = WithAlpha(Color.white, blocked ? 0.4f : 1f);
        slash.color = WithAlpha(denying ? failureColor : glyphColor, blocked ? (denying ? 1f : 0.8f) : 0f);
        reason.alpha = denying ? Mathf.Clamp01((reasonUntil - Time.unscaledTime) / 0.25f) : 0f;

        bool hold = prompt.Kind == InteractionKind.Hold;
        ringTrack.enabled = hold && !blocked;

        if (controller.IsHolding)
            ringValue = controller.HoldProgress;
        else
            ringValue = Mathf.MoveTowards(ringValue, 0f, dt * 4f);

        pulse = Mathf.MoveTowards(pulse, 0f, dt * 3f);
        ringFill.fillAmount = Mathf.Max(ringValue, pulse);
        ringFill.color = WithAlpha(ringColor, Mathf.Max(ringValue > 0f ? 1f : 0f, pulse));

        float targetScale = controller.IsHolding ? 0.92f : 1f;
        scale = Mathf.SmoothDamp(scale, targetScale, ref scaleVelocity, 0.08f, Mathf.Infinity, dt);
        button.localScale = Vector3.one * (scale + pulse * 0.2f);

        float shakeT = (Time.unscaledTime - shakeStart) / shakeDuration;
        float shake = shakeT < 1f ? Mathf.Sin(shakeT * 40f) * shakeAmplitude * (1f - shakeT) * (1f - shakeT) : 0f;
        button.anchoredPosition = new Vector2(shake, 0f);
    }

    private bool TryPlace(Vector3 worldPoint)
    {
        Camera cam = controller.ViewCamera;
        if (cam == null)
            return false;

        // Viewport, e não pixel de tela: a câmera do player renderiza numa RenderTexture
        // menor que a tela (efeito pixelado), e o pixel dela não é o pixel do Canvas.
        Vector3 viewport = cam.WorldToViewportPoint(worldPoint);
        if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
            return false;

        Rect area = canvasRect.rect;
        root.localPosition = new Vector3(
            Mathf.Lerp(area.xMin, area.xMax, viewport.x),
            Mathf.Lerp(area.yMin, area.yMax, viewport.y),
            0f);
        return true;
    }

    private void HandleDenied(IInteractable target, string message)
    {
        shakeStart = Time.unscaledTime;
        reasonUntil = Time.unscaledTime + failureDuration;
        reason.text = message;
    }

    private void HandleCompleted(IInteractable target)
    {
        pulse = 1f;
    }

    private void RefreshGlyph()
    {
        if (glyph == null)
            return;

        Sprite sprite = InputIcons.GetActionIcon("Interact");
        icon.sprite = sprite;
        icon.enabled = sprite != null;
        disc.enabled = sprite == null;
        glyph.enabled = sprite == null;

        if (sprite != null)
            return;

        string bindingGroup = InputIcons.ActiveBindingGroup;
        glyph.text = PlayerInputProvider.Player.Interact.GetBindingDisplayString(InputBinding.MaskByGroup(bindingGroup));
    }

    private void Build(TMP_FontAsset font)
    {
        // Direto no Canvas raiz: o objeto deste componente pode estar sob pais escalados
        // (nas cenas atuais, o Crosshair com escala 0.066).
        root = NewRect("Interaction Widget", canvasRect, Vector2.zero);
        root.SetAsLastSibling();
        rootGroup = root.gameObject.AddComponent<CanvasGroup>();
        rootGroup.alpha = 0f;
        rootGroup.blocksRaycasts = false;
        rootGroup.interactable = false;

        dotGroup = NewRect("Dot", root, Vector2.one * buttonSize * 0.22f).gameObject.AddComponent<CanvasGroup>();
        NewImage(dotGroup.transform, UIShapes.Disc, WithAlpha(glyphColor, 0.75f));

        button = NewRect("Button", root, Vector2.one * buttonSize);
        buttonGroup = button.gameObject.AddComponent<CanvasGroup>();

        float ringSize = buttonSize * 1.28f;
        ringTrack = NewImage(NewRect("RingTrack", button, Vector2.one * ringSize), UIShapes.Ring, WithAlpha(glyphColor, 0.18f));

        ringFill = NewImage(NewRect("RingFill", button, Vector2.one * ringSize), UIShapes.Ring, ringColor);
        ringFill.type = Image.Type.Filled;
        ringFill.fillMethod = Image.FillMethod.Radial360;
        ringFill.fillOrigin = (int)Image.Origin360.Top;
        ringFill.fillClockwise = true;
        ringFill.fillAmount = 0f;

        disc = NewImage(NewRect("Disc", button, Vector2.one * buttonSize), UIShapes.Disc, discColor);

        glyph = NewText("Glyph", button, font, buttonSize * 0.5f, TextAlignmentOptions.Center);
        glyph.rectTransform.sizeDelta = Vector2.one * buttonSize;
        glyph.fontStyle = FontStyles.Bold;

        icon = NewImage(NewRect("Icon", button, Vector2.one * buttonSize), null, Color.white);
        icon.preserveAspect = true;
        icon.enabled = false;

        RectTransform slashRect = NewRect("Slash", button, new Vector2(buttonSize * 0.08f, buttonSize * 1.05f));
        slashRect.localRotation = Quaternion.Euler(0f, 0f, -45f);
        slash = NewImage(slashRect, null, Color.clear);

        verb = NewText("Verb", button, font, buttonSize * 0.45f, TextAlignmentOptions.MidlineLeft);
        verb.rectTransform.pivot = new Vector2(0f, 0.5f);
        verb.rectTransform.anchoredPosition = new Vector2(ringSize * 0.5f + buttonSize * 0.2f, 0f);
        verb.rectTransform.sizeDelta = new Vector2(buttonSize * 8f, buttonSize);

        reason = NewText("Reason", button, font, buttonSize * 0.38f, TextAlignmentOptions.Top);
        reason.rectTransform.pivot = new Vector2(0.5f, 1f);
        reason.rectTransform.anchoredPosition = new Vector2(0f, -ringSize * 0.5f - buttonSize * 0.12f);
        reason.rectTransform.sizeDelta = new Vector2(buttonSize * 10f, buttonSize);
        reason.color = failureColor;
        reason.alpha = 0f;
    }

    private static RectTransform NewRect(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = size;
        return r;
    }

    private static Image NewImage(Transform target, Sprite sprite, Color color)
    {
        var image = target.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TMP_Text NewText(string name, Transform parent, TMP_FontAsset font, float size, TextAlignmentOptions alignment)
    {
        var text = NewRect(name, parent, Vector2.zero).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
            text.font = font;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = glyphColor;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }
}
