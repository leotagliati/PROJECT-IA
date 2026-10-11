using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class InteractionController : MonoBehaviour
{
    [Tooltip("Alcance do raycast: outline e o ponto de 'tem algo ali'.")]
    [SerializeField] private float raycastDistance = 10f;

    [Tooltip("Alcance para interagir de fato.")]
    [SerializeField] private float interactDistance = 2.5f;

    [SerializeField] private LayerMask blockingMask;

    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

    [Header("Arrasto (portas)")]
    [Tooltip("Metros de mão por unidade de delta do mouse. Sobe se a porta parecer pesada.")]
    [SerializeField] private float dragSensitivity = 0.003f;

    [Tooltip("Solta o objeto quando o ponto agarrado fica mais longe que isto da câmera.")]
    [SerializeField] private float dragReleaseDistance = 3f;

    private readonly RaycastHit[] hits = new RaycastHit[16];
    private static readonly IComparer<RaycastHit> byDistance = new HitDistanceComparer();

    private Camera cam;
    private HighlightTarget currentHighlightTarget;
    private IInteractable currentInteractable;
    private Vector3 currentHitPoint;
    private float currentHitDistance;
    private IDraggable activeDrag;

    private InteractionAnchor targetAnchor;
    private Renderer[] targetRenderers;

    private IInteractable holdTarget;
    private float holdElapsed;
    private float holdDuration;

    public IInteractable CurrentInteractable => currentInteractable;

    public bool HasPrompt { get; private set; }

    public InteractionPrompt CurrentPrompt { get; private set; }

    public bool IsInRange => currentInteractable != null && currentHitDistance <= interactDistance;

    public Vector3 TargetAnchor { get; private set; }

    public bool IsDragging => activeDrag != null;

    public bool IsHolding => holdTarget != null;

    public float HoldProgress => holdTarget != null && holdDuration > 0f ? Mathf.Clamp01(holdElapsed / holdDuration) : 0f;

    public event Action<IInteractable> TargetChanged;

    public event Action<IInteractable, string> InteractionDenied;

    public event Action<IInteractable> InteractionCompleted;

    public PlayerInventory Inventory { get; private set; }

    public Camera ViewCamera => cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        Inventory = GetComponentInParent<PlayerInventory>();

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
        CancelHold();
        EndDrag();
        SetHighlightTarget(null);
        SetInteractable(null);
    }

    void Update()
    {
        if (activeDrag != null)
        {
            UpdateDrag();
            RefreshPrompt();
            return;
        }

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        HighlightTarget highlight = null;
        IInteractable interactable = null;

        int count = Physics.RaycastNonAlloc(ray, hits, raycastDistance, ~0, triggerInteraction);
        Array.Sort(hits, 0, count, byDistance);

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
                currentHitDistance = hits[i].distance;
                break;
            }

            if ((blockingMask.value & (1 << col.gameObject.layer)) != 0)
                break;
        }

        SetInteractable(interactable);
        SetHighlightTarget(highlight);
        RefreshPrompt();
        UpdateHold();
    }

    private void RefreshPrompt()
    {
        if (currentInteractable == null || IsDestroyed(currentInteractable))
        {
            HasPrompt = false;
            return;
        }

        HasPrompt = currentInteractable.TryGetPrompt(this, out InteractionPrompt prompt);
        CurrentPrompt = prompt;
        TargetAnchor = ResolveAnchor();
    }

    private Vector3 ResolveAnchor()
    {
        if (targetAnchor != null)
            return targetAnchor.Position;

        if (targetRenderers == null || targetRenderers.Length == 0)
            return currentHitPoint;

        Bounds bounds = targetRenderers[0].bounds;
        for (int i = 1; i < targetRenderers.Length; i++)
            bounds.Encapsulate(targetRenderers[i].bounds);

        return bounds.center;
    }

    private void SetInteractable(IInteractable target)
    {
        // ReferenceEquals: o == do UnityEngine.Object não entra por interface.
        if (ReferenceEquals(currentInteractable, target))
            return;

        CancelHold();

        currentInteractable = target;

        var component = target as Component;
        targetAnchor = component != null ? component.GetComponentInChildren<InteractionAnchor>() : null;
        targetRenderers = component != null && targetAnchor == null ? component.GetComponentsInChildren<Renderer>() : null;

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
        if (!HasPrompt || !IsInRange || activeDrag != null || holdTarget != null)
            return;

        IInteractable target = currentInteractable;
        InteractionPrompt prompt = CurrentPrompt;

        // No controle não há delta de mouse para arrastar: o aperto vira uso comum.
        bool fromGamepad = ctx.control != null && ctx.control.device is Gamepad;
        InteractionKind kind = prompt.Kind;
        if (kind == InteractionKind.Drag && (fromGamepad || !(target is IDraggable)))
            kind = InteractionKind.Instant;

        if (!prompt.Available)
        {
            Report(target, target.Interact(this), prompt.BlockedReason);
            return;
        }

        switch (kind)
        {
            case InteractionKind.Hold:
                BeginHold(target, prompt.HoldDuration);
                break;

            case InteractionKind.Drag:
                var draggable = (IDraggable)target;
                InteractionResult result = draggable.BeginDrag(this, currentHitPoint);
                if (result.Succeeded)
                    activeDrag = draggable;
                else
                    Report(target, result, prompt.BlockedReason);
                break;

            default:
                Report(target, target.Interact(this), prompt.BlockedReason);
                break;
        }
    }

    private void OnInteractCanceled(InputAction.CallbackContext ctx)
    {
        CancelHold();
        EndDrag();
    }

    private void Report(IInteractable target, InteractionResult result, string fallbackReason)
    {
        if (result.Succeeded)
            InteractionCompleted?.Invoke(target);
        else
            InteractionDenied?.Invoke(target, string.IsNullOrEmpty(result.Message) ? fallbackReason : result.Message);
    }

    private void BeginHold(IInteractable target, float duration)
    {
        if (duration <= 0f)
        {
            Report(target, target.Interact(this), null);
            return;
        }

        holdTarget = target;
        holdDuration = duration;
        holdElapsed = 0f;

        if (target is IHoldFeedback feedback)
            feedback.OnHoldStarted();
    }

    private void UpdateHold()
    {
        if (holdTarget == null)
            return;

        if (!ReferenceEquals(holdTarget, currentInteractable) || !IsInRange || !HasPrompt || !CurrentPrompt.Available)
        {
            CancelHold();
            return;
        }

        holdElapsed += Time.deltaTime;
        if (holdElapsed < holdDuration)
            return;

        IInteractable target = holdTarget;
        holdTarget = null;
        Report(target, target.Interact(this), CurrentPrompt.BlockedReason);
    }

    private void CancelHold()
    {
        if (holdTarget == null)
            return;

        IInteractable target = holdTarget;
        holdTarget = null;

        if (target is IHoldFeedback feedback && !IsDestroyed(target))
            feedback.OnHoldCanceled();
    }

    private void UpdateDrag()
    {
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

        Vector2 delta = PlayerInputProvider.Player.Look.ReadValue<Vector2>();
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized;

        activeDrag.Drag((right * delta.x + forward * delta.y) * dragSensitivity);
    }

    private void EndDrag()
    {
        if (activeDrag == null)
            return;

        if (!IsDestroyed(activeDrag))
            activeDrag.EndDrag();

        activeDrag = null;
    }

    private static bool IsDestroyed(object target)
    {
        return target is UnityEngine.Object obj && obj == null;
    }

    private sealed class HitDistanceComparer : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
