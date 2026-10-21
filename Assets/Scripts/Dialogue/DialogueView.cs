using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Parte visível do diálogo: escreve nos textos, revela os caracteres, monta o prompt com o ícone da tecla e lê o
/// input de avançar, que só repassa em <see cref="AdvancePressed"/>. Não sabe o que é uma conversa nem quando ela
/// acaba — isso é do <see cref="DialogueController"/>.
/// </summary>
public class DialogueView : MonoBehaviour
{
    // Ação dedicada, com teclas concretas: o Submit do mapa UI é por usage (*/{Submit}) e não tem ícone para mostrar.
    private const string AdvanceAction = "UI/DialogueAdvance";

    [Tooltip("O que liga e desliga com a caixa. Vazio = este objeto.")]
    [SerializeField] private GameObject root;

    [Tooltip("Opcional. Some quando a fala não tem nome.")]
    [SerializeField] private TMP_Text speakerText;

    [SerializeField] private TMP_Text bodyText;

    [Tooltip("Opcional. Recebe o texto abaixo com o ícone da tecla no esquema ativo (teclado/controle).")]
    [SerializeField] private TMP_Text promptText;

    [Tooltip("Somem enquanto a conversa está aberta e voltam ao alpha que tinham. Via CanvasGroup (posto se faltar).")]
    [SerializeField] private GameObject[] hideWhileOpen;

    [SerializeField] private string typingPrompt = "Aperte {0} para pular";
    [SerializeField] private string waitingPrompt = "Aperte {0} para continuar";

    private InputAction advance;
    private bool lineComplete;
    private bool skippable;
    private bool shown;
    private CanvasGroup[] hiddenGroups;
    private float[] hiddenAlphas;

    public event Action AdvancePressed;

    public GameObject Root => root != null ? root : gameObject;

    private void Awake()
    {
        // Com o root salvo desligado, o Awake só roda dentro do SetActive(true) do primeiro Show — não pode
        // desligar de volta o que acabou de abrir.
        if (!shown)
            Root.SetActive(false);
    }

    public void Show()
    {
        if (shown)
            return;

        shown = true;
        Root.SetActive(true);

        // Assinado aqui e não no OnEnable: callback de input chega mesmo com o objeto desligado, e o root pode ser
        // este próprio objeto.
        advance = PlayerInputProvider.Actions.asset.FindAction(AdvanceAction, throwIfNotFound: true);
        advance.performed += OnAdvance;
        InputIcons.SchemeChanged += RefreshPrompt;

        HideOthers();
    }

    public void Hide()
    {
        if (!shown)
            return;

        Unsubscribe();
        Root.SetActive(false);
        RestoreOthers();
    }

    // CanvasGroup e não SetActive: desativar o objeto desligaria os scripts dele (eventos, corrotinas) e o
    // estado se perderia até a conversa acabar. Guarda todos os alphas antes de zerar, para um item repetido
    // na lista não gravar o 0 como "original".
    private void HideOthers()
    {
        if (hideWhileOpen == null || hideWhileOpen.Length == 0)
            return;

        hiddenGroups = new CanvasGroup[hideWhileOpen.Length];
        hiddenAlphas = new float[hideWhileOpen.Length];

        for (int i = 0; i < hideWhileOpen.Length; i++)
        {
            if (hideWhileOpen[i] == null)
                continue;

            var group = hideWhileOpen[i].GetComponent<CanvasGroup>();
            if (group == null)
                group = hideWhileOpen[i].AddComponent<CanvasGroup>();

            hiddenGroups[i] = group;
            hiddenAlphas[i] = group.alpha;
        }

        ApplyHidden();
    }

    // Ordem inversa: item repetido termina no alpha original. Grupo destruído (troca de cena) é pulado.
    private void RestoreOthers()
    {
        if (hiddenGroups == null)
            return;

        for (int i = hiddenGroups.Length - 1; i >= 0; i--)
        {
            if (hiddenGroups[i] != null)
                hiddenGroups[i].alpha = hiddenAlphas[i];
        }

        hiddenGroups = null;
        hiddenAlphas = null;
    }

    private void ApplyHidden()
    {
        foreach (CanvasGroup group in hiddenGroups)
        {
            if (group != null)
                group.alpha = 0f;
        }
    }

    // Reforço: quem mexe no próprio alpha todo Update (ex.: StaminaBarUI sem pausa) reapareceria durante a
    // conversa. O LateUpdate roda depois de todos os Update, então o zero sempre vence.
    private void LateUpdate()
    {
        if (shown && hiddenGroups != null)
            ApplyHidden();
    }

    private void Unsubscribe()
    {
        shown = false;
        advance.performed -= OnAdvance;
        InputIcons.SchemeChanged -= RefreshPrompt;
    }

    /// <summary>
    /// Monta a fala inteira, ainda invisível. Devolve quantos caracteres há para revelar. Sem <paramref name="canSkip"/>,
    /// o prompt some enquanto ela é digitada, em vez de prometer um pulo que não acontece.
    /// </summary>
    public int SetLine(string speaker, string text, bool canSkip = true)
    {
        if (speakerText != null)
        {
            speakerText.text = speaker;
            speakerText.gameObject.SetActive(!string.IsNullOrEmpty(speaker));
        }

        // Texto inteiro já montado e só o maxVisibleCharacters anda: a quebra de linha não pula no meio da palavra.
        bodyText.text = text;
        bodyText.maxVisibleCharacters = 0;
        bodyText.ForceMeshUpdate();

        skippable = canSkip;
        SetLineComplete(false);
        return bodyText.textInfo.characterCount;
    }

    public void SetVisibleCharacters(int count)
    {
        bodyText.maxVisibleCharacters = count;
    }

    /// <summary>Troca o prompt entre "pular" (digitando) e "continuar" (fala completa).</summary>
    public void SetLineComplete(bool complete)
    {
        lineComplete = complete;
        RefreshPrompt();
    }

    private void OnAdvance(InputAction.CallbackContext context) => AdvancePressed?.Invoke();

    private void RefreshPrompt()
    {
        if (promptText == null || !shown)
            return;

        string icon = InputIcons.GetActionIconName(AdvanceAction);
        string key = icon != null
            ? $"<sprite name=\"{icon}\">"
            : $"[{advance.GetBindingDisplayString(InputBinding.MaskByGroup(InputIcons.ActiveBindingGroup))}]";

        promptText.spriteAsset = InputIcons.Icons;
        promptText.text = lineComplete ? string.Format(waitingPrompt, key)
                        : skippable ? string.Format(typingPrompt, key)
                        : string.Empty;
    }

    // Só desassina: o asset de input e o InputIcons sobrevivem à cena e segurariam a view destruída.
    private void OnDestroy()
    {
        if (shown)
            Unsubscribe();
    }
}
