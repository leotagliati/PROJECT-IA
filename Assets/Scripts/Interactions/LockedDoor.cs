using UnityEngine;

public class LockedDoor : MonoBehaviour, IInteractable
{
    [SerializeField] private int requiredKeys = 3;

    [Header("Prompt")]
    [SerializeField] private string openPrompt = "Abrir porta";

    [Tooltip("{0} vira o número de chaves necessárias.")]
    [SerializeField] private string lockedPromptFormat = "Trancada — precisa de {0} chaves";

    private PlayerInventory cachedPlayerInventory;

    // Montada uma vez: o Prompt é lido todo frame enquanto a porta está na mira, e
    // string.Format ali alocaria a cada leitura.
    private string lockedPrompt;

    /// <summary>Muda com o inventário, então a UI relê enquanto a porta está na mira.</summary>
    public string Prompt => HasEnoughKeys() ? openPrompt : lockedPrompt;

    void Awake()
    {
        cachedPlayerInventory = FindFirstObjectByType<PlayerInventory>();
        lockedPrompt = string.Format(lockedPromptFormat, requiredKeys);
    }

    public void Interact()
    {
        if (!ResolveInventory()) return;

        if (cachedPlayerInventory.TryUseKey(requiredKeys))
        {
            OpenDoor();
        }
        else
        {
            Debug.Log($"Not enough keys! You need {requiredKeys} keys!");
        }
    }

    private bool HasEnoughKeys()
    {
        return ResolveInventory() && cachedPlayerInventory.KeyCount >= requiredKeys;
    }

    private bool ResolveInventory()
    {
        if (cachedPlayerInventory == null)
            cachedPlayerInventory = FindFirstObjectByType<PlayerInventory>();

        return cachedPlayerInventory != null;
    }

    private void OpenDoor()
    {
        Debug.Log($"Door opened succesfully!");
    }
}
