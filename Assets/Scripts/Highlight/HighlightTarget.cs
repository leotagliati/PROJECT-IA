using UnityEngine;

public class HighlightTarget : MonoBehaviour
{
    [HideInInspector] public int originalLayer;

    void Awake()
    {
        originalLayer = gameObject.layer;
    }
}
