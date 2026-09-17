using UnityEngine;

public class KeyItem : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Pegar chave";

    public string Prompt => prompt;

    public string ErrorMessage => null; // Não há erro possível ao pegar a chave


    public InteractionResult Interact(InteractionController interactor)
    {
        if (interactor.Inventory == null)
            return InteractionResult.Fail("Sem inventário");

        interactor.Inventory.AddKey(1);
        Destroy(gameObject);

        return InteractionResult.Success;
    }
}
