namespace Assets.Scripts.Behavior
{
    /// <summary>
    /// Representa o comportamento atual do agente.
    /// O BehaviorSystem decide quando alternar entre estes estados.
    /// </summary>
    public enum BehaviorState
    {
        /// <summary>
        /// Nenhum estimulo disponivel.
        /// O agente explora o ambiente.
        /// </summary>
        Searching,

        /// <summary>
        /// Existe cheiro do alvo, mas ele nao esta visivel.
        /// O agente segue o gradiente do cheiro.
        /// </summary>
        FollowingSmell,

        /// <summary>
        /// O alvo foi perdido.
        /// O agente se desloca ate a ultima posicao conhecida.
        /// </summary>
        Investigating,

        /// <summary>
        /// O alvo esta visivel.
        /// O agente realiza perseguicao direta.
        /// </summary>
        ChasingTarget
    }
}