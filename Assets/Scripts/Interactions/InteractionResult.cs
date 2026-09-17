/// <summary>
/// Resultado de um Interact(). A falha carrega o motivo em texto para a UI mostrar no lugar
/// do prompt ("Requer uma chave"); o interagível não sabe nem precisa saber quem exibe.
/// </summary>
public readonly struct InteractionResult
{
    public bool Succeeded { get; }

    public string Message { get; }

    private InteractionResult(bool succeeded, string message)
    {
        Succeeded = succeeded;
        Message = message;
    }

    public static InteractionResult Success => new InteractionResult(true, null);

    public static InteractionResult Fail(string message) => new InteractionResult(false, message);
}
