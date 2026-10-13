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

    [SerializeField] private string typingPrompt = "Aperte {0} para pular";
    [SerializeField] private string waitingPrompt = "Aperte {0} para continuar";

    private InputAction advance;
    private bool lineComplete;
    private bool shown;

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
    }

    public void Hide()
    {
        if (!shown)
            return;

        Unsubscribe();
        Root.SetActive(false);
    }

    private void Unsubscribe()
    {
        shown = false;
        advance.performed -= OnAdvance;
        InputIcons.SchemeChanged -= RefreshPrompt;
    }

    /// <summary>Monta a fala inteira, ainda invisível. Devolve quantos caracteres há para revelar.</summary>
    public int SetLine(string speaker, string text)
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
        promptText.text = string.Format(lineComplete ? waitingPrompt : typingPrompt, key);
    }

    // Só desassina: o asset de input e o InputIcons sobrevivem à cena e segurariam a view destruída.
    private void OnDestroy()
    {
        if (shown)
            Unsubscribe();
    }
}
