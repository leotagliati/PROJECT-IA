using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Abre uma conversa ao interagir. É a única ponte entre o sistema de interação e o de diálogo; para um passo de
/// tutorial esperar a conversa, ligue um HintSignalEmitter no <see cref="finished"/>.
/// </summary>
public class DialogueTrigger : MonoBehaviour, IInteractable
{
    [SerializeField] private DialogueAsset dialogue;
    [SerializeField] private string prompt = "Conversar";

    [Tooltip("Depois da primeira conversa completa, o objeto deixa de ser interagível.")]
    [SerializeField] private bool once;

    [SerializeField] private UnityEvent finished;

    private bool done;

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt result)
    {
        result = InteractionPrompt.Instant(prompt);

        if (once && done)
            return false;

        if (DialogueController.Current == null)
            result = result.Blocked("Sem caixa de diálogo na cena");

        return !DialogueController.IsOpen;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        DialogueController controller = DialogueController.Current;
        if (controller == null)
        {
            Debug.LogWarning($"[DialogueTrigger] '{name}': nenhum DialogueController ativo na cena.", this);
            return InteractionResult.Fail("Sem caixa de diálogo na cena");
        }

        if ((once && done) || DialogueController.IsOpen)
            return InteractionResult.Success;

        controller.Play(dialogue, OnFinished);
        return InteractionResult.Success;
    }

    private void OnFinished()
    {
        done = true;
        finished.Invoke();
    }
}
