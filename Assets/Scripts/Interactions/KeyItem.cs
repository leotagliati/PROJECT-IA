using UnityEngine;
using UnityEngine.Events;

public class KeyItem : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Pegar";

    [Header("Áudio")]
    [SerializeField] private string pickupSoundId = "key_pickup";

    [Tooltip("Ao ser coletada. A chave nasce de prefab em runtime: só alcança componentes do " +
             "próprio prefab (ex.: um HintSignalEmitter), não objetos da cena.")]
    [SerializeField] private UnityEvent pickedUp;

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
        pickedUp.Invoke();
        Destroy(gameObject);

        return InteractionResult.Success;
    }
}
