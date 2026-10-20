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

    [Tooltip("Vazio = procura no mesmo objeto. Sem stamina, corre sem limite.")]
    [SerializeField] private PlayerStamina stamina;

    [Header("Crouch")]
    [Tooltip("Altura da cápsula agachado. A de pé é a que estiver no CharacterController.")]
    [SerializeField] private float crouchHeight = 0.5f;

    [SerializeField] private float crouchSpeedMultiplier = 0.45f;

    [Tooltip("Ligado: um toque agacha, outro levanta. Desligado: agacha só enquanto segura.")]
    [SerializeField] private bool crouchToggle = true;

    [Tooltip("Duração aproximada da transição de pé para agachado, em segundos.")]
    [SerializeField] private float crouchTransitionTime = 0.14f;

    [Tooltip("O que conta como teto ao tentar levantar. Os colliders do próprio player são descartados.")]
    [SerializeField] private LayerMask standCheckMask = ~0;

    [Tooltip("Folga acima da cabeça de pé, em metros: teto mais baixo que isso segura o agachado. Só sem Headroom Volume.")]
    [SerializeField, Min(0f)] private float standCheckMargin = 0.3f;

    [Tooltip("Volume que tem que estar livre para levantar, fora dos ossos. Ligado como trigger usa a forma exata da física; desligado, só Box/Sphere. Vazio: procura pelo nome abaixo; sem ele, cápsula de pé + margem.")]
    [SerializeField] private Collider headroomVolume;

    [SerializeField] private string headroomVolumeName = "Head";

    [Header("Animation")]
    [SerializeField] private Animator animator;

    [Tooltip("Suavização do parâmetro moveSpeed do blend tree, em segundos. Sem isso o blend " +
             "salta de idle para corrida em um frame e a mistura de poses fica visível.")]
    [SerializeField] private float animBlendDampTime = 0.1f;

    [Tooltip("Input para trás além disso toca a caminhada ao contrário. Folga para o lateral " +
             "puro de stick, que oscila em volta de zero, não ficar trocando o sentido.")]
    [SerializeField, Range(0f, 1f)] private float backwardThreshold = 0.2f;

    [Header("Footsteps")]
    [SerializeField] private string footstepSoundId = "footstep";

    [Tooltip("Evento de passo de clipe com peso menor que isso no blend tree é descartado: na " +
             "mistura Walk↔Run os dois clipes disparam, e sem o filtro o passo sai dobrado.")]
    [SerializeField, Range(0f, 1f)] private float footstepMinClipWeight = 0.5f;

    [Tooltip("Volume do passo agachado. É o mesmo clipe, só que abafado.")]
    [SerializeField, Range(0f, 1f)] private float crouchStepVolume = 0.35f;

    [SerializeField] private float footstepHeightOffset = 0f;

    [Tooltip("Poeira no passo e na aterrissagem. Vazio = procura no mesmo objeto; sem StepDust, " +
             "passo sem poeira.")]
    [SerializeField] private StepDust stepDust;

    // Ossos do rig do Player.fbx. A poeira sai do pé mais baixo, que é o de apoio no evento.
    private const string LeftFootBone = "foot.L";
    private const string RightFootBone = "foot.R";

    // Blend tree 1D: 0 = idle, 1 = walk, 2 = run. Os thresholds vivem no PlayerAC.controller.
    // Agachado usa os mesmos valores (0 parado, 1 andando) na sub-árvore Crouching.
    private static readonly int MoveSpeedHash = Animator.StringToHash("moveSpeed");

    // Mistura Standing ↔ Crouching no blend raiz do estado Movement. Recebe o CrouchAmount, que
    // já é a transição suavizada da cápsula: corpo, câmera e colisão descem juntos.
    private static readonly int CrouchHash = Animator.StringToHash("crouch");
    private const float AnimIdle = 0f;
    private const float AnimWalk = 1f;
    private const float AnimRun = 2f;

    // Multiplicador de velocidade do estado Movement (Speed > Multiplier no PlayerAC): -1 toca
    // o blend ao contrário. Default 1 no controller, senão o estado nasce congelado.
    private static readonly int AnimDirectionHash = Animator.StringToHash("animDirection");

    private Vector2 moveInput;
    private bool sprintHeld;
    private bool isCrouching;
    private Vector3 velocity;
    private bool isGrounded;
    private float lastGroundedTime;

    private float standingHeight;
    private Vector3 standingCenter;
    private readonly Collider[] headroomHits = new Collider[16];

    private float crouchAmount;
    private float crouchVelocity;

    private Transform leftFoot;
    private Transform rightFoot;

    /// <summary>Input de movimento cru deste frame (x = lado, y = frente).</summary>
    public Vector2 MoveInput => moveInput;

    /// <summary>
    /// Corrida pedida e permitida agora — agachado ou exausto (<see cref="PlayerStamina"/>)
    /// nunca corre. Diferente de
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

    /// <summary>
    /// Andando de ré agora (input para trás além de <see cref="backwardThreshold"/>). Única
    /// fonte de verdade: o Animator toca a caminhada ao contrário e o SpineLook espelha a
    /// torção da bacia pelo mesmo critério — limiares diferentes deixariam a perna recuando
    /// virada para o lado errado na faixa entre os dois.
    /// </summary>
    public bool IsMovingBackward => moveInput.y < -backwardThreshold;

    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;

    public event Action<PlayerState> StateChanged;

    private void Awake()
    {

        controller = GetComponent<CharacterController>();

        if (stamina == null)
            stamina = GetComponent<PlayerStamina>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // A pose de pé é a que veio do prefab; agachar é sempre relativo a ela.
        standingHeight = controller.height;
        standingCenter = controller.center;

        if (headroomVolume == null && !string.IsNullOrEmpty(headroomVolumeName))
            headroomVolume = FindHeadroomVolume();

        if (headroomVolume != null)
            Debug.Log($"{name}: teto do agachar pelo volume '{headroomVolume.name}' ({headroomVolume.GetType().Name}, " +
                      $"{(headroomVolume.enabled ? "ligado: forma da física" : "desligado: só Box/Sphere")}).", headroomVolume);
        else
            Debug.Log($"{name}: sem volume '{headroomVolumeName}'; teto do agachar pela cápsula de pé + {standCheckMargin} m.", this);

        if (animator != null && !animator.TryGetComponent<PlayerFootsteps>(out _))
            animator.gameObject.AddComponent<PlayerFootsteps>();

        if (stepDust == null)
            stepDust = GetComponent<StepDust>();

        if (stepDust != null && animator != null)
        {
            leftFoot = FindDeep(animator.transform, LeftFootBone);
            rightFoot = FindDeep(animator.transform, RightFootBone);
        }
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
    }

    private void OnDisable()
    {
        PlayerInputProvider.Release();

        if (animator != null)
        {
            animator.SetFloat(MoveSpeedHash, AnimIdle);
            animator.SetFloat(AnimDirectionHash, 1f);
            animator.SetFloat(CrouchHash, crouchAmount);
        }
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

        moveInput = PlayerInputProvider.Player.Move.ReadValue<Vector2>();

        UpdateCrouch();

        sprintHeld = PlayerInputProvider.Player.Sprint.IsPressed()
                  && !isCrouching
                  && (stamina == null || stamina.CanSprint);

        UpdateState();

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * GetCurrentSpeed() * Time.deltaTime);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        UpdateAnimator();
    }

    private void UpdateCrouch()
    {
        // No toggle o toque inverte o estado atual; no hold o estado é o próprio botão. Nos dois
        // casos o teto baixo (abaixo) ainda segura o jogador agachado — no toggle o toque é
        // "gasto" e precisa de outro quando houver espaço, que é o esperado.
        bool wantsCrouch = crouchToggle
            ? isCrouching != PlayerInputProvider.Player.Crouch.WasPressedThisFrame()
            : PlayerInputProvider.Player.Crouch.IsPressed();

        if (crouchToggle && isCrouching && PlayerInputProvider.Player.Sprint.WasPressedThisFrame())
            wantsCrouch = false;

        if (!wantsCrouch && isCrouching && !HasHeadroom())
            wantsCrouch = true;

        isCrouching = wantsCrouch;

        float target = isCrouching ? 1f : 0f;

        crouchAmount = Mathf.SmoothDamp(crouchAmount, target, ref crouchVelocity, crouchTransitionTime);

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
        if (standingHeight - controller.height <= 0f)
            return true;

        int count = headroomVolume != null ? OverlapHeadroomVolume() : OverlapStandingCapsule();

        for (int i = 0; i < count; i++)
        {
            // Por hierarquia, não por layer: o player está na Default, junto com a mobília.
            if (!headroomHits[i].transform.IsChildOf(transform))
                return false;
        }

        return true;
    }

    private int OverlapHeadroomVolume()
    {
        Transform volume = headroomVolume.transform;

        // Ligado (trigger ou não), a física já tem a forma de verdade, com a escala e rotação do FBX.
        if (headroomVolume.enabled && headroomVolume.gameObject.activeInHierarchy)
        {
            Bounds bounds = headroomVolume.bounds;
            int found = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, headroomHits, Quaternion.identity,
                standCheckMask, QueryTriggerInteraction.Ignore);

            // A caixa do bounds pega a mais; a penetração confirma com as duas formas.
            int kept = 0;
            for (int i = 0; i < found; i++)
            {
                Collider other = headroomHits[i];

                if (other != headroomVolume && Physics.ComputePenetration(
                        headroomVolume, volume.position, volume.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out _))
                    headroomHits[kept++] = other;
            }

            return kept;
        }
        Vector3 scale = volume.lossyScale;
        scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

        // Pela forma serializada, não por bounds: bounds de collider desligado vem zerado.
        switch (headroomVolume)
        {
            case BoxCollider box:
                return Physics.OverlapBoxNonAlloc(volume.TransformPoint(box.center), Vector3.Scale(box.size, scale) * 0.5f,
                    headroomHits, volume.rotation, standCheckMask, QueryTriggerInteraction.Ignore);

            case SphereCollider sphere:
                return Physics.OverlapSphereNonAlloc(volume.TransformPoint(sphere.center),
                    sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)),
                    headroomHits, standCheckMask, QueryTriggerInteraction.Ignore);

            default:
                return 0;
        }
    }

    private int OverlapStandingCapsule()
    {
        // Em mundo: o prefab tem escala, e o CharacterController escala o raio pelo maior de X/Z.
        Vector3 scale = transform.lossyScale;
        float sizeXZ = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float sizeY = Mathf.Abs(scale.y);
        float radius = controller.radius * sizeXZ;
        Vector3 up = transform.up;

        // Da esfera do topo agachado até a do topo de pé: só o volume que a cabeça vai ocupar.
        Vector3 from = transform.TransformPoint(controller.center) + up * Mathf.Max(0f, controller.height * sizeY * 0.5f - radius);
        Vector3 to = transform.TransformPoint(standingCenter) + up * (Mathf.Max(0f, standingHeight * sizeY * 0.5f - radius) + standCheckMargin);

        // Sem a pele, a parede encostada do lado não conta como teto.
        float probe = Mathf.Max(0.01f, radius - controller.skinWidth * sizeXZ);

        return Physics.OverlapCapsuleNonAlloc(from, to, probe, headroomHits, standCheckMask, QueryTriggerInteraction.Ignore);
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

        StateChanged?.Invoke(next);
    }

    private void OnStateExit(PlayerState state)
    {
        switch (state)
        {
            // Aterrissagem não tem pé encostando em clipe nenhum, então fica no código.
            case PlayerState.Jumping:
                PlayFootstep(footstepSoundId);
                PuffDust(1f);
                break;
        }
    }

    // ----------------------------------------------------------------- animação

    /// <summary>Valor alvo de moveSpeed no blend tree para o estado atual.</summary>
    private float GetAnimMoveSpeed()
    {
        switch (CurrentState)
        {
            // Agachado usa os mesmos valores do em pé: quem troca o clipe é o parâmetro crouch.
            case PlayerState.Walking:
            case PlayerState.CrouchWalking:
                return AnimWalk;

            case PlayerState.Running:
                return AnimRun;

            // No ar mantém o valor que já estava: não há clipe de pulo, e cair para idle
            // faria o personagem "congelar" no meio do passo enquanto ainda se desloca.
            case PlayerState.Jumping:
                return animator.GetFloat(MoveSpeedHash);

            default:
                return AnimIdle;
        }
    }

    private void UpdateAnimator()
    {
        if (animator == null)
            return;

        // Todo frame, e não só na troca de estado: o SetFloat com damp só converge ao alvo
        // se for chamado continuamente com deltaTime.
        animator.SetFloat(MoveSpeedHash, GetAnimMoveSpeed(), animBlendDampTime, Time.deltaTime);

        // Sem damp: o CrouchAmount já vem suavizado do UpdateCrouch.
        animator.SetFloat(CrouchHash, crouchAmount);

        // Sem damp: inverter o sentido de playback não salta de pose, e suavizar passaria
        // por zero — a perna congelaria no meio da troca. No ar mantém o sentido que já
        // estava, pelo mesmo motivo do moveSpeed.
        if (CurrentState != PlayerState.Jumping)
            animator.SetFloat(AnimDirectionHash, IsMovingBackward ? -1f : 1f);
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

    // ---------------------------------------------------------------- footsteps

    /// <summary>
    /// Entrada do Animation Event, via <see cref="PlayerFootsteps"/>. O ritmo vem do pé
    /// encostar no clipe; aqui só se decide se o passo vale e com que volume.
    /// </summary>
    public void OnAnimationFootstep(AnimationEvent evt)
    {
        if (evt.animatorClipInfo.weight < footstepMinClipWeight)
            return;

        switch (CurrentState)
        {
            case PlayerState.Walking:
                PlayFootstep(footstepSoundId);
                PuffDust(0f);
                break;

            case PlayerState.Running:
                PlayFootstep(footstepSoundId);
                PuffDust(1f);
                break;

            // Agachado é o mesmo passo abafado, e sem poeira: é o andar furtivo.
            case PlayerState.CrouchWalking:
                PlayFootstep(footstepSoundId, crouchStepVolume);
                break;

            // No ar o blend continua ciclando (ver GetAnimMoveSpeed) e o clipe segue
            // disparando eventos; sem pé no chão não há passo.
        }
    }

    private void PlayFootstep(string soundId, float volumeScale = 1f)
    {
        if (string.IsNullOrEmpty(soundId))
            return;

        AudioProvider.PlayAt(soundId, transform.position + Vector3.up * footstepHeightOffset, volumeScale);
    }

    private void PuffDust(float intensity)
    {
        if (stepDust == null)
            return;

        // XZ do pé de apoio (o mais baixo), Y da base da cápsula: o osso é o tornozelo, e a
        // baforada saindo dali flutuaria acima do piso.
        Vector3 point = transform.position;
        if (leftFoot != null && rightFoot != null)
            point = leftFoot.position.y < rightFoot.position.y ? leftFoot.position : rightFoot.position;

        point.y = transform.TransformPoint(controller.center + Vector3.down * (controller.height * 0.5f)).y;

        stepDust.Puff(point, intensity);
    }

    // Pelo collider, não só pelo nome: o rig pode ter um osso "Head" sem collider.
    private Collider FindHeadroomVolume()
    {
        foreach (Collider candidate in GetComponentsInChildren<Collider>(true))
        {
            if (candidate.name == headroomVolumeName && candidate != controller)
                return candidate;
        }

        return null;
    }

    private static Transform FindDeep(Transform parent, string boneName)
    {
        if (parent.name == boneName)
            return parent;

        foreach (Transform child in parent)
        {
            Transform found = FindDeep(child, boneName);
            if (found != null)
                return found;
        }

        return null;
    }
}
