using UnityEngine;

/// <summary>
/// Mostra o arco do arremesso enquanto o jogador mira: linha que cresce com a carga e anel no ponto de batida,
/// simulados com a física do item. Ordem 130: depois de tudo que move a câmera.
/// </summary>
[DefaultExecutionOrder(130)]
public class ThrowTrajectory : MonoBehaviour
{
    [Tooltip("Vazio = procura neste objeto e nos filhos.")]
    [SerializeField] private PlayerThrow thrower;

    [Header("Simulação")]
    [Tooltip("Segundos de voo simulados; sem batida antes disso, sem anel.")]
    [SerializeField, Min(0.2f)] private float maxFlightTime = 3f;

    [Tooltip("Passo da simulação (s); menor = curva mais lisa e mais raycasts.")]
    [SerializeField, Range(0.01f, 0.1f)] private float step = 0.03f;

    [Header("Linha")]
    [SerializeField] private Color color = Color.white;

    [Tooltip("Largura (m) na mão; no fim é mais grossa para não sumir na RenderTexture.")]
    [SerializeField, Min(0.001f)] private float startWidth = 0.03f;

    [SerializeField, Min(0.001f)] private float endWidth = 0.12f;

    [Tooltip("Fração da linha, a partir da mão, que aparece aos poucos (colada na câmera tapa a vista).")]
    [SerializeField, Range(0f, 0.5f)] private float fadeInFraction = 0.15f;

    [SerializeField, Range(0f, 1f)] private float alpha = 0.85f;

    [Header("Anel no ponto de batida")]
    [SerializeField, Min(0.05f)] private float markerRadius = 0.35f;

    [SerializeField, Min(0.005f)] private float markerWidth = 0.06f;

    [SerializeField, Range(8, 64)] private int markerSegments = 24;

    [Tooltip("Afasta o anel da superfície (m) para não brigar no depth.")]
    [SerializeField, Min(0f)] private float markerLift = 0.03f;

    [Tooltip("Vazio = Sprites/Default (já incluído no build).")]
    [SerializeField] private Material material;

    private LineRenderer arc;
    private LineRenderer marker;
    private Vector3[] points;
    private Vector3[] ring;
    private readonly RaycastHit[] hits = new RaycastHit[16];

    private void Awake()
    {
        if (thrower == null)
            thrower = GetComponentInChildren<PlayerThrow>();

        if (material == null)
            material = new Material(Shader.Find("Sprites/Default")) { name = "ThrowTrajectory (runtime)" };

        arc = CreateLine("Throw Trajectory", false);
        arc.widthCurve = AnimationCurve.Linear(0f, startWidth, 1f, endWidth);
        arc.colorGradient = Fade(color, alpha, fadeInFraction);

        marker = CreateLine("Throw Impact Marker", true);
        marker.widthMultiplier = markerWidth;
        marker.startColor = marker.endColor = new Color(color.r, color.g, color.b, alpha);

        points = new Vector3[Mathf.CeilToInt(maxFlightTime / step) + 1];
        ring = new Vector3[markerSegments];
        Hide();
    }

    private void OnDisable()
    {
        Hide();
    }

    private void LateUpdate()
    {
        if (thrower == null || !thrower.IsAiming || !thrower.TryGetLaunch(out Vector3 origin, out Vector3 velocity))
        {
            Hide();
            return;
        }

        ThrowableItem item = thrower.HeldItem;
        int count = Simulate(origin, velocity, item.Radius, item.LinearDamping, out bool landed, out RaycastHit hit);

        arc.positionCount = count;
        arc.SetPositions(points);
        arc.enabled = count > 1;

        if (landed)
            PlaceMarker(hit.point, hit.normal);
        else
            marker.enabled = false;
    }

    // Mesmo integrador do PhysX (velocidade, depois posição; arrasto 1/(1 + dt·d)); ignora os colisores do jogador.
    private int Simulate(Vector3 origin, Vector3 velocity, float radius, float damping, out bool landed, out RaycastHit landing)
    {
        landed = false;
        landing = default;
        Vector3 gravity = Physics.gravity;
        Collider[] own = thrower.OwnColliders;

        Vector3 position = origin;
        points[0] = position;
        int count = 1;
        while (count < points.Length)
        {
            velocity += gravity * step;
            velocity *= 1f / (1f + step * damping);
            Vector3 next = position + velocity * step;
            Vector3 delta = next - position;
            float distance = delta.magnitude;

            if (distance > 1e-5f && TryCast(position, radius, delta / distance, distance, own, out RaycastHit hit))
            {
                points[count++] = position + delta / distance * hit.distance;
                landed = true;
                landing = hit;
                break;
            }

            position = next;
            points[count++] = position;
        }

        return count;
    }

    private bool TryCast(Vector3 from, float radius, Vector3 direction, float distance, Collider[] own, out RaycastHit closest)
    {
        closest = default;
        int n = Physics.SphereCastNonAlloc(from, radius, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            RaycastHit hit = hits[i];
            // distance 0 = já começava encostado (a mão rente a um móvel): não é para onde o voo vai.
            if (hit.distance <= 0f || hit.distance >= best || IsOwn(hit.collider, own))
                continue;

            best = hit.distance;
            closest = hit;
        }

        return best < float.MaxValue;
    }

    private static bool IsOwn(Collider collider, Collider[] own)
    {
        if (own == null)
            return false;

        foreach (Collider c in own)
            if (c == collider)
                return true;
        return false;
    }

    private void PlaceMarker(Vector3 point, Vector3 normal)
    {
        Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        Vector3 center = point + normal * markerLift;
        for (int i = 0; i < ring.Length; i++)
        {
            float angle = i * Mathf.PI * 2f / ring.Length;
            ring[i] = center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * markerRadius;
        }

        marker.positionCount = ring.Length;
        marker.SetPositions(ring);
        marker.enabled = true;
    }

    private void Hide()
    {
        if (arc != null)
            arc.enabled = false;
        if (marker != null)
            marker.enabled = false;
    }

    private LineRenderer CreateLine(string objectName, bool loop)
    {
        var go = new GameObject(objectName);
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = loop;
        line.sharedMaterial = material;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        line.numCapVertices = 2;
        return line;
    }

    private static Gradient Fade(Color color, float alpha, float fadeIn)
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, Mathf.Max(0.001f, fadeIn)), new GradientAlphaKey(alpha, 1f) });
        return gradient;
    }
}
