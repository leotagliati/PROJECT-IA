using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Texto embaixo da mira com a ação disponível ("[LMB] Destrancar cadeado") e, quando a
/// ação é recusada, o motivo no lugar dela por alguns instantes ("Requer uma chave").
///
/// É só view. Quem decide o alvo é o InteractionController (TargetChanged), quem descreve a
/// ação e o motivo da falha é o próprio IInteractable, e a tecla vem do binding atual do
/// Input System — trocar o botão no .inputactions troca o texto sem tocar aqui.
///
/// O Prompt é relido todo frame enquanto há alvo porque ele pode mudar sem o alvo trocar.
/// A comparação é de string; a montagem do texto final só acontece quando o prompt muda.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class InteractionPromptUI : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Vazio: procura na cena. O player costuma ser prefab instanciado, então nem sempre dá para arrastar.")]
    [SerializeField] private InteractionController controller;

    [SerializeField] private TMP_Text label;

    [Header("Prompt")]
    [Tooltip("{0} = tecla, {1} = ação.")]
    [SerializeField] private string format = "[{0}] {1}";

    [Tooltip("Grupo de binding usado para descobrir a tecla (nome do control scheme no .inputactions).")]
    [SerializeField] private string bindingGroup = "Keyboard&Mouse";

    [Header("Falha")]
    [Tooltip("Por quanto tempo o motivo da falha substitui o prompt.")]
    [SerializeField] private float failureDuration = 1.5f;

    [SerializeField] private Color failureColor = new Color(1f, 0.45f, 0.4f);

    [Header("Fade")]
    [SerializeField] private float fadeTime = 0.08f;

    private CanvasGroup group;
    private IInteractable target;
    private string keyLabel;
    private string shownPrompt;
    private float fadeVelocity;
    private float failureUntil;
    private bool showingFailure;

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
            return;
        }
    }

    private void OnEnable()
    {
        // Resolvido aqui, e não no Awake, para pegar rebind feito entre um enable e outro.
        keyLabel = PlayerInputProvider.Player.Interact
            .GetBindingDisplayString(InputBinding.MaskByGroup(bindingGroup));

        controller.TargetChanged += HandleTargetChanged;
        controller.InteractionFailed += HandleInteractionFailed;
        HandleTargetChanged(controller.CurrentInteractable);
    }

    private void OnDisable()
    {
        controller.TargetChanged -= HandleTargetChanged;
        controller.InteractionFailed -= HandleInteractionFailed;

        EndFailure();
        HandleTargetChanged(null);
        group.alpha = 0f;
    }

    private void HandleTargetChanged(IInteractable newTarget)
    {
        target = newTarget;
        RefreshPrompt();
    }

    private void HandleInteractionFailed(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        showingFailure = true;
        failureUntil = Time.time + failureDuration;

        label.text = message;
    }

    private void Update()
    {
        if (showingFailure && Time.time >= failureUntil)
            EndFailure();

        if (target != null)
            RefreshPrompt();

        bool visible = showingFailure || !string.IsNullOrEmpty(shownPrompt);
        group.alpha = Mathf.SmoothDamp(group.alpha, visible ? 1f : 0f, ref fadeVelocity, fadeTime);
    }

    private void RefreshPrompt()
    {
        string prompt = target?.Prompt;

        if (string.Equals(prompt, shownPrompt, System.StringComparison.Ordinal))
            return;

        shownPrompt = prompt;

        if (!showingFailure)
            ApplyPrompt();
    }

    private void EndFailure()
    {
        if (!showingFailure)
            return;

        showingFailure = false;
        ApplyPrompt();
    }

    private void ApplyPrompt()
    {
        // Prompt vazio deixa o texto anterior no lugar: é ele que aparece durante o fade out.
        if (!string.IsNullOrEmpty(shownPrompt))
            label.text = string.Format(format, keyLabel, shownPrompt);
    }
}
