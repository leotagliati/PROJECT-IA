using UnityEngine;

/// <summary>
/// Objeto que o jogador pega e arremessa (<see cref="PlayerThrow"/>). Só faz barulho depois de arremessado:
/// a batida toca o som e avisa a IA por <see cref="NoiseEvents"/>.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ThrowableItem : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Pegar";

    [SerializeField] private string handsFullMessage = "Mãos ocupadas";

    [Header("Na mão")]
    [Tooltip("Pose relativa ao socket da mão, em metros (a escala do esqueleto é compensada).")]
    [SerializeField] private Vector3 heldLocalPosition;

    [SerializeField] private Vector3 heldLocalEuler;

    [Header("Batida")]
    [Tooltip("Abaixo disto (m/s) é rolar ou assentar: nem som.")]
    [SerializeField] private float minSoundSpeed = 0.8f;

    [Tooltip("A partir disto (m/s) a batida também chama a IA. Entre minSoundSpeed e isto, só o som baixinho.")]
    [SerializeField] private float minImpactSpeed = 2.5f;

    [Tooltip("Velocidade em que a batida sai com volume e alcance cheios.")]
    [SerializeField] private float loudImpactSpeed = 9f;

    [Tooltip("Alcance da IA, em metros pelo grafo, na batida mais forte. A mais fraca ouve a metade.")]
    [SerializeField] private float hearingMeters = 25f;

    [Tooltip("Quiques seguidos dentro disto contam como uma batida só.")]
    [SerializeField] private float impactCooldown = 0.35f;

    [Header("Smash")]
    [Tooltip("Batida a partir disto (m/s), em qualquer superfície, vira smash.")]
    [SerializeField] private float smashSpeed = 11f;

    [Tooltip("Queda a partir disto (m, do ponto mais alto desde o último quique até o ponto da batida) vira smash.")]
    [SerializeField] private float smashFallHeight = 3f;

    [Header("Áudio")]
    [SerializeField] private string pickupSoundId = "throwable_pickup";

    [SerializeField] private string impactSoundId = "throwable_impact";

    [Tooltip("Vazio = este item não quebra, a batida forte usa o som de impacto.")]
    [SerializeField] private string smashSoundId = "throwable_smash";

    private Rigidbody body;
    private Collider[] colliders;
    private Collider[] ignoredColliders;
    private bool airborne;
    private float nextImpactTime;
    private float peakHeight;
    private Vector3 worldScale;

    public bool IsHeld { get; private set; }

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        colliders = GetComponentsInChildren<Collider>();
        worldScale = transform.lossyScale;
    }

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt result)
    {
        result = InteractionPrompt.Instant(prompt);

        if (IsHeld)
            return false;

        if (interactor.Thrower == null || interactor.Thrower.HasItem)
            result = result.Blocked(handsFullMessage);

        return true;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        if (interactor.Thrower == null || !interactor.Thrower.TryHold(this))
            return InteractionResult.Fail(handsFullMessage);

        AudioProvider.PlayAt(pickupSoundId, transform.position);
        return InteractionResult.Success;
    }

    public void AttachTo(Transform socket)
    {
        IsHeld = true;
        airborne = false;

        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.None;
        RestoreIgnoredCollisions();
        SetCollidersEnabled(false);

        // O socket herda a escala do esqueleto (Armature do Blender chega com x100). Escala local 1 lá
        // dentro deixaria o item gigante, e o Launch levaria esse tamanho para o mundo.
        transform.SetParent(socket, false);
        transform.localPosition = DivideByScale(heldLocalPosition, socket.lossyScale);
        transform.localRotation = Quaternion.Euler(heldLocalEuler);
        transform.localScale = DivideByScale(worldScale, socket.lossyScale);
    }

    /// <param name="ignore">Colisores do arremessador: o item nasce dentro da cápsula dele.</param>
    public void Launch(Vector3 position, Vector3 velocity, Collider[] ignore)
    {
        IsHeld = false;

        transform.SetParent(null, true);
        transform.position = position;
        transform.localScale = worldScale;

        SetCollidersEnabled(true);
        IgnoreCollisions(ignore);

        body.isKinematic = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.linearVelocity = velocity;
        body.angularVelocity = Random.onUnitSphere * 6f;

        airborne = true;
        nextImpactTime = 0f;
        peakHeight = position.y;
    }

    private void FixedUpdate()
    {
        if (!airborne)
            return;

        if (body.IsSleeping())
            airborne = false;
        else
            peakHeight = Mathf.Max(peakHeight, body.position.y);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!airborne)
            return;

        Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;

        // A altura recomeça a cada toque, inclusive os do cooldown: senão o quique seguinte herdaria a
        // queda inteira e viraria smash.
        float fall = peakHeight - point.y;
        peakHeight = body.position.y;

        if (Time.time < nextImpactTime)
            return;

        float speed = collision.relativeVelocity.magnitude;
        if (speed < minSoundSpeed)
            return;

        nextImpactTime = Time.time + impactCooldown;

        bool smash = !string.IsNullOrEmpty(smashSoundId)
            && (speed >= smashSpeed || fall >= smashFallHeight);

        if (smash)
        {
            AudioProvider.PlayAt(smashSoundId, point);
            NoiseEvents.Emit(point, hearingMeters);
            return;
        }

        float loudness = Mathf.InverseLerp(minSoundSpeed, loudImpactSpeed, speed);
        AudioProvider.PlayAt(impactSoundId, point, Mathf.Lerp(0.25f, 1f, loudness));

        if (speed >= minImpactSpeed)
            NoiseEvents.Emit(point, hearingMeters * Mathf.Lerp(0.5f, 1f, Mathf.InverseLerp(minImpactSpeed, loudImpactSpeed, speed)));
    }

    private static Vector3 DivideByScale(Vector3 value, Vector3 scale)
    {
        return new Vector3(
            Mathf.Abs(scale.x) > 1e-6f ? value.x / scale.x : value.x,
            Mathf.Abs(scale.y) > 1e-6f ? value.y / scale.y : value.y,
            Mathf.Abs(scale.z) > 1e-6f ? value.z / scale.z : value.z);
    }

    private void SetCollidersEnabled(bool value)
    {
        foreach (Collider c in colliders)
            c.enabled = value;
    }

    private void IgnoreCollisions(Collider[] others)
    {
        ignoredColliders = others;
        SetIgnored(true);
    }

    private void RestoreIgnoredCollisions()
    {
        SetIgnored(false);
        ignoredColliders = null;
    }

    private void SetIgnored(bool ignore)
    {
        if (ignoredColliders == null)
            return;

        foreach (Collider mine in colliders)
            foreach (Collider other in ignoredColliders)
                if (mine != null && other != null)
                    Physics.IgnoreCollision(mine, other, ignore);
    }
}
