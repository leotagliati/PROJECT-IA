using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Vapor pixelado saindo da boca do player, disparado pelas expirações do
/// <see cref="PlayerBreathing"/>. Nem toda expiração solta vapor: a chance sobe com o cansaço,
/// e há um intervalo mínimo, senão vira uma nuvem constante na frente da câmera.
/// </summary>
public class BreathVapor : MonoBehaviour
{
    [Tooltip("Vazio = câmera nos filhos.")]
    [SerializeField] private Transform head;

    // Unlit de propósito, diferente do StepDust: lit, a escuridão do jogo apaga o vapor até
    // na lanterna. O brilho fica por conta da cor — ela é o próprio tom final na tela.
    [Tooltip("Lit: só aparece onde há luz, e no escuro do jogo some. Unlit: nítido sempre; " +
             "controle o quanto ele se destaca pela cor.")]
    [SerializeField] private bool lit = false;

    [Header("Quando")]
    [Tooltip("Chance por expiração respirando calmo (x) e ofegante (y).")]
    [SerializeField] private Vector2 chanceRange = new Vector2(0.5f, 0.65f);

    [SerializeField, Min(0f)] private float minInterval = 2.5f;

    [Header("Onde")]
    [Tooltip("Boca em relação à câmera: frente (z) e abaixo (y negativo).")]
    [SerializeField] private Vector3 mouthOffset = new Vector3(0f, -0.12f, 0.2f);

    [SerializeField] private float exhaleSpeed = 0.5f;

    [Tooltip("Abaixo de x metros da câmera some; acima de y, opaco. Sem isso, andando para frente " +
             "a câmera atravessa o vapor e ele vira um borrão na tela inteira.")]
    [SerializeField] private Vector2 cameraFade = new Vector2(0.1f, 0.35f);

    [Header("Partícula")]
    [Tooltip("Partículas por baforada calmo (x) e ofegante (y).")]
    [SerializeField] private Vector2Int burstRange = new Vector2Int(2, 4);

    [SerializeField] private Color color = new Color(0.62f, 0.65f, 0.68f, 0.55f);
    [SerializeField] private Vector2 sizeRange = new Vector2(0.08f, 0.14f);
    [SerializeField] private Vector2 lifetimeRange = new Vector2(0.8f, 1.3f);
    [SerializeField, Range(4, 32)] private int texturePixels = 8;

    private ParticleSystem system;
    private CharacterController body;
    private Texture2D texture;
    private Material ownedMaterial;
    private float lastPuffTime = float.NegativeInfinity;

    private void Awake()
    {
        if (head == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null)
                head = cam.transform;
        }

        if (head == null)
        {
            Debug.LogWarning($"{nameof(BreathVapor)}: sem câmera nos filhos — vapor desligado.", this);
            enabled = false;
            return;
        }

        body = GetComponentInParent<CharacterController>();
        Build();
    }

    private void OnDestroy()
    {
        if (ownedMaterial != null) Destroy(ownedMaterial);
        if (texture != null) Destroy(texture);
    }

    /// <param name="exertion">0 = respirando calmo, 1 = ofegante.</param>
    public void Exhale(float exertion)
    {
        if (!enabled || system == null)
            return;

        if (Time.time - lastPuffTime < minInterval)
            return;

        if (Random.value > Mathf.Lerp(chanceRange.x, chanceRange.y, exertion))
            return;

        lastPuffTime = Time.time;

        // Herda a velocidade do corpo: em World, sem isso o vapor fica parado no ar e o player
        // anda para dentro dele no mesmo instante.
        Vector3 inherited = body != null ? body.velocity : Vector3.zero;
        inherited.y = 0f;

        // Para frente na horizontal e um pouco para baixo: é a boca, não os olhos, mesmo com a
        // câmera olhando para cima.
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f)
            forward = head.forward;

        var emit = new ParticleSystem.EmitParams
        {
            position = head.TransformPoint(mouthOffset),
            applyShapeToPosition = true
        };

        int count = Mathf.RoundToInt(Mathf.Lerp(burstRange.x, burstRange.y, exertion));

        // Uma a uma, com velocidade levemente espalhada: todas com o mesmo vetor saem empilhadas.
        for (int i = 0; i < count; i++)
        {
            emit.velocity = inherited + (forward + Vector3.down * 0.25f + Random.insideUnitSphere * 0.2f) * exhaleSpeed;
            system.Emit(emit, 1);
        }
    }

    private void Build()
    {
        var holder = new GameObject("Breath Vapor");
        holder.transform.SetParent(transform, false);

        system = holder.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        // Loop com emissão desligada: o sistema fica sempre tocando e só o Emit gera partícula.
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeRange.x, lifetimeRange.y);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeRange.x, sizeRange.y);
        main.startColor = color;
        // Sem rotação: textura em Point girada vira serrilhado tremendo.
        main.startRotation = 0f;
        main.maxParticles = burstRange.y * 4;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = false;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.02f;

        // Arrasto leve: o sopro perde força e o vapor fica pairando enquanto se dissolve.
        ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 100f;
        limit.dampen = 0f;
        limit.drag = 1.5f;

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 2.5f));

        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            // Patamar opaco até a metade da vida: fade longo deixa o vapor borrado e sem forma.
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = gradient;

        texture = PixelPuff.BuildTexture(texturePixels, "Breath Vapor");
        ownedMaterial = PixelPuff.BuildMaterial(texture, lit, "Breath Vapor", cameraFade);

        var renderer = holder.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = ownedMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        system.Play();
    }
}
