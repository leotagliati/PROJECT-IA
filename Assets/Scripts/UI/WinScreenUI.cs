using System.Collections;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(CanvasGroup))]
public class WinScreenUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerEscapedSequence sequence;
    [SerializeField] private TMP_Text messageLabel;
    [SerializeField] private TMP_Text hintLabel;

    [Header("Texto")]
    [SerializeField] private string message = "Você escapou.";
    [SerializeField] private string hint = "R — jogar de novo";
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

    // Mesmo motivo da GameOverUI: o Player action map é compartilhado e a sequência desliga os
    // outros usuários assim que o clarão fecha — se este fosse o único restante e ainda não
    // tivesse adquirido, o map apagaria e o R nunca chegaria.
    private void OnEnable() => PlayerInputProvider.Acquire();

    private void OnDisable() => PlayerInputProvider.Release();

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
