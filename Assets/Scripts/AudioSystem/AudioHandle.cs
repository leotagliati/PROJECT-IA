/// <summary>
/// Referência a um som em andamento, para Stop/IsPlaying. Carrega o slot e a geração do
/// slot no momento do Play: quando o pool rouba a voz para outro som, a geração muda e este
/// handle passa a não apontar para nada. Devolver o AudioSource cru, como antes, deixava um
/// Stop atrasado matar o som de outra pessoa.
/// </summary>
public readonly struct AudioHandle
{
    internal readonly int Slot;
    internal readonly int Generation;

    internal AudioHandle(int slot, int generation)
    {
        Slot = slot;
        Generation = generation;
    }

    /// <summary>Falso para o default e para o retorno de um Play que não tocou (mudo, id inválido).</summary>
    public bool IsValid => Generation > 0;

    public static AudioHandle None => default;
}
