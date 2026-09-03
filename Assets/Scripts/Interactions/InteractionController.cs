using UnityEngine;

public class InteractionController : MonoBehaviour
{
    [SerializeField] private float raycastDistance = 10f;
    [SerializeField] private string outlineLayerName = "Outline";

    private int outlineLayer;
    private GameObject lastHighlightedObject;

    // --- Adicionado para Interação ---
    private InputSystem_Actions playerInput;
    private Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        playerInput = new InputSystem_Actions();
        playerInput.Player.Interact.performed += ctx => OnInteractPressed();
    }

    void OnEnable()
    {
        playerInput.Player.Enable();
    }

    void OnDisable()
    {
        playerInput.Player.Disable();
    }
    // ---------------------------------

    void Start()
    {
        outlineLayer = LayerMask.NameToLayer(outlineLayerName);
    }

    void Update()
    {
        HighlightRaycastCheck();
    }

    void HighlightRaycastCheck()
    {
        Ray ray = cam.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

        if (Physics.Raycast(ray, out RaycastHit hit, raycastDistance))
        {
            if (hit.collider.TryGetComponent(out HighlightTarget target))
            {
                GameObject targetObject = target.gameObject;
                if (lastHighlightedObject != targetObject)
                {
                    ClearHighlight();
                    targetObject.layer = outlineLayer;
                    lastHighlightedObject = targetObject;
                }
                return;
            }
        }
        ClearHighlight();
    }

    void ClearHighlight()
    {
        if (lastHighlightedObject != null)
        {
            if (lastHighlightedObject.TryGetComponent(out HighlightTarget target))
            {
                lastHighlightedObject.layer = target.originalLayer;
            }
            lastHighlightedObject = null;
        }
    }

    private void OnInteractPressed()
    {
        if (lastHighlightedObject != null)
        {
            if (lastHighlightedObject.TryGetComponent(out IInteractable interactable))
            {
                interactable.Interact();
            }
        }
    }
}