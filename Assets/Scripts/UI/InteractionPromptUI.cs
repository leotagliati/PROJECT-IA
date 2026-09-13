using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Texto embaixo da mira com a ação disponível: "[LMB] Pegar chave".
///
/// É só view. Quem decide o alvo é o InteractionController (assinado via TargetChanged), quem
/// descreve a ação é o próprio IInteractable (Prompt), e a tecla vem do binding atual do
/// Input System — trocar o botão no .inputactions troca o texto sem tocar aqui.
///
/// O Prompt é relido todo frame enquanto há alvo porque ele pode mudar sem o alvo trocar
/// (a porta destranca quando o inventário enche). A comparação é de string; a montagem do
/// texto final só acontece quando o prompt de fato muda.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class InteractionPromptUI : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Vazio: procura na cena. O player costuma ser prefab instanciado, então nem sempre dá para arrastar.")]
    [SerializeField] private InteractionController controller;

    [SerializeField] private TMP_Text label;

    [Header("Texto")]
    [Tooltip("{0} = tecla, {1} = ação.")]
    [SerializeField] private string format = "[{0}] {1}";

    [Tooltip("Grupo de binding usado para descobrir a tecla (nome do control scheme no .inputactions).")]
    [SerializeField] private string bindingGroup = "Keyboard&Mouse";

    [Header("Fade")]
    [SerializeField] private float fadeTime = 0.08f;

    private CanvasGroup group;
    private IInteractable target;
    private string keyLabel;
    private string shownPrompt;
    private float fadeVelocity;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        if (label == null)
            label = GetComponentInChildren<TMP_Text>();

        if (controller == null)
            controller = FindFirstObjectByType<InteractionController>();

        if (controller == null || label == null)
        {
            Debug.LogError($"{nameof(InteractionPromptUI)}: controller ou label não encontrados.", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        // Resolvido aqui, e não no Awake, para pegar rebind feito entre um enable e outro.
        keyLabel = PlayerInputProvider.Player.Interact
            .GetBindingDisplayString(InputBinding.MaskByGroup(bindingGroup));

        controller.TargetChanged += HandleTargetChanged;
        HandleTargetChanged(controller.CurrentInteractable);
    }

    private void OnDisable()
    {
        controller.TargetChanged -= HandleTargetChanged;
        HandleTargetChanged(null);
        group.alpha = 0f;
    }

    private void HandleTargetChanged(IInteractable newTarget)
    {
        target = newTarget;
        RefreshPrompt();
    }

    private void Update()
    {
        if (target != null)
            RefreshPrompt();

        float targetAlpha = string.IsNullOrEmpty(shownPrompt) ? 0f : 1f;
        group.alpha = Mathf.SmoothDamp(group.alpha, targetAlpha, ref fadeVelocity, fadeTime);
    }

    private void RefreshPrompt()
    {
        string prompt = target?.Prompt;

        if (string.Equals(prompt, shownPrompt, System.StringComparison.Ordinal))
            return;

        shownPrompt = prompt;

        if (!string.IsNullOrEmpty(prompt))
            label.text = string.Format(format, keyLabel, prompt);
    }
}
