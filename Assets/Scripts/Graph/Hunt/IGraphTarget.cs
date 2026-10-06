using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O alvo que o seeker caça: o <see cref="GraphHider"/> no treino, o jogador (<see cref="GraphPlayerTarget"/>)
    /// no modo de jogo. Visão, procura e ping só enxergam isto, então trocar o alvo não muda a observação.
    /// </summary>
    public interface IGraphTarget
    {
        bool IsActive { get; }

        Vector3 Position { get; }

        /// <summary>Último nó do grafo em que o alvo pisou (-1 antes do primeiro).</summary>
        int CurrentNode { get; }

        /// <summary>Nó de ping em que o alvo acabou de pisar fazendo barulho, ou -1; consome o evento.</summary>
        int ConsumeArrival();

        /// <summary>Correndo agora: o barulho que o seeker ouve de longe (GraphPingSystem, audição).</summary>
        bool IsRunning { get; }
    }

    public static class GraphTarget
    {
        /// <summary>Existe, não foi destruído e está ativo (o "== null" do Unity não funciona através da interface).</summary>
        public static bool IsLive(IGraphTarget target) =>
            target is Object unityObject
                ? unityObject != null && target.IsActive
                : target != null && target.IsActive;
    }
}
