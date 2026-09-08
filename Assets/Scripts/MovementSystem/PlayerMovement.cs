using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Estados possíveis do player. Um de cada vez, sempre — quem quiser reagir ao player
/// (áudio, IA, UI) olha isso em vez de recalcular "está andando?" do próprio jeito.
/// </summary>
public enum PlayerState
{
    Idle,
    Walking,
    Running,
    Jumping,
    Crouching,
    CrouchWalking
}

public class PlayerMovement : MonoBehaviour
{
    private CharacterController controller;

    [Header("Movement settings")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float sprintMultiplier = 1.7f;
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -9.81f;

    [SerializeField] private float groundedGraceTime = 0.1f;

    [Header("Crouch")]
    [Tooltip("Altura da cápsula agachado. A de pé é a que estiver no CharacterController.")]
    [SerializeField] private float crouchHeight = 0.5f;

    [SerializeField] private float crouchSpeedMultiplier = 0.45f;

    [Tooltip("Duração aproximada da transição de pé para agachado, em segundos.")]
    [SerializeField] private float crouchTransitionTime = 0.14f;

    [Tooltip("O que conta como teto ao tentar levantar. A layer do próprio player é descartada.")]
    [SerializeField] private LayerMask standCheckMask = ~0;

    [Header("Animation")]
    [SerializeField] private Animator animator;

    [Header("Footsteps")]
    [SerializeField] private string footstepSoundId = "footstep";

    [SerializeField] private float stepDistance = 2f;

    [SerializeField] private float runStepDistance = 1.2f;

    [SerializeField] private float crouchStepDistance = 3f;

    [Tooltip("Volume do passo agachado. É o mesmo clipe, só que abafado.")]
    [SerializeField, Range(0f, 1f)] private float crouchStepVolume = 0.35f;

    [SerializeField] private float footstepHeightOffset = 0f;

    private static readonly int IsWalkingHash = Animator.StringToHash("isWalking");
    private static readonly int IsRunningHash = Animator.StringToHash("isRunning");

    private Vector2 moveInput;
    private bool sprintHeld;
    private bool isCrouching;
    private Vector3 velocity;
    private bool isGrounded;
    private float lastGroundedTime;

    private float standingHeight;
    private Vector3 standingCenter;
    private float crouchAmount;
    private float crouchVelocity;

    private Vector3 lastFootstepPosition;
    private float distanceSinceStep;

    /// <summary>Input de movimento cru deste frame (x = lado, y = frente).</summary>
    public Vector2 MoveInput => moveInput;

    /// <summary>
    /// Corrida pedida e permitida agora — agachado nunca corre. Diferente de
    /// <see cref="CurrentState"/> ser Running: continua verdadeiro no ar, onde o estado
    /// vira Jumping.
    /// </summary>
    public bool SprintHeld => sprintHeld;

    /// <summary>Agachado agora. Continua verdadeiro embaixo de um teto baixo, mesmo sem a tecla.</summary>
    public bool IsCrouching => isCrouching;

    /// <summary>
    /// 0 = de pé, 1 = agachado por completo. É a transição já suavizada; câmera e
    /// qualquer outro efeito que acompanha a altura devem usar isto, não o bool.
    /// </summary>
    public float CrouchAmount => crouchAmount;

    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;

    public event Action<PlayerState> StateChanged;

    private void Awake()
    {

        controller = GetComponent<CharacterController>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // A pose de pé é a que veio do prefab; agachar é sempre relativo a ela.
        standingHeight = controller.height;
        standingCenter = controller.center;

        lastFootstepPosition = transform.position;
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();

        // O asset de input é compartilhado e vive além desta instância, então a inscrição
        // sai no OnDisable — um lambda no Awake continuaria chamando Jump() de um player
        // já destruído depois de trocar de cena.
        PlayerInputProvider.Player.Jump.performed += OnJumpPerformed;
    }

    private void OnDisable()
    {
        PlayerInputProvider.Player.Jump.performed -= OnJumpPerformed;

        PlayerInputProvider.Release();
    }

    private void OnJumpPerformed(InputAction.CallbackContext context) => Jump();

    private void Update()
    {

        isGrounded = controller.isGrounded;

        if (isGrounded)
            lastGroundedTime = Time.time;

        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        moveInput = PlayerInputProvider.Player.Move.ReadValue<Vector2>();

        // Antes do sprint: agachar tem prioridade e cancela a corrida no mesmo frame,
        // senão o estado oscilaria entre Running e CrouchWalking com Shift+Ctrl juntos.
        UpdateCrouch();

        sprintHeld = PlayerInputProvider.Player.Sprint.IsPressed() && !isCrouching;

        UpdateState();

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * GetCurrentSpeed() * Time.deltaTime);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        UpdateFootsteps();
    }

    private void UpdateCrouch()
    {
        bool wantsCrouch = PlayerInputProvider.Player.Crouch.IsPressed();

        if (!wantsCrouch && isCrouching && !HasHeadroom())
            wantsCrouch = true;

        isCrouching = wantsCrouch;

        float target = isCrouching ? 1f : 0f;

        crouchAmount = Mathf.SmoothDamp(crouchAmount, target, ref crouchVelocity, crouchTransitionTime);

        // SmoothDamp chega perto mas nunca no valor exato: encosta e para, senão a
        // cápsula ficaria sendo reescrita todo frame por causa de um resto de 0.001.
        if (Mathf.Abs(crouchAmount - target) < 0.001f)
        {
            crouchAmount = target;
            crouchVelocity = 0f;
        }

        float height = Mathf.Lerp(standingHeight, CrouchedHeight, crouchAmount);

        if (!Mathf.Approximately(controller.height, height))
            ApplyHeight(height);
    }

    private float CrouchedHeight => Mathf.Clamp(crouchHeight, controller.radius * 2f, standingHeight);

    private void ApplyHeight(float height)
    {
        controller.height = height;
        controller.center = standingCenter - Vector3.up * ((standingHeight - height) * 0.5f);
    }

    /// <summary>Espaço livre acima para voltar à altura de pé.</summary>
    private bool HasHeadroom()
    {
        float radius = Mathf.Max(0.01f, controller.radius - controller.skinWidth);
        float distance = standingHeight - controller.height;

        if (distance <= 0f)
            return true;

        // Topo da cápsula atual, em mundo. Sobe dali até onde o topo ficaria de pé.
        Vector3 top = transform.position + controller.center +
                      Vector3.up * Mathf.Max(0f, controller.height * 0.5f - controller.radius);

        // A própria layer do player sai da conta: a sonda nasce dentro do corpo dele.
        int mask = standCheckMask & ~(1 << gameObject.layer);

        return !Physics.SphereCast(
            top,
            radius,
            Vector3.up,
            out _,
            distance + controller.skinWidth,
            mask,
            QueryTriggerInteraction.Ignore);
    }

    // ------------------------------------------------------------------- estado

    private void UpdateState()
    {
        PlayerState next;
        bool moving = moveInput.sqrMagnitude >= 0.01f;

        if (Time.time - lastGroundedTime > groundedGraceTime)
            next = PlayerState.Jumping;
        else if (isCrouching)
            next = moving ? PlayerState.CrouchWalking : PlayerState.Crouching;
        else if (!moving)
            next = PlayerState.Idle;
        else if (sprintHeld)
            next = PlayerState.Running;
        else
            next = PlayerState.Walking;

        if (next == CurrentState)
            return;

        PlayerState previous = CurrentState;
        CurrentState = next;

        OnStateExit(previous);
        OnStateEnter(next);

        StateChanged?.Invoke(next);
    }

    private void OnStateExit(PlayerState state)
    {
        switch (state)
        {
            case PlayerState.Jumping:
                PlayFootstep(footstepSoundId);
                distanceSinceStep = 0f;
                break;
        }
    }

    private void OnStateEnter(PlayerState state)
    {
        if (animator == null)
            return;

        switch (state)
        {
            // Sem clipe de agachado no controller ainda, então CrouchWalking reaproveita a
            // caminhada e Crouching, a parada. Quando existir a animação, é aqui que entra.
            case PlayerState.Walking:
            case PlayerState.CrouchWalking:
                animator.SetBool(IsWalkingHash, true);
                animator.SetBool(IsRunningHash, false);
                break;

            case PlayerState.Running:
                animator.SetBool(IsWalkingHash, false);
                animator.SetBool(IsRunningHash, true);
                break;

            case PlayerState.Idle:
            case PlayerState.Crouching:
                animator.SetBool(IsWalkingHash, false);
                animator.SetBool(IsRunningHash, false);
                break;
        }
    }

    /// <summary>Velocidade horizontal do estado atual.</summary>
    private float GetCurrentSpeed()
    {
        switch (CurrentState)
        {
            case PlayerState.Running:
                return moveSpeed * sprintMultiplier;

            // No ar continua com controle, mas sempre no ritmo de caminhada:
            // sprint no ar viraria voo rasante.
            case PlayerState.Jumping:
                return moveSpeed;

            case PlayerState.Walking:
                return moveSpeed;

            case PlayerState.CrouchWalking:
                return moveSpeed * crouchSpeedMultiplier;

            default:
                return 0f;
        }
    }

    /// <summary>Distância entre passos do estado atual; 0 significa "não faz passo".</summary>
    private float GetCurrentStepDistance()
    {
        switch (CurrentState)
        {
            case PlayerState.Walking:
                return stepDistance;

            case PlayerState.Running:
                return runStepDistance;

            case PlayerState.CrouchWalking:
                return crouchStepDistance;

            default:
                return 0f;   // parado ou no ar não pisa em nada
        }
    }

    // ---------------------------------------------------------------- footsteps

    /// <summary>
    /// Roda depois dos dois Move do frame, então mede o deslocamento que de fato aconteceu —
    /// esbarrar numa parede não gera passo, porque a posição não mudou.
    /// </summary>
    private void UpdateFootsteps()
    {
        Vector3 delta = transform.position - lastFootstepPosition;
        delta.y = 0f;   // cair ou subir rampa não conta como caminhada

        distanceSinceStep += delta.magnitude;
        lastFootstepPosition = transform.position;

        float threshold = GetCurrentStepDistance();
        if (threshold <= 0f || distanceSinceStep < threshold)
            return;

        distanceSinceStep = 0f;

        // Agachado é o mesmo passo abafado: é o que o Seeker escuta de menos longe.
        PlayFootstep(footstepSoundId, CurrentState == PlayerState.CrouchWalking ? crouchStepVolume : 1f);
    }

    private void PlayFootstep(string soundId, float volumeScale = 1f)
    {
        if (string.IsNullOrEmpty(soundId))
            return;

        // PlayAt e não PlayFollowing: o passo fica onde o pé bateu, não anda junto com o player.
        AudioSystem.PlayAt(soundId, transform.position + Vector3.up * footstepHeightOffset, volumeScale);
    }

    private void Jump()
    {
        // Agachado não pula: sair da cápsula baixa no meio do salto abriria a chance de
        // atravessar o teto que obrigou a agachar.
        if (isGrounded && !isCrouching)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
    }
}
