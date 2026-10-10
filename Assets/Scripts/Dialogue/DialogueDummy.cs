using UnityEngine;

public class DialogueDummy : MonoBehaviour, IInteractable
{
    [Header("Interação")]
    [SerializeField] private string prompt = "Conversar";

    [Header("Diálogo")]
    [SerializeField] private DialogueSystem dialogueSystem;

    [SerializeField] private string characterName = "DICA";

    [TextArea(2, 5)]
    [SerializeField]
    private string[] dialogueLines =
    {
        "Olá! Este é um diálogo de teste.",
        "O sistema está funcionando corretamente.",
        "Pressione para continuar e finalizar a conversa."
    };

    public string Prompt => prompt;

    public string ErrorMessage => null;

    
    public InteractionResult Interact(InteractionController interactor)
    {
        if (dialogueSystem == null)
        {
            Debug.LogWarning(
                $"DialogueDummy '{gameObject.name}' não possui um DialogueSystem.",
                this
            );

            return InteractionResult.Fail(
                "Sistema de diálogo indisponível."
            );
        }

        // Ignora interações repetidas durante o diálogo.
        if (dialogueSystem.IsDialogueActive || PauseControler.IsPaused)
        {
            return InteractionResult.Success;
        }

        dialogueSystem.BeginDialogue(dialogueLines, characterName);

        return InteractionResult.Success;
    }

}
