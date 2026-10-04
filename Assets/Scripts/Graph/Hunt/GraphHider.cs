using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A presa SCRIPTADA do seeker: anda de nó em nó pelo grafo (em linha reta; as ligações são
    /// validadas contra parede) e não aprende nada, para o seeker treinar contra um oponente fixo.
    /// Ao chegar num nó de ping (NavGraph.IsPingSource) deixa PendingArrival, que o GraphPingSystem
    /// do seeker consome como ping: o seeker nunca recebe a posição do hider, só o rastro.
    ///
    /// Modos (currículo, hider_mode): 0 Nenhum (desligado), 1 Parado, 2 Anda (vagueia), 3 Foge
    /// (com o seeker perto, escolhe a saída que mais aumenta a distância PELO GRAFO em metros).
    /// Velocidade (v4.4): anda a _walkFraction da velocidade e CORRE só fugindo, enquanto houver
    /// estamina (GraphStamina, o mesmo modelo do seeker); a fuga da v4.5 dá a ele mais estamina.
    /// Solto (hider_loose): vai a pontos aleatórios dentro do nó e, às vezes, a um esconderijo
    /// (ponto pouco visível das portas da sala), onde fica parado mais tempo.
    ///
    /// Um por arena. Não use o HiderAgent.cs antigo com o grafo: ele não conhece nós, logo sem ping.
    /// </summary>
    public class GraphHider : MonoBehaviour, IGraphTarget
    {
        public enum Mode
        {
            None = 0,
            Static = 1,
            Wander = 2,
            Flee = 3,
        }

        [Header("-----Movimento-----")]
        // Velocidade CORRENDO (m/s) quando o currículo não manda outra (hider_speed). O hider faz o papel do
        // JOGADOR no treino: 10.2 = a corrida do PlayerDummy (6 x 1.7), a mesma do seeker (GraphLocomotion).
        [SerializeField, Min(0f)] private float _speed = 10.2f;

        // Andar = correr x isto. 0.588 = 6 / 10.2, a razão andar/correr do jogador.
        [SerializeField, Range(0.1f, 1f)] private float _walkFraction = 0.588f;

        // Só gasta fugindo. Segundos do tanque vêm do currículo (hider_stamina); 0 lá = os daqui.
        [SerializeField] private GraphStamina _stamina = new GraphStamina();

        private float _currentSpeed;

        // Chance de uma chegada num nó de ping virar ping, por episódio (currículo: hider_noise).
        private float _noiseChance = 1f;

        // Considera "cheguei" a esta distância (m) do centro do nó; menor que a do seeker de propósito.
        [SerializeField] private float _arriveDistance = 0.4f;

        // Pausa máxima ao chegar num nó, em steps de física (sorteada entre 0 e isto).
        [SerializeField, Min(0)] private int _maxPauseSteps = 100;

        // Altura (m) do corpo sobre o nó.
        [SerializeField] private float _heightOffset = 0.15f;

        [Header("-----Fuga-----")]
        // Distância planar (m) a partir da qual o hider passa a fugir em vez de vaguear.
        [SerializeField] private float _fleeRadius = 12f;

        [Header("-----Solto e escondido (hider_loose)-----")]
        // Margem (m) entre o ponto sorteado e a borda do retângulo do nó.
        [SerializeField, Min(0f)] private float _looseMargin = 0.6f;

        // Chance de o próximo ponto ser um esconderijo (o menos visível das portas da sala).
        [SerializeField, Range(0f, 1f)] private float _hideChance = 0.5f;

        // Pontos sorteados para escolher o esconderijo.
        [SerializeField, Min(1)] private int _hideCandidates = 6;

        // Multiplicador da pausa quando escondido.
        [SerializeField, Min(1f)] private float _hidePauseMultiplier = 3f;

        [Header("-----Spawn-----")]
        // Distância mínima (m pelo grafo) do nó de spawn do hider ao nó do seeker.
        [SerializeField, Min(0f)] private float _minSpawnDistanceMeters = 40f;

        [Header("-----Referências-----")]
        // Fallback: o grafo da arena.
        [SerializeField] private NavGraph _graph;

        // Opcional e só visual (Animator do modelo): vazio = acha nos filhos; sem ele o hider treina igual.
        [SerializeField] private GraphHiderAnimationSystem _animationSystem;

        private Rigidbody _rigidbody;

        // O seeker desta arena (fuga e spawn longe dele); vem no ResetEpisode, a arena sabe quem é.
        private Transform _seeker;
        private Mode _mode = Mode.None;
        private int _currentNode = -1;
        private int _previousNode = -1;
        private int _targetNode = -1;
        private int _pauseLeft;
        private bool _loose;

        // Destino atual (centro do nó ou ponto solto); no solto, _finalGoal vale depois de passar pelo centro do nó atual.
        private Vector3 _goal;
        private Vector3 _finalGoal;
        private bool _viaCenter;
        private bool _goalIsHiding;

        /// <summary>Ligado neste episódio (modo != None).</summary>
        public bool IsActive => _mode != Mode.None;

        /// <summary>Último nó em que o hider PISOU (-1 antes do primeiro).</summary>
        public int CurrentNode => _currentNode;

        public Vector3 Position => transform.position;

        /// <summary>Correndo neste step (fugindo com estamina). Para a animação.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Nó de ping em que o hider acabou de chegar, ou -1; fica de pé até <see cref="ConsumeArrival"/>.
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

            if (_animationSystem == null)
                _animationSystem = GetComponentInChildren<GraphHiderAnimationSystem>();
            if (_animationSystem != null)
                _animationSystem.Initialize();
        }

        /// <summary>
        /// Chamar DEPOIS do spawn do seeker (o hider nasce longe dele). Modo None desativa o GameObject.
        /// Velocidade e estamina &lt;= 0 na lição usam as do Inspector.
        /// </summary>
        public void ResetEpisode(in GraphEpisodeSettings settings, Transform seeker)
        {
            _seeker = seeker;
            _mode = settings.HiderMode;
            _loose = settings.HiderLoose;
            _stamina.Reset(settings.HiderStamina);
            IsRunning = false;
            _viaCenter = false;
            _goalIsHiding = false;
            _currentSpeed = settings.HiderSpeed > 0f ? settings.HiderSpeed : _speed;
            _noiseChance = Mathf.Clamp01(settings.HiderNoise);
            PendingArrival = -1;
            _previousNode = -1;
            _targetNode = -1;
            _pauseLeft = 0;
            if (_animationSystem != null)
                _animationSystem.ResetEpisode();

            if (_graph == null || _mode == Mode.None)
            {
                gameObject.SetActive(false);
                return;
            }

            _graph.EnsureBaked();
            gameObject.SetActive(true);

            _currentNode = PickSpawnNode(seeker.position);
            if (_currentNode < 0)
            {
                gameObject.SetActive(false);
                return;
            }

            Place(_loose ? PointInNode(_currentNode, hide: true, from: _graph.NodePosition(_currentNode)) : _graph.NodePosition(_currentNode));

            MakeNoiseAt(_currentNode);

            if (_mode == Mode.Static)
                return;

            ChooseNextNode();
            SetGoalForTarget();
        }

        /// <summary>Tira o hider de cena (modo de jogo: quem foge é o jogador).</summary>
        public void Deactivate()
        {
            _mode = Mode.None;
            PendingArrival = -1;
            IsRunning = false;
            gameObject.SetActive(false);
        }

        private void FixedUpdate()
        {
            if (_mode == Mode.None)
                return;

            bool moved = _mode != Mode.Static && _graph != null && StepMovement();
            if (_animationSystem != null)
                _animationSystem.Tick(moved, moved && IsRunning);
        }

        // Um step de física da navegação. Devolve se o corpo andou (para a animação: parado, pausado ou chegando = idle).
        private bool StepMovement()
        {
            // Antes da pausa: parado também recupera estamina.
            IsRunning = _stamina.Step(_mode == Mode.Flee && SeekerIsNear(), Time.fixedDeltaTime);

            if (_pauseLeft > 0)
            {
                _pauseLeft--;
                return false;
            }

            if (_targetNode < 0)
            {
                ChooseNextNode();
                SetGoalForTarget();
                return false;
            }

            Vector3 goal = _goal + Vector3.up * _heightOffset;
            Vector3 delta = goal - transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;

            if (distance <= _arriveDistance)
            {
                if (_viaCenter)
                {
                    _viaCenter = false;
                    _goal = _finalGoal;
                    return true;
                }

                Arrive();
                return false;
            }

            float speed = IsRunning ? _currentSpeed : _currentSpeed * _walkFraction;
            Vector3 step = delta / distance * speed * Time.fixedDeltaTime;
            if (step.magnitude > distance)
                step = delta;

            Move(transform.position + step, delta);
            return true;
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

        // Destino no próximo nó: o centro ou, solto, um ponto dentro dele. Se a reta até o ponto bate
        // em parede, passa antes pelo centro do nó atual (as ligações só garantem a reta entre centros).
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

        // Ponto livre no retângulo do nó (porta: sempre o centro). Escondendo, o visto por menos portas
        // da sala entre _hideCandidates sorteados. Sem ponto válido, o centro.
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

        // Anda: vizinho aleatório, evitando voltar por onde veio. Foge, com o seeker perto: o vizinho
        // que mais aumenta a distância pelo grafo até o nó do seeker.
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

        // Nó de spawn: sala diferente da do seeker (NavGraph.RandomSpawnNode) a
        // >= _minSpawnDistanceMeters dele; mapa pequeno demais aceita o último sorteado.
        private int PickSpawnNode(Vector3 seekerPosition)
        {
            int seekerNode = _graph.FindNearestReachableNode(seekerPosition);
            int seekerRoom = seekerNode >= 0 ? _graph.RoomOf(seekerNode) : -1;
            int fallback = -1;

            for (int attempt = 0; attempt < _graph.RoomCount * 2; attempt++)
            {
                int candidate = _graph.RandomSpawnNode(seekerRoom);
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

        // Gizmo verde-azulado escuro: linha até o destino atual.
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _graph == null || _mode == Mode.None || _targetNode < 0)
                return;

            Gizmos.color = new Color(0f, 0.55f, 0.5f, 0.9f);
            Gizmos.DrawLine(transform.position, _goal + Vector3.up * _heightOffset);
        }
    }
}
