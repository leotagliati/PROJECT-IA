using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class InteractionController : MonoBehaviour
{
    [SerializeField] private float raycastDistance = 10f;

    [SerializeField] private LayerMask blockingMask;

    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

    private readonly RaycastHit[] hits = new RaycastHit[16];
    private static readonly IComparer<RaycastHit> byDistance = new HitDistanceComparer();

    private Camera cam;
    private HighlightTarget currentHighlightTarget;
    private IInteractable currentInteractable;

    public IInteractable CurrentInteractable => currentInteractable;

    /// <summary>
    /// Disparado só quando o alvo na mira troca (null = saiu de todos). Quem mostra prompt
    /// assina isso em vez de comparar CurrentInteractable todo frame.
    /// </summary>
    public event Action<IInteractable> TargetChanged;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

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
    }

    void OnDisable()
    {
        PlayerInputProvider.Player.Interact.performed -= OnInteractPerformed;
        PlayerInputProvider.Release();
        SetHighlightTarget(null);
        SetInteractable(null);
    }

    void Update()
    {
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
                break;

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
        currentInteractable?.Interact();
    }

    private sealed class HitDistanceComparer : IComparer<RaycastHit>
    {
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
