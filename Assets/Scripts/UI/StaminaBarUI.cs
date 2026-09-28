using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Barra de stamina do HUD. Só lê o <see cref="PlayerStamina"/>; não decide nada.
///
/// Com <see cref="fill"/> vazio, monta a própria barra (Canvas, fundo e preenchimento) em
/// código: basta o componente na cena. Para uma barra desenhada à mão, atribua o fill (Image
/// com Image Type = Filled) e ponha um CanvasGroup no mesmo objeto deste componente.
///
/// Some com a barra cheia: em jogo de terror o HUD parado na tela o tempo todo tira a imersão,
/// e a barra só importa enquanto está sendo gasta ou recuperada.
/// </summary>
public class StaminaBarUI : MonoBehaviour
{
    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private PlayerStamina stamina;

    [Tooltip("Vazio = a barra é montada em código com o layout abaixo.")]
    [SerializeField] private Image fill;

    [Header("Layout (barra montada em código)")]
    [SerializeField] private Vector2 size = new(220f, 5f);

    [Tooltip("Distância da borda de baixo da tela, em pixels de referência (1920x1080).")]
    [SerializeField] private float bottomMargin = 70f;

    [SerializeField] private Color backgroundColor = new(0f, 0f, 0f, 0.45f);

    [SerializeField] private int sortingOrder = 10;

    [Header("Cores")]
    [SerializeField] private Color normalColor = new(0.9f, 0.9f, 0.9f, 0.85f);

    // Enquanto exausto a barra enche mas não libera a corrida: sem a cor diferente, o jogador
    // vê barra e não entende por que não corre.
    [SerializeField] private Color exhaustedColor = new(0.8f, 0.15f, 0.15f, 0.85f);

    [Header("Esconder quando cheia")]
    [SerializeField] private bool hideWhenFull = true;

    [Tooltip("Segundos com a barra cheia antes de começar a sumir.")]
    [SerializeField, Min(0f)] private float hideDelay = 1f;

    [SerializeField, Min(0.01f)] private float fadeDuration = 0.4f;

    private CanvasGroup group;
    private Texture2D builtTexture;
    private Sprite builtSprite;
    private float fullSince = float.NegativeInfinity;

    private void Awake()
    {
        if (fill == null)
        {
            BuildBar();
        }
        else
        {
            group = GetComponent<CanvasGroup>();
            if (group == null)
                group = gameObject.AddComponent<CanvasGroup>();

            if (fill.type != Image.Type.Filled)
                Debug.LogWarning($"{name}: a Image do fill não está com Image Type = Filled — a barra não vai encolher.", this);
        }

        group.alpha = hideWhenFull ? 0f : 1f;
    }

    private void Start()
    {
        if (stamina == null)
            stamina = FindFirstObjectByType<PlayerStamina>();

        if (stamina == null)
        {
            Debug.LogError($"{name}: PlayerStamina não encontrado na cena.", this);
            group.alpha = 0f;
            enabled = false;
        }
    }

    private void OnDestroy()
    {
        if (builtSprite != null) Destroy(builtSprite);
        if (builtTexture != null) Destroy(builtTexture);
    }

    private void Update()
    {
        float value = stamina.Normalized;

        fill.fillAmount = value;
        fill.color = stamina.IsExhausted ? exhaustedColor : normalColor;

        if (!hideWhenFull)
            return;

        bool full = value >= 1f;
        if (!full)
            fullSince = float.PositiveInfinity;
        else if (float.IsPositiveInfinity(fullSince))
            fullSince = Time.time;

        // Aparece rápido (é informação que o jogador precisa agora) e some devagar.
        bool visible = !full || Time.time - fullSince < hideDelay;
        float speed = visible ? 1f / 0.1f : 1f / fadeDuration;
        group.alpha = Mathf.MoveTowards(group.alpha, visible ? 1f : 0f, Time.deltaTime * speed);
    }

    private void BuildBar()
    {
        var canvasObject = new GameObject("StaminaCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        // Mesmo tamanho aparente em qualquer resolução.
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        group = canvasObject.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;

        // Image Filled sem sprite desenha o quad inteiro e ignora o fillAmount.
        builtTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "StaminaWhite" };
        builtTexture.SetPixel(0, 0, Color.white);
        builtTexture.Apply(false, true);
        builtSprite = Sprite.Create(builtTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
        builtSprite.name = "StaminaWhite";

        Image background = CreateImage("Background", canvasObject.transform, backgroundColor);
        var backgroundRect = background.rectTransform;
        backgroundRect.anchorMin = backgroundRect.anchorMax = new Vector2(0.5f, 0f);
        backgroundRect.pivot = new Vector2(0.5f, 0f);
        backgroundRect.sizeDelta = size;
        backgroundRect.anchoredPosition = new Vector2(0f, bottomMargin);

        fill = CreateImage("Fill", background.transform, normalColor);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = (int)Image.OriginHorizontal.Left;

        var fillRect = fill.rectTransform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
    }

    private Image CreateImage(string objectName, Transform parent, Color color)
    {
        var imageObject = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        Image image = imageObject.GetComponent<Image>();
        image.sprite = builtSprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }
}
