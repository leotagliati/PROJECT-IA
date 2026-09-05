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
    Jumping
}

public class PlayerMovement : MonoBehaviour
{
    private PlayerInputActions playerInput;
    private CharacterController controller;

    [Header("Movement settings")]
    [SerializeField] private float moveSpeed = 5f;      
    [SerializeField] private float sprintMultiplier = 1.7f;
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -9.81f;

    [SerializeField] private float groundedGraceTime = 0.1f;

    [Header("Animation")]
    [SerializeField] private Animator animator;

    [Header("Footsteps")]
    [SerializeField] private string footstepSoundId = "footstep";

    [SerializeField] private float stepDistance = 2f;

    [SerializeField] private float runStepDistance = 1.2f;

    [SerializeField] private float footstepHeightOffset = 0f;

    private static readonly int IsWalkingHash = Animator.StringToHash("isWalking");
    private static readonly int IsRunningHash = Animator.StringToHash("isRunning");

    private Vector2 moveInput;
    private bool sprintHeld;
    private Vector3 velocity;
    private bool isGrounded;
    private float lastGroundedTime;

    private Vector3 lastFootstepPosition;
    private float distanceSinceStep;

    /// <summary>Input de movimento cru deste frame (x = lado, y = frente).</summary>
    public Vector2 MoveInput => moveInput;

    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;

    public event Action<PlayerState> StateChanged;

    private void Awake()
    {

        playerInput = new PlayerInputActions();
        controller = GetComponent<CharacterController>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        playerInput.Player.Jump.performed += ctx => Jump();

        lastFootstepPosition = transform.position;
    }

    private void OnEnable()
    {

        playerInput.Player.Enable();
    }

    private void OnDisable()
    {
        playerInput.Player.Disable();
    }

    private void Update()
    {

        isGrounded = controller.isGrounded;

        if (isGrounded)
            lastGroundedTime = Time.time;

        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        moveInput = playerInput.Player.Move.ReadValue<Vector2>();
        sprintHeld = playerInput.Player.Sprint.IsPressed();

        UpdateState();

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * GetCurrentSpeed() * Time.deltaTime);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        UpdateFootsteps();
    }

    private void UpdateState()
    {
        PlayerState next;

        if (Time.time - lastGroundedTime > groundedGraceTime)
            next = PlayerState.Jumping;
        else if (moveInput.sqrMagnitude < 0.01f)
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
            case PlayerState.Walking:
                animator.SetBool(IsWalkingHash, true);
                animator.SetBool(IsRunningHash, false);
                break;

            case PlayerState.Running:
                animator.SetBool(IsWalkingHash, false);
                animator.SetBool(IsRunningHash, true);
                break;

            case PlayerState.Idle:
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
        PlayFootstep(footstepSoundId);
    }

    private void PlayFootstep(string soundId)
    {
        if (string.IsNullOrEmpty(soundId))
            return;

        // PlayAt e não PlayFollowing: o passo fica onde o pé bateu, não anda junto com o player.
        AudioSystem.PlayAt(soundId, transform.position + Vector3.up * footstepHeightOffset);
    }

    private void Jump()
    {
        if (isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
    }
}
