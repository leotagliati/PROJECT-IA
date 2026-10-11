using UnityEngine;

public class InteractionAnchor : MonoBehaviour
{
    [SerializeField] private Vector3 offset;
    public Vector3 Position => transform.TransformPoint(offset);

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(Position, 0.05f);
    }
#endif
}
