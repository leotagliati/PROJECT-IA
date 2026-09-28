using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Tela preta do game over. Aparece no corte da PlayerCaughtSequence, não no evento Lost.
/// Só mostra: quem recarrega a cena é o <see cref="GameManager"/>.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class GameOverUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerCaughtSequence sequence;
    [SerializeField] private TMP_Text messageLabel;

    [Header("Texto")]
    [SerializeField] private string message = "Ele te encontrou.";
    [SerializeField] private float messageDelay = 1f;

    private CanvasGroup group;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        if (messageLabel == null)
        {
            Debug.LogError($"{name}: messageLabel não atribuído no Inspector.", this);
            enabled = false;
            return;
        }

        messageLabel.text = string.Empty;
    }

    private void Start()
    {
        if (sequence == null)
            sequence = FindFirstObjectByType<PlayerCaughtSequence>();

        if (sequence != null)
            sequence.CutToBlack += Show;
        else
            Debug.LogError($"{name}: PlayerCaughtSequence não encontrada na cena.", this);
    }

    private void OnDestroy()
    {
        if (sequence != null)
            sequence.CutToBlack -= Show;
    }

    public void Show() => StartCoroutine(ShowRoutine());

    private IEnumerator ShowRoutine()
    {
        group.alpha = 1f;
        yield return new WaitForSeconds(messageDelay);
        messageLabel.text = message;
    }
}
