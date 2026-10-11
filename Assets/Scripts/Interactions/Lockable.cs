using System.Collections.Generic;
using UnityEngine;

public class Lockable : MonoBehaviour
{
    [Tooltip("Vazio = destrancado.")]
    [SerializeField] private List<DoorLock> locks = new List<DoorLock>();

    [Tooltip("{0} = cadeados restantes.")]
    [SerializeField] private string lockedMessageFormat = "Ainda há {0} cadeado(s)";

    [Tooltip("Toca ao tentar usar trancado. Vazio = sem som.")]
    [SerializeField] private string lockedSoundId = "lockedDoor";

    public int RemainingLocks => locks.Count;

    public bool IsLocked => locks.Count > 0;

    public string LockedMessage => IsLocked ? string.Format(lockedMessageFormat, locks.Count) : null;

    private void Awake()
    {
        locks.RemoveAll(l => l == null || l.IsUnlocked);
    }

    private void OnEnable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked += HandleLockUnlocked;
    }

    private void OnDisable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked -= HandleLockUnlocked;
    }

    /// <summary>Falha com som e mensagem se ainda trancado.</summary>
    public bool TryRefuse(out InteractionResult result)
    {
        result = InteractionResult.Success;

        if (!IsLocked)
            return false;

        if (!string.IsNullOrEmpty(lockedSoundId))
            AudioProvider.PlayAt(lockedSoundId, transform.position);

        result = InteractionResult.Fail(LockedMessage);
        return true;
    }

    private void HandleLockUnlocked(DoorLock doorLock)
    {
        doorLock.Unlocked -= HandleLockUnlocked;
        locks.Remove(doorLock);
    }
}
