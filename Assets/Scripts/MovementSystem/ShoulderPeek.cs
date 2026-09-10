using UnityEngine;
using UnityEngine.InputSystem;

public enum PeekMode
{
    Lean,
    LookBack
}
[DefaultExecutionOrder(50)]
public class ShoulderPeek : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    [SerializeField] private PlayerMovement movement;

    [Header("Look Back Settings")]
    [SerializeField, Range(0f, 180f)] private float lookBackAngle = 135f;
    [SerializeField] private float lookBackRoll = 2.5f;
    [SerializeField] private float lookBackEnterTime = 0.11f;
    [SerializeField] private float lookBackExitTime = 0.16f;

    [Header("Lean Settings")]
    [SerializeField] private float leanDistance = 0.45f;
    [SerializeField] private float leanRoll = 12f;
    [SerializeField] private float leanYaw = 6f;
    [SerializeField] private float leanEnterTime = 0.13f;
    [SerializeField] private float leanExitTime = 0.18f;

    [Header("Lean Collision")]
    [Tooltip("Impede a câmera de atravessar parede ao inclinar.")]
    [SerializeField] private bool probeLeanCollision = true;

    [SerializeField] private LayerMask leanCollisionMask = ~0;

    [SerializeField] private float leanProbeRadius = 0.15f;

    [Tooltip("Folga mantida da parede.")]
    [SerializeField] private float leanSkin = 0.08f;

    [Tooltip("Input para frente acima disso conta como 'correndo para frente'.")]
    [SerializeField] private float movementThreshold = 0.1f;

    [Tooltip("Quanto tempo o movimento precisa contrariar o modo travado antes do peek ser cancelado.")]
    [SerializeField] private float modeSwitchDelay = 0.2f;

    private Transform cameraTransform;

    // -1 = esquerda, 0 = nenhum, +1 = direita. Quem chegou primeiro manda.
    private int activeSide;

    // Índice 0 = esquerda, 1 = direita.
    private readonly bool[] held = new bool[2];

    // Tecla que já gastou o peek dela: só volta a valer depois de soltar e apertar de novo.
    private readonly bool[] blocked = new bool[2];

    private PeekMode currentMode = PeekMode.Lean;
    private float modeTimer;

    private float currentYaw, yawVelocity;
    private float currentLean, leanVelocity;
    private float currentRoll, rollVelocity;

    /// <summary>Giro atual em graus. Negativo = ombro esquerdo.</summary>
    public float PeekAngle => currentYaw;

    /// <summary>Deslocamento lateral atual em metros. Negativo = esquerda.</summary>
    public float LeanOffset => currentLean;

    /// <summary>Modo ativo agora. Animator e IA podem reagir a isso.</summary>
    public PeekMode CurrentMode => currentMode;

    /// <summary>-1, 0 ou +1. Zero quando não está espiando.</summary>
    public int ActiveSide => activeSide;

    private void Awake()
    {
        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();

        var player = PlayerInputProvider.Player;

        player.PeekLeft.started += OnPeekLeftStarted;
        player.PeekLeft.canceled += OnPeekLeftCanceled;
        player.PeekRight.started += OnPeekRightStarted;
        player.PeekRight.canceled += OnPeekRightCanceled;

        // Tecla já segurada quando o componente liga não gera 'started', então ela entra
        // travada: o peek começa no aperto seguinte, não no meio de um hold antigo.
        SyncHeldOnEnable(0, player.PeekLeft.IsPressed());
        SyncHeldOnEnable(1, player.PeekRight.IsPressed());
    }

    private void OnDisable()
    {
        var player = PlayerInputProvider.Player;

        player.PeekLeft.started -= OnPeekLeftStarted;
        player.PeekLeft.canceled -= OnPeekLeftCanceled;
        player.PeekRight.started -= OnPeekRightStarted;
        player.PeekRight.canceled -= OnPeekRightCanceled;

        PlayerInputProvider.Release();

        activeSide = 0;
        modeTimer = 0f;
    }

    private void SyncHeldOnEnable(int index, bool pressed)
    {
        held[index] = pressed;
        blocked[index] = pressed;
    }

    private void Start()
    {
        if (targetCamera == null)
        {
            Debug.LogError($"{nameof(ShoulderPeek)}: nenhuma câmera encontrada.", this);
            enabled = false;
            return;
        }

        cameraTransform = targetCamera.transform;
    }

    private void LateUpdate()
    {
        UpdateModeWatchdog();
        UpdateTargets();
        ApplyToCamera();
    }

    // ------------------------------------------------------------------- input

    private void OnPeekLeftStarted(InputAction.CallbackContext _) => BeginPeek(0);

    private void OnPeekRightStarted(InputAction.CallbackContext _) => BeginPeek(1);

    private void OnPeekLeftCanceled(InputAction.CallbackContext _) => EndPeek(0);

    private void OnPeekRightCanceled(InputAction.CallbackContext _) => EndPeek(1);

    /// <summary>
    /// Trava o lado na primeira tecla apertada: enquanto Q estiver segurado, E é ignorado
    /// por completo, e vice-versa. O modo também é decidido aqui, no aperto, e não muda
    /// mais até o peek acabar.
    /// </summary>
    private void BeginPeek(int index)
    {
        held[index] = true;

        // O dono do peek continua sendo quem chegou primeiro.
        if (activeSide != 0 || blocked[index])
            return;

        activeSide = SideOf(index);
        currentMode = IsRunningForward() ? PeekMode.LookBack : PeekMode.Lean;
        modeTimer = 0f;
    }

    /// <summary>
    /// Soltar sempre destrava a tecla. Se quem soltou era o dono do peek e a outra tecla
    /// ainda está segurada e válida, ela assume — segurar uma tecla e não ter resposta ao
    /// soltar a outra seria mais estranho do que a troca.
    /// </summary>
    private void EndPeek(int index)
    {
        held[index] = false;
        blocked[index] = false;

        if (activeSide != SideOf(index))
            return;

        activeSide = 0;
        modeTimer = 0f;

        int other = 1 - index;

        if (held[other] && !blocked[other])
            BeginPeek(other);
    }

    /// <summary>
    /// Cancela o peek quando o estado de movimento deixa de casar com o modo travado no
    /// aperto — correr, espiar por cima do ombro e parar não vira uma inclinada; vira o
    /// fim da espiada. A tecla ainda segurada fica travada até ser solta, senão o peek
    /// voltaria sozinho no frame seguinte, no outro modo, que é exatamente o vai-e-vem
    /// que se quer evitar.
    ///
    /// O atraso existe porque soltar o W por um instante no meio de uma fuga não deveria
    /// custar a espiada: só um estado de movimento que se mantém contrário cancela.
    /// </summary>
    private void UpdateModeWatchdog()
    {
        if (activeSide == 0)
        {
            modeTimer = 0f;
            return;
        }

        PeekMode desired = IsRunningForward() ? PeekMode.LookBack : PeekMode.Lean;

        if (desired == currentMode)
        {
            modeTimer = 0f;
            return;
        }

        modeTimer += Time.deltaTime;

        if (modeTimer < modeSwitchDelay)
            return;

        CancelPeek();
    }

    /// <summary>
    /// Encerra a espiada em curso e trava toda tecla de peek ainda segurada. O modo fica
    /// como estava, para a volta da câmera usar o tempo de saída do modo certo.
    /// </summary>
    private void CancelPeek()
    {
        activeSide = 0;
        modeTimer = 0f;

        for (int i = 0; i < held.Length; i++)
        {
            if (held[i])
                blocked[i] = true;
        }
    }

    private static int SideOf(int index) => index == 0 ? -1 : 1;

    private void UpdateTargets()
    {
        float side = activeSide;
        bool peeking = activeSide != 0;

        float targetYaw = 0f;
        float targetLean = 0f;
        float targetRoll = 0f;
        float smoothTime;

        if (!peeking)
        {
            // Saindo: usa o tempo de volta do modo em que estava.
            smoothTime = currentMode == PeekMode.LookBack ? lookBackExitTime : leanExitTime;
        }
        else if (currentMode == PeekMode.LookBack)
        {
            targetYaw = side * lookBackAngle;
            targetRoll = -side * lookBackRoll;
            smoothTime = lookBackEnterTime;
        }
        else
        {
            targetLean = ResolveLeanDistance(side);
            targetYaw = side * leanYaw;
            targetRoll = -side * leanRoll;
            smoothTime = leanEnterTime;
        }

        currentYaw = Mathf.SmoothDamp(currentYaw, targetYaw, ref yawVelocity, smoothTime);
        currentLean = Mathf.SmoothDamp(currentLean, targetLean, ref leanVelocity, smoothTime);
        currentRoll = Mathf.SmoothDamp(currentRoll, targetRoll, ref rollVelocity, smoothTime);
    }

    /// <summary>
    /// Sonda o espaço ao lado antes de deslocar a câmera. Sem isso, inclinar encostado
    /// numa parede põe a câmera dentro dela e mostra o outro lado do cenário.
    /// </summary>
    private float ResolveLeanDistance(float side)
    {
        if (!probeLeanCollision)
            return side * leanDistance;

        Vector3 direction = transform.right * Mathf.Sign(side);

        // Tira a própria layer do player da conta: a sonda nasce dentro do corpo dele.
        int mask = leanCollisionMask & ~(1 << gameObject.layer);

        bool blocked = Physics.SphereCast(
            cameraTransform.position,
            leanProbeRadius,
            direction,
            out RaycastHit hit,
            leanDistance + leanProbeRadius,
            mask,
            QueryTriggerInteraction.Ignore);

        if (!blocked)
            return side * leanDistance;

        float allowed = Mathf.Clamp(hit.distance - leanSkin, 0f, leanDistance);
        return side * allowed;
    }

    private void ApplyToCamera()
    {
        if (Mathf.Abs(currentYaw) < 0.01f &&
            Mathf.Abs(currentLean) < 0.001f &&
            Mathf.Abs(currentRoll) < 0.01f)
            return;

        // O CameraJuice reatribui localPosition inteiro todo frame (bob + dip), então
        // somar aqui não acumula. Vector3.right é o lado do player: a câmera só carrega
        // pitch, o yaw mora no transform raiz.
        cameraTransform.localPosition += Vector3.right * currentLean;

        // Yaw pré-multiplicado: entra no espaço do player, antes do pitch. Pós-multiplicado
        // ele giraria no espaço já inclinado e, olhando para cima, o "olhar para trás"
        // sairia torto em vez de horizontal. O roll fica por último, sobre o que o
        // CameraJuice já aplicou, e segue a convenção dele (direita = Z negativo).
        cameraTransform.localRotation =
            Quaternion.Euler(0f, currentYaw, 0f) *
            cameraTransform.localRotation *
            Quaternion.Euler(0f, 0f, currentRoll);
    }

    /// <summary>
    /// Só correndo para frente. Olhar por cima do ombro é o gesto de quem está fugindo:
    /// andando, parado ou correndo de lado o certo é inclinar, que deixa a mira e o rumo
    /// intactos. A diagonal (W+D correndo) ainda conta como frente — o eixo lateral só
    /// desqualifica quando manda mais que o de frente.
    /// </summary>
    private bool IsRunningForward()
    {
        if (movement == null || !movement.SprintHeld)
            return false;

        Vector2 input = movement.MoveInput;

        return input.y > movementThreshold && input.y >= Mathf.Abs(input.x);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        lookBackEnterTime = Mathf.Max(0.01f, lookBackEnterTime);
        lookBackExitTime = Mathf.Max(0.01f, lookBackExitTime);
        leanEnterTime = Mathf.Max(0.01f, leanEnterTime);
        leanExitTime = Mathf.Max(0.01f, leanExitTime);

        leanDistance = Mathf.Max(0f, leanDistance);
        leanProbeRadius = Mathf.Max(0.01f, leanProbeRadius);
        leanSkin = Mathf.Max(0f, leanSkin);
        modeSwitchDelay = Mathf.Max(0f, modeSwitchDelay);
        movementThreshold = Mathf.Max(0.001f, movementThreshold);
    }
#endif
}
