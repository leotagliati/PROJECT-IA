public interface IInteractable
{
    /// <summary>
    /// Verbo curto do que Interact() faria agora: "Pegar chave", "Abrir porta". É só a ação;
    /// a tecla quem põe é a UI, a partir do binding atual. Pode mudar com o estado do objeto
    /// (porta trancada vs. destrancada), por isso é lido enquanto o alvo está na mira, e não
    /// só quando entra nela. Null ou vazio = interagível sem prompt.
    /// </summary>
    string Prompt { get; }

    void Interact();
}
