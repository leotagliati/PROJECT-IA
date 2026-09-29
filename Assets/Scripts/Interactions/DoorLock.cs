using System;
using UnityEngine;

public class DoorLock : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Destrancar cadeado";

    [SerializeField] private string noKeyMessage = "Requer uma chave";

    [Header("Áudio")]
    [SerializeField] private string openSoundId = "padlock_open";

    public bool IsUnlocked { get; private set; }

    public string Prompt => IsUnlocked ? null : prompt;

    public string ErrorMessage => IsUnlocked ? null : noKeyMessage;


    public event Action<DoorLock> Unlocked;

    public InteractionResult Interact(InteractionController interactor)
    {
        if (IsUnlocked)
            return InteractionResult.Success;

        PlayerInventory inventory = interactor.Inventory;

        if (inventory == null || !inventory.TryUseKey(1))
            return InteractionResult.Fail(noKeyMessage);

        Unlock();
        return InteractionResult.Success;
    }

    /// <summary>Abre sem cobrar chave. Para scripts de cena, cutscene, debug.</summary>
    public virtual void Unlock()
    {
        if (IsUnlocked)
            return;

        IsUnlocked = true;

        // Som e evento antes de desativar: a posição do cadeado ainda é válida aqui, e quem
        // assina pode querer ler a transform dele (spawnar partícula ali, por exemplo).
        // Fica no Unlock, e não no Interact, para abertura por script também soar.
        AudioProvider.PlayAt(openSoundId, transform.position);
        Unlocked?.Invoke(this);

        gameObject.SetActive(false);
    }
}
