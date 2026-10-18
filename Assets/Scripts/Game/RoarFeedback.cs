using UnityEngine;

/// <summary>
/// O que o jogador sente quando o monstro grita de raiva por tê-lo perdido (<see cref="SeekerRoarEvents"/>): um
/// tremor SUTIL da câmera e a névoa engrossando por alguns segundos, voltando ao normal depois. Os dois caem com a
/// distância: até <see cref="fullEffectDistance"/> é o efeito inteiro, em <see cref="maxDistance"/> não sente nada
/// (o som do grito tem o alcance dele, na AudioLibrary). Distância em linha reta: grito atravessa parede.
///
/// A névoa é a do Lighting da cena (RenderSettings): a densidade (exp/exp²) ou o fim (linear) é escalado e volta ao
/// valor de antes. O FlashlightBeam lê esses valores todo frame, então o feixe se apaga junto. Cena sem névoa: só o
/// tremor. No PlayerDummy; tirar o componente desliga o efeito (o grito do monstro continua).
/// </summary>
public class RoarFeedback : MonoBehaviour
{
    [Tooltip("Vazio = procura nos filhos (o da Main Camera do jogador).")]
    [SerializeField] private CameraShake cameraShake;

    [Header("Distância (m)")]
    [SerializeField, Min(0f)] private float fullEffectDistance = 12f;

    [SerializeField, Min(0.1f)] private float maxDistance = 45f;

    [Header("Tremor")]
    [Tooltip("Sutil de propósito: metade do tremor da abertura (0.06). Em unidades locais da câmera.")]
    [SerializeField, Min(0f)] private float shakeMagnitude = 0.03f;

    [SerializeField, Min(0f)] private float shakeDuration = 1.2f;

    [Header("Névoa")]
    [Tooltip("Quanto a névoa engrossa no pico, com o efeito inteiro (2 = o dobro da densidade).")]
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
