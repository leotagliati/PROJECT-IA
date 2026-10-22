using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

/// <summary>
/// Texto da vitória por cima do clarão da PlayerEscapedSequence e, depois de um tempo, "aperte qualquer botão" →
/// créditos. A vitória não recarrega a cena: é aqui que a sessão termina.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class WinScreenUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerEscapedSequence sequence;
    [SerializeField] private TMP_Text messageLabel;

    [Header("Texto")]
    [SerializeField] private string message = "Você escapou.";
    [SerializeField] private float messageDelay = 1f;

    [Header("Continuar")]
    [Tooltip("Opcional. Vazio = sem texto, mas qualquer botão ainda leva aos créditos.")]
    [SerializeField] private TMP_Text continueLabel;
    [SerializeField] private string continueMessage = "Aperte qualquer botão";
    [Tooltip("Segundos depois do texto até aceitar o botão: o jogador que vinha correndo não pula a tela sem querer.")]
    [SerializeField, Min(0f)] private float continueDelay = 2f;
    [SerializeField] private SceneReference creditsScene;

    private CanvasGroup group;
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
        {
            Debug.LogError($"{name}: messageLabel não atribuído no Inspector.", this);
            enabled = false;
            return;
        }

        messageLabel.text = string.Empty;
        if (continueLabel != null)
            continueLabel.text = string.Empty;
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
        group.alpha = 1f;
        yield return new WaitForSeconds(messageDelay);
        messageLabel.text = message;

        yield return new WaitForSeconds(continueDelay);
        if (continueLabel != null)
            continueLabel.text = continueMessage;

        if (creditsScene == null || !creditsScene.IsSet)
        {
            Debug.LogError($"{name}: creditsScene não atribuída — a vitória para aqui.", this);
            yield break;
        }

        InputSystem.onAnyButtonPress.CallOnce(_ => GoToCredits());
    }

    private void GoToCredits()
    {
        if (leaving || this == null)
            return;

        leaving = true;
        GameManager.EndSession();
        creditsScene.Load();
    }
}
