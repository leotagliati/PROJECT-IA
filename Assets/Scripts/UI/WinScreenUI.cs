using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Texto da vitória por cima do clarão da PlayerEscapedSequence. Só mostra: quem recarrega a
/// cena é o <see cref="GameManager"/>.
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
    }
}
