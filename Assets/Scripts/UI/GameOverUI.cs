using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>Tela preta do game over. Aparece no corte da PlayerCaughtSequence, não no evento Lost.</summary>
[RequireComponent(typeof(CanvasGroup))]
public class GameOverUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerCaughtSequence sequence;
    [SerializeField] private TMP_Text messageLabel;
    [SerializeField] private TMP_Text hintLabel;

    [Header("Texto")]
    [SerializeField] private string message = "Ele te encontrou.";
    [SerializeField] private string hint = "R — tentar de novo";
    [SerializeField] private float messageDelay = 1f;
    [SerializeField] private float hintDelay = 1.5f;

    private CanvasGroup group;
    private bool canRestart;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        if (messageLabel == null || hintLabel == null)
        {
            Debug.LogError($"{name}: messageLabel/hintLabel não atribuídos no Inspector.", this);
            enabled = false;
            return;
        }

        messageLabel.text = string.Empty;
        hintLabel.text = string.Empty;
    }

    // Acquire/Release aqui e não só quando a tela aparece: o Player action map é compartilhado
    // e o PlayerCaughtSequence desliga todos os outros usuários ao ser pego — se este fosse o
    // único que sobrasse e ainda não tivesse adquirido, o map apagaria e o R nunca chegaria.
    private void OnEnable() => PlayerInputProvider.Acquire();

    private void OnDisable() => PlayerInputProvider.Release();

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

    private void Update()
    {
        if (canRestart && PlayerInputProvider.Player.Restart.WasPressedThisFrame())
            GameManager.Current.Restart();
    }

    public void Show() => StartCoroutine(ShowRoutine());

    private IEnumerator ShowRoutine()
    {
        group.alpha = 1f;
        yield return new WaitForSeconds(messageDelay);
        messageLabel.text = message;
        yield return new WaitForSeconds(hintDelay);
        hintLabel.text = hint;
        canRestart = true;
    }
}
