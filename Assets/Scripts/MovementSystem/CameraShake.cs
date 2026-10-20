using System.Collections;
using UnityEngine;
using UnityEngine.Events;

[DefaultExecutionOrder(60)]
public class CameraShake : MonoBehaviour
{
    [SerializeField, Min(0f)] private float duration = 1.2f;
    [SerializeField, Min(0f)] private float magnitude = 0.06f;

    [Tooltip("Quando um Play termina sozinho (não quando é interrompido nem pelo disable). Tremor avulso não dispara.")]
    [SerializeField] private UnityEvent finished;

    private Coroutine running;
    private bool playRunning;
    private Vector3 offset;
    private Vector3 lastWritten;
    private Vector3 lastOffset;
    private bool hasWritten;

    private void OnDisable()
    {
        if (running != null)
        {
            StopCoroutine(running);
            running = null;
        }

        playRunning = false;
        offset = Vector3.zero;
        hasWritten = false;
    }

    /// <summary>
    /// O tremor configurado no Inspector; ao terminar, dispara o <c>finished</c> (na cena, o sinal
    /// <c>tremor_terminou</c> do tutorial). Sem parâmetros para caber num UnityEvent.
    /// </summary>
    public void Play()
    {
        if (running != null)
            StopCoroutine(running);

        playRunning = true;
        running = StartCoroutine(ShakeRoutine(duration, magnitude, notify: true));
    }

    /// <summary>
    /// Tremor avulso: não dispara o <c>finished</c> nem corta um <see cref="Play"/> em andamento —
    /// um rugido no meio da abertura não pode apagar o tremor que o tutorial está esperando acabar.
    /// </summary>
    public void Shake(float shakeDuration, float shakeMagnitude)
    {
        if (running != null && playRunning)
            return;

        if (running != null)
            StopCoroutine(running);

        running = StartCoroutine(ShakeRoutine(shakeDuration, shakeMagnitude, notify: false));
    }

    private IEnumerator ShakeRoutine(float shakeDuration, float shakeMagnitude, bool notify)
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
        playRunning = false;
        if (notify)
            finished.Invoke();
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
