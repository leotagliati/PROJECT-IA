using UnityEngine;

public class ObjectHighlighter : MonoBehaviour
{
    [SerializeField] private float raycastDistance = 10f;
    [SerializeField] private string outlineLayerName = "Outline";

    private int outlineLayer;
    private GameObject lastHighlightedObject;

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
        Ray ray = Camera.main.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

        if(Physics.Raycast(ray, out RaycastHit hit, raycastDistance))
        {
            if(hit.collider.TryGetComponent(out HighlightTarget target))
            {
                GameObject targetObject = target.gameObject;
                if(lastHighlightedObject != targetObject)
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
        if(lastHighlightedObject != null)
        {
            if(lastHighlightedObject.TryGetComponent(out HighlightTarget target))
            {
                lastHighlightedObject.layer = target.originalLayer;
            }
            lastHighlightedObject = null;
        }
    }
}