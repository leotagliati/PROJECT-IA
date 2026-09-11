using UnityEngine;

public class InteractionController : MonoBehaviour
{
    [SerializeField] private float raycastDistance = 10f;
    [SerializeField] private string outlineLayerName = "Outline";

    private int outlineLayer;
    private Camera cam;
    private InputSystem_Actions playerInput;

    // Estados separados: visual vs funcionalidade
    private HighlightTarget currentHighlightTarget;
    private IInteractable currentInteractable;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        playerInput = new InputSystem_Actions();
        playerInput.Player.Interact.performed += ctx => OnInteractPressed();
    }

    void OnEnable() => playerInput.Player.Enable();
    void OnDisable() => playerInput.Player.Disable();

    void Start()
    {
        outlineLayer = LayerMask.NameToLayer(outlineLayerName);
    }

    void Update()
    {
        PerformRaycast();
    }

    void PerformRaycast()
    {
        Ray ray = cam.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

        if (Physics.Raycast(ray, out RaycastHit hit, raycastDistance))
        {
            // Lógica da interação
            currentInteractable = hit.collider.GetComponentInParent<IInteractable>();

            // Lógica do contorno
            if (hit.collider.TryGetComponent(out HighlightTarget target) && target.CanHighlight())
            {
                if (currentHighlightTarget != target)
                {
                    ClearHighlight();
                    target.gameObject.layer = outlineLayer;
                    currentHighlightTarget = target;
                }
                return;
            }
        }
        else
        {
            currentInteractable = null;
        }

        ClearHighlight();
    }

    void ClearHighlight()
    {
        if (currentHighlightTarget != null)
        {
            currentHighlightTarget.gameObject.layer = currentHighlightTarget.originalLayer;
            currentHighlightTarget = null;
        }
    }

    private void OnInteractPressed()
    {
        if (currentInteractable != null)
        {
            currentInteractable.Interact();
        }
    }
}