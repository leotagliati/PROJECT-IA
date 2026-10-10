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
    /// Solto (hider_loose): vai a pontos aleatórios dentro do nó, longe do centro (portas inclusive), e, às
    /// vezes, a um esconderijo (ponto pouco visível das portas da sala), onde fica parado mais tempo. O centro
    /// do nó atual só é usado como passagem quando a reta até o ponto bate em parede.
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
        // JOGADOR no treino: 10.2 = a corrida do PlayerDummy (6 x 1.7); o seeker persegue a 10 (GraphLocomotion).
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

        // Segundos que ele CONTINUA fugindo depois que o seeker sai do _fleeRadius (06/10). Sem isso, na borda dos
        // 12 m ele alternava fugir (correndo) e vaguear (andando, vizinho aleatório, às vezes de volta para o
        // seeker), e parecia hesitar.
        [SerializeField, Min(0f)] private float _fleeMemorySeconds = 3f;

        // Steps de física entre as checagens de "estou correndo para o seeker" no meio de uma aresta (custa um
        // Dijkstra). 10 = 0.2 s.
        [SerializeField, Min(1)] private int _reverseCheckSteps = 10;

        // Foge também ao VER o seeker (linha livre contra parede, em qualquer direção) até esta distância (m), não só
        // dentro do _fleeRadius (06/10). Um jogador corre assim que vê o monstro; o hider esperava ele chegar a 12 m.
        // 0 = só o raio.
        [SerializeField, Min(0f)] private float _fleeSightDistance = 25f;

        // Na escolha da fuga, vizinho FORA da vista do seeker vale como se estivesse estes metros mais longe (06/10):
        // dobrar a esquina ou passar a porta ganha de seguir reto num corredor à vista. Antes ele só maximizava a
        // distância pelo grafo e fugia em linha reta na frente do monstro.
        [SerializeField, Min(0f)] private float _hiddenExitBonus = 10f;

        // Altura (m) acima do pivô do seeker da linha de visão (a mesma de GraphHiderPerception._eyeHeight); a
        // vista é testada nessa altura sobre o X/Z dos dois pontos, como a visão do monstro.
        [SerializeField] private float _seekerEyeHeight = 1.67f;

        // Até onde (m) o seeker vê, para "está à vista": além disso conta como escondido (GraphHiderPerception, 22).
        [SerializeField, Min(1f)] private float _seekerViewDistance = 22f;

        [Header("-----Solto e escondido (hider_loose)-----")]
        // Margem (m) entre o ponto sorteado e a borda do retângulo do nó. Em porta e em nó pequeno demais para ela,
        // vale só o raio do corpo (NavGraph.LinkClearance): com 0.6 o retângulo encolhia a zero e sobrava o centro.
        [SerializeField, Min(0f)] private float _looseMargin = 0.6f;

        // Distância mínima (m) do ponto sorteado ao centro do nó (08/10). O jogador nunca anda pelos centros, e o
        // seeker treinado contra um hider que parava neles ia até o NÓ do jogador e ficava no meio dele. Ponto mais
        // perto que isto só entra se nenhum sorteio passar; o centro exato, só se nem isso.
        [SerializeField, Min(0f)] private float _minCenterOffset = 0.6f;

        // Pontos sorteados por destino quando não está se escondendo (era 2: com parede e móvel no nó, os dois
        // falhavam com frequência e o destino caía no centro).
        [SerializeField, Min(1)] private int _looseCandidates = 8;

        // Chance de o próximo ponto ser um esconderijo (o menos visível das portas da sala).
        [SerializeField, Range(0f, 1f)] private float _hideChance = 0.5f;

        // Pontos sorteados para escolher o esconderijo.
        [SerializeField, Min(1)] private int _hideCandidates = 6;

        // Multiplicador da pausa quando escondido.
        [SerializeField, Min(1f)] private float _hidePauseMultiplier = 3f;

        // FORA DOS NÓS (09/10): chance de um esconderijo ficar FORA da área de qualquer nó (canto de corredor sem nó,
        // atrás de móvel), até _offNodeReach metros além do retângulo do nó. O jogador se esconde aí, e o seeker
        // treinado só contra pontos dentro dos nós não sabia chegar (a rota NavMesh da GraphHiderPerception mostra o
        // caminho; isto ensina a política a segui-la até um canto). Precisa de linha livre do centro do nó (mesmo
        // espaço, sem atravessar parede); sem ponto assim, fica o esconderijo normal dentro do nó.
        [SerializeField, Range(0f, 1f)] private float _offNodeChance = 0.4f;
        [SerializeField, Min(0f)] private float _offNodeReach = 4f;

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

        // Fugindo neste step (seeker perto, ou saiu dele há menos de _fleeMemorySeconds). Atualizado uma vez por step.
        private bool _fleeing;
        private int _fleeMemoryLeft;
        private bool _reversedThisEdge;
        private int _moveSteps;

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
            _fleeing = false;
            _fleeMemoryLeft = 0;
            _reversedThisEdge = false;
            _moveSteps = 0;
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
            _moveSteps++;
            UpdateFleeing();

            // Antes da pausa: parado também recupera estamina.
            IsRunning = _stamina.Step(_fleeing, Time.fixedDeltaTime);

            // Fugindo não pausa: a pausa ao chegar no nó é do vaguear, e no meio de uma fuga era o seeker ganhando
            // até 2 s de graça a cada nó.
            if (_pauseLeft > 0 && !_fleeing)
            {
                _pauseLeft--;
                return false;
            }

            _pauseLeft = 0;

            if (_targetNode < 0)
            {
                ChooseNextNode();
                SetGoalForTarget();
                return false;
            }

            ReverseIfRunningIntoSeeker();

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
            _reversedThisEdge = false;

            MakeNoiseAt(_currentNode);

            _pauseLeft = _maxPauseSteps > 0 && !_fleeing ? Random.Range(0, _maxPauseSteps + 1) : 0;
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

            bool hide = !_fleeing && Random.value < _hideChance;
            Vector3 here = transform.position - Vector3.up * _heightOffset;
            Vector3 point = PointInNode(_targetNode, hide, _graph.NodePosition(_targetNode));
            _goalIsHiding = hide;

            if (_graph.IsSegmentClear(here, point))
            {
                _goal = point;
                return;
            }

            // O centro do nó atual é só passagem (sem pausa). O destino final é refeito a partir dele em vez de cair
            // no centro do nó de destino, como antes.
            Vector3 currentCenter = _graph.NodePosition(_currentNode);
            _viaCenter = true;
            _finalGoal = _graph.IsSegmentClear(currentCenter, point) ? point : PointInNode(_targetNode, false, currentCenter);
            _goalIsHiding = hide && _finalGoal == point;
            _goal = currentCenter;
        }

        // Ponto livre no retângulo do nó, a >= _minCenterOffset do centro (porta também: o vão inteiro, não só o
        // meio). Escondendo, o visto por menos portas da sala entre os sorteados. Sem ponto válido longe do centro,
        // o primeiro válido mais perto; sem nenhum, o centro.
        private Vector3 PointInNode(int node, bool hide, Vector3 from)
        {
            Vector3 center = _graph.NodePosition(node);
            float radius = _graph.LinkClearance;
            bool isDoor = _graph.IsDoor(node);

            NavNode navNode = _graph.GetNode(node);
            Vector3 areaCenter = _graph.AreaCenterOf(navNode);
            Vector2 fullHalf = _graph.HalfExtentsOf(navNode);
            float margin = isDoor ? radius : _looseMargin;
            Vector2 half = new Vector2(Mathf.Max(0f, fullHalf.x - margin), Mathf.Max(0f, fullHalf.y - margin));
            if (half.x < _minCenterOffset && half.y < _minCenterOffset)
                half = new Vector2(Mathf.Max(0f, fullHalf.x - radius), Mathf.Max(0f, fullHalf.y - radius));

            // Esconder só faz sentido em chão de sala; na porta o ponto é só para não passar pelo meio do vão.
            hide = hide && !isDoor;

            int room = _graph.RoomOf(node);
            int[] doors = room >= 0 ? _graph.DoorsOfRoom(room) : null;

            if (hide && Random.value < _offNodeChance
                && TryOffNodePoint(center, areaCenter, fullHalf, radius, doors, out Vector3 offNode))
                return offNode;

            int tries = hide ? Mathf.Max(_hideCandidates, _looseCandidates) : _looseCandidates;
            Vector3 best = center;
            bool found = false;
            int bestSeen = int.MaxValue;
            Vector3 near = center;
            bool haveNear = false;

            for (int k = 0; k < tries; k++)
            {
                var point = new Vector3(
                    areaCenter.x + Random.Range(-half.x, half.x),
                    center.y,
                    areaCenter.z + Random.Range(-half.y, half.y));

                if (!_graph.IsBodyClear(point, radius) || !_graph.IsSegmentClear(from, point))
                    continue;

                Vector3 offset = point - center;
                offset.y = 0f;
                if (offset.magnitude < _minCenterOffset)
                {
                    if (!haveNear)
                    {
                        near = point;
                        haveNear = true;
                    }

                    continue;
                }

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
                    found = true;
                }
            }

            if (found)
                return best;

            return haveNear ? near : center;
        }

        // Esconderijo FORA de toda área de nó, no anel de até _offNodeReach em volta do retângulo do nó: corpo cabe, reta
        // livre do centro do nó (mesmo espaço) e, entre os válidos, o visto por menos portas da sala.
        private bool TryOffNodePoint(Vector3 center, Vector3 areaCenter, Vector2 half, float radius, int[] doors, out Vector3 point)
        {
            point = center;
            bool found = false;
            int bestSeen = int.MaxValue;
            Vector2 outer = half + Vector2.one * _offNodeReach;
            int tries = Mathf.Max(_hideCandidates, _looseCandidates) * 2;

            for (int k = 0; k < tries; k++)
            {
                var candidate = new Vector3(
                    areaCenter.x + Random.Range(-outer.x, outer.x),
                    center.y,
                    areaCenter.z + Random.Range(-outer.y, outer.y));

                if (_graph.FindNodeAt(candidate) >= 0)
                    continue;

                if (!_graph.IsBodyClear(candidate, radius) || !_graph.IsSegmentClear(center, candidate))
                    continue;

                int seen = 0;
                if (doors != null)
                {
                    foreach (int door in doors)
                    {
                        if (_graph.CanSeeFromNode(_graph.NodePosition(door), candidate))
                            seen++;
                    }
                }

                if (seen < bestSeen)
                {
                    bestSeen = seen;
                    point = candidate;
                    found = true;
                }
            }

            return found;
        }

        // Fugir com memória: perto do seeker liga e recarrega _fleeMemorySeconds; longe, gasta a memória antes de
        // voltar a vaguear.
        private void UpdateFleeing()
        {
            if (_mode != Mode.Flee)
            {
                _fleeing = false;
                return;
            }

            if (SeekerIsNear() || SeesSeeker())
                _fleeMemoryLeft = Mathf.RoundToInt(_fleeMemorySeconds / Time.fixedDeltaTime);
            else if (_fleeMemoryLeft > 0)
                _fleeMemoryLeft--;

            _fleeing = _fleeMemoryLeft > 0;
        }

        // O destino é escolhido só ao chegar no nó: se o seeker aparece na frente no meio da aresta, ele seguia
        // correndo para o seeker até o nó. Fugindo, se o nó de destino está mais perto do seeker (pelo grafo) que
        // o nó de onde saiu, dá meia-volta, uma vez por aresta (sem isso, com o seeker parado entre os dois, ele
        // ia e voltava no lugar).
        private void ReverseIfRunningIntoSeeker()
        {
            if (!_fleeing || _reversedThisEdge || _seeker == null || _currentNode < 0 || _targetNode == _currentNode
                || _moveSteps % _reverseCheckSteps != 0)
                return;

            int seekerNode = _graph.FindNearestReachableNode(_seeker.position);
            if (seekerNode < 0
                || !_graph.TryFindPathTo(_targetNode, seekerNode, out _, out float ahead)
                || !_graph.TryFindPathTo(_currentNode, seekerNode, out _, out float behind)
                || ahead >= behind - 0.5f)
                return;

            _targetNode = _currentNode;
            _reversedThisEdge = true;
            SetGoalForTarget();
        }

        // O hider vê o seeker: linha livre de parede até _fleeSightDistance, em qualquer direção (a presa está atenta).
        private bool SeesSeeker()
        {
            if (_seeker == null || _fleeSightDistance <= 0f)
                return false;

            Vector3 toSeeker = _seeker.position - transform.position;
            toSeeker.y = 0f;
            return toSeeker.magnitude <= _fleeSightDistance && LineIsClear(transform.position, _seeker.position);
        }

        // O seeker veria este ponto se olhasse para ele (sem cone: a cabeça dele vira rápido)?
        private bool SeekerCanSee(Vector3 point)
        {
            if (_seeker == null)
                return false;

            Vector3 toPoint = point - _seeker.position;
            toPoint.y = 0f;
            return toPoint.magnitude <= _seekerViewDistance && LineIsClear(point, _seeker.position);
        }

        // Linha livre de parede entre dois pontos, na altura do olho do seeker sobre o X/Z de cada um.
        private bool LineIsClear(Vector3 a, Vector3 b)
        {
            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            float eyeY = _seeker.position.y + _seekerEyeHeight;
            Vector3 from = new Vector3(a.x, eyeY, a.z);
            Vector3 ray = new Vector3(b.x, eyeY, b.z) - from;
            float length = ray.magnitude;
            return length < 1e-3f || !Physics.Raycast(from, ray / length, length, walls, QueryTriggerInteraction.Ignore);
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
                if (_fleeing || SeekerIsNear())
                {
                    int seekerNode = _graph.FindNearestReachableNode(_seeker.position);
                    int best = -1;
                    float bestDistance = -1f;

                    foreach (int neighbor in neighbors)
                    {
                        if (!_graph.IsNodeEnabled(neighbor))
                            continue;

                        float distance = seekerNode >= 0 && _graph.TryFindPathTo(neighbor, seekerNode, out _, out float d) ? d : 0f;
                        if (_hiddenExitBonus > 0f && !SeekerCanSee(_graph.NodePosition(neighbor)))
                            distance += _hiddenExitBonus;

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
                // Cinemático (o PlayerDummy de treino) não tem velocidade, e o Unity 6 avisa a cada reset se zerar.
                if (!_rigidbody.isKinematic)
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
