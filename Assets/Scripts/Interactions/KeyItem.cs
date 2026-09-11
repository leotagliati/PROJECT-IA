using UnityEngine;

public class KeyItem : MonoBehaviour, IInteractable
{
    public void Interact()
    {        
        // Procura o inventário do jogador na cena
        PlayerInventory playerInventory = FindFirstObjectByType<PlayerInventory>();

        if (playerInventory != null)
        {
            playerInventory.AddKey(1);
            Destroy(gameObject);
        }
        else
        {
            Debug.LogWarning("PlayerInventory não foi encontrado na cena!");
        }
    }
}