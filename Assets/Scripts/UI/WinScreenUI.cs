using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UI;

/// <summary>
/// Tela de vitória, por cima do clarão da PlayerEscapedSequence (que é o único fade de entrada: a imagem do clarão
/// tem a mesma cor do fundo desta tela, então a troca não aparece). "Você escapou." é digitado, a linha cresce, o tempo de jogo aparece e o "aperte qualquer botão" pulsa. Qualquer
/// botão escurece a tela e leva aos créditos: a vitória não recarrega a cena, é aqui que a sessão termina.
///
/// O texto do título vem daqui (<see cref="message"/>); os outros textos e a arrumação são os da cena (o menu
/// PROJECT-IA → "Final: montar tela de vitória" monta tudo).
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class WinScreenUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerEscapedSequence sequence;

    [Header("Título")]
    [SerializeField] private TMP_Text messageLabel;
    [SerializeField] private string message = "Você escapou.";
    [SerializeField, Min(0f)] private float messageDelay = 0.6f;
    [Tooltip("Segundos por letra.")]
    [SerializeField, Min(0.01f)] private float typeInterval = 0.09f;
    [Tooltip("Entrada da AudioLibrary tocada a cada letra. Vazio = sem som.")]
    [SerializeField] private string typeSoundId = "dialogue_type";

    [Header("Linha")]
    [SerializeField] private RectTransform divider;
    [SerializeField, Min(0.01f)] private float dividerGrow = 0.4f;

    [Header("Tempo de jogo")]
    [Tooltip("O texto fixo (\"Tempo de jogo\"). Só a opacidade é controlada.")]
    [SerializeField] private TMP_Text timeCaption;
    [SerializeField] private TMP_Text timeLabel;
    [SerializeField, Min(0.01f)] private float timeFade = 0.6f;

    [Header("Continuar")]
    [SerializeField] private TMP_Text continueLabel;
    [SerializeField] private string continueMessage = "Aperte qualquer botão";
    [Tooltip("Espera depois do tempo de jogo até aceitar o botão: quem vinha correndo não pula a tela sem querer.")]
    [SerializeField, Min(0f)] private float continueDelay = 1.2f;
    [Tooltip("Segundos de um ciclo do pulso.")]
    [SerializeField, Min(0.1f)] private float continuePulse = 1.8f;
    [SerializeField, Range(0f, 1f)] private float continueMinAlpha = 0.25f;

    [Header("Saída")]
    [SerializeField, Min(0.01f)] private float blackFade = 1.5f;
    [SerializeField] private SceneReference creditsScene;

    private CanvasGroup group;
    private float dividerWidth;
    private bool accepting;
    private bool leaving;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        // A tela não tem nada clicável: com alpha 0 e raycast ligado, ela vira um painel invisível em tela cheia
        // que come o mouse de quem estiver embaixo (o menu de Pause, conforme a ordem dos irmãos no Canvas).
        group.blocksRaycasts = false;
        group.interactable = false;

        if (messageLabel == null)
            Debug.LogWarning($"{name}: messageLabel não atribuído — a vitória sai sem título.", this);

        SetText(messageLabel, string.Empty);
        SetAlpha(timeCaption, 0f);
        SetAlpha(timeLabel, 0f);
        SetAlpha(continueLabel, 0f);

        if (divider != null)
        {
            dividerWidth = divider.sizeDelta.x;
            divider.sizeDelta = new Vector2(0f, divider.sizeDelta.y);
        }
    }

    private void Start()
    {
        if (sequence == null)
            sequence = FindFirstObjectByType<PlayerEscapedSequence>();

        if (sequence != null)
            sequence.CutToWhite += Show;
        else
            Debug.LogError($"{name}: PlayerEscapedSequence não encontrada na cena.", this);
    }

    private void OnDestroy()
    {
        if (sequence != null)
            sequence.CutToWhite -= Show;
    }

    public void Show() => StartCoroutine(ShowRoutine());

    private IEnumerator ShowRoutine()
    {
        // O fade já foi o clarão da PlayerEscapedSequence: aqui a tela só entra por baixo dele, na mesma cor.
        group.alpha = 1f;
        yield return new WaitForSeconds(messageDelay);

        if (messageLabel != null)
            yield return Type(messageLabel, message);

        if (divider != null)
            yield return Fade(k => divider.sizeDelta = new Vector2(dividerWidth * k, divider.sizeDelta.y), dividerGrow);

        if (timeLabel != null)
        {
            float seconds = GameManager.Current != null ? GameManager.Current.ElapsedTime : 0f;
            timeLabel.text = FormatTime(seconds);
        }

        yield return Fade(a =>
        {
            SetAlpha(timeCaption, a);
            SetAlpha(timeLabel, a);
        }, timeFade);

        yield return new WaitForSeconds(continueDelay);

        if (creditsScene == null || !creditsScene.IsSet)
        {
            Debug.LogError($"{name}: creditsScene não atribuída — a vitória para aqui.", this);
            yield break;
        }

        SetText(continueLabel, continueMessage);
        accepting = true;
        InputSystem.onAnyButtonPress.CallOnce(_ => Leave());

        // Pulsa até sair. A primeira subida parte do zero, para o texto não aparecer de uma vez; dali em diante
        // oscila entre o mínimo e o máximo (no pico os dois trechos coincidem, sem salto).
        for (float t = 0f; !leaving; t += Time.deltaTime)
        {
            float wave = 0.5f - 0.5f * Mathf.Cos(t / continuePulse * Mathf.PI * 2f);
            float floor = t < continuePulse * 0.5f ? 0f : continueMinAlpha;
            SetAlpha(continueLabel, Mathf.Lerp(floor, 1f, wave));
            yield return null;
        }
    }

    // maxVisibleCharacters em vez de cortar a string: o layout do texto fica parado enquanto ele aparece.
    private IEnumerator Type(TMP_Text label, string text)
    {
        label.text = text;
        label.maxVisibleCharacters = 0;
        label.ForceMeshUpdate();
        int total = label.textInfo.characterCount;

        // A tela não tem posição no mundo: o som sai no ouvinte, como o RumbleSoundEffect.
        AudioListener listener = FindFirstObjectByType<AudioListener>();
        Vector3 ear = listener != null ? listener.transform.position : transform.position;

        for (int i = 1; i <= total; i++)
        {
            label.maxVisibleCharacters = i;

            char shown = label.textInfo.characterInfo[i - 1].character;
            if (!char.IsWhiteSpace(shown) && !string.IsNullOrEmpty(typeSoundId))
                AudioProvider.PlayAt(typeSoundId, ear);

            yield return new WaitForSeconds(typeInterval);
        }
    }

    private void Leave()
    {
        if (!accepting || leaving || this == null)
            return;

        leaving = true;
        StartCoroutine(LeaveRoutine());
    }

    private IEnumerator LeaveRoutine()
    {
        SetAlpha(continueLabel, 0f);

        // Véu preto por cima de tudo desta tela: o texto escurece junto com o fundo, sem passar por cinza.
        var veil = new GameObject("BlackVeil", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        veil.transform.SetParent(transform, false);
        veil.raycastTarget = false;
        var rect = veil.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        yield return Fade(a => veil.color = new Color(0f, 0f, 0f, a), blackFade);

        GameManager.EndSession();
        creditsScene.Load();
    }

    // Termina sempre em 1: o laço sai com t um pouco antes do fim.
    private static IEnumerator Fade(Action<float> apply, float duration)
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            apply(Mathf.SmoothStep(0f, 1f, t / duration));
            yield return null;
        }

        apply(1f);
    }

    private static void SetAlpha(TMP_Text label, float alpha)
    {
        if (label != null)
            label.alpha = alpha;
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null)
            label.text = text;
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.FloorToInt(seconds);
        int hours = total / 3600;
        int minutes = total % 3600 / 60;
        int secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes:00}:{secs:00}";
    }
}
