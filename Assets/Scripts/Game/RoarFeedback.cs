using UnityEngine;

/// <summary>
/// Tremor sutil da câmera e névoa engrossando quando o monstro grita (<see cref="SeekerRoarEvents"/>), com queda pela distância em linha reta.
/// A névoa é a do RenderSettings (o FlashlightBeam a lê todo frame).
/// </summary>
public class RoarFeedback : MonoBehaviour
{
    [Tooltip("Vazio = procura nos filhos (o da Main Camera do jogador).")]
    [SerializeField] private CameraShake cameraShake;

    [Header("Distância (m)")]
    [SerializeField, Min(0f)] private float fullEffectDistance = 12f;

    [SerializeField, Min(0.1f)] private float maxDistance = 45f;

    [Header("Tremor")]
    [Tooltip("Sutil de propósito: metade do tremor da abertura (0.06).")]
    [SerializeField, Min(0f)] private float shakeMagnitude = 0.03f;

    [SerializeField, Min(0f)] private float shakeDuration = 1.2f;

    [Header("Névoa")]
    [Tooltip("Multiplicador da névoa no pico (2 = o dobro da densidade).")]
    [SerializeField, Min(1f)] private float fogMultiplier = 2.2f;

    [SerializeField, Min(0.01f)] private float fogRiseSeconds = 0.5f;

    [SerializeField, Min(0f)] private float fogHoldSeconds = 1f;

    [SerializeField, Min(0.01f)] private float fogFallSeconds = 2.5f;

    private bool fogActive;
    private float fogTime;
    private float fogStrength;
    private float baseDensity;
    private float baseEnd;

    private void Awake()
    {
        if (cameraShake == null)
            cameraShake = GetComponentInChildren<CameraShake>();
    }

    private void OnEnable() => SeekerRoarEvents.Roared += HandleRoar;

    private void OnDisable()
    {
        SeekerRoarEvents.Roared -= HandleRoar;
        RestoreFog();
    }

    private void HandleRoar(Vector3 position)
    {
        Transform listener = cameraShake != null ? cameraShake.transform : transform;
        float distance = Vector3.Distance(listener.position, position);
        float strength = 1f - Mathf.InverseLerp(fullEffectDistance, maxDistance, distance);
        if (strength <= 0f)
            return;

        if (cameraShake != null && shakeMagnitude > 0f)
            cameraShake.Impulse(shakeDuration, shakeMagnitude * strength);

        if (RenderSettings.fog && fogMultiplier > 1f)
        {
            // Grito em cima de grito: a base continua a de antes do primeiro (não a névoa já engrossada).
            if (!fogActive)
            {
                baseDensity = RenderSettings.fogDensity;
                baseEnd = RenderSettings.fogEndDistance;
            }

            fogActive = true;
            fogTime = 0f;
            fogStrength = Mathf.Max(fogStrength, strength);
        }
    }

    private void Update()
    {
        if (!fogActive)
            return;

        fogTime += Time.deltaTime;
        float total = fogRiseSeconds + fogHoldSeconds + fogFallSeconds;
        if (fogTime >= total)
        {
            RestoreFog();
            return;
        }

        float envelope = fogTime < fogRiseSeconds
            ? Mathf.SmoothStep(0f, 1f, fogTime / fogRiseSeconds)
            : Mathf.SmoothStep(0f, 1f, (total - fogTime) / fogFallSeconds);
        float factor = 1f + (fogMultiplier - 1f) * fogStrength * Mathf.Clamp01(envelope);

        if (RenderSettings.fogMode == FogMode.Linear)
            RenderSettings.fogEndDistance = Mathf.Lerp(RenderSettings.fogStartDistance, baseEnd, 1f / factor);
        else
            RenderSettings.fogDensity = baseDensity * factor;
    }

    private void RestoreFog()
    {
        if (!fogActive)
            return;

        RenderSettings.fogDensity = baseDensity;
        RenderSettings.fogEndDistance = baseEnd;
        fogActive = false;
        fogStrength = 0f;
    }
}
