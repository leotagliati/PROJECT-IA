using System;
using UnityEngine;

public class DoorLock : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Destrancar cadeado";

    [SerializeField] private string noKeyMessage = "Requer uma chave";

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

        // Avisa antes de desativar: quem assina pode querer ler a transform do cadeado
        // (spawnar partícula ali, por exemplo) enquanto ele ainda está ativo.
        Unlocked?.Invoke(this);

        gameObject.SetActive(false);
    }
}
