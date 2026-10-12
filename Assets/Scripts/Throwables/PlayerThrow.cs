using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Segurar Throw ergue o braço (mira), soltar arremessa. O braço é uma camada Override do Animator com
/// máscara só do braço: andar/correr seguem no resto do corpo, e o SpineLook continua girando a coluna,
/// que leva o braço junto. O peso da camada é daqui, não de um estado vazio: estado vazio com Write
/// Defaults ligado trava o braço na pose de bind.
///
/// Contrato com o PlayerAC: camada <see cref="layerName"/>; estados Raise → Aim (loop) → Release;
/// trigger <see cref="releaseTrigger"/> leva Raise/Aim a Release; o clipe do Release chama o evento
/// ThrowRelease no frame em que a mão abre (<see cref="ThrowAnimationEvents"/>).
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

    [Tooltip("Filho do osso da mão (hand.R) onde o item fica.")]
    [SerializeField] private Transform handSocket;

    [Tooltip("Vazio = procura nos filhos. Dá a câmera (direção) e o estado de arrasto de porta.")]
    [SerializeField] private InteractionController interaction;

    [Header("Animator")]
    [SerializeField] private string layerName = "Throw";

    [SerializeField] private string raiseState = "Raise";

    [SerializeField] private string releaseState = "Release";

    [SerializeField] private string releaseTrigger = "throwRelease";

    [Tooltip("Peso da camada por segundo, entrando e saindo.")]
    [SerializeField] private float layerBlendSpeed = 8f;

    [Header("Arremesso")]
    [SerializeField] private float minThrowSpeed = 6f;

    [SerializeField] private float maxThrowSpeed = 14f;

    [Tooltip("Segundos de mira até a força máxima.")]
    [SerializeField] private float chargeTime = 0.8f;

    [Tooltip("Inclina a direção da câmera para cima: arremesso reto parece cair cedo demais.")]
    [SerializeField] private float upwardBias = 0.15f;

    [Tooltip("Sem o evento ThrowRelease (ou sem a camada), solta depois disto (s) do botão solto.")]
    [SerializeField] private float releaseTimeout = 0.6f;

    [Tooltip("Teto (s) para a camada começar a sair depois de soltar, se o estado Release não terminar antes.")]
    [SerializeField] private float maxRecoverTime = 1.2f;

    private Phase phase;
    private ThrowableItem held;
    private Collider[] ownColliders;
    private int layer = -1;
    private int raiseHash;
    private int releaseStateHash;
    private int releaseTriggerHash;
    private float aimStartTime;
    private float throwTime;
    private float recoverStartTime;
    private float charge;

    public bool HasItem => held != null;

    public bool IsAiming => phase == Phase.Aiming;

    /// <summary>Mirando ou no meio do arremesso: a mão está ocupada para interagir.</summary>
    public bool IsBusy => phase != Phase.Idle;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (interaction == null)
            interaction = GetComponentInChildren<InteractionController>();

        // Antes de qualquer item entrar na mão: senão os colisores dele entrariam na lista.
        ownColliders = GetComponentsInChildren<Collider>();

        raiseHash = Animator.StringToHash(raiseState);
        releaseStateHash = Animator.StringToHash(releaseState);
        releaseTriggerHash = Animator.StringToHash(releaseTrigger);
        layer = animator != null ? animator.GetLayerIndex(layerName) : -1;

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
        return true;
    }

    /// <summary>Chamado pelo evento de animação (via <see cref="ThrowAnimationEvents"/>) ou pelo timeout.</summary>
    public void ThrowRelease()
    {
        if (phase != Phase.Throwing || held == null)
            return;

        Transform aim = interaction != null && interaction.ViewCamera != null
            ? interaction.ViewCamera.transform
            : Camera.main.transform;

        Vector3 direction = (aim.forward + Vector3.up * upwardBias).normalized;
        float speed = Mathf.Lerp(minThrowSpeed, maxThrowSpeed, charge);

        held.Launch(SafeOrigin(aim.position, handSocket.position), direction * speed, ownColliders);
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
            animator.Play(raiseHash, layer, 0f);
        }
    }

    private void OnThrowCanceled(InputAction.CallbackContext ctx)
    {
        if (phase != Phase.Aiming)
            return;

        charge = chargeTime > 0f ? Mathf.Clamp01((Time.time - aimStartTime) / chargeTime) : 1f;
        phase = Phase.Throwing;
        throwTime = Time.time;

        if (layer >= 0)
            animator.SetTrigger(releaseTriggerHash);
        else
            ThrowRelease();
    }

    private void Update()
    {
        if (phase == Phase.Throwing && Time.time - throwTime >= releaseTimeout)
            ThrowRelease();

        if (phase == Phase.Recovering && RecoverDone())
            phase = Phase.Idle;

        if (layer >= 0)
        {
            float target = phase == Phase.Idle ? 0f : 1f;
            float weight = Mathf.MoveTowards(animator.GetLayerWeight(layer), target, layerBlendSpeed * Time.deltaTime);
            animator.SetLayerWeight(layer, weight);
        }
    }

    private bool RecoverDone()
    {
        if (layer < 0)
            return true;

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
        if (!animator.IsInTransition(layer) && state.shortNameHash == releaseStateHash && state.normalizedTime >= 1f)
            return true;

        return Time.time - recoverStartTime >= maxRecoverTime;
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
