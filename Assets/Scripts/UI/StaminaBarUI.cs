using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Barra de stamina do HUD (prefab <c>Prefabs/UI/StaminaBar</c>, dentro de um Canvas). Só lê o
/// <see cref="PlayerStamina"/>; não decide nada.
///
/// Some com a barra cheia: em jogo de terror o HUD parado na tela o tempo todo tira a imersão,
/// e a barra só importa enquanto está sendo gasta ou recuperada.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class StaminaBarUI : MonoBehaviour
{
    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private PlayerStamina stamina;

    [Tooltip("Image Type = Filled.")]
    [SerializeField] private Image fill;

    [Tooltip("Opcional. Pinta junto com o fill: os sprites são cinza e a cor vem daqui.")]
    [SerializeField] private Image icon;

    [Header("Cores")]
    [SerializeField] private Color normalColor = new(0.9f, 0.9f, 0.9f, 0.85f);

    // Enquanto exausto a barra enche mas não libera a corrida: sem a cor diferente, o jogador
    // vê barra e não entende por que não corre.
    [SerializeField] private Color exhaustedColor = new(0.8f, 0.15f, 0.15f, 0.85f);

    [Header("Ao ficar exausto")]
    [Tooltip("Segundos até a cor chegar no vermelho; o ícone pulsa nesse intervalo. A volta ao normal usa o mesmo tempo, sem pulsar.")]
    [SerializeField, Min(0.01f)] private float exhaustTransition = 0.6f;

    [SerializeField, Min(1)] private int pulses = 2;

    [Tooltip("Escala extra do ícone no pico de cada pulso.")]
    [SerializeField, Range(0f, 1f)] private float pulseScale = 0.3f;

    [Header("Esconder quando cheia")]
    [SerializeField] private bool hideWhenFull = true;

    [Tooltip("Segundos com a barra cheia antes de começar a sumir.")]
    [SerializeField, Min(0f)] private float hideDelay = 1f;

    [SerializeField, Min(0.01f)] private float showDuration = 0.15f;

    [SerializeField, Min(0.01f)] private float fadeDuration = 0.4f;

    private CanvasGroup group;
    private float fullSince = float.NegativeInfinity;
    private bool paused;
    private float exhaustBlend;
    private float pulseTime = float.PositiveInfinity;
    private bool wasExhausted;
    private Vector3 iconScale = Vector3.one;

    public float Visibility => group != null ? group.alpha : 0f;

    private void OnEnable()
    {
        PauseControler.OnPaused += HandlePaused;
        PauseControler.OnResumed += HandleResumed;
    }

    private void OnDisable()
    {
        PauseControler.OnPaused -= HandlePaused;
        PauseControler.OnResumed -= HandleResumed;
    }

    private void HandlePaused()
    {
        paused = true;
        group.alpha = 0f;
    }

    private void HandleResumed() => paused = false;

    private void Awake()
    {
        // O RequireComponent não adiciona em objeto que já estava salvo na cena antes dele.
        if (!TryGetComponent(out group))
            group = gameObject.AddComponent<CanvasGroup>();

        group.interactable = false;
        group.blocksRaycasts = false;
        group.alpha = hideWhenFull ? 0f : 1f;

        if (icon != null)
            iconScale = icon.rectTransform.localScale;

        if (fill != null && fill.type != Image.Type.Filled)
            Debug.LogWarning($"{name}: a Image do fill não está com Image Type = Filled — a barra não vai encolher.", this);
    }

    private void Start()
    {
        if (stamina == null)
            stamina = FindFirstObjectByType<PlayerStamina>();

        if (stamina == null || fill == null)
        {
            Debug.LogError($"{name}: {(fill == null ? "fill não atribuído" : "PlayerStamina não encontrado na cena")}.", this);
            group.alpha = 0f;
            enabled = false;
        }
    }

    private void Update()
    {
        // Com timeScale 0 o deltaTime é 0 e o fade não andaria: o alpha é zerado direto. O diálogo e o bilhete
        // zeram o timeScale sem passar pelo PauseControler, e a barra ficava congelada por cima da caixa.
        if (paused || DialogueController.IsOpen || TutorialNoteController.IsOpen)
        {
            group.alpha = 0f;
            return;
        }

        float value = stamina.Normalized;
        fill.fillAmount = value;
        UpdateExhaustion(stamina.IsExhausted);

        if (!hideWhenFull)
            return;

        bool full = value >= 1f;
        if (!full)
            fullSince = float.PositiveInfinity;
        else if (float.IsPositiveInfinity(fullSince))
            fullSince = Time.time;

        // Aparece rápido (é informação que o jogador precisa agora) e some devagar.
        bool visible = !full || Time.time - fullSince < hideDelay;
        float speed = visible ? 1f / showDuration : 1f / fadeDuration;
        group.alpha = Mathf.MoveTowards(group.alpha, visible ? 1f : 0f, Time.deltaTime * speed);
    }

    private void UpdateExhaustion(bool exhausted)
    {
        if (exhausted && !wasExhausted)
            pulseTime = 0f;

        wasExhausted = exhausted;

        exhaustBlend = Mathf.MoveTowards(exhaustBlend, exhausted ? 1f : 0f, Time.deltaTime / exhaustTransition);
        Color color = Color.Lerp(normalColor, exhaustedColor, Mathf.SmoothStep(0f, 1f, exhaustBlend));

        fill.color = color;

        if (icon == null)
            return;

        icon.color = color;

        // Pulsos que morrem até o fim da transição: o vermelho "bate" em vez de só acender.
        float scale = 1f;
        if (pulseTime < exhaustTransition)
        {
            pulseTime += Time.deltaTime;
            float t = Mathf.Clamp01(pulseTime / exhaustTransition);
            scale += pulseScale * Mathf.Abs(Mathf.Sin(t * pulses * Mathf.PI)) * (1f - t);
        }

        icon.rectTransform.localScale = iconScale * scale;
    }
}
