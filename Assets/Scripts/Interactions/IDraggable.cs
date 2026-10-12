using UnityEngine;

public interface IDraggable
{
    Vector3 GrabPoint { get; }

    InteractionResult BeginDrag(InteractionController interactor, Vector3 grabPoint);

    void Drag(Vector3 handPosition);

    void EndDrag();
}
