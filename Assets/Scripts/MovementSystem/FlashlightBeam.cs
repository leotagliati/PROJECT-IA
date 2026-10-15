using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Feixe visível da lanterna: um cone filho da luz do <see cref="Flashlight"/>, desenhado pelo
/// shader PROJECT-IA/FlashlightBeam (raymarch curto dentro do cone, cortado pela profundidade
/// da cena). Segue a luz em tudo — posição com atraso, ângulo, cor, liga/desliga e falhas —
/// então não há estado próprio além do visual.
///
/// Depende da Depth Texture ligada no URP asset (PC_RPAsset liga); sem ela o feixe atravessa
/// as paredes.
/// </summary>
[DefaultExecutionOrder(110)]   // depois do Flashlight (100), que move a luz e escreve a intensidade
public class FlashlightBeam : MonoBehaviour
{
    [SerializeField] private Shader beamShader;

    [Tooltip("Vazio = procura no mesmo objeto.")]
    [SerializeField] private Flashlight flashlight;

    [Tooltip("Brilho do feixe com a lanterna em intensidade cheia. Aditivo: pouco já é muito.")]
    [SerializeField, Min(0f)] private float intensity = 0.08f;

    [Tooltip("Comprimento do cone em metros. Limitado ao alcance da luz.")]
    [SerializeField, Min(0.5f)] private float length = 10f;

    [Tooltip("Fração do ângulo externo da luz. O feixe visível é mais fechado que a área iluminada.")]
    [SerializeField, Range(0.2f, 1f)] private float angleScale = 0.7f;

    [Tooltip("Metros a partir da câmera em que o feixe ainda não aparece. Sem isso a câmera, que " +
             "fica dentro do cone, vê uma névoa cobrindo a tela inteira.")]
    [SerializeField, Min(0.01f)] private float nearFade = 0.6f;

    [Header("Poeira no feixe")]
    [SerializeField, Range(0f, 1f)] private float noiseAmount = 0.5f;
    [SerializeField, Min(0.01f)] private float noiseScale = 1.5f;
    [SerializeField] private Vector3 noiseDrift = new Vector3(0.05f, 0.08f, 0.03f);

    [SerializeField, Range(8, 64)] private int segments = 24;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    private static readonly int LengthId = Shader.PropertyToID("_Length");
    private static readonly int TanAngleId = Shader.PropertyToID("_TanAngle");
    private static readonly int NearFadeId = Shader.PropertyToID("_NearFade");
    private static readonly int NoiseAmountId = Shader.PropertyToID("_NoiseAmount");
    private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
    private static readonly int NoiseDriftId = Shader.PropertyToID("_NoiseDrift");
    private static readonly int FogParamsId = Shader.PropertyToID("_FogParams");

    private GameObject coneObject;
    private MeshRenderer coneRenderer;
    private Mesh mesh;
    private Material material;

    private float builtLength;
    private float builtTan;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponent<Flashlight>();

        if (flashlight == null || beamShader == null)
        {
            Debug.LogWarning($"{nameof(FlashlightBeam)}: sem Flashlight ou sem shader — feixe desligado.", this);
            enabled = false;
        }
    }

    private void OnDestroy()
    {
        if (coneObject != null) Destroy(coneObject);
        if (mesh != null) Destroy(mesh);
        if (material != null) Destroy(material);
    }

    private void OnDisable()
    {
        if (coneRenderer != null)
            coneRenderer.enabled = false;
    }

    private void LateUpdate()
    {
        // A luz só existe depois do Start do Flashlight (pode ser gerada lá).
        Light light = flashlight.SpotLight;
        if (light == null)
            return;

        if (coneObject == null)
            Build(light.transform);

        float brightness = flashlight.Brightness;
        coneRenderer.enabled = brightness > 0.001f;
        if (!coneRenderer.enabled)
            return;

        float coneLength = Mathf.Min(length, light.range);
        float tan = Mathf.Tan(light.spotAngle * 0.5f * angleScale * Mathf.Deg2Rad);

        if (!Mathf.Approximately(coneLength, builtLength) || !Mathf.Approximately(tan, builtTan))
            RebuildMesh(coneLength, tan);

        material.SetColor(ColorId, light.color);
        material.SetFloat(IntensityId, intensity * brightness);
        material.SetFloat(NearFadeId, nearFade);
        material.SetFloat(NoiseAmountId, noiseAmount);
        material.SetFloat(NoiseScaleId, noiseScale);
        material.SetVector(NoiseDriftId, noiseDrift);
        material.SetVector(FogParamsId, CurrentFog());
    }

    private void Build(Transform lightTransform)
    {
        coneObject = new GameObject("Flashlight Volume");
        coneObject.transform.SetParent(lightTransform, false);

        mesh = new Mesh { name = "Flashlight Volume" };
        coneObject.AddComponent<MeshFilter>().sharedMesh = mesh;

        material = new Material(beamShader) { name = "Flashlight Volume" };

        coneRenderer = coneObject.AddComponent<MeshRenderer>();
        coneRenderer.sharedMaterial = material;
        coneRenderer.shadowCastingMode = ShadowCastingMode.Off;
        coneRenderer.receiveShadows = false;
        coneRenderer.lightProbeUsage = LightProbeUsage.Off;
        coneRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    /// <summary>
    /// Cone fechado, ápice na origem e eixo +Z (o forward da luz). Só serve de volume para o
    /// raymarch, então não precisa de normal nem UV — mas precisa ser fechado (tampa no fim),
    /// senão olhando para o fundo do feixe não há face para desenhar.
    /// </summary>
    private void RebuildMesh(float coneLength, float tan)
    {
        builtLength = coneLength;
        builtTan = tan;

        float radius = coneLength * tan;
        var vertices = new Vector3[segments + 2];
        var triangles = new int[segments * 6];

        int apex = segments;
        int capCenter = segments + 1;
        vertices[apex] = Vector3.zero;
        vertices[capCenter] = new Vector3(0f, 0f, coneLength);

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices[i] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, coneLength);
        }

        // Winding horário visto de fora = face da frente no Unity; o shader desenha só a de trás.
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int t = i * 6;

            triangles[t] = apex;
            triangles[t + 1] = next;
            triangles[t + 2] = i;

            triangles[t + 3] = capCenter;
            triangles[t + 4] = i;
            triangles[t + 5] = next;
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
    }

    private static Vector4 CurrentFog()
    {
        if (!RenderSettings.fog)
            return Vector4.zero;

        float mode = RenderSettings.fogMode switch
        {
            FogMode.Linear => 1f,
            FogMode.Exponential => 2f,
            _ => 3f
        };

        return new Vector4(mode, RenderSettings.fogDensity, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance);
    }
}
