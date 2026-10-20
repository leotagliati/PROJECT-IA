using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Base dos prompts de interação em teste (<see cref="InteractionLabelWidget"/>, <see cref="InteractionRingWidget"/>).
/// Cuida do que é igual entre eles e o <see cref="InteractionWidget"/>: achar controller e Canvas, assinar os eventos
/// e o troca-esquema de input, posicionar por viewport e montar a hierarquia. Cada filho só desenha.
///
/// Vai no mesmo objeto do InteractionWidget (o Crosshair): para testar, desligue ele e ligue um destes.
/// </summary>
public abstract class InteractionPromptBase : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] protected InteractionController controller;

    [Tooltip("Vazio = a fonte do label antigo do Crosshair.")]
    [SerializeField] protected TMP_FontAsset font;

    [Tooltip("Tecla 9-slice (Images/HUD/KeyCap). Vazio = sem tecla desenhada, só o texto.")]
    [SerializeField] protected Sprite keyCapSprite;

    [Header("Cores")]
    [SerializeField] protected Color textColor = new(0.93f, 0.92f, 0.88f, 1f);
    [SerializeField] protected Color keyColor = new(0.85f, 0.76f, 0.48f, 1f);
    [SerializeField] protected Color progressColor = new(0.85f, 0.12f, 0.12f, 1f);
    [SerializeField] protected Color failureColor = new(1f, 0.45f, 0.4f, 1f);

    [Header("Recusa")]
    [SerializeField] protected float failureDuration = 1.5f;
    [SerializeField] protected float shakeDuration = 0.4f;
    [SerializeField] protected float shakeAmplitude = 5f;

    protected RectTransform canvasRect;
    protected RectTransform root;
    protected CanvasGroup rootGroup;

    protected float shakeStart = -10f;
    protected float reasonUntil;
    protected string reasonText;
    protected float completedAt = -10f;

    protected bool Denying => Time.unscaledTime < reasonUntil;

    protected float Shake
    {
        get
        {
            float t = (Time.unscaledTime - shakeStart) / shakeDuration;
            return t < 1f ? Mathf.Sin(t * 40f) * shakeAmplitude * (1f - t) * (1f - t) : 0f;
        }
    }

    /// <summary>Pisca duas vezes logo depois de completar.</summary>
    protected bool CompletionFlash
    {
        get
        {
            float t = Time.unscaledTime - completedAt;
            return t < 0.3f && (int)(t / 0.075f) % 2 == 0;
        }
    }

    protected virtual void Awake()
    {
        if (TryGetComponent(out CanvasGroup ownGroup))
            ownGroup.alpha = 0f;

        Canvas parentCanvas = GetComponentInParent<Canvas>();
        Canvas canvas = parentCanvas != null ? parentCanvas.rootCanvas : null;

        // O Crosshair tem o label do prompt antigo: empresta a fonte e o esconde, como o InteractionWidget.
        TMP_Text oldLabel = GetComponentInChildren<TMP_Text>();
        if (font == null)
            font = oldLabel != null ? oldLabel.font : TMP_Settings.defaultFontAsset;
        if (oldLabel != null)
            oldLabel.enabled = false;

        if (controller == null)
            controller = FindFirstObjectByType<InteractionController>();

        if (controller == null || canvas == null)
        {
            Debug.LogError($"{GetType().Name}: controller ou canvas não encontrados.", this);
            enabled = false;
            return;
        }

        canvasRect = (RectTransform)canvas.transform;

        // Direto no Canvas raiz: o Crosshair tem escala 0.066 e encolheria tudo.
        root = NewRect(GetType().Name, canvasRect, Vector2.zero);
        root.SetAsLastSibling();
        rootGroup = root.gameObject.AddComponent<CanvasGroup>();
        rootGroup.alpha = 0f;
        rootGroup.blocksRaycasts = false;
        rootGroup.interactable = false;

        Build();
    }

    protected virtual void OnDestroy()
    {
        if (root != null)
            Destroy(root.gameObject);
    }

    protected virtual void OnEnable()
    {
        if (controller == null)
            return;

        controller.InteractionDenied += HandleDenied;
        controller.InteractionCompleted += HandleCompleted;
        InputIcons.SchemeChanged += RefreshKey;
        RefreshKey();
    }

    protected virtual void OnDisable()
    {
        if (controller == null)
            return;

        controller.InteractionDenied -= HandleDenied;
        controller.InteractionCompleted -= HandleCompleted;
        InputIcons.SchemeChanged -= RefreshKey;

        if (rootGroup != null)
            rootGroup.alpha = 0f;
    }

    protected abstract void Build();

    /// <summary>Chamado quando o esquema (teclado/controle) muda: atualiza a tecla mostrada.</summary>
    protected abstract void RefreshKey();

    private void HandleDenied(IInteractable target, string message)
    {
        shakeStart = Time.unscaledTime;
        reasonUntil = Time.unscaledTime + failureDuration;
        reasonText = message;
    }

    private void HandleCompleted(IInteractable target) => completedAt = Time.unscaledTime;

    /// <summary>Texto da tecla de Interact no esquema ativo ("E", "LMB"...).</summary>
    protected static string KeyLabel() =>
        PlayerInputProvider.Player.Interact.GetBindingDisplayString(InputBinding.MaskByGroup(InputIcons.ActiveBindingGroup));

    /// <summary>
    /// Ponto do mundo em coordenadas do Canvas. Por viewport, e não pixel de tela: a câmera do player renderiza numa
    /// RenderTexture menor que a tela, e o pixel dela não é o pixel do Canvas.
    /// </summary>
    protected bool TryWorldToCanvas(Vector3 worldPoint, out Vector2 position)
    {
        position = default;
        Camera cam = controller.ViewCamera;
        if (cam == null)
            return false;

        Vector3 viewport = cam.WorldToViewportPoint(worldPoint);
        if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
            return false;

        Rect area = canvasRect.rect;
        position = new Vector2(Mathf.Lerp(area.xMin, area.xMax, viewport.x), Mathf.Lerp(area.yMin, area.yMax, viewport.y));
        return true;
    }

    protected static RectTransform NewRect(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = size;
        return r;
    }

    protected static Image NewImage(Transform target, Sprite sprite, Color color)
    {
        var image = target.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        if (sprite != null && sprite.border != Vector4.zero)
            image.type = Image.Type.Sliced;
        return image;
    }

    protected TMP_Text NewText(string name, Transform parent, float size, TextAlignmentOptions alignment)
    {
        var text = NewRect(name, parent, Vector2.zero).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
            text.font = font;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = textColor;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    protected static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }
}
