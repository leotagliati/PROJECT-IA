using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Legado: porta animada que abre quando não sobra <see cref="DoorLock"/>. Portas novas são <see cref="SwingDoor"/> + <see cref="Lockable"/>.
/// </summary>
[Obsolete("Use SwingDoor + Lockable instead.")]
public class LockedDoor : MonoBehaviour, IInteractable
{
    [SerializeField] private List<DoorLock> locks = new List<DoorLock>();

    [Header("Prompt")]
    [SerializeField] private string openPrompt = "Abrir";

    [Tooltip("{0} = cadeados restantes.")]
    [SerializeField] private string lockedMessageFormat = "Ainda há {0} cadeado(s)";

    [Header("Áudio")]
    [SerializeField] private string lockedSoundId = "lockedDoor";

    [Header("Animação")]
    [SerializeField] private Animator doorAnimator;

    private bool isOpen;

    private static readonly int OpenedHash = Animator.StringToHash("Opened");

    public int RemainingLocks => locks.Count;

    public bool IsUnlocked => locks.Count == 0;

    void Awake()
    {
        locks.RemoveAll(l => l == null || l.IsUnlocked);

        if (doorAnimator == null)
            doorAnimator = GetComponentInChildren<Animator>();
    }

    void OnEnable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked += HandleLockUnlocked;
    }

    void OnDisable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked -= HandleLockUnlocked;
    }

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt prompt)
    {
        prompt = InteractionPrompt.Instant(openPrompt);

        if (isOpen)
            return false;

        if (!IsUnlocked)
            prompt = prompt.Blocked(string.Format(lockedMessageFormat, locks.Count));

        return true;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        if (isOpen)
            return InteractionResult.Success;

        if (!IsUnlocked)
        {
            if (!string.IsNullOrEmpty(lockedSoundId))
                AudioProvider.PlayAt(lockedSoundId, transform.position);

            return InteractionResult.Fail(string.Format(lockedMessageFormat, locks.Count));
        }

        isOpen = true;
        if (doorAnimator != null)
            doorAnimator.SetTrigger(OpenedHash);

        return InteractionResult.Success;
    }

    private void HandleLockUnlocked(DoorLock doorLock)
    {
        doorLock.Unlocked -= HandleLockUnlocked;
        locks.Remove(doorLock);
    }
}
