using Assets.Scripts.Graph;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// O jogador como alvo do monstro da v8 no modo de jogo, sem grafo: só posição e "está correndo" (pegadas). O
    /// <see cref="FreeArenaController"/> acha o jogador pela tag e adiciona isto em runtime se faltar. Faz o papel do
    /// <see cref="FreeHider"/> sem mover nada; nó e ping não existem na v8.
    /// </summary>
    public class FreePlayerTarget : MonoBehaviour, IGraphTarget
    {
        private PlayerMovement _movement;

        public bool IsActive => isActiveAndEnabled;

        public Vector3 Position => transform.position;

        // Pular conta como correr: a aterrissagem faz barulho.
        public bool IsRunning => _movement != null
            && (_movement.CurrentState == PlayerState.Running || _movement.CurrentState == PlayerState.Jumping);

        public int CurrentNode => -1;

        public int ConsumeArrival() => -1;

        private void Awake()
        {
            _movement = GetComponent<PlayerMovement>();
            if (_movement == null)
                _movement = GetComponentInChildren<PlayerMovement>();
        }
    }
}
