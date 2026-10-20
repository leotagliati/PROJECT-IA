using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Reflexo planar para vidro/espelho: uma câmera espelhada no plano do vidro renderiza numa RenderTexture que o
/// material <c>PROJECT-IA/GlassReflection</c> soma por cima. Reflete o que se mexe (seeker, lanterna), ao contrário
/// de um Reflection Probe baked. Vai no objeto da malha do vidro.
///
/// Order 130: depois de toda a cadeia de câmera (SpineLook → ... → AtmosphericParticles, 120). Copiar a pose antes
/// dela daria o reflexo com um frame de atraso.
/// </summary>
[DefaultExecutionOrder(130)]
[RequireComponent(typeof(Renderer))]
public class PlanarReflection : MonoBehaviour
{
    private static readonly int ReflectionTexId = Shader.PropertyToID("_ReflectionTex");

    private static readonly System.Collections.Generic.HashSet<Camera> reflectionCameras = new();

    /// <summary>Se a câmera é de algum reflexo. Para quem muda o que aparece só no espelho (ShowInReflections).</summary>
    public static bool IsReflectionCamera(Camera camera) => reflectionCameras.Contains(camera);

    // Estático sobrevive ao Stop com o domain reload desligado (ver PlayerInputProvider.ResetStatics).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => reflectionCameras.Clear();

    [Tooltip("Câmera refletida. Vazio = Camera.main (a câmera do PlayerDummy).")]
    [SerializeField] private Camera sourceCamera;

    [Tooltip("Fração da RenderTexture da câmera do player (a PixelatedCameraTexture). 0.5 = metade da resolução dela.")]
    [SerializeField, Range(0.125f, 1f)] private float resolutionScale = 0.5f;

    [Tooltip("Só se a câmera desenhar direto na tela (sem Output Texture).")]
    [SerializeField] private Vector2Int fallbackResolution = new(256, 128);

    [SerializeField] private LayerMask reflectionMask = ~0;

    [Tooltip("Normal do vidro no espaço do objeto. (0,0,0) = automático: o eixo mais fino da malha no mundo. Errar o " +
             "eixo espelha num plano perpendicular ao vidro (o player aparece de lado, num canto).")]
    [SerializeField] private Vector3 localNormal = Vector3.zero;

    [Tooltip("Além desta distância a câmera do reflexo desliga.")]
    [SerializeField] private float maxDistance = 12f;

    [Tooltip("Afasta o plano de corte do vidro: sem isso a própria porta/moldura aparece no reflexo.")]
    [SerializeField] private float clipOffset = 0.02f;

    [Tooltip("Sombras na câmera do reflexo. Desligado custa menos; ligado o reflexo da lanterna fica igual ao real.")]
    [SerializeField] private bool renderShadows = false;

    private Renderer glass;
    private Camera reflectionCamera;
    private RenderTexture texture;
    private MaterialPropertyBlock block;
    private readonly Plane[] frustum = new Plane[6];

    private void Awake()
    {
        glass = GetComponent<Renderer>();
        block = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        glass.forceRenderingOff = false;

        if (reflectionCamera != null)
            reflectionCamera.enabled = false;
    }

    // O vidro nunca entra no próprio reflexo: ele amostra a RT que está sendo desenhada (a do frame anterior) e
    // viraria um reflexo do reflexo. O begin roda antes do culling da câmera, então desligar aqui basta.
    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera == reflectionCamera)
            glass.forceRenderingOff = true;
    }

    private void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera == reflectionCamera)
            glass.forceRenderingOff = false;
    }

    private void OnDestroy()
    {
        if (reflectionCamera != null)
        {
            reflectionCameras.Remove(reflectionCamera);
            Destroy(reflectionCamera.gameObject);
        }

        if (texture != null)
            texture.Release();
    }

    private void LateUpdate()
    {
        Camera source = sourceCamera != null ? sourceCamera : Camera.main;
        if (source == null)
            return;

        EnsureResources(source);

        Vector3 localAxis = (localNormal == Vector3.zero ? ThinnestAxis() : localNormal).normalized;
        Vector3 normal = transform.TransformDirection(localAxis).normalized;

        // Vale dos dois lados do vidro: a normal vira para o lado de onde a câmera olha.
        float side = Vector3.Dot(source.transform.position - transform.position, normal);
        if (side < 0f)
        {
            normal = -normal;
            side = -side;
        }

        // O espelho é a face voltada para a câmera, não o centro: com o plano no meio, a face da frente do vidro
        // ficava do lado visível e aparecia no próprio reflexo (um segundo reflexo, que seguia a câmera).
        Vector3 point = transform.position + normal * HalfThickness(localAxis);

        GeometryUtility.CalculateFrustumPlanes(source, frustum);
        bool active = side > 0.01f
                      && Vector3.Distance(source.transform.position, point) < maxDistance
                      && GeometryUtility.TestPlanesAABB(frustum, glass.bounds);

        reflectionCamera.enabled = active;
        if (!active)
            return;

        Matrix4x4 reflection = ReflectionMatrix(normal, -Vector3.Dot(normal, point));

        reflectionCamera.fieldOfView = source.fieldOfView;
        reflectionCamera.aspect = source.aspect;
        reflectionCamera.nearClipPlane = source.nearClipPlane;
        reflectionCamera.farClipPlane = source.farClipPlane;
        reflectionCamera.cullingMask = reflectionMask;

        // Transform só para culling/LOD/luzes; quem vale para a imagem é o worldToCameraMatrix.
        reflectionCamera.transform.SetPositionAndRotation(
            reflection.MultiplyPoint(source.transform.position),
            Quaternion.LookRotation(Vector3.Reflect(source.transform.forward, normal),
                                    Vector3.Reflect(source.transform.up, normal)));
        reflectionCamera.worldToCameraMatrix = source.worldToCameraMatrix * reflection;

        // Projeção oblíqua: o plano próximo vira o próprio vidro, e nada atrás dele entra no reflexo.
        Vector4 clipPlane = CameraSpacePlane(reflectionCamera.worldToCameraMatrix, point, normal);

        // A reflexão inverte o sentido dos triângulos e o culling descartaria as faces da frente (a parede some e
        // aparece o fora do mapa). GL.invertCulling não pega na câmera renderizada pelo fluxo normal do URP, então
        // a projeção é espelhada em X para desfazer a inversão; o shader desespelha a amostragem.
        reflectionCamera.projectionMatrix = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)) * source.CalculateObliqueMatrix(clipPlane);

        glass.GetPropertyBlock(block);
        block.SetTexture(ReflectionTexId, texture);
        glass.SetPropertyBlock(block);
    }

    private void EnsureResources(Camera source)
    {
        // Segue a RT da câmera do player: mesma proporção (senão o reflexo estica, porque o shader amostra pela
        // posição na tela dela) e pixel proporcional ao que o RetroScreen vai mostrar.
        RenderTexture target = source.targetTexture;
        Vector2Int resolution = target != null
            ? new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(target.width * resolutionScale)),
                             Mathf.Max(1, Mathf.RoundToInt(target.height * resolutionScale)))
            : fallbackResolution;

        if (texture == null || texture.width != resolution.x || texture.height != resolution.y)
        {
            if (texture != null)
                texture.Release();

            texture = new RenderTexture(resolution.x, resolution.y, 24) { name = $"{name} Reflection", filterMode = FilterMode.Point };
            if (reflectionCamera != null)
                reflectionCamera.targetTexture = texture;
        }

        if (reflectionCamera != null)
            return;

        var go = new GameObject($"{name} Reflection Camera") { hideFlags = HideFlags.DontSave };
        reflectionCamera = go.AddComponent<Camera>();
        reflectionCameras.Add(reflectionCamera);
        reflectionCamera.enabled = false;
        reflectionCamera.clearFlags = source.clearFlags;
        reflectionCamera.backgroundColor = source.backgroundColor;
        reflectionCamera.targetTexture = texture;
        // Antes da câmera do player: o reflexo já está pronto quando ela desenha o vidro.
        reflectionCamera.depth = source.depth - 1f;

        var data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;
        data.renderShadows = renderShadows;
        // O FlashlightBeam marcha o raio até a profundidade da cena: sem a Depth Texture o feixe some do reflexo.
        data.requiresDepthTexture = true;
    }

    // No mundo e não na escala local: malha importada do Blender vem sob pais rodados e escalados (a porta tem -90°
    // em X e escala 100), e o eixo fino do Cube do vidro acaba sendo o Y.
    private Vector3 ThinnestAxis()
    {
        Vector3 size = TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null
            ? filter.sharedMesh.bounds.size
            : Vector3.one;

        float x = size.x * transform.TransformVector(Vector3.right).magnitude;
        float y = size.y * transform.TransformVector(Vector3.up).magnitude;
        float z = size.z * transform.TransformVector(Vector3.forward).magnitude;

        if (x <= y && x <= z)
            return Vector3.right;
        return y <= z ? Vector3.up : Vector3.forward;
    }

    private float HalfThickness(Vector3 localAxis)
    {
        if (!TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
            return 0f;

        Vector3 size = filter.sharedMesh.bounds.size;
        float local = 0.5f * (Mathf.Abs(localAxis.x) * size.x + Mathf.Abs(localAxis.y) * size.y + Mathf.Abs(localAxis.z) * size.z);
        return local * transform.TransformVector(localAxis).magnitude;
    }

    private Vector4 CameraSpacePlane(Matrix4x4 worldToCamera, Vector3 point, Vector3 normal)
    {
        Vector3 position = worldToCamera.MultiplyPoint(point + normal * clipOffset);
        Vector3 direction = worldToCamera.MultiplyVector(normal).normalized;
        return new Vector4(direction.x, direction.y, direction.z, -Vector3.Dot(position, direction));
    }

    private static Matrix4x4 ReflectionMatrix(Vector3 n, float d)
    {
        var m = Matrix4x4.identity;
        m.m00 = 1f - 2f * n.x * n.x; m.m01 = -2f * n.x * n.y;     m.m02 = -2f * n.x * n.z;     m.m03 = -2f * d * n.x;
        m.m10 = -2f * n.y * n.x;     m.m11 = 1f - 2f * n.y * n.y; m.m12 = -2f * n.y * n.z;     m.m13 = -2f * d * n.y;
        m.m20 = -2f * n.z * n.x;     m.m21 = -2f * n.z * n.y;     m.m22 = 1f - 2f * n.z * n.z; m.m23 = -2f * d * n.z;
        return m;
    }
}
