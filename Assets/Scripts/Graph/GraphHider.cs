using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A presa SCRIPTADA do seeker, andando pelo mesmo grafo de nós. Não aprende nada: é o
    /// oponente fixo contra o qual o seeker treina — um hider que também aprendesse (self-play)
    /// produziria dois comportamentos estranhos que só funcionam um contra o outro, e você
    /// perderia a régua ("o seeker melhorou ou o hider piorou?").
    ///
    /// Anda de nó em nó em linha reta (as ligações do grafo são validadas contra parede, então
    /// a reta entre dois nós ligados é percorrível). Ao CHEGAR num nó primário, avisa: é isso
    /// que o <see cref="GraphPingSystem"/> do seeker lê para disparar o ping — "ouvi passos
    /// naquela sala". O seeker nunca recebe a posição do hider; recebe o rastro.
    ///
    /// Modos (currículo, hider_mode):
    ///   0 Nenhum   — o hider fica desligado; o episódio é só exploração.
    ///   1 Parado   — nasce num nó e fica. O ping toca uma vez; o seeker aprende a ir até lá.
    ///   2 Anda     — vagueia pelo grafo sem olhar para o seeker. O rastro se move.
    ///   3 Foge     — quando o seeker chega perto, escolhe a saída que mais aumenta a distância
    ///                em ARESTAS até ele (contornar parede conta; linha reta não).
    /// Um por arena. Substitui o HiderAgent antigo (que andava em cardinais e virava ao bater):
    /// aquele não conhece nós, e sem nó não há ping.
    /// </summary>
    public class GraphHider : MonoBehaviour
    {
        public enum Mode
        {
            None = 0,
            Static = 1,
            Wander = 2,
            Flee = 3,
        }

        [Header("-----Movimento-----")]
        // Velocidade PADRÃO (m/s), usada quando o currículo não manda outra (hider_speed). O
        // currículo começa em 1.0 e sobe até 3.2 — sempre abaixo do seeker (5): um hider mais
        // rápido é impegável e o seeker nunca recebe o sinal de captura; começar devagar deixa
        // o seeker aprender a seguir o rastro antes de precisar correr.
        [SerializeField, Min(0f)] private float _speed = 1f;

        private float _currentSpeed;

        // Considera "cheguei" a este tanto do centro do nó, em metros. Menor que o raio de
        // chegada do seeker de propósito: o hider tem que pisar de fato no nó para o ping tocar.
        [SerializeField] private float _arriveDistance = 0.4f;

        // Pausa ao chegar num nó, em steps de física (sorteada entre 0 e isto). Dá ritmo de
        // "parou para ouvir" e evita que o rastro seja um relógio.
        [SerializeField, Min(0)] private int _maxPauseSteps = 100;

        // Altura do corpo sobre o nó, como o spawn do seeker.
        [SerializeField] private float _heightOffset = 0.15f;

        [Header("-----Fuga-----")]
        // Distância planar (m) a partir da qual o hider passa a fugir em vez de vaguear.
        [SerializeField] private float _fleeRadius = 12f;

        [Header("-----Spawn-----")]
        // Distância mínima, em ARESTAS, do nó de spawn do seeker. 6 no mapa atual (diâmetro
        // 42) coloca o hider fora da sala inicial sem mandá-lo para o outro lado do mundo.
        [SerializeField, Min(1)] private int _minSpawnDistance = 6;

        [Header("-----Referências-----")]
        [SerializeField] private NavGraph _graph;
        // O seeker desta arena. Só o modo Foge usa; fallback por GetComponentInChildren no pai.
        [SerializeField] private Transform _seeker;

        private Rigidbody _rigidbody;
        private Mode _mode = Mode.None;
        private int _currentNode = -1;
        private int _previousNode = -1;
        private int _targetNode = -1;
        private int _pauseLeft;

        /// <summary>Ligado neste episódio (modo != None).</summary>
        public bool IsActive => _mode != Mode.None;

        /// <summary>Último nó em que o hider PISOU (-1 antes do primeiro).</summary>
        public int CurrentNode => _currentNode;

        /// <summary>
        /// Nó primário em que o hider acabou de chegar, ou -1. Fica de pé até alguém consumir
        /// com <see cref="ConsumeArrival"/> — é o "barulho" que o GraphPingSystem transforma
        /// em ping. Um por chegada: pisar no mesmo nó parado não toca de novo.
        /// </summary>
        public int PendingArrival { get; private set; } = -1;

        public int ConsumeArrival()
        {
            int node = PendingArrival;
            PendingArrival = -1;
            return node;
        }

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();

            if (_graph == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                _graph = arena != null ? arena.Graph : GetComponentInParent<NavGraph>();
            }

            if (_seeker == null)
            {
                GraphExplorerManager manager = GetComponentInParent<GraphArenaController>() != null
                    ? GetComponentInParent<GraphArenaController>().GetComponentInChildren<GraphExplorerManager>()
                    : null;
                if (manager != null)
                    _seeker = manager.transform;
            }
        }

        /// <summary>
        /// Chamar DEPOIS do spawn do seeker: o hider nasce longe dele. Com modo None fica
        /// desligado (GameObject inativo) e não emite nada.
        /// </summary>
        /// <param name="speed">m/s neste episódio; &lt;= 0 usa o padrão do Inspector.</param>
        public void ResetEpisode(Mode mode, float speed, Vector3 seekerPosition)
        {
            _mode = mode;
            _currentSpeed = speed > 0f ? speed : _speed;
            PendingArrival = -1;
            _previousNode = -1;
            _targetNode = -1;
            _pauseLeft = 0;

            if (_graph == null || mode == Mode.None)
            {
                gameObject.SetActive(false);
                return;
            }

            _graph.EnsureBaked();
            gameObject.SetActive(true);

            _currentNode = PickSpawnNode(seekerPosition);
            if (_currentNode < 0)
            {
                gameObject.SetActive(false);
                return;
            }

            Place(_graph.NodePosition(_currentNode));

            // Nascer num primário já é um barulho: o seeker ganha o primeiro ping de graça.
            if (_graph.IsNodePrimary(_currentNode))
                PendingArrival = _currentNode;

            if (mode == Mode.Static)
                return;

            ChooseNextNode();
        }

        private void FixedUpdate()
        {
            if (_mode == Mode.None || _mode == Mode.Static || _graph == null)
                return;

            if (_pauseLeft > 0)
            {
                _pauseLeft--;
                return;
            }

            if (_targetNode < 0)
            {
                ChooseNextNode();
                return;
            }

            Vector3 goal = _graph.NodePosition(_targetNode) + Vector3.up * _heightOffset;
            Vector3 delta = goal - transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;

            if (distance <= _arriveDistance)
            {
                Arrive();
                return;
            }

            Vector3 step = delta / distance * _currentSpeed * Time.fixedDeltaTime;
            if (step.magnitude > distance)
                step = delta;

            Move(transform.position + step, delta);
        }

        private void Arrive()
        {
            _previousNode = _currentNode;
            _currentNode = _targetNode;
            _targetNode = -1;

            if (_graph.IsNodePrimary(_currentNode))
                PendingArrival = _currentNode;

            _pauseLeft = _maxPauseSteps > 0 ? Random.Range(0, _maxPauseSteps + 1) : 0;
        }

        // Anda: vizinho aleatório, evitando voltar por onde veio quando há alternativa. Foge:
        // se o seeker está perto, o vizinho que mais AUMENTA a distância em arestas até o nó do
        // seeker; senão anda normalmente.
        private void ChooseNextNode()
        {
            int[] neighbors = _graph.GetNeighbors(_currentNode);
            if (neighbors.Length == 0)
                return;

            if (_mode == Mode.Flee && _seeker != null)
            {
                Vector3 toSeeker = _seeker.position - transform.position;
                toSeeker.y = 0f;

                if (toSeeker.magnitude <= _fleeRadius)
                {
                    int seekerNode = _graph.FindNearestReachableNode(_seeker.position);
                    int best = -1;
                    int bestDistance = -1;

                    foreach (int neighbor in neighbors)
                    {
                        if (!_graph.IsNodeEnabled(neighbor))
                            continue;

                        int distance = seekerNode >= 0 && _graph.TryFindPathTo(neighbor, seekerNode, out _, out int d) ? d : 0;
                        if (distance > bestDistance)
                        {
                            bestDistance = distance;
                            best = neighbor;
                        }
                    }

                    if (best >= 0)
                    {
                        _targetNode = best;
                        return;
                    }
                }
            }

            // Vaguear: sorteio entre os ativos que não são o nó de onde veio (se houver outro).
            int chosen = -1;
            int options = 0;
            foreach (int neighbor in neighbors)
            {
                if (!_graph.IsNodeEnabled(neighbor) || neighbor == _previousNode)
                    continue;

                options++;
                if (Random.Range(0, options) == 0)
                    chosen = neighbor;
            }

            if (chosen < 0 && _previousNode >= 0 && _graph.IsNodeEnabled(_previousNode))
                chosen = _previousNode;

            _targetNode = chosen;
        }

        // Nó ativo a pelo menos _minSpawnDistance arestas do nó mais próximo do seeker. Sorteio
        // com rejeição; se o mapa for pequeno demais para a distância pedida, aceita qualquer um.
        private int PickSpawnNode(Vector3 seekerPosition)
        {
            int seekerNode = _graph.FindNearestReachableNode(seekerPosition);
            int count = _graph.NodeCount;
            int fallback = -1;

            for (int attempt = 0; attempt < count * 2; attempt++)
            {
                int candidate = Random.Range(0, count);
                if (!_graph.IsNodeEnabled(candidate) || candidate == seekerNode)
                    continue;

                fallback = candidate;

                if (seekerNode < 0)
                    return candidate;

                if (_graph.TryFindPathTo(candidate, seekerNode, out _, out int distance) && distance >= _minSpawnDistance)
                    return candidate;
            }

            return fallback;
        }

        private void Place(Vector3 nodePosition)
        {
            Vector3 position = nodePosition + Vector3.up * _heightOffset;
            if (_rigidbody != null)
            {
                _rigidbody.position = position;
                _rigidbody.linearVelocity = Vector3.zero;
            }

            transform.position = position;
        }

        private void Move(Vector3 position, Vector3 facing)
        {
            Quaternion rotation = facing.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(facing.normalized, Vector3.up) : transform.rotation;
            if (_rigidbody != null)
            {
                _rigidbody.MovePosition(position);
                _rigidbody.MoveRotation(rotation);
            }
            else
            {
                transform.SetPositionAndRotation(position, rotation);
            }
        }

        // Verde-azulado escuro: não é nenhuma das cores da memória, da fronteira (magenta), do
        // ping (rosa) nem da estrutura. Linha até o nó para onde está indo.
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _graph == null || _mode == Mode.None || _targetNode < 0)
                return;

            Gizmos.color = new Color(0f, 0.55f, 0.5f, 0.9f);
            Gizmos.DrawLine(transform.position, _graph.NodePosition(_targetNode) + Vector3.up * _heightOffset);
        }
    }
}
