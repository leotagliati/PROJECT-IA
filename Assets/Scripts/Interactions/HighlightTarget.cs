using UnityEngine;

public class HighlightTarget : MonoBehaviour
{
    [HideInInspector] public int originalLayer;

    protected virtual void Awake()
    {
        originalLayer = gameObject.layer;
    }

    public virtual bool CanHighlight()
    {
        return true;
    }
}
