using System.Collections;
using UnityEngine;

/// <summary>
/// Tremor de câmera do início da partida: ao entrar em <see cref="GameState.Playing"/> treme
/// a câmera e toca o grito. Fica no Main Camera do jogador, porque escreve a transform dela e
/// o som a acompanha.
///
/// Ordem 60 de propósito: o CameraJuice (0) REATRIBUI a localPosition inteira todo LateUpdate
/// e o ShoulderPeek (50) soma o lean, então qualquer escrita feita antes (como a da coroutine,
/// que roda no Update) é apagada. Aqui o tremor é só um offset somado por cima, depois deles.
/// </summary>
[DefaultExecutionOrder(60)]
public class CameraShake : MonoBehaviour
{
    [SerializeField, Min(0f)] private float duration = 1.2f;
    [SerializeField, Min(0f)] private float magnitude = 0.06f;

    [Tooltip("Entrada da AudioLibrary tocada junto com o tremor. Vazio = sem som.")]
    [SerializeField] private string soundId = "intro_scream";

    private Coroutine running;
    private AudioHandle soundHandle;

    private Vector3 offset;

    // Guarda o que escrevemos no último frame: se ninguém reescreveu a base (CameraJuice
    // desligado), a posição ainda é a nossa e o offset antigo precisa sair antes de somar o
    // novo — senão o tremor acumula e a câmera deriva.
    private Vector3 lastWritten;
    private Vector3 lastOffset;
    private bool hasWritten;

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable()
    {
        GameManager.StateChanged -= HandleGameState;

        if (running != null)
        {
            StopCoroutine(running);
            running = null;
        }

        offset = Vector3.zero;
        hasWritten = false;
    }

    // O AudioPool sobrevive ao reload da cena (DontDestroyOnLoad): sem isto o fim do grito
    // vazaria para a partida nova. Handle de slot já roubado é no-op no Stop.
    private void OnDestroy() => AudioProvider.Stop(soundHandle);

    private void HandleGameState(GameState state)
    {
        if (state != GameState.Playing)
            return;

        Shake(duration, magnitude);

        if (!string.IsNullOrEmpty(soundId))
            soundHandle = AudioProvider.PlayFollowing(soundId, transform);
    }

    public void Shake(float shakeDuration, float shakeMagnitude)
    {
        if (running != null)
            StopCoroutine(running);

        running = StartCoroutine(ShakeRoutine(shakeDuration, shakeMagnitude));
    }

    private IEnumerator ShakeRoutine(float shakeDuration, float shakeMagnitude)
    {
        for (float t = 0f; t < shakeDuration; t += Time.deltaTime)
        {
            // Morre suave: o corte seco no fim parece um defeito, não um tremor.
            float falloff = 1f - t / shakeDuration;
            offset = Random.insideUnitSphere * (shakeMagnitude * falloff);
            yield return null;
        }

        offset = Vector3.zero;
        running = null;
    }

    private void LateUpdate()
    {
        Vector3 position = transform.localPosition;

        if (hasWritten && position == lastWritten)
            position -= lastOffset;

        if (offset == Vector3.zero)
        {
            transform.localPosition = position;
            hasWritten = false;
            return;
        }

        transform.localPosition = position + offset;
        lastWritten = transform.localPosition;
        lastOffset = offset;
        hasWritten = true;
    }
}
