using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A VISÃO do seeker sobre o hider: cone à frente + linha livre de parede, a memória da última
    /// posição vista e a CAPTURA (hider ao alcance, sem parede no meio). É local de propósito:
    /// fora do cone ou atrás de parede o seeker não sabe onde o hider está.
    ///
    /// Observação [16..20]: vendo, já viu, direção X/Z e distância à última posição vista (congela
    /// ao perder de vista). "Vendo" inclui o RASTRO: depois de ver, segue com a posição real por um tempo
    /// sem linha livre (_trackMemorySeconds). Paga, no GraphRewardSystem: avistar (com cooldown), metros de
    /// aproximação enquanto vê e capturar (terminal).
    ///
    /// Também é dona da APROXIMAÇÃO (distância na decisão anterior), que só vale vendo nas duas.
    /// Um por agente; Tick a cada step de física. CanSeePoint também é usado pela GraphRoomMemory
    /// e pela GraphSuspicionMap. O alvo (hider ou jogador) vem da arena no Configure.
    /// </summary>
    public class GraphHiderPerception : MonoBehaviour
    {
        [Header("-----Cone-----")]
        // Abertura TOTAL do cone, em graus, em torno de para onde a CABEÇA olha (GraphLocomotion: o
        // olhar [2..3] vira o pescoço em relação ao corpo, que segue o movimento).
        // 140 (10/10; era 100): no jogo o jogador passava pelo lado do monstro sem ser visto.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;

        // Alcance da visão, em metros. 35 (10/10; era 22, e 15 até 06/10): a sala grande (S24 ~32 x 36 m) fica toda
        // à vista da porta. Também é o alcance com que a visão conclui salas e limpa suspeita: a patrulha cobre o mapa
        // mais rápido, e o v5.2 foi treinado com 22 (testar no jogo antes de treinar com isto).
        [SerializeField, Min(1f)] private float _viewDistance = 35f;

        // Altura (m) dos olhos acima do pivô (o pé) do seeker; o ponto olhado fica nessa mesma altura (AtEyeLevel).
        // Mobília bloqueia a visão: baixo demais, qualquer mesa tapa o cone.
        [SerializeField] private float _eyeHeight = 1.67f;

        [Header("-----Visão do ALVO (não vale para nós)-----")]
        // Já VENDO o alvo, o cone para ele abre e alcança mais (06/10): o monstro "trava" no jogador. Com o cone de
        // 100° seguindo a cabeça, um passo de lado a 3 m já tirava o jogador da vista. 200° / 45 m (10/10; era 160° /
        // 30 m), na mesma proporção do cone normal. Só vale para manter, não para avistar: a primeira vista continua
        // sendo o cone normal.
        [SerializeField, Range(10f, 360f)] private float _lockedViewAngle = 200f;
        [SerializeField, Min(1f)] private float _lockedViewDistance = 45f;

        // Perto assim (m), vê o alvo em qualquer direção (com linha livre): ouve e sente quem está colado nele.
        [SerializeField, Min(0f)] private float _closeSenseDistance = 4f;

        // Meia largura (m) do alvo: as colunas da grade do corpo vão de um ombro ao outro (até 90% do raio do collider).
        // Com um raio só, o batente de uma porta ou a quina de um móvel escondia o jogador inteiro.
        [SerializeField, Min(0f)] private float _targetHalfWidth = 0.35f;

        // Quanto abaixo do topo do collider do alvo fica a CABEÇA mirada (m). Os raios saem do olho e descem até o
        // CORPO do alvo (cabeça e peito, 06/10), não mais até a altura do olho sobre o X/Z dele: daquele jeito a
        // linha passava por cima de qualquer móvel mais baixo que o olho (2.78 m na arena) e o monstro via o jogador
        // agachado atrás de um armário. Agora a mesa não esconde (a linha até a cabeça passa por cima), um armário
        // alto entre os dois esconde. Sem collider no alvo, volta ao teste antigo na altura do olho.
        [SerializeField, Min(0f)] private float _targetHeadOffset = 0.15f;

        [Header("-----Visão do alvo em 3D (como uma câmera)-----")]
        // O CORPO do alvo é amostrado numa grade (linhas da cabeça até perto do pé x colunas de ombro a ombro, de frente
        // para o olho), e cada ponto é testado como numa câmera: dentro do cone na horizontal, dentro do
        // _verticalViewAngle na vertical, e com linha livre. Vê com _minVisibleSamples pontos livres. 14/10: com só
        // cabeça + peito + ombros na mesma vertical, um móvel na frente da linha do meio escondia o jogador EM PÉ que,
        // pelo ângulo, aparecia de lado ou por cima.
        [SerializeField, Range(1, 8)] private int _bodyRows = 4;
        [SerializeField, Range(1, 5)] private int _bodyColumns = 3;

        // Abertura vertical TOTAL (graus), centrada na horizontal (a cabeça do monstro não inclina). 90 = 45° para baixo:
        // com o olho a 2.78 m, o pé do alvo entra no quadro a partir de ~2.8 m. Colado (_closeSenseDistance) não vale.
        [SerializeField, Range(10f, 180f)] private float _verticalViewAngle = 90f;

        // Pontos livres para contar como VISTO. 2 (de 12): uma ponta de ombro na quina não entrega o jogador; meia
        // cabeça por cima de um móvel, sim.
        [SerializeField, Min(1)] private int _minVisibleSamples = 2;

        // Fração da altura do alvo, a partir do pé, da linha mais baixa (o pé em si fica fora: rente ao chão qualquer
        // rodapé tapa).
        [SerializeField, Range(0f, 0.5f)] private float _bodyBottomFraction = 0.15f;

        [Header("-----Depuração-----")]
        // Desenha a "câmera" do monstro (o quadro 3D) e os raios até o corpo do alvo SEM precisar selecionar o objeto.
        // Só Gizmos: nada disso existe no build. Na Game view, ligue o botão Gizmos.
        [SerializeField] private bool _alwaysDrawVision = true;

        [Header("-----Rastro: depois de ver, não solta na hora-----")]
        // Depois de VER o alvo, o seeker segue "vendo" (posição real, Perseguição, cone travado) por _trackMemorySeconds
        // sem linha livre; recarrega a cada step com linha livre. 3 s (09/10; era 0.5 s, _trackGraceSteps 25): o jogador
        // dobrava uma esquina e o monstro ia até onde ele SUMIU, já com ele 10-20 m adiante. 3 s = o _chaseHoldSeconds
        // da locomoção; depois a previsão (2 s) e a audição de corrida (30 m pelo grafo) assumem. Para a rede é a mesma
        // perseguição de sempre ([16] = 1, posição real), por isso o 50M usa sem treinar.
        // Testado e recusado no mesmo dia: 5-15 s e "sentir" o alvo a < 6-15 m através de parede (exagero, disse o
        // Arthur: esconder perto tem que funcionar).
        [SerializeField, Min(0f)] private float _trackMemorySeconds = 3f;

        [Header("-----Rota até o alvo (NavMesh)-----")]
        // A observação [18..20] aponta para a próxima QUINA do caminho NavMesh até o alvo, não em linha reta (09/10):
        // vendo o jogador por cima de uma mesa, ou com ele num canto sem nó, a reta mandava o monstro para dentro do
        // móvel. O NavMesh cobre o chão livre todo, então a perseguição não fica presa aos nós. Com reta livre a quina
        // é o próprio alvo (igual a antes, por isso o 50M usa sem treinar). Sem NavMesh assado na arena, volta à reta.
        // Só a ROTA é calculada: o alvo é o que ele viu (ou rastreia), e nada paga seguir a rota.
        [SerializeField] private bool _useNavMeshRoute = true;

        // Raio (m) para achar o NavMesh sob o seeker e sob o alvo (pivô no pé; móvel encostado pode empurrar o ponto).
        [SerializeField, Min(0.1f)] private float _navMeshSnap = 2f;

        // Quina mais perto que isto (m) já foi alcançada: aponta para a seguinte, senão a direção gira em cima dela.
        [SerializeField, Min(0f)] private float _cornerReached = 0.5f;

        [Header("-----Procura depois de perder de vista-----")]
        // Segundos depois de perder o alvo de vista em que o seeker fica em PROCURA: a observação de exploração
        // segue zerada e só suspeita e ping contam (GraphObservations). Passou disso sem ver, volta a ver o mapa
        // para explorar. 20 s = a perseguição que segura (3 s) + boa parte do alerta (10 s) + folga.
        [SerializeField, Min(0f)] private float _searchFocusSeconds = 20f;

        // Chegou a menos disto (m) do lugar em que o alvo sumiu (a previsão, já parada) sem achá-lo: ali já foi
        // conferido e a procura acaba (IsSearchingLastKnown). Sem isso a direção até a última vista ficava ligada o
        // episódio inteiro e puxava o monstro de volta: no v8.2_mapa_04 ele perdia o jogador numa sala, ia para a do
        // lado e voltava para onde o perdeu, em loop (14/10). Só a FreeExplorer usa; a GraphExplorer segue no IsSearching.
        [SerializeField, Min(0.5f)] private float _lastKnownReachedDistance = 3f;

        [Header("-----Captura-----")]
        // Distância planar (m) entre centros em que o hider conta como pego (com linha livre).
        // Não exige o cone nem trombar de frente.
        [SerializeField, Min(0.5f)] private float _captureDistance = 2.5f;

        [Header("-----Métricas-----")]
        // Steps de física sem ver para contar como "perdeu de vista" (Hunt/LostSight). 50 = 1 s: o raio até o centro
        // do hider pisca numa quina ou atrás de uma coluna, e cada piscada não é uma perda de verdade.
        [SerializeField, Min(1)] private int _lostSightSteps = 50;

        // O hider no treino, o jogador no modo de jogo (GraphArenaController.Target).
        private IGraphTarget _target;

        private NavGraph _graph;
        private int _lastSeenStep = int.MinValue;
        private int _lastVisibleStep = int.MinValue;
        private int _step;

        /// <summary>Vendo o hider agora (dentro do cone, com linha de visão livre).</summary>
        public bool IsSeeing { get; private set; }

        /// <summary>Alcance do cone (m). A GraphRoomMemory filtra por ele antes de gastar raycast.</summary>
        public float ViewDistance => _viewDistance;

        /// <summary>Já viu o hider neste episódio.</summary>
        public bool HasSeen { get; private set; }

        /// <summary>
        /// Perdeu o alvo de vista há menos de _searchFocusSeconds: procurando ELE, não o mapa. Falso vendo (aí é
        /// perseguição) e antes da primeira vista.
        /// </summary>
        public bool IsSearching => HasSeen && !IsSeeing && (_step - _lastSeenStep) * Time.fixedDeltaTime < _searchFocusSeconds;

        /// <summary>Já foi até onde o alvo sumiu (a previsão) e não o achou; volta a falso ao ver de novo.</summary>
        public bool LastKnownChecked { get; private set; }

        /// <summary>
        /// Procurando E ainda sem ter conferido o lugar em que o alvo sumiu: a única fase em que a última vista é pista.
        /// Depois de conferir, ela não diz mais nada (o alvo não está lá) e o monstro tem que procurar em outro lugar.
        /// </summary>
        public bool IsSearchingLastKnown => IsSearching && !LastKnownChecked;

        /// <summary>Última posição em que viu (a atual, enquanto vê). Válida só com HasSeen.</summary>
        public Vector3 LastSeenPosition { get; private set; }

        /// <summary>Distância planar ao hider AGORA. Válida só com IsSeeing.</summary>
        public float CurrentDistance { get; private set; }

        /// <summary>
        /// Velocidade planar do hider (m/s), medida entre dois steps seguidos de visão. Zero sem
        /// ver ou no primeiro step de visão.
        /// </summary>
        public Vector3 HiderVelocity { get; private set; }

        private Vector3 _previousSeenPosition;
        private bool _hasPreviousSeenPosition;

        // PREVISÃO (v5.2, 08/10): ao perder de vista (depois do rastro), a última posição é projetada com a velocidade
        // que ele VIU, por até _predictSeconds e parando antes de parede. A observação [18..20] aponta para isso em vez
        // da última posição parada: o 50M corria até onde o jogador sumiu e o procurava ali, com ele já 20 m adiante.
        // Só usa o que o monstro viu ou rastreou (posição e velocidade), e nada paga ir até lá.
        [Header("-----Previsão ao perder de vista-----")]
        // Segundos de projeção (2 = ~20 m do jogador correndo). Depois disso o ponto para: chute longo erra mais.
        [SerializeField, Min(0f)] private float _predictSeconds = 2f;

        // Suavização (s) da velocidade vista: a medida por step pisca no meio de uma curva ou quina.
        [SerializeField, Min(0.01f)] private float _velocitySmoothing = 0.3f;

        // Folga (m) antes da parede onde a projeção para.
        [SerializeField, Min(0f)] private float _predictWallMargin = 0.6f;

        private Vector3 _smoothedVelocity;

        /// <summary>
        /// Onde o alvo deve estar: a posição atual enquanto vê (ou rastreia); sem ver, a última conhecida projetada
        /// com a velocidade vista (até _predictSeconds, parando antes de parede). Válida só com HasSeen.
        /// </summary>
        public Vector3 PredictedPosition
        {
            get
            {
                if (IsSeeing || !HasSeen || _predictSeconds <= 0f)
                    return LastSeenPosition;

                float seconds = Mathf.Min((_step - _lastSeenStep) * Time.fixedDeltaTime, _predictSeconds);
                Vector3 travel = _smoothedVelocity * seconds;
                float length = travel.magnitude;
                if (length < 1e-3f)
                    return LastSeenPosition;

                // Raio na altura do peito do alvo: a projeção não atravessa parede (a layer do grafo, a mesma da visão).
                LayerMask walls = Walls;
                Vector3 direction = travel / length;
                Vector3 origin = LastSeenPosition + Vector3.up;
                if (walls.value != 0 && Physics.Raycast(origin, direction, out RaycastHit hit, length, walls, QueryTriggerInteraction.Ignore))
                    length = Mathf.Max(0f, hit.distance - _predictWallMargin);

                return LastSeenPosition + direction * length;
            }
        }

        // Só para o TensorBoard (Hunt/Sightings, Hunt/LostSight, Hunt/SightToCatchSeconds); nada disso entra na
        // observação nem na recompensa.
        private int _unseenRun;
        private int _firstSeenStep;

        /// <summary>Vezes que passou a ver o hider no episódio (aquisições separadas por >= _lostSightSteps sem ver).</summary>
        public int EpisodeSightings { get; private set; }

        /// <summary>Vezes que perdeu o hider de vista por pelo menos _lostSightSteps.</summary>
        public int EpisodeLostSight { get; private set; }

        /// <summary>Segundos entre a primeira vez que viu e a captura; negativo se não pegou ou nunca viu.</summary>
        public float SightToCatchSeconds => Caught && HasSeen ? (_step - _firstSeenStep) * Time.fixedDeltaTime : -1f;

        // Direção da cabeça (SetViewDirection). Sem ela, o cone segue o corpo (seeker.forward).
        private Vector3 _viewForward;
        private bool _hasViewDirection;

        /// <summary>
        /// Avistou (não-vendo -> vendo, fora do cooldown) desde o último <see cref="ClearStepFlags"/>.
        /// </summary>
        public bool Spotted { get; private set; }

        /// <summary>
        /// Pegou o hider. Fica de pé até o ResetEpisode: é evento TERMINAL (o GraphRewardSystem paga, o Manager encerra).
        /// </summary>
        public bool Caught { get; private set; }

        // Visão e distância no ClearStepFlags anterior (a decisão anterior, com TakeActionsBetweenDecisions).
        // APROXIMAÇÃO por RECORDE (06/10): paga só os metros que batem a menor distância já vista no episódio.
        // Antes pagava todo metro encurtado vendo, sem descontar o que o hider abria fora da vista; com o hider que
        // foge de verdade (~12 perdas de vista por episódio) cada nova perseguição pagava de novo e perseguir rendia
        // mais que pegar (reward ~100 com a captura caindo de 0.95 para 0.86). Teto: distância da 1ª vista menos a
        // captura (~20-30 m).
        private float _closestSeenDistance = float.PositiveInfinity;
        private float _approachThisStep;

        /// <summary>Bateu o recorde de proximidade neste step (vendo).</summary>
        public bool HasApproach => _approachThisStep > 0f;

        /// <summary>Metros abaixo do recorde de proximidade do episódio desde o último ClearStepFlags (>= 0).</summary>
        public float ApproachDelta => _approachThisStep;

        public void Configure(NavGraph graph, IGraphTarget target) => Configure(graph, target, default);

        /// <summary>
        /// Sem NavGraph (a arena da v8, só NavMesh), a parede da visão e da captura vem de <paramref name="wallsWithoutGraph"/>.
        /// Com grafo vale a máscara dele, como sempre. Máscara vazia = visão atravessando parede.
        /// </summary>
        public void Configure(NavGraph graph, IGraphTarget target, LayerMask wallsWithoutGraph)
        {
            _graph = graph;
            _wallsWithoutGraph = wallsWithoutGraph;
            _target = target;

            // O corpo do alvo (CapsuleCollider do hider, CharacterController do jogador) para mirar cabeça e peito.
            // Com TargetVisionBody (o jogador), a largura mirada é a dele, mais fina.
            _targetCollider = null;
            _targetVisionBody = null;
            if (target is Component component && component != null)
            {
                _targetCollider = component.GetComponentInChildren<Collider>();
                _targetVisionBody = component.GetComponentInChildren<TargetVisionBody>();
            }
        }

        private Collider _targetCollider;
        private TargetVisionBody _targetVisionBody;
        private bool _spottedThisEpisode;
        private LayerMask _wallsWithoutGraph;

        // A máscara de parede da visão, da captura e da previsão: a do grafo, ou a dada no Configure sem grafo.
        private LayerMask Walls => _graph != null ? _graph.WallLayer : _wallsWithoutGraph;

        // Criado na primeira consulta: o construtor do NavMeshPath é nativo e não deve rodar na serialização.
        private NavMeshPath _route;
        private readonly Vector3[] _routeCorners = new Vector3[32];
        private bool _warnedNoNavMesh;

        // Quinas da última rota calculada, só para o gizmo (0 = sem rota: reta ou sem NavMesh).
        private int _routeCount;

        /// <summary>
        /// Para onde correr até a última posição conhecida do alvo (a real enquanto vê ou rastreia): a próxima quina
        /// do caminho NavMesh, ou o próprio alvo com reta livre ou sem NavMesh. <paramref name="distance"/> = metros
        /// pelo caminho (reta sem NavMesh). Válido só com HasSeen.
        /// </summary>
        public Vector3 ChaseAim(Vector3 from, out float distance)
        {
            // Vendo ou rastreando, a posição real; sem ver, a previsão a partir da última conhecida.
            Vector3 goal = PredictedPosition;
            distance = PlanarDistance(from, goal);
            _routeCount = 0;
            if (!_useNavMeshRoute)
                return goal;

            if (!NavMesh.SamplePosition(from, out NavMeshHit start, _navMeshSnap, NavMesh.AllAreas))
            {
                if (!_warnedNoNavMesh)
                {
                    _warnedNoNavMesh = true;
                    Debug.LogWarning($"{name}: sem NavMesh sob o seeker; a rota até o alvo fica em linha reta. " +
                                     "Asse um NavMeshSurface na arena (NodeTraining - V5 training).", this);
                }

                return goal;
            }

            if (!NavMesh.SamplePosition(goal, out NavMeshHit end, _navMeshSnap, NavMesh.AllAreas))
                return goal;

            if (_route == null)
                _route = new NavMeshPath();

            // Parcial (alvo numa ilha do NavMesh) ainda serve: leva o mais perto possível.
            if (!NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, _route)
                || _route.status == NavMeshPathStatus.PathInvalid)
                return goal;

            int count = _route.GetCornersNonAlloc(_routeCorners);
            if (count < 3)
                return goal; // reta livre: o próprio alvo, não o ponto projetado no NavMesh

            _routeCount = count;

            int next = 1;
            if (PlanarDistance(from, _routeCorners[1]) < _cornerReached)
                next = 2;

            distance = PlanarDistance(from, _routeCorners[1]);
            for (int i = 1; i < count - 1; i++)
                distance += PlanarDistance(_routeCorners[i], _routeCorners[i + 1]);

            return next >= count - 1 ? goal : _routeCorners[next];
        }

        private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(b.x - a.x, b.z - a.z).magnitude;

        /// <summary>Para onde a cabeça olha (planar, no mundo); o cone passa a seguir isto, não o corpo.</summary>
        public void SetViewDirection(Vector3 forward)
        {
            forward.y = 0f;
            _hasViewDirection = forward.sqrMagnitude > 1e-6f;
            if (_hasViewDirection)
                _viewForward = forward.normalized;
        }

        public void ResetEpisode()
        {
            IsSeeing = false;
            HasSeen = false;
            LastKnownChecked = false;
            LastSeenPosition = Vector3.zero;
            CurrentDistance = 0f;
            HiderVelocity = Vector3.zero;
            _smoothedVelocity = Vector3.zero;
            _hasPreviousSeenPosition = false;
            _hasViewDirection = false;
            Caught = false;
            _lastSeenStep = int.MinValue;
            _lastVisibleStep = int.MinValue;
            _closestSeenDistance = float.PositiveInfinity;
            _approachThisStep = 0f;
            _spottedThisEpisode = false;
            _step = 0;
            _unseenRun = 0;
            _firstSeenStep = 0;
            EpisodeSightings = 0;
            EpisodeLostSight = 0;
            ClearStepFlags();
        }

        /// <summary>Consome o "avistou" e guarda a distância desta decisão para a próxima aproximação.</summary>
        public void ClearStepFlags()
        {
            Spotted = false;
            _approachThisStep = 0f;
        }

        /// <summary>Desfaz a captura (modo de jogo: encostar no jogador antes de a partida começar).</summary>
        public void ForgetCaught() => Caught = false;

        /// <summary>Chamar a cada step de física, com o transform do seeker.</summary>
        public void Tick(Transform seeker)
        {
            _step++;

            bool visible = GraphTarget.IsLive(_target) && CanSeeTarget(seeker, _target.Position);
            if (visible)
                _lastVisibleStep = _step;

            bool seeing = visible || (IsSeeing && GraphTarget.IsLive(_target) && KeepsTracking());

            if (seeing)
            {
                if (!HasSeen)
                {
                    _firstSeenStep = _step;
                    EpisodeSightings++;
                }
                else if (_unseenRun >= _lostSightSteps)
                {
                    EpisodeSightings++;
                }

                _unseenRun = 0;

                Vector3 delta = _target.Position - seeker.position;
                CurrentDistance = new Vector2(delta.x, delta.z).magnitude;
                LastSeenPosition = _target.Position;
                HasSeen = true;
                LastKnownChecked = false;

                Vector3 moved = LastSeenPosition - _previousSeenPosition;
                HiderVelocity = _hasPreviousSeenPosition
                    ? new Vector3(moved.x, 0f, moved.z) / Time.fixedDeltaTime
                    : Vector3.zero;
                _previousSeenPosition = LastSeenPosition;

                // Primeiro step de uma vista nova: sem medida ainda, a média recomeça do zero (não herda a corrida
                // de quando o perdeu da última vez).
                if (!_hasPreviousSeenPosition)
                    _smoothedVelocity = Vector3.zero;
                else
                    _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, HiderVelocity, Mathf.Clamp01(Time.fixedDeltaTime / _velocitySmoothing));

                _hasPreviousSeenPosition = true;

                // Avistar paga só a PRIMEIRA vista do episódio (06/10): reencontrar depois de deixar escapar virava
                // renda com o hider fugindo de verdade. Achar de novo vale pelo que leva à captura.
                if (!_spottedThisEpisode)
                {
                    Spotted = true;
                    _spottedThisEpisode = true;
                }

                if (CurrentDistance < _closestSeenDistance)
                {
                    if (!float.IsPositiveInfinity(_closestSeenDistance))
                        _approachThisStep += _closestSeenDistance - CurrentDistance;
                    _closestSeenDistance = CurrentDistance;
                }

                _lastSeenStep = _step;
            }
            else
            {
                // Sem posição anterior, a próxima aquisição não vira um salto de velocidade absurdo.
                HiderVelocity = Vector3.zero;
                _hasPreviousSeenPosition = false;

                _unseenRun++;
                if (HasSeen && _unseenRun == _lostSightSteps)
                    EpisodeLostSight++;

                // Conferiu onde ele sumiu: só depois de a previsão parar (antes disso o ponto ainda anda).
                if (HasSeen && !LastKnownChecked
                    && (_step - _lastSeenStep) * Time.fixedDeltaTime >= _predictSeconds
                    && PlanarDistance(seeker.position, PredictedPosition) <= _lastKnownReachedDistance)
                {
                    LastKnownChecked = true;
                }
            }

            IsSeeing = seeing;

            if (!Caught && GraphTarget.IsLive(_target) && IsWithinReach(seeker, _target.Position))
                Caught = true;
        }

        // Rastro sem linha livre (só chamado já vendo, então _lastVisibleStep é de verdade).
        private bool KeepsTracking() => (_step - _lastVisibleStep) * Time.fixedDeltaTime <= _trackMemorySeconds;

        private bool IsWithinReach(Transform seeker, Vector3 hiderPosition)
        {
            Vector3 delta = hiderPosition - seeker.position;
            if (new Vector2(delta.x, delta.z).magnitude > _captureDistance)
                return false;

            // Pegar através de parede fina não conta.
            LayerMask walls = Walls;
            if (walls.value == 0)
                return true;

            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = AtEyeLevel(hiderPosition, eye);
            Vector3 ray = target - eye;
            return !Physics.Raycast(eye, ray.normalized, ray.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// O ponto está no cone, ao alcance e com linha livre de parede? É o teste que decide se o
        /// hider foi visto e que a GraphSuspicionMap/GraphRoomMemory aplicam aos nós.
        /// </summary>
        public bool CanSeePoint(Transform seeker, Vector3 point)
        {
            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = AtEyeLevel(point, eye);
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            if (distance > _viewDistance || distance < 1e-3f)
                return false;

            Vector3 forward = ViewForward(seeker);
            if (Vector3.Angle(forward, planar) > _viewAngle * 0.5f)
                return false;

            // Mesma máscara que valida as ligações do grafo; inclui a mobília, por isso _eyeHeight importa.
            LayerMask walls = Walls;
            if (walls.value == 0)
                return true;

            return !Physics.Raycast(eye, delta.normalized, delta.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // Visão do ALVO: como CanSeePoint, mas já vendo o cone abre (_lockedViewAngle/_lockedViewDistance), colado
        // (_closeSenseDistance) não precisa de cone, e a linha livre vale para o centro OU um dos ombros.
        private bool CanSeeTarget(Transform seeker, Vector3 point)
        {
            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = AtEyeLevel(point, eye);
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            float range = IsSeeing ? Mathf.Max(_viewDistance, _lockedViewDistance) : _viewDistance;
            if (distance > range || distance < 1e-3f)
                return false;

            if (distance > _closeSenseDistance)
            {
                float angle = IsSeeing ? Mathf.Max(_viewAngle, _lockedViewAngle) : _viewAngle;
                if (Vector3.Angle(ViewForward(seeker), planar) > angle * 0.5f)
                    return false;
            }

            LayerMask walls = Walls;
            if (walls.value == 0)
                return true;

            Vector3 side = Vector3.Cross(Vector3.up, planar / distance) * _targetHalfWidth;

            if (!TryGetTargetBody(out Bounds body))
            {
                // Sem corpo para mirar: linha horizontal na altura do olho, como os nós.
                return IsClear(eye, target, walls)
                       || (_targetHalfWidth > 0f && (IsClear(eye, target + side, walls) || IsClear(eye, target - side, walls)));
            }

            // Corpo inteiro em 3D: basta _minVisibleSamples pontos dentro do quadro e com linha livre.
            bool framed = distance > _closeSenseDistance;
            float halfAngle = (IsSeeing ? Mathf.Max(_viewAngle, _lockedViewAngle) : _viewAngle) * 0.5f;
            return CountVisibleSamples(eye, ViewForward(seeker), framed, halfAngle, body, walls,
                                       _minVisibleSamples, false) >= _minVisibleSamples;
        }

        // O corpo mirado: o fino (TargetVisionBody) se houver, senão o collider. False = sem corpo.
        private bool TryGetTargetBody(out Bounds body)
        {
            if (_targetVisionBody != null && _targetVisionBody.IsUsable)
            {
                body = _targetVisionBody.Bounds;
                return true;
            }

            bool usable = _targetCollider != null && _targetCollider.enabled;
            body = usable ? _targetCollider.bounds : default;
            return usable;
        }

        /// <summary>
        /// Pontos da grade do corpo (<paramref name="body"/>) que o olho vê: dentro do quadro (se <paramref name="framed"/>)
        /// e com linha livre. Para em <paramref name="stopAt"/> (o gizmo passa int.MaxValue e desenha todos).
        /// </summary>
        private int CountVisibleSamples(Vector3 eye, Vector3 forward, bool framed, float halfAngle, Bounds body,
                                        LayerMask walls, int stopAt, bool draw)
        {
            Vector3 center = body.center;
            Vector3 toBody = new Vector3(center.x - eye.x, 0f, center.z - eye.z);
            Vector3 across = toBody.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, toBody.normalized) : Vector3.right;

            // A grade fica de frente para o olho: as colunas vão de ombro a ombro na perpendicular da linha de visão.
            float halfWidth = Mathf.Min(_targetHalfWidth, 0.9f * Mathf.Min(body.extents.x, body.extents.z));
            float top = Mathf.Max(center.y, body.max.y - _targetHeadOffset);
            float bottom = Mathf.Min(top, body.min.y + _bodyBottomFraction * body.size.y);
            int rows = Mathf.Max(1, _bodyRows);
            int columns = Mathf.Max(1, _bodyColumns);
            float cosHalf = Mathf.Cos(Mathf.Min(halfAngle, 180f) * Mathf.Deg2Rad);
            float tanHalfVertical = Mathf.Tan(Mathf.Min(_verticalViewAngle * 0.5f, 89f) * Mathf.Deg2Rad);

            int visible = 0;
            for (int r = 0; r < rows; r++)
            {
                float y = rows == 1 ? top : Mathf.Lerp(top, bottom, (float)r / (rows - 1));
                for (int c = 0; c < columns; c++)
                {
                    float offset = columns == 1 ? 0f : Mathf.Lerp(-halfWidth, halfWidth, (float)c / (columns - 1));
                    Vector3 sample = new Vector3(center.x, y, center.z) + across * offset;

                    if (framed && !InFrame(eye, forward, sample, cosHalf, tanHalfVertical))
                    {
                        if (draw)
                        {
                            Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.25f);
                            Gizmos.DrawLine(eye, sample);
                        }

                        continue;
                    }

                    bool clear = walls.value == 0 || IsClear(eye, sample, walls);
                    if (draw)
                    {
                        Gizmos.color = clear ? new Color(0.4f, 0.7f, 1f, 1f) : new Color(0.5f, 0.5f, 0.5f, 0.9f);
                        Gizmos.DrawLine(eye, sample);
                    }

                    if (clear && ++visible >= stopAt)
                        return visible;
                }
            }

            return visible;
        }

        // Dentro do quadro da "câmera" do monstro: o cone na horizontal e o _verticalViewAngle na vertical.
        private static bool InFrame(Vector3 eye, Vector3 forward, Vector3 point, float cosHalf, float tanHalfVertical)
        {
            Vector3 delta = point - eye;
            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            if (distance < 1e-3f)
                return true;

            return Vector3.Dot(forward, planar / distance) >= cosHalf && Mathf.Abs(delta.y) <= distance * tanHalfVertical;
        }

        private static bool IsClear(Vector3 eye, Vector3 target, LayerMask walls)
        {
            Vector3 ray = target - eye;
            return !Physics.Raycast(eye, ray.normalized, ray.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // O ponto olhado fica na ALTURA DO OLHO, sobre o X/Z do ponto: visão horizontal. Assim não importa em que
        // altura o ponto mora (nó do grafo, que no mapa v4 fica ~1.8 m acima do piso, ou pé do alvo, no piso): o
        // monstro olha reto, e mobília mais alta que o olho tapa. Com o seeker no nível do nó (prefabs antigos) dá o
        // mesmo de antes; mapa de um andar só.
        private static Vector3 AtEyeLevel(Vector3 point, Vector3 eye) => new Vector3(point.x, eye.y, point.z);

        private Vector3 ViewForward(Transform seeker)
        {
            if (_hasViewDirection)
                return _viewForward;

            Vector3 forward = new Vector3(seeker.forward.x, 0f, seeker.forward.z);
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        // Gizmo (azul-claro): o QUADRO da visão em 3D (cone na horizontal x _verticalViewAngle na vertical, até o
        // alcance; mais forte e aberto quando está vendo, o cone travado); em Play, os raios até a grade do corpo do alvo
        // (azul-claro = livre, cinza = bloqueado, cinza apagado = fora do quadro), um círculo no alvo enquanto vê e X na
        // última posição vista quando não vê. Vermelho-vivo: a rota NavMesh que a observação segue quando a reta até o
        // alvo bate em parede ou móvel. _alwaysDrawVision desenha sem selecionar.
        private void OnDrawGizmos()
        {
            if (_alwaysDrawVision)
                DrawVisionGizmos();
        }

        private void OnDrawGizmosSelected()
        {
            if (!_alwaysDrawVision)
                DrawVisionGizmos();
        }

        // O quadro: 4 arestas do olho até os cantos, a borda de cima e de baixo em arco no alcance, e o arco do meio
        // (a altura do olho) para ler o cone de cima.
        private void DrawViewFrame(Vector3 eye, Vector3 forward, float angle, float range, Color color)
        {
            Gizmos.color = color;
            float half = Mathf.Min(angle, 360f) * 0.5f;
            float rise = range * Mathf.Tan(Mathf.Min(_verticalViewAngle * 0.5f, 80f) * Mathf.Deg2Rad);
            const int segments = 16;
            Vector3 previousTop = Vector3.zero, previousMid = Vector3.zero, previousBottom = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float yaw = Mathf.Lerp(-half, half, (float)i / segments);
                Vector3 far = eye + Quaternion.Euler(0f, yaw, 0f) * forward * range;
                Vector3 top = far + Vector3.up * rise;
                Vector3 bottom = far - Vector3.up * rise;
                if (i > 0)
                {
                    Gizmos.DrawLine(previousTop, top);
                    Gizmos.DrawLine(previousMid, far);
                    Gizmos.DrawLine(previousBottom, bottom);
                }

                if (i == 0 || i == segments)
                {
                    Gizmos.DrawLine(eye, top);
                    Gizmos.DrawLine(eye, bottom);
                    Gizmos.DrawLine(eye, far);
                    Gizmos.DrawLine(top, bottom);
                }

                previousTop = top;
                previousMid = far;
                previousBottom = bottom;
            }
        }

        private void DrawVisionGizmos()
        {
            Vector3 eye = transform.position + Vector3.up * _eyeHeight;
            Vector3 forward = ViewForward(transform);

            if (Application.isPlaying && IsSeeing)
                DrawViewFrame(eye, forward, Mathf.Max(_viewAngle, _lockedViewAngle), Mathf.Max(_viewDistance, _lockedViewDistance),
                              new Color(0.4f, 0.7f, 1f, 0.9f));
            else
                DrawViewFrame(eye, forward, _viewAngle, _viewDistance, new Color(0.4f, 0.7f, 1f, 0.35f));

            // Colado (_closeSenseDistance) vê em qualquer direção: o círculo no chão em volta do olho.
            if (_closeSenseDistance > 0f)
            {
                Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.35f);
                Vector3 previous = eye + Vector3.forward * _closeSenseDistance;
                for (int i = 1; i <= 24; i++)
                {
                    Vector3 next = eye + Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward * _closeSenseDistance;
                    Gizmos.DrawLine(previous, next);
                    previous = next;
                }
            }

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            if (!Application.isPlaying)
                return;

            // Os raios de verdade até a grade do CORPO do alvo: azul-claro cheio = livre, cinza = bloqueado, cinza
            // apagado = fora do quadro (cone ou vertical). Não olha o alcance; para depurar "por que não me vê".
            if (GraphTarget.IsLive(_target) && TryGetTargetBody(out Bounds body))
            {
                float angle = IsSeeing ? Mathf.Max(_viewAngle, _lockedViewAngle) : _viewAngle;
                CountVisibleSamples(eye, forward, true, angle * 0.5f, body, Walls, int.MaxValue, true);

                Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            }

            if (IsSeeing && GraphTarget.IsLive(_target))
            {
                Gizmos.DrawWireSphere(_target.Position, 0.5f);
            }
            else if (HasSeen)
            {
                Vector3 p = LastSeenPosition + Vector3.up * _eyeHeight;
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, -0.4f), p + new Vector3(0.4f, 0f, 0.4f));
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, 0.4f), p + new Vector3(0.4f, 0f, -0.4f));
            }

            // Rota NavMesh até o alvo (vermelho-vivo, um pouco acima do chão), só quando a reta não passa.
            if (HasSeen && _routeCount >= 3)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.2f, 1f);
                Vector3 lift = Vector3.up * 0.3f;
                for (int i = 0; i < _routeCount - 1; i++)
                    Gizmos.DrawLine(_routeCorners[i] + lift, _routeCorners[i + 1] + lift);
            }
        }
    }
}
