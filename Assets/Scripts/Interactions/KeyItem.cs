using UnityEngine;
using UnityEngine.Events;

public class KeyItem : MonoBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Pegar chave";

    [Header("Áudio")]
    [SerializeField] private string pickupSoundId = "key_pickup";

    [Tooltip("Ao ser coletada. A chave nasce de prefab em runtime: só alcança componentes do " +
             "próprio prefab (ex.: um HintSignalEmitter), não objetos da cena.")]
    [SerializeField] private UnityEvent pickedUp;

    public string Prompt => prompt;

    public string ErrorMessage => null; // Não há erro possível ao pegar a chave


    public InteractionResult Interact(InteractionController interactor)
    {
        if (interactor.Inventory == null)
            return InteractionResult.Fail("Sem inventário");

        interactor.Inventory.AddKey(1);

        // PlayAt usa uma fonte do pool, não um AudioSource da chave: o som sobrevive ao
        // Destroy logo abaixo.
        AudioProvider.PlayAt(pickupSoundId, transform.position);
        pickedUp.Invoke();
        Destroy(gameObject);

        return InteractionResult.Success;
    }
}
