using System;
using System.Collections;
using UnityEngine;

public class PlayerEscapedSequence : MonoBehaviour
{
    [Header("Clarão")]
    [Tooltip("CanvasGroup de uma imagem branca em tela cheia. Começa em 0.")]
    [SerializeField] private CanvasGroup whiteFade;

    [Tooltip("Segundos até a tela ficar toda branca. Devagar de propósito: o clarão é alívio, não susto.")]
    [SerializeField, Min(0.01f)] private float fadeDuration = 2f;

    [Header("Depois do clarão")]
    [Tooltip("Desligados só quando a tela já está branca. Vazio: movimento, câmera, SpineLook, CameraJuice, ShoulderPeek e InteractionController deste objeto.")]
    [SerializeField] private Behaviour[] disableAfterFade;

    [SerializeField] private Flashlight flashlight;

    [Header("Áudio")]
    [Tooltip("Vazio = sem som.")]
    [SerializeField] private string stingerSoundId = "";

    public event Action CutToWhite;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponentInChildren<Flashlight>();

        // Tudo que lê input do Player entra aqui, não só quem move a câmera: o
        // InteractionController continuaria aceitando E numa porta por baixo da tela branca.
        if (disableAfterFade == null || disableAfterFade.Length == 0)
        {
            disableAfterFade = new Behaviour[]
            {
                GetComponentInChildren<PlayerMovement>(),
                GetComponentInChildren<PlayerCamera>(),
                GetComponentInChildren<SpineLook>(),
                GetComponentInChildren<CameraJuice>(),
                GetComponentInChildren<ShoulderPeek>(),
                GetComponentInChildren<InteractionController>(),
            };
        }

        if (whiteFade != null)
            whiteFade.alpha = 0f;
        else
            Debug.LogError($"{name}: whiteFade não atribuído — a vitória acontece sem clarão.", this);
    }

    private void OnEnable() => GameManager.StateChanged += HandleState;

    private void OnDisable() => GameManager.StateChanged -= HandleState;

    private void HandleState(GameState state)
    {
        if (state == GameState.Won)
            StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        if (!string.IsNullOrEmpty(stingerSoundId))
            AudioProvider.PlayAt(stingerSoundId, transform.position);

        for (float t = 0f; t < fadeDuration; t += Time.deltaTime)
        {
            if (whiteFade != null)
                whiteFade.alpha = Mathf.SmoothStep(0f, 1f, t / fadeDuration);

            yield return null;
        }

        // O loop sai com t < fadeDuration, então o alpha pararia um pouco antes de 1 e a tela
        // ficaria quase branca para sempre. O texto da vitória entra por cima disto.
        if (whiteFade != null)
            whiteFade.alpha = 1f;

        // Só agora: o jogador correu o clarão inteiro no controle dele.
        foreach (Behaviour behaviour in disableAfterFade)
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }

        if (flashlight != null)
            flashlight.InputLocked = true;

        CutToWhite?.Invoke();
    }
}
