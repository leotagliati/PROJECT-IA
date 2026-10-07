using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Texto embaixo da mira com a ação disponível ("(ícone) Destrancar cadeado") e, quando a
/// ação é recusada, o motivo no lugar dela por alguns instantes ("Requer uma chave").
///
/// É só view. Quem decide o alvo é o InteractionController (TargetChanged), quem descreve a
/// ação e o motivo da falha é o próprio IInteractable, e a tecla/ícone vem do
/// <see cref="HintController"/>: mesma marcação, mesmos sprites e a mesma troca automática
/// teclado ↔ controle das dicas.
///
/// Não é uma dica do HintController de propósito: lá é uma linha só, empilhada, e o prompt
/// de mirar numa porta esconderia a instrução do tutorial que ainda está esperando o
/// jogador. Daqui só se usa o <see cref="HintController.Format"/>. Cena sem HintController
/// cai no texto simples "[tecla] ação".
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

    [Tooltip("Vazio: procura na cena. Sem nenhum, o prompt usa o texto simples.")]
    [SerializeField] private HintController hints;

    [SerializeField] private TMP_Text label;

    [Header("Prompt")]
    [Tooltip("Marcação do HintController ({Action}, [Tecla], *destaque*). {0} = texto da ação.")]
    [SerializeField] private string promptMarkup = "{Interact} {0}";

    [Tooltip("Só sem HintController: grupo de binding usado para descobrir a tecla.")]
    [SerializeField] private string bindingGroup = "Keyboard&Mouse";

    [Header("Falha")]
    [Tooltip("Por quanto tempo o motivo da falha substitui o prompt.")]
    [SerializeField] private float failureDuration = 1.5f;

    [SerializeField] private Color failureColor = new Color(1f, 0.45f, 0.4f);

    [Header("Fade")]
    [SerializeField] private float fadeTime = 0.08f;

    private CanvasGroup group;
    private IInteractable target;
    private string fallbackKey;
    private string shownPrompt;
    private string failureMessage;
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

        if (hints == null)
            hints = FindFirstObjectByType<HintController>();

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
        fallbackKey = PlayerInputProvider.Player.Interact
            .GetBindingDisplayString(InputBinding.MaskByGroup(bindingGroup));

        controller.TargetChanged += HandleTargetChanged;
        controller.InteractionFailed += HandleInteractionFailed;

        if (hints != null)
            hints.SchemeChanged += Redraw;

        HandleTargetChanged(controller.CurrentInteractable);
    }

    private void OnDisable()
    {
        controller.TargetChanged -= HandleTargetChanged;
        controller.InteractionFailed -= HandleInteractionFailed;

        if (hints != null)
            hints.SchemeChanged -= Redraw;

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
        failureMessage = message;
        Redraw();
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
            Redraw();
    }

    private void EndFailure()
    {
        if (!showingFailure)
            return;

        showingFailure = false;
        Redraw();
    }

    // Também é o handler do SchemeChanged: pegar o controle troca o ícone do prompt já na
    // tela, sem esperar a mira mudar de alvo.
    private void Redraw()
    {
        if (showingFailure)
        {
            string message = hints != null ? hints.Format(failureMessage) : failureMessage;
            label.text = $"<color=#{ColorUtility.ToHtmlStringRGBA(failureColor)}>{message}</color>";
            return;
        }

        // Prompt vazio deixa o texto anterior no lugar: é ele que aparece durante o fade out.
        if (string.IsNullOrEmpty(shownPrompt))
            return;

        if (hints != null)
        {
            // Os <sprite> do Format apontam para o asset do esquema ativo; este label é
            // outro TMP, então o asset tem que vir junto.
            label.spriteAsset = hints.Icons;

            // Replace, e não string.Format: as chaves de {Interact} brigariam com as do Format.
            label.text = hints.Format(promptMarkup.Replace("{0}", shownPrompt));
        }
        else
        {
            label.text = $"[{fallbackKey}] {shownPrompt}";
        }
    }
}
