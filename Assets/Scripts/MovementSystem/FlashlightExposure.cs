using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lanterna acesa entrega o jogador ao monstro (FreeTrackSense): apontada para ele, a pista cai na sua posição (e de
/// perto ele já persegue); apontada para outro lado, só o ponto iluminado, de perto. Todo o ajuste fica aqui;
/// desmarcar o componente desliga o efeito.
/// </summary>
public class FlashlightExposure : MonoBehaviour
{
    [Tooltip("Vazio = procura neste objeto e nos filhos.")]
    [SerializeField] private Flashlight flashlight;

    [Header("Quando entrega")]
    [Tooltip("Brilho mínimo (0-1) para entregar: falha e o fade de apagar não contam.")]
    [SerializeField, Range(0f, 1f)] private float minBrightness = 0.3f;

    [Tooltip("Luz apontada PARA o monstro: até onde (m) entrega sua posição. 0 = desliga.")]
    [SerializeField, Min(0f)] private float aimedDistance = 30f;

    [Tooltip("Ângulo (graus) entre o feixe e o monstro para contar como apontada para ele.")]
    [SerializeField, Range(5f, 90f)] private float aimAngle = 40f;

    [Tooltip("Luz na cara dele a menos disto (m): ele já parte para a perseguição. 0 = desliga.")]
    [SerializeField, Min(0f)] private float chaseDistance = 10f;

    [Tooltip("Luz apontada para outro lado: até onde (m) ele vê o ponto iluminado. 0 = desliga.")]
    [SerializeField, Min(0f)] private float spotVisibleDistance = 18f;

    [Tooltip("Segundos que a pista dura na memória do monstro (o som dura 8).")]
    [SerializeField, Min(0.1f)] private float cueSeconds = 8f;

    [Tooltip("Alcance (m) do raio que acha o ponto iluminado.")]
    [SerializeField, Min(1f)] private float beamRange = 25f;

    private static readonly List<FlashlightExposure> active = new List<FlashlightExposure>();

    private int spotFrame = -1;
    private bool hasSpot;
    private Vector3 spot;

    public static IReadOnlyList<FlashlightExposure> Active => active;

    public bool IsLit => flashlight != null && flashlight.SpotLight != null && flashlight.Brightness >= minBrightness;

    public Vector3 Source => flashlight.SpotLight.transform.position;

    public float AimedDistance => aimedDistance;

    public float ChaseDistance => chaseDistance;

    public float SpotVisibleDistance => spotVisibleDistance;

    /// <summary>O feixe aponta para este ponto (dentro de aimAngle)?</summary>
    public bool IsAimedAt(Vector3 point)
    {
        Transform beam = flashlight.SpotLight.transform;
        Vector3 to = point - beam.position;
        return to.sqrMagnitude > 1e-4f && Vector3.Angle(beam.forward, to) <= aimAngle;
    }

    public float CueSeconds => cueSeconds;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponentInChildren<Flashlight>();
    }

    private void OnEnable() => active.Add(this);

    private void OnDisable() => active.Remove(this);

    /// <summary>Onde o feixe bate (um raio por frame, compartilhado entre os monstros).</summary>
    public bool TryGetLitSpot(out Vector3 point)
    {
        if (spotFrame != Time.frameCount)
        {
            spotFrame = Time.frameCount;
            Transform beam = flashlight.SpotLight.transform;
            hasSpot = Physics.Raycast(beam.position, beam.forward, out RaycastHit hit, beamRange, ~0, QueryTriggerInteraction.Ignore);
            spot = hasSpot ? hit.point : Vector3.zero;
        }

        point = spot;
        return hasSpot;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => active.Clear();
}
