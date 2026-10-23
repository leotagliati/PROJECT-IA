using System;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SwingDoor : MonoBehaviour, IInteractable, IDraggable
{
    [Header("Dobradiça")]
    [Tooltip("Eixo de giro no espaço local. Modelo do Blender costuma chegar com a raiz deitada, e aí o 'para cima' é o X ou Z local.")]
    [SerializeField] private Vector3 hingeLocalAxis = Vector3.up;

    [Tooltip("Limites em graus a partir da pose da cena. Negativo abre para o outro lado.")]
    [SerializeField] private float minAngle = 0f;

    [SerializeField] private float maxAngle = 100f;

    [SerializeField] private float startAngle = 0f;

    [Header("Arrasto")]
    [Tooltip("Quão rápido o ponto agarrado alcança a mão (1/s). Menor deixa a porta mais pesada.")]
    [SerializeField] private float followSharpness = 12f;

    [Tooltip("Mão mais perto da dobradiça que esta fração do braço é ignorada: ali o ângulo pula de lado.")]
    [Range(0f, 1f)]
    [SerializeField] private float minReachFraction = 0.3f;

    [Tooltip("Teto em graus/s, segurando ou não.")]
    [SerializeField] private float maxAngularSpeed = 400f;

    [Header("Inércia")]
    [Tooltip("Decaimento exponencial da velocidade depois de soltar (1/s).")]
    [SerializeField] private float friction = 3f;

    [Tooltip("Fração da velocidade devolvida ao bater no limite.")]
    [Range(0f, 1f)]
    [SerializeField] private float limitBounce = 0.15f;

    [Header("Trinco")]
    [Tooltip("Graus a partir do batente em que a porta solta é puxada para fechar. 0 desliga.")]
    [SerializeField] private float latchRange = 6f;

    [Tooltip("Aceleração do puxão (graus/s²). Escapa dele quem sai com mais de sqrt(2 * pull * range).")]
    [SerializeField] private float latchPull = 400f;

    [Header("Controle (botão abre/fecha)")]
    [SerializeField] private float autoSpeed = 110f;

    [SerializeField] private float autoAcceleration = 360f;

    [SerializeField] private float autoArriveSpeed = 30f;

    [Header("Prompt")]
    [SerializeField] private string openLabel = "Abrir";

    [SerializeField] private string closeLabel = "Fechar";

    [Header("Contato")]
    [Tooltip("Graus que a porta precisa se afastar de um limite para um novo contato contar.")]
    [SerializeField] private float contactRearmAngle = 2f;

    [SerializeField] private Lockable lockable;

    private Rigidbody body;
    private Quaternion closedLocalRotation;
    private Vector3 localGrabPoint;
    private float angle;
    private float angularVelocity;
    private Vector3 handTarget;
    private bool hasHandTarget;
    private bool grabbed;
    private bool autoMoving;
    private float autoTarget;
    private bool restingOnLow;
    private bool restingOnHigh;
    private bool sealOnAwake;

    /// <summary>Saiu do batente.</summary>
    public event Action LeftClosed;

    /// <summary>Chegou num limite. closing = batente; speed em graus/s.</summary>
    public event Action<bool, float> LimitReached;

    public float Angle => angle;

    public float AngularSpeed => Mathf.Abs(angularVelocity);

    public float MaxAngularSpeed => maxAngularSpeed;

    public bool IsClosed => Mathf.Abs(angle - ClosedAngle) <= contactRearmAngle;

    /// <summary>0 no batente, 1 toda aberta.</summary>
    public float OpenFraction => Mathf.InverseLerp(ClosedAngle, OpenAngle, angle);

    public bool IsLocked => lockable != null && lockable.IsLocked;

    public Vector3 GrabPoint => transform.TransformPoint(localGrabPoint);

    private float ClosedAngle => Mathf.Abs(minAngle) <= Mathf.Abs(maxAngle) ? minAngle : maxAngle;

    private float OpenAngle => Mathf.Abs(minAngle) <= Mathf.Abs(maxAngle) ? maxAngle : minAngle;

    // Mesmo eixo que o FixedUpdate gira (pai * pose fechada * eixo local), e não o up do pai.
    private Vector3 HingeAxis
    {
        get
        {
            Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
            return parentRotation * closedLocalRotation * LocalAxis;
        }
    }

    private Vector3 LocalAxis => hingeLocalAxis.sqrMagnitude > 1e-6f ? hingeLocalAxis.normalized : Vector3.up;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;

        if (lockable == null)
            lockable = GetComponent<Lockable>();

        closedLocalRotation = transform.localRotation;
        angle = Mathf.Clamp(startAngle, Mathf.Min(minAngle, maxAngle), Mathf.Max(minAngle, maxAngle));
        transform.localRotation = closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis);

        restingOnLow = angle <= Mathf.Min(minAngle, maxAngle) + contactRearmAngle;
        restingOnHigh = angle >= Mathf.Max(minAngle, maxAngle) - contactRearmAngle;

        if (sealOnAwake)
            CloseImmediately();
    }

    private void OnDisable()
    {
        grabbed = false;
        autoMoving = false;
    }

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt prompt)
    {
        bool opening = autoMoving ? Mathf.Approximately(autoTarget, ClosedAngle) : IsClosed;
        prompt = InteractionPrompt.Drag(opening ? openLabel : closeLabel);

        if (grabbed)
            return false;

        // Lacrada sem mensagem parece uma porta comum: a resposta vem ao tentar (sealedRefused), não no prompt.
        if (IsLocked && !string.IsNullOrEmpty(lockable.LockedMessage))
            prompt = prompt.Blocked(lockable.LockedMessage);

        return true;
    }

    /// <summary>Caminho do controle. Fechada abre, qualquer outra coisa fecha; no meio do movimento inverte.</summary>
    public InteractionResult Interact(InteractionController interactor)
    {
        if (lockable != null && lockable.TryRefuse(out InteractionResult refused))
            return refused;

        bool goingOpen = autoMoving ? Mathf.Approximately(autoTarget, ClosedAngle) : IsClosed;

        autoTarget = goingOpen ? OpenAngle : ClosedAngle;
        autoMoving = true;
        return InteractionResult.Success;
    }

    /// <summary>Bate fechada na hora e tranca de vez. Para porta longe do jogador: não anima nem toca som.</summary>
    public void Seal()
    {
        if (lockable == null && !TryGetComponent(out lockable))
            lockable = gameObject.AddComponent<Lockable>();

        lockable.Seal();

        // Porta em objeto inativo ainda não passou pelo Awake (sem body nem pose fechada): fecha quando acordar.
        if (body == null)
        {
            sealOnAwake = true;
            return;
        }

        CloseImmediately();
    }

    private void CloseImmediately()
    {
        grabbed = false;
        autoMoving = false;
        angularVelocity = 0f;
        angle = ClosedAngle;

        // Já no batente: sem isso o próximo passo de física dispara LimitReached e toca a batida.
        bool closedIsLow = Mathf.Approximately(ClosedAngle, Mathf.Min(minAngle, maxAngle));
        restingOnLow = closedIsLow;
        restingOnHigh = !closedIsLow;

        Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
        Quaternion closed = parentRotation * closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis);
        // Teleporte, não MoveRotation: com interpolação ligada a porta varreria o caminho até o batente.
        body.rotation = closed;
        transform.rotation = closed;
    }

    public InteractionResult BeginDrag(InteractionController interactor, Vector3 grabPoint)
    {
        if (lockable != null && lockable.TryRefuse(out InteractionResult refused))
            return refused;

        autoMoving = false;
        localGrabPoint = transform.InverseTransformPoint(grabPoint);
        hasHandTarget = false;
        grabbed = true;
        return InteractionResult.Success;
    }

    public void Drag(Vector3 handPosition)
    {
        if (!grabbed)
            return;

        handTarget = handPosition;
        hasHandTarget = true;
    }

    public void EndDrag()
    {
        grabbed = false;
    }

    // Quanto girar para o ponto agarrado ficar do lado da mão. Usa a pose do próprio `angle`, não o
    // transform: com interpolação, o transform no FixedUpdate não é a pose simulada.
    private float AngleToHand()
    {
        if (!hasHandTarget)
            return 0f;

        Vector3 axis = HingeAxis;
        Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
        Quaternion rotation = parentRotation * closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis);
        Vector3 grabWorld = transform.position + rotation * Vector3.Scale(localGrabPoint, transform.lossyScale);

        Vector3 arm = Vector3.ProjectOnPlane(grabWorld - transform.position, axis);
        Vector3 reach = Vector3.ProjectOnPlane(handTarget - transform.position, axis);

        if (arm.sqrMagnitude < 1e-4f || reach.magnitude < arm.magnitude * minReachFraction)
            return 0f;

        return Vector3.SignedAngle(arm, reach, axis);
    }

    // MoveRotation no passo de física: só assim o Rigidbody cinemático empurra o jogador.
    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float maxStep = maxAngularSpeed * dt;
        float step;

        if (grabbed)
        {
            float follow = 1f - Mathf.Exp(-followSharpness * dt);
            step = Mathf.Clamp(AngleToHand() * follow, -maxStep, maxStep);
            angularVelocity = Mathf.Lerp(angularVelocity, step / dt, 0.35f);
        }
        else if (autoMoving)
        {
            float remaining = Mathf.Abs(autoTarget - angle);
            float brakingSpeed = Mathf.Sqrt(2f * autoAcceleration * remaining);
            float speed = Mathf.Min(autoSpeed, Mathf.Max(autoArriveSpeed, brakingSpeed));

            angularVelocity = Mathf.MoveTowards(angularVelocity, Mathf.Sign(autoTarget - angle) * speed, autoAcceleration * dt);
            step = Mathf.Clamp(angularVelocity * dt, -maxStep, maxStep);
        }
        else
        {
            float toClosed = ClosedAngle - angle;
            if (Mathf.Abs(toClosed) > 0.01f && Mathf.Abs(toClosed) < latchRange)
                angularVelocity += Mathf.Sign(toClosed) * latchPull * dt;

            angularVelocity *= Mathf.Exp(-friction * dt);
            step = Mathf.Clamp(angularVelocity * dt, -maxStep, maxStep);
        }

        angle += step;
        ApplyLimits(Mathf.Abs(step) / dt);

        if (autoMoving && Mathf.Approximately(angle, autoTarget))
            autoMoving = false;

        Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
        body.MoveRotation(parentRotation * closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis));
    }

    private void ApplyLimits(float impactSpeed)
    {
        float low = Mathf.Min(minAngle, maxAngle);
        float high = Mathf.Max(minAngle, maxAngle);

        bool closedIsLow = Mathf.Approximately(ClosedAngle, low);
        bool wasRestingClosed = closedIsLow ? restingOnLow : restingOnHigh;

        if (angle > low + contactRearmAngle) restingOnLow = false;
        if (angle < high - contactRearmAngle) restingOnHigh = false;

        bool restingClosed = closedIsLow ? restingOnLow : restingOnHigh;
        if (wasRestingClosed && !restingClosed)
            LeftClosed?.Invoke();

        if (angle >= low && angle <= high)
            return;

        bool hitHigh = angle > high;
        angle = Mathf.Clamp(angle, low, high);

        bool intoLimit = hitHigh ? angularVelocity > 0f : angularVelocity < 0f;
        bool alreadyResting = hitHigh ? restingOnHigh : restingOnLow;

        if (hitHigh) restingOnHigh = true;
        else restingOnLow = true;

        if (!alreadyResting)
            LimitReached?.Invoke(Mathf.Approximately(angle, ClosedAngle), impactSpeed);

        if (intoLimit)
            angularVelocity = -angularVelocity * limitBounce;
    }
}
