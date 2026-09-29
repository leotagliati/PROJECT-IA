public interface IInteractable
{
    string Prompt { get; }

    string? ErrorMessage { get; }

    InteractionResult Interact(InteractionController interactor);
}
