using Unity.MLAgents;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Baforada de poeira pixelada a cada passo. Não sabe de quem é o pé: quem chama
/// (<c>SeekerFootsteps</c>, <see cref="PlayerMovement"/>) passa o ponto no chão e a intensidade.
///
/// Montado por código (sistema, textura e material), no molde do ItemSparkles e do
/// AtmosphericDust: quem usa adiciona com os defaults se não estiver no prefab. Coloque no
/// prefab só para ajustar valores.
/// </summary>
public class StepDust : MonoBehaviour
{
    [Tooltip("Vazio = material montado por código (URP Particles/Lit com textura pixelada).")]
    [SerializeField] private Material materialOverride;

    [Tooltip("Lit: só aparece onde há luz (lanterna). Unlit brilharia no escuro.")]
    [SerializeField] private bool lit = true;

    [Header("Baforada")]
    [Tooltip("Partículas por passo com intensidade 0 (x) e 1 (y).")]
    [SerializeField] private Vector2Int burstRange = new Vector2Int(3, 7);

    [SerializeField] private Color color = new Color(0.55f, 0.5f, 0.45f, 0.55f);
    [SerializeField] private Vector2 sizeRange = new Vector2(0.15f, 0.3f);
    [SerializeField] private Vector2 lifetimeRange = new Vector2(0.5f, 0.9f);
    [SerializeField] private float spreadSpeed = 0.6f;
    [SerializeField] private float riseSpeed = 0.15f;

    [Tooltip("Pixels de lado da textura. Pouco = blocos grandes; filtrada por Point de propósito.")]
    [SerializeField, Range(4, 32)] private int texturePixels = 8;

    private ParticleSystem system;
    private Texture2D texture;
    private Material ownedMaterial;

    private void Awake()
    {
        // Mesmo gate do SeekerAudioSystem: no treino ninguém vê, e são 9 arenas em timescale alto.
        if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
        {
            enabled = false;
            return;
        }

        Build();
    }

    private void OnDestroy()
    {
        if (ownedMaterial != null) Destroy(ownedMaterial);
        if (texture != null) Destroy(texture);
    }

    /// <param name="position">Ponto no chão, não o tornozelo: a baforada nasce ali.</param>
    /// <param name="intensity">0–1, entre os dois extremos de <see cref="burstRange"/>.</param>
    public void Puff(Vector3 position, float intensity)
    {
        if (!enabled || system == null)
            return;

        int count = Mathf.RoundToInt(Mathf.Lerp(burstRange.x, burstRange.y, intensity));
        if (count <= 0)
            return;

        // Emit com posição em vez de mover o sistema: simulação em World, então as baforadas
        // anteriores ficam onde nasceram.
        var emit = new ParticleSystem.EmitParams { position = position, applyShapeToPosition = true };
        system.Emit(emit, count);
    }

    private void Build()
    {
        var holder = new GameObject("Step Dust");
        holder.transform.SetParent(transform, false);

        system = holder.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        // Loop com emissão desligada: o sistema fica sempre tocando e só o Emit gera partícula.
        // Sem loop, passada a duration ele para e as baforadas seguintes não simulam.
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeRange.x, lifetimeRange.y);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeRange.x, sizeRange.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(spreadSpeed * 0.3f, spreadSpeed);
        main.startColor = color;
        // Sem rotação: textura em Point girada vira serrilhado tremendo.
        main.startRotation = 0f;
        main.maxParticles = burstRange.y * 8;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = false;

        // Círculo deitado no chão: espalha rente ao piso, e a subida vem só do Y abaixo.
        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.1f;
        shape.rotation = new Vector3(90f, 0f, 0f);

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = 0f;
        velocity.y = riseSpeed;
        velocity.z = 0f;

        // Freia o espalhamento: poeira assenta, não voa.
        ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 100f;
        limit.dampen = 0f;
        limit.drag = 3f;

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.4f));

        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = gradient;

        var renderer = holder.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        if (materialOverride != null)
        {
            renderer.sharedMaterial = materialOverride;
        }
        else
        {
            texture = PixelPuff.BuildTexture(texturePixels, "Step Dust");
            ownedMaterial = PixelPuff.BuildMaterial(texture, lit, "Step Dust");
            renderer.sharedMaterial = ownedMaterial;
        }

        system.Play();
    }
}
