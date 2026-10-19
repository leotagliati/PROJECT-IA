using TMPro;
using UnityEngine;

/// <summary>
/// Contador de chaves do HUD (ícone + "× N"), ao lado da barra de stamina. Só lê o <see cref="PlayerInventory"/>.
///
/// Acende só quando o jogador pega uma chave e, sem pegar outra, vai ficando discreto: HUD aceso o tempo todo tira
/// a imersão, e o número só é novidade na hora da coleta. Gastar chave atualiza o número sem acender. Sem chave
/// nenhuma, some.
///
/// A posição salva na cena é a de cima da barra; quando a <see cref="StaminaBarUI"/> se esconde, o contador desce
/// para o lugar dela, acompanhando o fade da barra, em vez de ficar flutuando sobre um vazio.
/// </summary>
public class KeyCounterUI : MonoBehaviour
{
    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private PlayerInventory inventory;

    [Tooltip("Vazio = procura na cena. Sem barra, o contador fica sempre na posição de baixo.")]
    [SerializeField] private StaminaBarUI staminaBar;

    [SerializeField] private TMP_Text countText;

    [SerializeField] private string format = "× {0}";

    [Tooltip("O que sobe e desce com a barra. Vazio = este objeto.")]
    [SerializeField] private RectTransform body;

    [Tooltip("Quanto desce quando a barra some, em unidades do Canvas.")]
    [SerializeField] private float dropDistance = 32f;

    [Header("Aceso / discreto")]
    [Tooltip("Segundos aceso depois de pegar uma chave.")]
    [SerializeField, Min(0f)] private float activeTime = 3f;

    [SerializeField, Min(0.01f)] private float dimDuration = 1.2f;

    [SerializeField, Range(0f, 1f)] private float idleAlpha = 0.4f;

    [Tooltip("Segundos do pulo do número ao pegar.")]
    [SerializeField, Min(0.01f)] private float popDuration = 0.35f;

    [SerializeField] private float popScale = 1.45f;

    private CanvasGroup group;
    private Vector2 restPosition;
    private int count;
    private float pickedAt = float.NegativeInfinity;
    private bool paused;

    private void Awake()
    {
        if (body == null)
            body = (RectTransform)transform;

        restPosition = body.anchoredPosition;

        if (!TryGetComponent(out group))
            group = gameObject.AddComponent<CanvasGroup>();

        group.interactable = false;
        group.blocksRaycasts = false;
        group.alpha = 0f;
    }

    private void OnEnable()
    {
        PlayerInventory.OnKeyCountChanged += HandleCountChanged;
        PauseControler.OnPaused += HandlePaused;
        PauseControler.OnResumed += HandleResumed;
    }

    private void OnDisable()
    {
        PlayerInventory.OnKeyCountChanged -= HandleCountChanged;
        PauseControler.OnPaused -= HandlePaused;
        PauseControler.OnResumed -= HandleResumed;
    }

    private void Start()
    {
        if (inventory == null)
            inventory = FindFirstObjectByType<PlayerInventory>();

        if (staminaBar == null)
            staminaBar = FindFirstObjectByType<StaminaBarUI>();

        // Chave que já veio da cena (keyCount no Inspector) aparece discreta, sem a animação de coleta.
        count = inventory != null ? inventory.KeyCount : 0;
        RefreshText();
    }

    private void HandleCountChanged(int total)
    {
        if (total > count)
            pickedAt = Time.time;

        count = total;
        RefreshText();
    }

    private void HandlePaused()
    {
        paused = true;
        group.alpha = 0f;
    }

    private void HandleResumed() => paused = false;

    private void RefreshText()
    {
        if (countText != null)
            countText.text = string.Format(format, count);
    }

    private void Update()
    {
        // Mesmo motivo da barra: com timeScale 0 nada anda, e o menu de pausa fica limpo.
        if (paused)
            return;

        // Tempo escalado: bilhete de tutorial ou diálogo aberto logo depois da coleta não gastam o tempo aceso.
        float since = Time.time - pickedAt;
        float lit = 1f - Mathf.Clamp01((since - activeTime) / dimDuration);
        float target = count > 0 ? Mathf.Lerp(idleAlpha, 1f, lit) : 0f;

        // Acende na hora (é a coleta acontecendo); apaga no ritmo do dimDuration.
        group.alpha = target > group.alpha ? target : Mathf.MoveTowards(group.alpha, target, Time.deltaTime / dimDuration);

        float bar = staminaBar != null ? staminaBar.Visibility : 0f;
        body.anchoredPosition = restPosition + Vector2.down * (dropDistance * (1f - bar));

        if (countText != null)
        {
            float pop = since < popDuration ? 1f - since / popDuration : 0f;
            countText.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, popScale, pop * pop);
        }
    }
}
