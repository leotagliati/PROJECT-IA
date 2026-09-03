using UnityEngine;

public class LockedDoor : HighlightTarget, IInteractable
{
    [Header("Door Requirements")]
    [Tooltip("Amount ok keys required to enable interaction with the door.")]
    [SerializeField] private int requiredKeys = 3;

    private PlayerInventory cachedPlayerInventory;

    protected override void Awake()
    {
        base.Awake();
        cachedPlayerInventory = FindFirstObjectByType<PlayerInventory>();
    }

    public void Interact()
    {
        if (cachedPlayerInventory == null)
            cachedPlayerInventory = FindFirstObjectByType<PlayerInventory>();

        if (cachedPlayerInventory == null) return;

        if (cachedPlayerInventory.TryUseKey(requiredKeys))
        {
            OpenDoor();
        }
        else
        {
            Debug.Log($"Not enough keys! You need {requiredKeys} keys!");
        }
    }

    private void OpenDoor()
    {
        Debug.Log($"Door opened succesfully!");
    }
}