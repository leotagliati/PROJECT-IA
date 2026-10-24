using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class Lockable : MonoBehaviour
{
    [Tooltip("Vazio = destrancado.")]
    [SerializeField] private List<DoorLock> locks = new List<DoorLock>();

    [Tooltip("{0} = cadeados restantes.")]
    [SerializeField] private string lockedMessageFormat = "Ainda há {0} cadeado(s)";

    [Tooltip("Toca ao tentar usar trancado. Vazio = sem som.")]
    [SerializeField] private string lockedSoundId = "lockedDoor";

    // "Trancada" seco faz o jogador caçar uma chave que não existe: quem explica é o diálogo do sealedRefused.
    [Tooltip("Depois de Seal. Vazio = o prompt fica como o de uma porta comum e só o sealedRefused responde.")]
    [SerializeField] private string sealedMessage = "";

    [Tooltip("Tentou abrir depois de Seal. Ligue o Play de um DialogueTrigger fora da porta (senão ele tapa a porta " +
             "no raycast).")]
    [SerializeField] private UnityEvent sealedRefused;

    // Na porta do começo, quem tentava o cadeado antes da porta concluía "trancada, vou procurar outro lugar", e não
    // há outro lugar: o diálogo que solta a caça tem que abrir por aqui também.
    [Tooltip("Tentou um cadeado desta porta sem chave. Ligue o Play de um DialogueTrigger, por exemplo.")]
    [SerializeField] private UnityEvent lockRefused;

    private bool isSealed;

    public bool IsSealed => isSealed;

    public int RemainingLocks => locks.Count;

    public bool IsLocked => isSealed || locks.Count > 0;

    public string LockedMessage =>
        isSealed ? sealedMessage
        : IsLocked ? string.Format(lockedMessageFormat, locks.Count)
        : null;

    private void Awake()
    {
        locks.RemoveAll(l => l == null || l.IsUnlocked);
    }

    private void OnEnable()
    {
        foreach (DoorLock doorLock in locks)
        {
            doorLock.Unlocked += HandleLockUnlocked;
            doorLock.Refused += HandleLockRefused;
        }
    }

    private void OnDisable()
    {
        foreach (DoorLock doorLock in locks)
        {
            doorLock.Unlocked -= HandleLockUnlocked;
            doorLock.Refused -= HandleLockRefused;
        }
    }

    /// <summary>Tira todos os cadeados sem chave e sem som (checkpoint restaurado no load).</summary>
    public void UnlockSilently()
    {
        // Cópia: o Unlocked de cada cadeado tira ele da lista no meio do laço.
        foreach (DoorLock doorLock in locks.ToArray())
            doorLock.UnlockSilently();
    }

    /// <summary>Tranca de vez: some com os cadeados que sobraram, senão uma chave da caça ainda abriria a porta.</summary>
    public void Seal()
    {
        isSealed = true;

        foreach (DoorLock doorLock in locks)
        {
            if (doorLock == null)
                continue;

            doorLock.Unlocked -= HandleLockUnlocked;
            doorLock.Refused -= HandleLockRefused;
            Destroy(doorLock.gameObject);
        }

        locks.Clear();
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

        if (isSealed)
            sealedRefused.Invoke();

        return true;
    }

    private void HandleLockUnlocked(DoorLock doorLock)
    {
        doorLock.Unlocked -= HandleLockUnlocked;
        doorLock.Refused -= HandleLockRefused;
        locks.Remove(doorLock);
    }

    private void HandleLockRefused(DoorLock doorLock) => lockRefused.Invoke();
}
