using UnityEngine;

public class TutorialNoteInteractable : MonoBehaviour, IInteractable
{
    [Header("Tutorial")]
    [SerializeField] private TutorialNoteTrigger tutorialTrigger;

    [Header("Interação")]
    [SerializeField] private string prompt = "Ler anotações";

    public bool TryGetPrompt(
        InteractionController interactor,
        out InteractionPrompt result)
    {
        result = InteractionPrompt.Instant(prompt);

        // Impede a interação se o componente não foi configurado.
        if (tutorialTrigger == null)
        {
            result = result.Blocked("Anotações indisponíveis");
            return true;
        }

        return true;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        if (tutorialTrigger == null)
        {
            Debug.LogWarning(
                $"[TutorialNoteInteractable] Nenhum TutorialNoteTrigger configurado em '{name}'.",
                this
            );

            return InteractionResult.Fail(
                "Não é possível ler as anotações no momento."
            );
        }

        tutorialTrigger.Show();

        return InteractionResult.Success;
    }
}
