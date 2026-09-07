using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(120)]
public class AtmosphericParticles : MonoBehaviour
{
    [SerializeField] private Transform followTarget;

    [SerializeField] private Vector3 volumeSize = new Vector3(34f, 14f, 34f);

    [SerializeField] private float verticalOffset = 0f;

    [SerializeField] private float particlesPerCubicMeter = 0.022f;

    [SerializeField] private int maxParticlesCap = 1200;

    [Header("Appearence")]
    [SerializeField] private Color dustColor = new Color(0.72f, 0.71f, 0.66f, 0.16f);

    [SerializeField] private Vector2 sizeRange = new Vector2(0.015f, 0.06f);

    [SerializeField] private Vector2 lifetimeRange = new Vector2(16f, 30f);

    [SerializeField, Range(0.01f, 0.5f)] private float fadeInFraction = 0.22f;

    [SerializeField, Range(0.01f, 0.5f)] private float fadeOutFraction = 0.3f;

    [Header("Movement")]
    [SerializeField] private float fallSpeed = 0.05f;

    [SerializeField] private float driftSpeed = 0.06f;

    [SerializeField] private float spinDegreesPerSecond = 8f;

    [Header("Noise")]
    [SerializeField] private float noiseStrength = 0.12f;
    [SerializeField] private float noiseFrequency = 0.14f;
    [SerializeField] private float noiseScrollSpeed = 0.03f;

    [Header("Render")]
    [SerializeField] private bool litByFlashlight = true;
    [SerializeField] private Material materialOverride;
    [SerializeField] private bool softParticles = true;
    [SerializeField] private float softFadeDistance = 0.8f;
    [SerializeField] private float cameraNearFade = 0.3f;
    [SerializeField] private float cameraFarFade = 1f;

    private ParticleSystem system;
    private ParticleSystemRenderer systemRenderer;
    private GameObject systemObject;
    private Material material;
    private Texture2D texture;

    // Material vindo do inspector é asset do projeto: não pode ser destruído nem alterado.
    private bool ownsMaterial;

    private void Awake()
    {
        ResolveFollowTarget();
        Build();
    }

    private void Start()
    {
        if (system != null)
            system.Play();
    }

    private void OnDestroy()
    {
        if (systemObject != null)
            Destroy(systemObject);

        if (ownsMaterial && material != null)
            Destroy(material);

        if (texture != null)
            Destroy(texture);
    }

    private void LateUpdate()
    {
        if (systemObject == null || followTarget == null)
            return;

        systemObject.transform.position = followTarget.position + Vector3.up * verticalOffset;
    }

    private void ResolveFollowTarget()
    {
        if (followTarget != null)
            return;

        if (Camera.main != null)
        {
            followTarget = Camera.main.transform;
            return;
        }

        Camera any = FindAnyObjectByType<Camera>();

        if (any != null)
            followTarget = any.transform;
        else
            Debug.LogWarning($"{nameof(AtmosphericParticles)}: nenhuma câmera encontrada, a poeira vai ficar parada na origem.", this);
    }

    private void Build()
    {
        systemObject = new GameObject("Atmospheric Dust");
        systemObject.transform.position = followTarget != null ? followTarget.position : transform.position;

        system = systemObject.AddComponent<ParticleSystem>();
        systemRenderer = systemObject.GetComponent<ParticleSystemRenderer>();

        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (materialOverride != null)
        {
            material = materialOverride;
            ownsMaterial = false;
        }
        else
        {
            texture = BuildDustTexture(64);
            material = BuildMaterial();
            ownsMaterial = true;
        }

        Configure();
    }

    private void Configure()
    {
        int targetCount = ResolveTargetCount();
        float averageLifetime = Mathf.Max(0.1f, (lifetimeRange.x + lifetimeRange.y) * 0.5f);

        var main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.prewarm = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeRange.x, lifetimeRange.y);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeRange.x, sizeRange.y);
        main.startSpeed = 0f; // toda a velocidade vem do velocityOverLifetime
        main.startColor = dustColor;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = 0f;
        main.maxParticles = Mathf.CeilToInt(targetCount * 1.2f);

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = targetCount / averageLifetime;

        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = volumeSize;
        shape.position = Vector3.zero;
        shape.rotation = Vector3.zero;
        shape.randomDirectionAmount = 0f;

        var velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = new ParticleSystem.MinMaxCurve(-driftSpeed, driftSpeed);
        velocity.y = new ParticleSystem.MinMaxCurve(-fallSpeed, -fallSpeed * 0.2f);
        velocity.z = new ParticleSystem.MinMaxCurve(-driftSpeed, driftSpeed);

        var noise = system.noise;
        noise.enabled = noiseStrength > 0f;
        noise.strength = noiseStrength;
        noise.frequency = noiseFrequency;
        noise.scrollSpeed = noiseScrollSpeed;
        noise.octaveCount = 1;
        noise.quality = ParticleSystemNoiseQuality.Medium;
        noise.damping = true;

        var rotation = system.rotationOverLifetime;
        rotation.enabled = spinDegreesPerSecond > 0f;
        // A API de rotação trabalha em radianos, mesmo o inspector mostrando graus.
        float spin = spinDegreesPerSecond * Mathf.Deg2Rad;
        rotation.z = new ParticleSystem.MinMaxCurve(-spin, spin);

        var colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient());

        systemRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        systemRenderer.alignment = ParticleSystemRenderSpace.View;
        systemRenderer.sortMode = ParticleSystemSortMode.Distance;
        systemRenderer.material = material;
        systemRenderer.shadowCastingMode = ShadowCastingMode.Off;
        systemRenderer.receiveShadows = false;
    }

    private int ResolveTargetCount()
    {
        float volume = Mathf.Abs(volumeSize.x * volumeSize.y * volumeSize.z);
        int count = Mathf.RoundToInt(volume * particlesPerCubicMeter);

        return Mathf.Clamp(count, 1, Mathf.Max(1, maxParticlesCap));
    }

    private Gradient BuildFadeGradient()
    {
        float fadeIn = Mathf.Clamp(fadeInFraction, 0.01f, 0.49f);
        float fadeOut = Mathf.Clamp(1f - fadeOutFraction, fadeIn + 0.01f, 0.99f);

        var gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, fadeIn),
                new GradientAlphaKey(1f, fadeOut),
                new GradientAlphaKey(0f, 1f)
            });

        return gradient;
    }

    /// <summary>
    /// Monta o material do zero. Criar material por código pula o ShaderGUI do URP, que
    /// normalmente é quem calcula os parâmetros de blend e de fade — por isso tudo aqui
    /// é setado na mão, incluindo as keywords.
    /// </summary>
    private Material BuildMaterial()
    {
        Shader shader = Shader.Find(litByFlashlight
            ? "Universal Render Pipeline/Particles/Lit"
            : "Universal Render Pipeline/Particles/Unlit");

        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

        if (shader == null)
        {
            Debug.LogError($"{nameof(AtmosphericParticles)}: shaders de partícula do URP não encontrados.", this);
            return null;
        }

        var created = new Material(shader) { name = "Atmospheric Dust" };

        created.SetTexture("_BaseMap", texture);
        created.SetColor("_BaseColor", Color.white);

        // Transparente com blend alpha.
        created.SetFloat("_Surface", 1f);
        created.SetFloat("_Blend", 0f);
        created.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        created.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        created.SetFloat("_ZWrite", 0f);
        created.SetFloat("_Cull", (float)CullMode.Off);
        created.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        created.DisableKeyword("_ALPHATEST_ON");
        created.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        created.renderQueue = (int)RenderQueue.Transparent;

        ApplyFadeParams(created);

        return created;
    }

    /// <summary>
    /// _SoftParticleFadeParams e _CameraFadeParams são HideInInspector: o shader espera
    /// (near, 1/(far - near), 0, 0) já calculado. Passar os valores crus não funciona.
    /// </summary>
    private void ApplyFadeParams(Material target)
    {
        bool useSoft = softParticles && softFadeDistance > 0f;

        if (useSoft)
        {
            target.SetFloat("_SoftParticlesEnabled", 1f);
            target.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            target.SetFloat("_SoftParticlesFarFadeDistance", softFadeDistance);
            target.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / softFadeDistance, 0f, 0f));
            target.EnableKeyword("_SOFTPARTICLES_ON");
        }
        else
        {
            target.SetFloat("_SoftParticlesEnabled", 0f);
            target.SetVector("_SoftParticleFadeParams", Vector4.zero);
            target.DisableKeyword("_SOFTPARTICLES_ON");
        }

        bool useCameraFade = cameraFarFade > cameraNearFade;

        if (useCameraFade)
        {
            target.SetFloat("_CameraFadingEnabled", 1f);
            target.SetFloat("_CameraNearFadeDistance", cameraNearFade);
            target.SetFloat("_CameraFarFadeDistance", cameraFarFade);
            target.SetVector("_CameraFadeParams",
                new Vector4(cameraNearFade, 1f / (cameraFarFade - cameraNearFade), 0f, 0f));
        }
        else
        {
            target.SetFloat("_CameraFadingEnabled", 0f);
            target.SetVector("_CameraFadeParams", new Vector4(0f, Mathf.Infinity, 0f, 0f));
        }

        // Keyword mestra do caminho de fade. Sem ela as duas opções acima não têm efeito.
        if (useSoft || useCameraFade)
            target.EnableKeyword("_FADING_ON");
        else
            target.DisableKeyword("_FADING_ON");
    }

    /// <summary>
    /// Grão redondo com borda macia. Quadrado é o que denuncia partícula barata.
    /// </summary>
    private static Texture2D BuildDustTexture(int size)
    {
        var created = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "DustParticle",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };

        var pixels = new Color32[size * size];
        float center = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center) / center;
                float dy = (y - center) / center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha = 1f - Mathf.SmoothStep(0.1f, 1f, distance);
                alpha *= alpha; // segunda queda: deixa a borda bem mais macia que o centro

                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
            }
        }

        created.SetPixels32(pixels);
        created.Apply(true, false);

        return created;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Reaplica tudo ao mexer no inspector, inclusive em play mode — densidade e cor de
    /// poeira só se acertam olhando para a tela.
    /// </summary>
    private void OnValidate()
    {
        sizeRange.y = Mathf.Max(sizeRange.x, sizeRange.y);
        lifetimeRange.x = Mathf.Max(0.1f, lifetimeRange.x);
        lifetimeRange.y = Mathf.Max(lifetimeRange.x, lifetimeRange.y);
        cameraFarFade = Mathf.Max(cameraNearFade, cameraFarFade);
        particlesPerCubicMeter = Mathf.Max(0f, particlesPerCubicMeter);
        volumeSize = Vector3.Max(volumeSize, Vector3.one);

        if (!Application.isPlaying || system == null || material == null)
            return;

        Configure();

        // Nunca mexer num material do inspector: OnValidate gravaria a mudança no asset.
        if (ownsMaterial)
            ApplyFadeParams(material);
    }
#endif
}
