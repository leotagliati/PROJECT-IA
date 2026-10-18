using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Segurar Throw ergue o braço (mira), soltar arremessa. O braço é uma camada Override do Animator com
/// máscara só do braço: andar/correr seguem no resto do corpo, e o SpineLook continua girando a coluna,
/// que leva o braço junto. O peso da camada é daqui, não de um estado vazio: estado vazio com Write
/// Defaults ligado trava o braço na pose de bind. Com item na mão a camada fica ligada (Hold).
///
/// Contrato com o PlayerAC: camada <see cref="layerName"/>; Hold (padrão, loop) → Aim (loop, por
/// crossfade daqui) → Release → Hold (exit time); trigger <see cref="releaseTrigger"/> vai de Any State
/// a Release — de Any State porque soltar o botão no meio do crossfade Hold → Aim ainda tem que
/// arremessar. O clipe do Release chama o evento ThrowRelease no frame em que a mão abre
/// (<see cref="ThrowAnimationEvents"/>, adicionado no Animator por aqui se faltar).
/// </summary>
public class PlayerThrow : MonoBehaviour
{
    private enum Phase
    {
        Idle,
        Aiming,
        Throwing,
        Recovering
    }

    [Header("Referências")]
    [SerializeField] private Animator animator;

    [Tooltip("Filho do osso hand.L onde o item fica: a mão da Avatar Mask PlayerThrowArm. A direita é a da lanterna.")]
    [SerializeField] private Transform handSocket;

    [Tooltip("Vazio = procura nos filhos. Dá a câmera (direção) e o estado de arrasto de porta.")]
    [SerializeField] private InteractionController interaction;

    [Tooltip("Vazio = procura neste objeto. Dá o estado Running.")]
    [SerializeField] private PlayerMovement movement;

    [Header("Animator")]
    [SerializeField] private string layerName = "Throw";

    [SerializeField] private string holdState = "Hold";

    [SerializeField] private string aimState = "Aim";

    [Tooltip("Segundos do crossfade Hold → Aim. Não há clipe de erguer o braço: é esta mistura.")]
    [SerializeField, Min(0f)] private float raiseTime = 0.15f;

    [SerializeField] private string releaseTrigger = "throwRelease";

    [Tooltip("Peso da camada por segundo, entrando e saindo.")]
    [SerializeField] private float layerBlendSpeed = 8f;

    [Header("Arremesso")]
    [SerializeField] private float minThrowSpeed = 6f;

    [SerializeField] private float maxThrowSpeed = 14f;

    [Tooltip("Segundos de mira até a força e o ângulo máximos.")]
    [SerializeField] private float chargeTime = 1.6f;

    [Tooltip("Graus acima da mira sem carga; o ângulo cresce com a carga.")]
    [SerializeField, Range(0f, 45f)] private float minArcAngle = 5f;

    [Tooltip("Graus acima da mira com a carga cheia.")]
    [SerializeField, Range(0f, 60f)] private float maxArcAngle = 25f;

    [Tooltip("Teto da direção final, em graus acima do horizonte.")]
    [SerializeField, Range(30f, 89f)] private float maxLaunchPitch = 70f;

    [Tooltip("Correr (ou apertar Sprint) cancela a mira; o item fica na mão.")]
    [SerializeField] private bool cancelOnSprint = true;

    [Tooltip("Sem o evento ThrowRelease (ou sem a camada), solta depois disto (s) do botão solto.")]
    [SerializeField] private float releaseTimeout = 0.6f;

    [Tooltip("Teto (s) para dar o arremesso por encerrado, se o Release não voltar ao Hold antes.")]
    [SerializeField] private float maxRecoverTime = 1.2f;

    private Phase phase;
    private ThrowableItem held;
    private Collider[] ownColliders;
    private int layer = -1;
    private int holdHash;
    private int aimHash;
    private int releaseTriggerHash;
    private float aimStartTime;
    private float throwTime;
    private float recoverStartTime;
    private float charge;

    // Travados ao soltar o botão: a mão só abre no evento ThrowRelease, e a linha da ThrowTrajectory tem que valer.
    private Vector3 lockedOrigin;
    private Vector3 lockedVelocity;
    private Vector3 lockedRoot;

    public bool HasItem => held != null;

    public bool IsAiming => phase == Phase.Aiming;

    public float Charge => phase == Phase.Aiming ? ChargeAt(Time.time) : charge;

    public ThrowableItem HeldItem => held;

    public Collider[] OwnColliders => ownColliders;

    /// <summary>Mirando ou no meio do arremesso: a mão está ocupada para interagir.</summary>
    public bool IsBusy => phase != Phase.Idle;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (interaction == null)
            interaction = GetComponentInChildren<InteractionController>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();

        // Antes de qualquer item entrar na mão: senão os colisores dele entrariam na lista.
        ownColliders = GetComponentsInChildren<Collider>();

        holdHash = Animator.StringToHash(holdState);
        aimHash = Animator.StringToHash(aimState);
        releaseTriggerHash = Animator.StringToHash(releaseTrigger);
        layer = animator != null ? animator.GetLayerIndex(layerName) : -1;

        // O evento ThrowRelease só chega no objeto do Animator, que fica dentro do modelo.
        if (animator != null && !animator.TryGetComponent<ThrowAnimationEvents>(out _))
            animator.gameObject.AddComponent<ThrowAnimationEvents>();

        if (handSocket == null)
            Debug.LogWarning($"[PlayerThrow] Sem handSocket em {name}: não dá para segurar itens.", this);
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
        PlayerInputProvider.Player.Throw.started += OnThrowStarted;
        PlayerInputProvider.Player.Throw.canceled += OnThrowCanceled;
    }

    private void OnDisable()
    {
        PlayerInputProvider.Player.Throw.started -= OnThrowStarted;
        PlayerInputProvider.Player.Throw.canceled -= OnThrowCanceled;
        PlayerInputProvider.Release();

        phase = Phase.Idle;
        if (layer >= 0 && animator != null)
            animator.SetLayerWeight(layer, 0f);
    }

    public bool TryHold(ThrowableItem item)
    {
        if (held != null || handSocket == null || item == null)
            return false;

        held = item;
        held.AttachTo(handSocket);

        // A camada pode ter ficado parada em outro estado com peso zero; entra já segurando.
        if (layer >= 0 && phase == Phase.Idle)
            animator.Play(holdHash, layer, 0f);

        return true;
    }

    /// <summary>Origem e velocidade de saída; fonte única do arremesso e da ThrowTrajectory.</summary>
    public bool TryGetLaunch(out Vector3 origin, out Vector3 velocity)
    {
        origin = velocity = Vector3.zero;
        if (held == null)
            return false;

        if (phase == Phase.Aiming)
            return ComputeLaunch(ChargeAt(Time.time), out origin, out velocity);

        if (phase != Phase.Throwing)
            return false;

        origin = lockedOrigin + (transform.position - lockedRoot);
        velocity = lockedVelocity;
        return true;
    }

    /// <summary>Chamado pelo evento de animação (via <see cref="ThrowAnimationEvents"/>) ou pelo timeout.</summary>
    public void ThrowRelease()
    {
        if (phase != Phase.Throwing || held == null)
            return;

        TryGetLaunch(out Vector3 origin, out Vector3 velocity);
        Transform aim = AimTransform();
        if (aim != null)
            origin = SafeOrigin(aim.position, origin);

        held.Launch(origin, velocity, ownColliders);
        held = null;

        phase = Phase.Recovering;
        recoverStartTime = Time.time;
    }

    private void OnThrowStarted(InputAction.CallbackContext ctx)
    {
        if (held == null || phase != Phase.Idle)
            return;

        if (interaction != null && interaction.IsDragging)
            return;

        phase = Phase.Aiming;
        aimStartTime = Time.time;

        if (layer >= 0)
        {
            animator.ResetTrigger(releaseTriggerHash);
            animator.CrossFadeInFixedTime(aimHash, raiseTime, layer);
        }
    }

    private void OnThrowCanceled(InputAction.CallbackContext ctx)
    {
        if (phase != Phase.Aiming)
            return;

        charge = ChargeAt(Time.time);
        ComputeLaunch(charge, out lockedOrigin, out lockedVelocity);
        lockedRoot = transform.position;
        phase = Phase.Throwing;
        throwTime = Time.time;

        if (layer >= 0)
            animator.SetTrigger(releaseTriggerHash);
        else
            ThrowRelease();
    }

    // Soltar o botão depois não faz nada: o OnThrowCanceled só age em Aiming.
    private void CancelAim()
    {
        phase = Phase.Idle;

        if (layer >= 0)
            animator.CrossFadeInFixedTime(holdHash, raiseTime, layer);
    }

    private bool WantsToRun()
    {
        return PlayerInputProvider.Player.Sprint.WasPressedThisFrame()
               || (movement != null && movement.CurrentState == PlayerState.Running);
    }

    private void Update()
    {
        if (phase == Phase.Aiming && cancelOnSprint && WantsToRun())
            CancelAim();

        if (phase == Phase.Throwing && Time.time - throwTime >= releaseTimeout)
            ThrowRelease();

        if (phase == Phase.Recovering && RecoverDone())
            phase = Phase.Idle;

        if (layer >= 0)
        {
            // Item na mão já é motivo: o Hold é a pose de segurar, não só a de mirar.
            float target = phase == Phase.Idle && held == null ? 0f : 1f;
            float weight = Mathf.MoveTowards(animator.GetLayerWeight(layer), target, layerBlendSpeed * Time.deltaTime);
            animator.SetLayerWeight(layer, weight);
        }
    }

    private bool RecoverDone()
    {
        if (layer < 0)
            return true;

        // De volta ao Hold pela transição de exit time do Release. Antes do trigger ser
        // consumido o estado ainda é Aim, então não há falso positivo logo após soltar.
        if (!animator.IsInTransition(layer) && animator.GetCurrentAnimatorStateInfo(layer).shortNameHash == holdHash)
            return true;

        return Time.time - recoverStartTime >= maxRecoverTime;
    }

    private float ChargeAt(float time)
    {
        return chargeTime > 0f ? Mathf.Clamp01((time - aimStartTime) / chargeTime) : 1f;
    }

    private Transform AimTransform()
    {
        if (interaction != null && interaction.ViewCamera != null)
            return interaction.ViewCamera.transform;

        Camera main = Camera.main;
        return main != null ? main.transform : null;
    }

    private bool ComputeLaunch(float charge01, out Vector3 origin, out Vector3 velocity)
    {
        origin = velocity = Vector3.zero;
        Transform aim = AimTransform();
        if (aim == null || handSocket == null)
            return false;

        Vector3 forward = aim.forward;
        Vector3 flat = new Vector3(forward.x, 0f, forward.z);
        if (flat.sqrMagnitude < 1e-4f)
            flat = new Vector3(aim.up.x, 0f, aim.up.z) * -Mathf.Sign(forward.y); // olhando reto para cima ou baixo
        flat.Normalize();

        float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float arc = Mathf.Lerp(minArcAngle, maxArcAngle, charge01);
        float launchPitch = Mathf.Max(pitch, Mathf.Min(pitch + arc, maxLaunchPitch)) * Mathf.Deg2Rad;
        Vector3 direction = flat * Mathf.Cos(launchPitch) + Vector3.up * Mathf.Sin(launchPitch);

        origin = SafeOrigin(aim.position, handSocket.position);
        velocity = direction * Mathf.Lerp(minThrowSpeed, maxThrowSpeed, charge01);
        return true;
    }

    // De cara para a parede, a mão já está do outro lado dela: o item nasceria atravessado.
    private static Vector3 SafeOrigin(Vector3 eye, Vector3 hand)
    {
        Vector3 toHand = hand - eye;
        float distance = toHand.magnitude;
        if (distance < 1e-3f)
            return hand;

        Vector3 direction = toHand / distance;
        if (Physics.Raycast(eye, direction, out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore))
            return hit.point - direction * 0.1f;

        return hand;
    }
}
