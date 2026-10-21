using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UI;

/// <summary>
/// Tela de vitória, dona da sequência inteira de "escapou": ao entrar em <see cref="GameState.Won"/>, a tela (o branco
/// é a Image deste objeto, sob o CanvasGroup) aparece devagar enquanto o som do jogo inteiro desce na mesma curva,
/// e depois, no molde da <see cref="GameOverUI"/>: texto → animação do texto → tempo de jogo → "aperte qualquer
/// botão" → créditos. É a tela quem encerra a sessão (<see cref="GameManager.EndSession"/>).
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

    [Header("Texto")]
    [Tooltip("O texto é o escrito na cena; aqui só a opacidade é controlada.")]
    [SerializeField] private TMP_Text messageLabel;
    [SerializeField, Min(0f)] private float messageDelay = 1f;
    [SerializeField, Min(0.01f)] private float messageFadeDuration = 0.8f;

    [Tooltip("Animator que leva o texto à posição final. Fica desligado até o texto terminar de aparecer.")]
    [SerializeField] private Animator textAnimator;

    [Header("Tempo de jogo")]
    [Tooltip("O texto da cena (ex.: \"Seu tempo de jogo:\") vira o prefixo; o tempo é acrescentado depois dele.")]
    [SerializeField] private TMP_Text timeLabel;
    [SerializeField, Min(0.01f)] private float timeFadeDuration = 0.6f;

    [Header("Continuar")]
    [Tooltip("\"Aperte qualquer botão\". Pode ficar desativado na cena: o código o liga.")]
    [SerializeField] private TMP_Text continueLabel;
    [SerializeField, Min(0f)] private float continueDelay = 1f;
    [SerializeField, Min(0.01f)] private float continueFadeDuration = 0.6f;
    [SerializeField, Min(0.01f)] private float exitFadeDuration = 0.6f;

    [Tooltip("Segundos do branco ao preto antes de carregar os créditos. Longo de propósito: o jogo termina devagar.")]
    [SerializeField, Min(0.01f)] private float blackFadeDuration = 6f;

    [Tooltip("Cena carregada ao apertar um botão (precisa estar no Build Settings).")]
    [SerializeField] private string creditsScene = "Credits";

    [Header("Audio")]
    [Tooltip("AudioLibrary entry played when the fade starts. Empty = no sound.")]
    [SerializeField] private string stingerSoundId = "";

    /// <summary>A tela ficou toda branca (o som já está em zero). Dali em diante nada mais aparece por baixo dela.</summary>
    public static event Action CutToWhite;

    private CanvasGroup group;
    private bool fadingAudio;
    private string timePrefix = string.Empty;
    private IDisposable anyButton;
    private bool leaving;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;

        // Sem label a vitória ainda acontece, só sem texto: desligar o componente aqui levaria junto o clarão e o som.
        if (messageLabel != null)
            messageLabel.alpha = 0f;
        else
            Debug.LogWarning($"{name}: messageLabel não atribuído no Inspector — a vitória sai sem texto.", this);

        if (textAnimator != null)
            textAnimator.enabled = false;

        if (timeLabel != null)
        {
            timePrefix = timeLabel.text;
            timeLabel.alpha = 0f;
        }

        // Ativo desde o início: invisível pelo alpha, para o fade não depender de SetActive.
        if (continueLabel != null)
        {
            continueLabel.gameObject.SetActive(true);
            continueLabel.alpha = 0f;
        }
    }

    private void OnEnable() => GameManager.StateChanged += HandleState;

    private void OnDisable()
    {
        GameManager.StateChanged -= HandleState;
        StopListening();
    }

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
            yield return Fade(a => messageLabel.alpha = a, messageFadeDuration);

        if (textAnimator != null)
        {
            textAnimator.enabled = true;
            // Um frame para o Animator entrar no estado; antes disso normalizedTime ainda é do estado anterior e o
            // loop sairia na hora.
            yield return null;
            while (textAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime < 1f)
                yield return null;
        }

        if (timeLabel != null)
        {
            float seconds = GameManager.Current != null ? GameManager.Current.ElapsedTime : 0f;
            timeLabel.text = $"{timePrefix} {FormatTime(seconds)}";
            yield return Fade(a => timeLabel.alpha = a, timeFadeDuration);
        }

        yield return new WaitForSeconds(continueDelay);

        if (continueLabel != null)
            yield return Fade(a => continueLabel.alpha = a, continueFadeDuration);

        // Só agora: botão apertado durante a animação não pula a tela (nem o tempo de jogo).
        anyButton = InputSystem.onAnyButtonPress.Call(OnAnyButton);
    }

    private void OnAnyButton(InputControl control)
    {
        if (leaving || IsReserved(control))
            return;

        leaving = true;
        StopListening();
        StartCoroutine(Leave());
    }

    // Pause/Cancel do input map (Esc, Start...) ficam de fora, para não sair por acidente nem brigar com o pause.
    private static bool IsReserved(InputControl control)
    {
        return Matches(PlayerInputProvider.Player.Pause.controls, control)
            || Matches(PlayerInputProvider.UI.Cancel.controls, control);
    }

    private static bool Matches(ReadOnlyArray<InputControl> controls, InputControl control)
    {
        foreach (InputControl reserved in controls)
        {
            if (reserved == control)
                return true;
        }

        return false;
    }

    private void StopListening()
    {
        anyButton?.Dispose();
        anyButton = null;
    }

    private IEnumerator Leave()
    {
        // O branco é a Image deste objeto: escurece a cor dela até o preto, com os textos sumindo mais rápido por cima.
        Graphic background = GetComponent<Graphic>();
        Color from = background != null ? background.color : Color.white;

        Coroutine textsOut = StartCoroutine(Fade(a =>
        {
            float k = 1f - a;
            if (messageLabel != null) messageLabel.alpha = k;
            if (timeLabel != null) timeLabel.alpha = k;
            if (continueLabel != null) continueLabel.alpha = k;
        }, exitFadeDuration));

        yield return Fade(a =>
        {
            if (background != null)
                background.color = Color.Lerp(from, Color.black, a);
        }, blackFadeDuration);

        yield return textsOut;

        GameManager.EndSession(creditsScene);
    }

    // Termina sempre em 1: o loop sai com t < duration e o texto ficaria quase transparente para sempre.
    private static IEnumerator Fade(Action<float> apply, float duration)
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            apply(Mathf.SmoothStep(0f, 1f, t / duration));
            yield return null;
        }

        apply(1f);
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.FloorToInt(seconds);
        int hours = total / 3600;
        int minutes = total % 3600 / 60;
        int secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes:00}:{secs:00}";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => CutToWhite = null;
}
