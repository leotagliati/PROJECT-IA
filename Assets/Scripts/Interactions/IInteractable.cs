public interface IInteractable
{
    bool TryGetPrompt(InteractionController interactor, out InteractionPrompt prompt);
    InteractionResult Interact(InteractionController interactor);
}
