using System;
using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Tela de vitória, dona da sequência inteira de "escapou": ao entrar em <see cref="GameState.Won"/>, a tela (o branco
/// é a Image deste objeto, sob o CanvasGroup) aparece devagar enquanto o som do jogo inteiro desce na mesma curva,
/// e o texto entra por cima depois. Quem recarrega a cena / vai ao menu é o <see cref="GameManager"/>.
///
/// <see cref="CutToWhite"/> é estático para ninguém precisar achar a tela: o player (<see cref="PlayerEscapedSequence"/>)
/// e a cutscene final ouvem e param o que é deles quando a tela fica toda branca.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class WinScreenUI : MonoBehaviour
{
    [Header("White Fade")]
    [Tooltip("Seconds until the screen is fully white. Slow on purpose: the flash is relief, not a scare.")]
    [SerializeField, Min(0.01f)] private float fadeDuration = 2f;

    [Header("Message")]
    [SerializeField] private TMP_Text messageLabel;
    [SerializeField] private string message = "Você escapou.";
    [SerializeField] private float messageDelay = 1f;

    [Header("Audio")]
    [Tooltip("AudioLibrary entry played when the fade starts. Empty = no sound.")]
    [SerializeField] private string stingerSoundId = "";

    /// <summary>A tela ficou toda branca (o som já está em zero). Dali em diante nada mais aparece por baixo dela.</summary>
    public static event Action CutToWhite;

    private CanvasGroup group;
    private bool fadingAudio;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        // Sem label a vitória ainda acontece, só sem texto: desligar o componente aqui levaria junto o clarão e o som.
        if (messageLabel != null)
            messageLabel.text = string.Empty;
        else
            Debug.LogWarning($"{name}: messageLabel não atribuído no Inspector — a vitória sai sem texto.", this);
    }

    private void OnEnable() => GameManager.StateChanged += HandleState;

    private void OnDisable() => GameManager.StateChanged -= HandleState;

    // O AudioListener é global e sobrevive ao load: sem isto o menu abriria mudo. Roda na descarga da cena, antes de
    // a próxima começar, então ela já nasce no volume que o jogador configurou.
    private void OnDestroy()
    {
        if (fadingAudio)
            AudioProvider.Fade = 1f;
    }

    private void HandleState(GameState state)
    {
        if (state == GameState.Won)
            StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // A tela não tem posição no mundo: o stinger segue o ouvinte, como o RumbleSoundEffect.
        if (!string.IsNullOrEmpty(stingerSoundId))
        {
            AudioListener listener = FindFirstObjectByType<AudioListener>();
            AudioProvider.PlayFollowing(stingerSoundId, listener != null ? listener.transform : transform);
        }

        // O som do jogo inteiro some junto com a imagem, na mesma curva. Pelo Fade do AudioProvider, e não pelo
        // volume do jogador: a configuração dele fica intacta e o OnDestroy devolve o som antes da próxima cena.
        fadingAudio = true;

        for (float t = 0f; t < fadeDuration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / fadeDuration);
            group.alpha = k;
            AudioProvider.Fade = 1f - k;
            yield return null;
        }

        // O loop sai com t < fadeDuration, então o alpha pararia um pouco antes de 1 e a tela ficaria quase branca
        // para sempre.
        group.alpha = 1f;
        AudioProvider.Fade = 0f;

        CutToWhite?.Invoke();

        yield return new WaitForSeconds(messageDelay);

        if (messageLabel != null)
            messageLabel.text = message;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => CutToWhite = null;
}
