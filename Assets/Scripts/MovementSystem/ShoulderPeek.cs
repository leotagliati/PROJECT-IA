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

    [Tooltip("Input de movimento acima disso conta como 'em movimento'.")]
    [SerializeField] private float movementThreshold = 0.1f;
    [SerializeField] private float modeSwitchDelay = 0.2f;

    private PlayerInputActions playerInput;
    private Transform cameraTransform;

    // -1 = esquerda, 0 = nenhum, +1 = direita. Quem chegou primeiro manda.
    private int activeSide;

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
        playerInput = new PlayerInputActions();

        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();
    }

    private void OnEnable()
    {
        playerInput.Player.Enable();
    }

    private void OnDisable()
    {
        playerInput.Player.Disable();
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
        UpdateActiveSide();
        UpdateMode();
        UpdateTargets();
        ApplyToCamera();
    }

    /// <summary>
    /// Trava o lado na primeira tecla apertada: enquanto Q estiver segurado, E é ignorado
    /// por completo, e vice-versa. Se você soltar a primeira e a segunda ainda estiver
    /// pressionada, ela assume — segurar uma tecla e não ter resposta ao soltar a outra
    /// seria mais estranho do que a troca.
    /// </summary>
    private void UpdateActiveSide()
    {
        bool left = playerInput.Player.PeekLeft.IsPressed();
        bool right = playerInput.Player.PeekRight.IsPressed();

        // Enquanto o dono do peek continuar segurando, ninguém toma o lugar dele.
        if (activeSide < 0 && left)
            return;

        if (activeSide > 0 && right)
            return;

        if (left)
            activeSide = -1;
        else if (right)
            activeSide = 1;
        else
            activeSide = 0;
    }

    /// <summary>
    /// Escolhe entre inclinar e olhar para trás pelo estado de movimento, com um atraso
    /// de confirmação. Sem esse atraso, soltar o W por um instante no meio de uma fuga
    /// faria a câmera começar a girar de volta — ruim justamente na hora mais tensa.
    /// </summary>
    private void UpdateMode()
    {
        PeekMode desired = IsMoving() ? PeekMode.LookBack : PeekMode.Lean;

        // Fora do peek não há nada para estabilizar: já entra no modo certo, para a
        // próxima espiada não começar no modo errado e corrigir no meio.
        if (activeSide == 0)
        {
            currentMode = desired;
            modeTimer = 0f;
            return;
        }

        if (desired == currentMode)
        {
            modeTimer = 0f;
            return;
        }

        modeTimer += Time.deltaTime;

        if (modeTimer < modeSwitchDelay)
            return;

        currentMode = desired;
        modeTimer = 0f;
    }

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

    private bool IsMoving()
    {
        if (movement == null)
            return false;

        return movement.MoveInput.sqrMagnitude > movementThreshold * movementThreshold;
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
