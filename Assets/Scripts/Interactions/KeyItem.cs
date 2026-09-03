using UnityEngine;

public class KeyItem : MonoBehaviour, IInteractable
{
[SerializeField] private string targetTag = "Key";

    public void Interact()
    {        
        // Garante que só interage se bater com a tag configurada
        if (!CompareTag(targetTag)) return;

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