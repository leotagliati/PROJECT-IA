using UnityEngine;

/// <summary>
/// [Efeito 4 — faíscas] Duas ou três partículas aditivas subindo devagar do item, o "shine"
/// de RE/Silent Hill. Nenhum material do item é tocado, e o sistema morre junto com o
/// objeto quando ele é pego. O ParticleSystem é montado por código, no molde do
/// AtmosphericParticles, para o efeito ser um Add Component e não um prefab a manter.
///
/// Risco conhecido: na pixelização forte, partícula pequena vira dois pixels tremendo —
/// teste o startSize na câmera do jogo, não na Scene view.
/// </summary>
public class ItemSparkles : MonoBehaviour
{
    [Tooltip("Assets/Materials/ItemSparkle.mat (shader ItemFeedback/Sparkle).")]
    [SerializeField] private Material sparkleMaterial;

    [Header("Emissão")]
    [SerializeField] private float particlesPerSecond = 2.5f;
    [SerializeField] private float emitRadius = 0.12f;

    [Tooltip("Sobe o ponto de emissão do centro do item.")]
    [SerializeField] private float heightOffset = 0.05f;

    [Header("Partícula")]
    [SerializeField] private Color color = new Color(1f, 0.95f, 0.8f, 1f);
    [SerializeField] private Vector2 sizeRange = new Vector2(0.03f, 0.06f);
    [SerializeField] private Vector2 lifetimeRange = new Vector2(0.9f, 1.6f);
    [SerializeField] private float riseSpeed = 0.25f;

    [SerializeField] private bool pauseWhileHighlighted = true;

    private ParticleSystem system;
    private HighlightTarget highlight;

    private void Awake()
    {
        highlight = GetComponent<HighlightTarget>();

        if (sparkleMaterial == null)
        {
            Debug.LogWarning($"{nameof(ItemSparkles)}: sem material.", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void Build()
    {
        var holder = new GameObject("Sparkles");
        holder.transform.SetParent(transform, false);
        holder.transform.localPosition = Vector3.up * heightOffset;

        system = holder.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeRange.x, lifetimeRange.y);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeRange.x, sizeRange.y);
        main.startColor = color;
        main.maxParticles = Mathf.CeilToInt(particlesPerSecond * lifetimeRange.y) + 2;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = particlesPerSecond;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = emitRadius;

        // Sobe reto: startSpeed na shape esférica empurraria para fora em todas as direções,
        // então ele fica em zero e a subida vem só do Y aqui.
        main.startSpeed = 0f;

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = 0f;
        velocity.y = riseSpeed;
        velocity.z = 0f;

        // Nasce apagada, acende, apaga: sem isso a partícula "pipoca" e "some" seco.
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.25f),
                new GradientAlphaKey(1f, 0.6f),
                new GradientAlphaKey(0f, 1f)
            });

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = gradient;

        var renderer = holder.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = sparkleMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private void OnEnable()
    {
        if (system != null)
            system.Play();
    }

    private void OnDisable()
    {
        if (system != null)
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void Update()
    {
        if (!pauseWhileHighlighted || highlight == null)
            return;

        bool shouldEmit = !highlight.IsHighlighted;

        // Stop sem Clear: as que já subiram terminam o fade em vez de sumir no mesmo frame.
        if (shouldEmit && !system.isEmitting)
            system.Play();
        else if (!shouldEmit && system.isEmitting)
            system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }
}
