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
    [Tooltip("1 = o ponto agarrado acompanha a mão. Maior deixa a porta mais leve.")]
    [SerializeField] private float dragGain = 1f;

    [Tooltip("Teto em graus/s, segurando ou não.")]
    [SerializeField] private float maxAngularSpeed = 400f;

    [Header("Inércia")]
    [Tooltip("Decaimento exponencial da velocidade depois de soltar (1/s).")]
    [SerializeField] private float friction = 3f;

    [Tooltip("Fração da velocidade devolvida ao bater no limite.")]
    [Range(0f, 1f)]
    [SerializeField] private float limitBounce = 0.15f;

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
    private float pendingAngle;
    private bool grabbed;
    private bool autoMoving;
    private float autoTarget;
    private bool restingOnLow;
    private bool restingOnHigh;

    /// <summary>Saiu do batente.</summary>
    public event Action LeftClosed;

    /// <summary>Chegou num limite. closing = batente; speed em graus/s.</summary>
    public event Action<bool, float> LimitReached;

    public float Angle => angle;

    public float AngularSpeed => Mathf.Abs(angularVelocity);

    public float MaxAngularSpeed => maxAngularSpeed;

    public bool IsClosed => Mathf.Abs(angle - ClosedAngle) <= contactRearmAngle;

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

        if (IsLocked)
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

    public InteractionResult BeginDrag(InteractionController interactor, Vector3 grabPoint)
    {
        if (lockable != null && lockable.TryRefuse(out InteractionResult refused))
            return refused;

        autoMoving = false;
        localGrabPoint = transform.InverseTransformPoint(grabPoint);
        pendingAngle = 0f;
        grabbed = true;
        return InteractionResult.Success;
    }

    public void Drag(Vector3 handDelta)
    {
        if (!grabbed)
            return;

        Vector3 axis = HingeAxis;
        Vector3 arm = Vector3.ProjectOnPlane(GrabPoint - transform.position, axis);

        // Braço ~0 (agarrou na dobradiça) daria ganho infinito.
        float armSqr = Mathf.Max(arm.sqrMagnitude, 0.2f * 0.2f);

        // Cross(eixo, braço) tem módulo |braço|; dividir por |braço|² dá radianos.
        Vector3 tangent = Vector3.Cross(axis, arm);
        pendingAngle += Vector3.Dot(handDelta, tangent) / armSqr * Mathf.Rad2Deg * dragGain;
    }

    public void EndDrag()
    {
        grabbed = false;
    }

    // MoveRotation no passo de física: só assim o Rigidbody cinemático empurra o jogador.
    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float maxStep = maxAngularSpeed * dt;
        float step;

        if (grabbed)
        {
            step = Mathf.Clamp(pendingAngle, -maxStep, maxStep);
            pendingAngle = 0f;
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
