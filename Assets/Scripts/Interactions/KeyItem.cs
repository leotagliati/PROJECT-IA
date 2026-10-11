using UnityEngine;

public class KeyItem : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Pegar";

    [Header("Áudio")]
    [SerializeField] private string pickupSoundId = "key_pickup";

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt result)
    {
        result = InteractionPrompt.Instant(prompt);
        return true;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        if (interactor.Inventory == null)
            return InteractionResult.Fail("Sem inventário");

        interactor.Inventory.AddKey(1);

        // PlayAt usa fonte do pool: o som sobrevive ao Destroy.
        AudioProvider.PlayAt(pickupSoundId, transform.position);
        Destroy(gameObject);

        return InteractionResult.Success;
    }
}
