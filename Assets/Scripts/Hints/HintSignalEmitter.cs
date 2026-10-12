using UnityEngine;

/// <summary>
/// Ponte entre UnityEvent e <see cref="HintSignals"/>. Os objetos do jogo (porta, chave,
/// trigger) só expõem eventos; é aqui, pelo Inspector, que um deles vira sinal do roteiro de
/// dicas — sem nenhum script de gameplay depender de Hints.
///
/// Sem estado: qualquer emissor serve para qualquer sinal. Um prefab instanciado em runtime
/// (a chave) não alcança objetos da cena, então leva o próprio emissor.
/// </summary>
public class HintSignalEmitter : MonoBehaviour
{
    [Tooltip("Sinal do Raise() sem argumento.")]
    [SerializeField] private string signal;

    public void Raise() => HintSignals.Raise(signal);

    /// <summary>Alvo de UnityEvent com o nome do sinal digitado no próprio evento.</summary>
    public void Raise(string id) => HintSignals.Raise(id);
}
