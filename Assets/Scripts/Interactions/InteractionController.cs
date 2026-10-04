using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class InteractionController : MonoBehaviour
{
    [SerializeField] private float raycastDistance = 10f;

    [SerializeField] private LayerMask blockingMask;

    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

    [Header("Arrasto (portas)")]
    [Tooltip("Metros de mão por unidade de delta do mouse. Sobe se a porta parecer pesada.")]
    [SerializeField] private float dragSensitivity = 0.003f;

    [Tooltip("Solta o objeto quando o ponto agarrado fica mais longe que isto da câmera.")]
    [SerializeField] private float dragReleaseDistance = 3f;

    [Tooltip("Congela a mira durante o arrasto: o mouse passa a mover só o objeto.")]
    [SerializeField] private bool blockLookWhileDragging = true;

    private readonly RaycastHit[] hits = new RaycastHit[16];
    private static readonly IComparer<RaycastHit> byDistance = new HitDistanceComparer();

    private Camera cam;
    private HighlightTarget currentHighlightTarget;
    private IInteractable currentInteractable;
    private Vector3 currentHitPoint;
    private IDraggable activeDrag;
    private PlayerCamera playerCamera;

    public IInteractable CurrentInteractable => currentInteractable;

    public bool IsDragging => activeDrag != null;

    public event Action<IInteractable> TargetChanged;

    public event Action<string> InteractionFailed;

    /// <summary>
    /// Inventário do jogador dono deste controller. Interagíveis leem daqui em vez de
    /// procurar na cena — em multiplayer local ou teste com dois players, é o do jogador
    /// certo. Pode ser null: nem toda cena de teste tem inventário.
    /// </summary>
    public PlayerInventory Inventory { get; private set; }

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        // O controller mora na câmera e o inventário na raiz do player: sobe a hierarquia.
        Inventory = GetComponentInParent<PlayerInventory>();

        playerCamera = GetComponentInParent<PlayerCamera>();
        if (playerCamera == null)
            playerCamera = transform.root.GetComponentInChildren<PlayerCamera>();

        if (blockingMask.value == 0)
            blockingMask = LayerMask.GetMask("Wall");
    }

    void Reset()
    {
        blockingMask = LayerMask.GetMask("Wall");
    }

    void OnEnable()
    {
        PlayerInputProvider.Acquire();
        PlayerInputProvider.Player.Interact.performed += OnInteractPerformed;
        PlayerInputProvider.Player.Interact.canceled += OnInteractCanceled;
    }

    void OnDisable()
    {
        PlayerInputProvider.Player.Interact.performed -= OnInteractPerformed;
        PlayerInputProvider.Player.Interact.canceled -= OnInteractCanceled;
        PlayerInputProvider.Release();
        EndDrag();
        SetHighlightTarget(null);
        SetInteractable(null);
    }

    void Update()
    {
        // Durante o arrasto o alvo fica preso ao objeto agarrado: o raycast sairia dele assim
        // que a porta gira, e o prompt/outline piscariam no meio do movimento.
        if (activeDrag != null)
        {
            UpdateDrag();
            return;
        }

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        HighlightTarget highlight = null;
        IInteractable interactable = null;

        int count = Physics.RaycastNonAlloc(ray, hits, raycastDistance, ~0, triggerInteraction);
        System.Array.Sort(hits, 0, count, byDistance);

        for (int i = 0; i < count; i++)
        {
            Collider col = hits[i].collider;

            interactable = col.GetComponentInParent<IInteractable>();
            highlight = col.GetComponentInParent<HighlightTarget>();

            if (highlight != null && !highlight.CanHighlight())
                highlight = null;

            if (interactable != null || highlight != null)
            {
                currentHitPoint = hits[i].point;
                break;
            }

            if ((blockingMask.value & (1 << col.gameObject.layer)) != 0)
                break;
        }

        SetInteractable(interactable);
        SetHighlightTarget(highlight);
    }

    private void SetInteractable(IInteractable target)
    {
        // ReferenceEquals, e não ==: o alvo é interface, e o == sobrecarregado do
        // UnityEngine.Object não entra por esse tipo. Objeto destruído enquanto está na mira
        // vira um raycast que já não o acha, então a troca para null acontece naturalmente.
        if (ReferenceEquals(currentInteractable, target))
            return;

        currentInteractable = target;
        TargetChanged?.Invoke(target);
    }

    private void SetHighlightTarget(HighlightTarget target)
    {
        if (currentHighlightTarget == target)
            return;

        if (currentHighlightTarget != null)
            currentHighlightTarget.SetHighlighted(false);

        currentHighlightTarget = target;

        if (currentHighlightTarget != null)
            currentHighlightTarget.SetHighlighted(true);
    }

    private void OnInteractPerformed(InputAction.CallbackContext ctx)
    {
        if (currentInteractable == null || activeDrag != null)
            return;

        // Interact é Button sem interaction: performed no aperto, canceled ao soltar. Para um
        // arrastável, o aperto agarra e o canceled solta.
        InteractionResult result;
        if (currentInteractable is IDraggable draggable)
        {
            result = draggable.BeginDrag(this, currentHitPoint);
            if (result.Succeeded)
                StartDrag(draggable);
        }
        else
        {
            result = currentInteractable.Interact(this);
        }

        if (!result.Succeeded)
            InteractionFailed?.Invoke(result.Message);
    }

    private void OnInteractCanceled(InputAction.CallbackContext ctx) => EndDrag();

    private void StartDrag(IDraggable draggable)
    {
        activeDrag = draggable;
    }

    private void UpdateDrag()
    {
        // Objeto destruído no meio do arrasto (troca de cena, script de cutscene).
        if (IsDestroyed(activeDrag))
        {
            EndDrag();
            return;
        }

        if (Vector3.Distance(cam.transform.position, activeDrag.GrabPoint) > dragReleaseDistance)
        {
            EndDrag();
            return;
        }

        // Mouse para frente = mão para frente no plano do chão, para os lados = para os lados
        // da câmera. Delta do mouse já é por frame, então não multiplica por deltaTime.
        Vector2 delta = PlayerInputProvider.Player.Look.ReadValue<Vector2>();
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized;

        activeDrag.Drag((right * delta.x + forward * delta.y) * dragSensitivity);
    }

    // Idempotente: chamado ao soltar o botão, no OnDisable e quando o jogador se afasta.
    // Desligar o mapa de input (pausa) também dispara canceled, então pausar solta a porta.
    private void EndDrag()
    {
        if (activeDrag == null)
            return;

        if (!IsDestroyed(activeDrag))
            activeDrag.EndDrag();

        activeDrag = null;
    }

    // Mesma pegadinha do SetInteractable: por interface o == do UnityEngine.Object não entra.
    private static bool IsDestroyed(IDraggable draggable)
    {
        return draggable is UnityEngine.Object obj && obj == null;
    }

    private sealed class HitDistanceComparer : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
