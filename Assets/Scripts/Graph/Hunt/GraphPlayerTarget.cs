using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O jogador como alvo do seeker no modo de jogo: faz o papel do <see cref="GraphHider"/> (nó atual e
    /// passos que viram ping) sem mover nada. O <see cref="GraphArenaController"/> acha o jogador pela tag e
    /// adiciona este componente em runtime se ele não tiver; adicione na mão para ajustar o barulho.
    /// Roda antes do seeker no step de física, para o ping do mesmo step já ver o passo.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GraphPlayerTarget : MonoBehaviour, IGraphTarget
    {
        // Chance de um passo num nó de ping virar ping, pelo estado do jogador. Andar ou correr sempre toca
        // (mais que os ~30% do hider no treino: no jogo, todo passo descuidado denuncia); agachado não.
        [SerializeField, Range(0f, 1f)] private float _walkNoise = 1f;
        [SerializeField, Range(0f, 1f)] private float _runNoise = 1f;
        [SerializeField, Range(0f, 1f)] private float _crouchNoise = 0f;

        private NavGraph _graph;
        private PlayerMovement _movement;
        private int _currentNode = -1;

        public bool IsActive => isActiveAndEnabled;

        public Vector3 Position => transform.position;

        public int CurrentNode => _currentNode;

        public int PendingArrival { get; private set; } = -1;

        public int ConsumeArrival()
        {
            int node = PendingArrival;
            PendingArrival = -1;
            return node;
        }

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();
            _currentNode = -1;
            PendingArrival = -1;

            _movement = GetComponent<PlayerMovement>();
            if (_movement == null)
                _movement = GetComponentInChildren<PlayerMovement>();
        }

        // Entre dois nós o FindNodeAt devolve -1: fica o último pisado, como no hider.
        private void FixedUpdate()
        {
            if (_graph == null)
                return;

            int node = _graph.FindNodeAt(transform.position, _currentNode);
            if (node < 0 || node == _currentNode)
                return;

            _currentNode = node;
            if (_graph.IsPingSource(node) && Random.value < NoiseChance())
                PendingArrival = node;
        }

        private float NoiseChance()
        {
            if (_movement == null)
                return _walkNoise;

            switch (_movement.CurrentState)
            {
                case PlayerState.Running:
                case PlayerState.Jumping:
                    return _runNoise;
                case PlayerState.Crouching:
                case PlayerState.CrouchWalking:
                    return _crouchNoise;
                default:
                    return _walkNoise;
            }
        }
    }
}
