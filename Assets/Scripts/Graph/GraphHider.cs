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
    /// a reta entre dois nós ligados é percorrível). Ao CHEGAR num nó de ping (NavGraph.IsPingSource), avisa: é isso
    /// que o <see cref="GraphPingSystem"/> do seeker lê para disparar o ping — "ouvi passos
    /// naquela sala". O seeker nunca recebe a posição do hider; recebe o rastro.
    ///
    /// Modos (currículo, hider_mode):
    ///   0 Nenhum   — o hider fica desligado; o episódio é só exploração.
    ///   1 Parado   — nasce num nó e fica. O ping toca uma vez; o seeker aprende a ir até lá.
    ///   2 Anda     — vagueia pelo grafo sem olhar para o seeker. O rastro se move.
    ///   3 Foge     — quando o seeker chega perto, escolhe a saída que mais aumenta a distância
    ///                PELO GRAFO até ele, em metros (contornar parede conta; linha reta não).
    /// SOLTO (hider_loose, S6): em vez de andar de centro em centro, vai para um ponto ALEATÓRIO
    /// dentro do retângulo do nó e, metade das vezes, escolhe o ponto menos visível das portas da
    /// sala (um canto atrás da linha de visão) e fica parado mais tempo — se escondendo. Sem isso o
    /// seeker aprende a olhar só para o centro dos ladrilhos, onde a presa de trilho sempre está.
    ///
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
        // currículo começa em 1.0 e sobe até 3.2 — sempre abaixo do seeker (6): um hider mais
        // rápido é impegável e o seeker nunca recebe o sinal de captura; começar devagar deixa
        // o seeker aprender a seguir o rastro antes de precisar correr.
        [SerializeField, Min(0f)] private float _speed = 1f;

        private float _currentSpeed;

        // Chance de uma chegada num nó de ping fazer BARULHO (virar ping), por episódio — vem do
        // currículo (hider_noise). 1 = toda chegada pinga, o comportamento antigo, que era quase um
        // GPS; com 0.25 o seeker ouve um passo a cada quatro e entre um e outro quem guia é a
        // dedução (GraphSuspicionMap). No jogo é o player andando com cuidado ou não.
        private float _noiseChance = 1f;

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

        [Header("-----Solto e escondido (hider_loose)-----")]
        // Distância mínima (m) do ponto sorteado até a borda do retângulo do nó: o corpo não pode
        // nascer encostado na parede.
        [SerializeField, Min(0f)] private float _looseMargin = 0.6f;

        // Chance de, ao escolher o próximo ponto, procurar um ESCONDERIJO (o menos visível das portas
        // da sala) em vez de um ponto qualquer.
        [SerializeField, Range(0f, 1f)] private float _hideChance = 0.5f;

        // Quantos pontos são sorteados para escolher o esconderijo.
        [SerializeField, Min(1)] private int _hideCandidates = 6;

        // Escondido, a pausa é este tanto maior: quem se esconde fica quieto.
        [SerializeField, Min(1f)] private float _hidePauseMultiplier = 3f;

        [Header("-----Spawn-----")]
        // Distância mínima, em METROS pelo grafo, do nó de spawn do seeker. 40 m ~ as 6
        // arestas de antes (mediana 7.4 m): fora da sala inicial sem mandar o hider para o
        // outro lado do mundo. Em metros para não mudar quando o grafo for adensado.
        // Nome novo de propósito: o antigo (_minSpawnDistance) era em arestas, e o 6 salvo no
        // prefab viraria "6 metros" em silêncio.
        [SerializeField, Min(0f)] private float _minSpawnDistanceMeters = 40f;

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
        private bool _loose;

        // Para onde está andando agora (centro do nó, ou o ponto sorteado no modo solto) e, no solto,
        // o ponto final quando o caminho direto não passa: primeiro volta ao centro do nó atual.
        private Vector3 _goal;
        private Vector3 _finalGoal;
        private bool _viaCenter;
        private bool _goalIsHiding;

        /// <summary>Ligado neste episódio (modo != None).</summary>
        public bool IsActive => _mode != Mode.None;

        /// <summary>Último nó em que o hider PISOU (-1 antes do primeiro).</summary>
        public int CurrentNode => _currentNode;

        /// <summary>
        /// Nó de ping em que o hider acabou de chegar, ou -1. Fica de pé até alguém consumir
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
        /// <param name="noiseChance">Chance (0..1) de cada chegada virar ping.</param>
        /// <param name="loose">Modo solto (S6): pontos aleatórios dentro do nó e esconderijos.</param>
        public void ResetEpisode(Mode mode, float speed, float noiseChance, Vector3 seekerPosition, bool loose)
        {
            _mode = mode;
            _loose = loose;
            _viaCenter = false;
            _goalIsHiding = false;
            _currentSpeed = speed > 0f ? speed : _speed;
            _noiseChance = Mathf.Clamp01(noiseChance);
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

            // Solto: nasce num ponto do nó, de preferência escondido.
            Place(_loose ? PointInNode(_currentNode, hide: true, from: _graph.NodePosition(_currentNode)) : _graph.NodePosition(_currentNode));

            // Nascer num nó de ping pode ser um barulho: com hider_noise 1 o seeker ganha o primeiro
            // ping de graça; com menos, às vezes começa sem pista nenhuma e tem que procurar.
            MakeNoiseAt(_currentNode);

            if (mode == Mode.Static)
                return;

            ChooseNextNode();
            SetGoalForTarget();
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
                SetGoalForTarget();
                return;
            }

            Vector3 goal = _goal + Vector3.up * _heightOffset;
            Vector3 delta = goal - transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;

            if (distance <= _arriveDistance)
            {
                // Chegou ao centro do nó atual a caminho de um ponto solto: segue para o ponto.
                if (_viaCenter)
                {
                    _viaCenter = false;
                    _goal = _finalGoal;
                    return;
                }

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

            MakeNoiseAt(_currentNode);

            _pauseLeft = _maxPauseSteps > 0 ? Random.Range(0, _maxPauseSteps + 1) : 0;
            if (_goalIsHiding)
                _pauseLeft = Mathf.RoundToInt(_pauseLeft * _hidePauseMultiplier);
        }

        // Destino do próximo nó: o centro (de trilho) ou, solto, um ponto dentro dele. Se a reta da
        // posição atual até o ponto bate em parede, passa antes pelo centro do nó atual — as ligações
        // do grafo só garantem a reta entre CENTROS.
        private void SetGoalForTarget()
        {
            _viaCenter = false;
            _goalIsHiding = false;
            if (_targetNode < 0)
                return;

            if (!_loose)
            {
                _goal = _graph.NodePosition(_targetNode);
                return;
            }

            bool fleeing = _mode == Mode.Flee && SeekerIsNear();
            bool hide = !fleeing && Random.value < _hideChance;
            Vector3 here = transform.position - Vector3.up * _heightOffset;
            Vector3 point = PointInNode(_targetNode, hide, _graph.NodePosition(_targetNode));
            _goalIsHiding = hide;

            if (_graph.IsSegmentClear(here, point))
            {
                _goal = point;
                return;
            }

            _viaCenter = true;
            _finalGoal = _graph.IsSegmentClear(_graph.NodePosition(_currentNode), point) ? point : _graph.NodePosition(_targetNode);
            _goal = _graph.NodePosition(_currentNode);
        }

        // Um ponto livre dentro do retângulo do nó (porta: sempre o centro — é por ali que se passa).
        // Escondendo: entre _hideCandidates pontos, o visto pelo menor número de portas da sala.
        // Nenhum ponto livre (ou que a reta desde from alcance): o centro.
        private Vector3 PointInNode(int node, bool hide, Vector3 from)
        {
            Vector3 center = _graph.NodePosition(node);
            if (_graph.IsDoor(node))
                return center;

            NavNode navNode = _graph.GetNode(node);
            Vector3 areaCenter = _graph.AreaCenterOf(navNode);
            Vector2 half = _graph.HalfExtentsOf(navNode);
            half = new Vector2(Mathf.Max(0f, half.x - _looseMargin), Mathf.Max(0f, half.y - _looseMargin));

            int room = _graph.RoomOf(node);
            int[] doors = room >= 0 ? _graph.DoorsOfRoom(room) : null;
            int tries = hide ? _hideCandidates : 2;
            Vector3 best = center;
            int bestSeen = int.MaxValue;
            float radius = _graph.LinkClearance;

            for (int k = 0; k < tries; k++)
            {
                var point = new Vector3(
                    areaCenter.x + Random.Range(-half.x, half.x),
                    center.y,
                    areaCenter.z + Random.Range(-half.y, half.y));

                if (!_graph.IsBodyClear(point, radius) || !_graph.IsSegmentClear(from, point))
                    continue;

                if (!hide)
                    return point;

                int seen = 0;
                if (doors != null)
                {
                    foreach (int door in doors)
                    {
                        if (_graph.CanSeeFromNode(_graph.NodePosition(door), point))
                            seen++;
                    }
                }

                if (seen < bestSeen)
                {
                    bestSeen = seen;
                    best = point;
                }
            }

            return best;
        }

        private bool SeekerIsNear()
        {
            if (_seeker == null)
                return false;

            Vector3 toSeeker = _seeker.position - transform.position;
            toSeeker.y = 0f;
            return toSeeker.magnitude <= _fleeRadius;
        }

        private void MakeNoiseAt(int node)
        {
            if (_graph.IsPingSource(node) && Random.value < _noiseChance)
                PendingArrival = node;
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
                if (SeekerIsNear())
                {
                    int seekerNode = _graph.FindNearestReachableNode(_seeker.position);
                    int best = -1;
                    float bestDistance = -1f;

                    foreach (int neighbor in neighbors)
                    {
                        if (!_graph.IsNodeEnabled(neighbor))
                            continue;

                        float distance = seekerNode >= 0 && _graph.TryFindPathTo(neighbor, seekerNode, out _, out float d) ? d : 0f;
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

        // Nó de SPAWN: sala sorteada por igual, DIFERENTE da sala do seeker, e um nó com folga dela
        // (GraphArenaController.RandomSpawnNodeByRoom — a mesma regra do spawn do seeker), a pelo
        // menos _minSpawnDistanceMeters do nó mais próximo do seeker. Se o mapa for pequeno demais
        // para a distância pedida, aceita o último sorteado.
        private int PickSpawnNode(Vector3 seekerPosition)
        {
            int seekerNode = _graph.FindNearestReachableNode(seekerPosition);
            int seekerRoom = seekerNode >= 0 ? _graph.RoomOf(seekerNode) : -1;
            int fallback = -1;

            for (int attempt = 0; attempt < _graph.RoomCount * 2; attempt++)
            {
                int candidate = GraphArenaController.RandomSpawnNodeByRoom(_graph, seekerRoom);
                if (candidate < 0)
                    break;

                if (candidate == seekerNode)
                    continue;

                fallback = candidate;

                if (seekerNode < 0)
                    return candidate;

                if (_graph.TryFindPathTo(candidate, seekerNode, out _, out float distance) && distance >= _minSpawnDistanceMeters)
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
            Gizmos.DrawLine(transform.position, _goal + Vector3.up * _heightOffset);
        }
    }
}
