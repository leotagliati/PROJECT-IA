using System.Collections;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Agente explorador: o ÚNICO que conhece os callbacks do ML-Agents. Não lê o mundo, não monta observação
    /// e não calcula recompensa; ORQUESTRA a ordem do step (sentir -> observar -> agir -> avaliar -> terminar)
    /// e delega:
    ///   GraphExplorationMemory  nós e salas (cobertura, portas, calor)    | estado de episódio, Tick por step
    ///   GraphHiderPerception    visão, captura, aproximação               | idem
    ///   GraphPingSystem         barulho do hider / pings aleatórios        | idem
    ///   GraphSuspicionMap       crença de onde o hider está                | idem
    ///   GraphLocomotion         estado de alerta -> velocidade (7 / 8.5 / 10), pescoço (+ SeekerMovementSystem)
    ///   GraphRewardSystem       recompensa (função pura do GraphStepContext)
    ///   GraphObservations       layout do vetor (188) e da planta de salas  | classe simples, criada aqui
    ///   GraphBodyTracker        parede, batidas, suavidade, parado          | classe simples, criada aqui
    ///   GraphAnimationSystem    estado -> moveSpeed do Animator (opcional, só visual)
    ///   SeekerChaseState/AudioSystem/StaticVisual  luz, estática e áudio do modelo (opcionais, do SeekerAgentV2)
    ///   GraphArenaController    currículo (Settings), spawn, alvo (hider/jogador) | no pai, um por arena
    /// Os componentes podem estar neste objeto ou em FILHOS: são achados no Awake (GetComponentInChildren).
    /// Visão, ping, procura e locomoção são criados com os padrões se faltarem (e o Console avisa).
    /// Erro de wiring em ML-Agents é silencioso (treino que não converge): olhe o ValidateSetup no Console.
    /// </summary>
    public class GraphExplorerManager : Agent
    {
        [Header("-----Sistemas (vazio = acha neste objeto ou nos filhos)-----")]
        [SerializeField] private GraphExplorationMemory _memory;
        [SerializeField] private GraphRewardSystem _rewardSystem;
        // Driver de Rigidbody reaproveitado do seeker antigo (MoveFacing / ResetMovement).
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphLocomotion _locomotion;
        [SerializeField] private GraphHiderPerception _perception;
        [SerializeField] private GraphPingSystem _ping;
        [SerializeField] private GraphSuspicionMap _suspicion;
        // Opcionais e só visuais/áudio do modelo do monstro (os mesmos do SeekerAgentV2): sem eles treina igual.
        [SerializeField] private GraphAnimationSystem _animationSystem;
        [SerializeField] private SeekerChaseState _chaseState;
        [SerializeField] private SeekerAudioSystem _audioSystem;
        [SerializeField] private SeekerStaticVisual _staticVisual;
        // Vazio = o GraphArenaController nos pais.
        [SerializeField] private GraphArenaController _arenaController;

        [Header("-----Observação-----")]
        // Slots de vizinhos na observação. Excedentes (os mais distantes) são cortados em silêncio; o
        // ValidateSetup avisa. Mudar exige ajustar o VectorObservationSize e retreinar.
        [SerializeField] private int _neighborSlots = 8;

        // Normalizador (m) da distância a um nó. Da ordem da MAIOR aresta do mapa: acima dele toda
        // distância satura em 1.0 e a observação morre.
        [SerializeField] private float _maxNodeDistance = 35f;

        // Passagens pelo vizinho que levam "quantas vezes" a 1.0 (ida e volta num beco = 2; de 3 em diante é repetição).
        [SerializeField, Min(1)] private int _revisitSaturation = 4;

        // Normalizador (m/s) da velocidade do hider em [24..25]; acima disso satura em ±1.
        [SerializeField, Min(0.1f)] private float _hiderVelocityScale = 5f;

        [Header("-----Settings-----")]
        // Em steps de FÍSICA, não decisões: 35000 = 700 s a 0.02 s, ou 7000 decisões com Decision Period 5
        // (o Episode Length do TensorBoard). v5.0: 20000 (400 s); o v5.1_zero_01 fazia ~20 das 26 salas e o tempo
        // acabava antes de ele descer para a parte de baixo do mapa, então 700 s (05/10). Contato com parede,
        // estagnação e hider em vista são cobrados por step: mudar isto muda o teto deles (tabela no cabeçalho
        // do GraphRewardSystem).
        [SerializeField] private int _maxEpisodeSteps = 35000;

        [Header("-----Porta (custo reduzido)-----")]
        // Layer das PORTAS (as peças Door_Hole): parede para visão, grafo e observação [8], mas o contato custa só
        // GraphRewardSystem._doorPenaltyScale do de parede. O resto do Wall Layer do NavGraph (Wall, Obstacle) é
        // parede com custo cheio. Tem que estar TAMBÉM no Wall Layer do NavGraph (o ValidateSetup avisa).
        // Padrão (Add Component / Reset): Door. Desde 06/10 as Door_Hole do mapa estão em Wall (o monstro raspava o
        // batente barato e ficava preso na porta), então nada usa esta layer e porta custa como parede.
        [SerializeField] private LayerMask _doorLayer;

        [Header("-----Diagnóstico-----")]
        // Loga no fim do episódio os 3 nós com mais loop/pisca-pisca. Desligado no treino (várias arenas
        // enchem o Console); ligue ao assistir um .onnx no Play.
        [SerializeField] private bool _logLoopNodes = false;

        private NavGraph _graph;
        private Rigidbody _rigidbody;
        private BufferSensorComponent _roomSensor;
        private GraphObservations _observations;
        private readonly GraphBodyTracker _body = new GraphBodyTracker();
        private GraphEpisodeSettings _settings;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private int _elapsedSteps;
        private bool _episodeEnding;

        // Modo de jogo: só caça com o GameManager em Playing (parado na preparação e depois do fim).
        private bool _hunting = true;

        /// <summary>Normalizador de distância a nó. O NavGraphPlacer usa como alcance do teste "tem nó à vista?".</summary>
        public float MaxNodeDistance => _maxNodeDistance;

        public int ObservationSize => GraphObservations.SizeFor(_neighborSlots);

        // Andar X/Z + olhar X/Z.
        private const int ContinuousActions = 4;

        private GraphRoomMemory Rooms => _memory.Rooms;

        private bool IsGameMode => _arenaController != null && _arenaController.GameMode;

        // ================================================================================
        // Montagem: achar (Awake) -> configurar (Initialize)
        // ================================================================================

        // Antes do base.Awake/OnEnable: o sensor da planta tem que existir quando o Agent coleta os sensores, e o
        // modo de jogo tem que ajustar o Behavior Parameters antes de a política ser criada.
        protected override void Awake()
        {
            ResolveSystems();
            _roomSensor = GraphObservations.EnsureRoomSensor(gameObject);
            EnforceBrainShape();
            ApplyGameModeBehavior();
            base.Awake();
        }

        // O tamanho do vetor e das ações SAI DO CÓDIGO, não do Inspector: um VectorObservationSize errado
        // treinava em silêncio com o vetor truncado. Antes do base.Awake, que cria a política.
        private void EnforceBrainShape()
        {
            BehaviorParameters behavior = GetComponent<BehaviorParameters>();
            if (behavior == null)
                return;

            BrainParameters brain = behavior.BrainParameters;
            if (brain.VectorObservationSize != ObservationSize)
            {
                Debug.LogWarning(
                    $"{name}: VectorObservationSize estava {brain.VectorObservationSize}; ajustado para {ObservationSize} " +
                    "(o tamanho que o agente emite). Salve o prefab com o valor certo.", this);
                brain.VectorObservationSize = ObservationSize;
            }

            if (brain.ActionSpec.NumContinuousActions != ContinuousActions || brain.ActionSpec.NumDiscreteActions != 0)
                brain.ActionSpec = ActionSpec.MakeContinuous(ContinuousActions);
        }

#if UNITY_EDITOR
        // Add Component / Reset: deixa o agente pronto para o treino atual sem acertar nada à mão (os padrões dos
        // campos acima são os da v5; aqui entra o que um inicializador de campo não consegue setar).
        private void Reset()
        {
            _doorLayer = LayerMask.GetMask("Door");

            BehaviorParameters behavior = GetComponent<BehaviorParameters>();
            if (behavior != null)
            {
                behavior.BehaviorName = "GraphExplorer";
                behavior.BrainParameters.VectorObservationSize = ObservationSize;
                behavior.BrainParameters.NumStackedVectorObservations = 1;
                behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ContinuousActions);
            }

            DecisionRequester requester = GetComponent<DecisionRequester>();
            if (requester != null)
            {
                requester.DecisionPeriod = 5;
                requester.TakeActionsBetweenDecisions = true;
            }
        }
#endif

        // Campo do Inspector > este objeto ou filhos > (só os que têm padrão sensato) criado aqui com aviso.
        // Checagem explícita, não ??= (fake null do Unity).
        private void ResolveSystems()
        {
            if (_arenaController == null)
                _arenaController = GetComponentInParent<GraphArenaController>();

            _memory = Find(_memory);
            _rewardSystem = Find(_rewardSystem);
            _movementSystem = Find(_movementSystem);
            _locomotion = FindOrCreate(_locomotion);
            _perception = FindOrCreate(_perception);
            _ping = FindOrCreate(_ping);
            _suspicion = FindOrCreate(_suspicion);
            _animationSystem = Find(_animationSystem);
            _chaseState = Find(_chaseState);
            _audioSystem = Find(_audioSystem);
            _staticVisual = Find(_staticVisual);
            _rigidbody = GetComponent<Rigidbody>();
        }

        private T Find<T>(T assigned) where T : Component =>
            assigned != null ? assigned : GetComponentInChildren<T>();

        private T FindOrCreate<T>(T assigned) where T : Component
        {
            T found = Find(assigned);
            if (found != null)
                return found;

            Debug.LogWarning(
                $"{name}: sem {typeof(T).Name} no agente — criado em runtime com os valores padrão. " +
                "Adicione no prefab para ajustar no Inspector.", this);
            return gameObject.AddComponent<T>();
        }

        // Modo de jogo roda o .onnx sem trainer, e determinístico (a média da política, sem sorteio).
        private void ApplyGameModeBehavior()
        {
            if (!IsGameMode)
                return;

            BehaviorParameters behavior = GetComponent<BehaviorParameters>();
            if (behavior == null)
                return;

            // Determinístico ANTES do tipo: só o setter do tipo recria a política.
            behavior.DeterministicInference = true;
            behavior.BehaviorType = BehaviorType.InferenceOnly;
            if (behavior.Model == null)
                Debug.LogError($"{name}: modo de jogo sem Model no Behavior Parameters — o seeker não vai se mexer.", this);
        }

        public override void Initialize()
        {
            _initialLocalPosition = transform.localPosition;
            _initialLocalRotation = transform.localRotation;

            if (!ValidateSetup())
                return;

            _graph.EnsureBaked();
            IGraphTarget target = _arenaController.Target;

            _locomotion.Configure(_movementSystem);
            _perception.Configure(_graph, target);
            _ping.Configure(_graph, target);
            _suspicion.Configure(_graph, _perception, _ping, target);
            _memory.Configure(_graph, _perception, _suspicion);

            if (_animationSystem != null)
                _animationSystem.Initialize();
            if (_audioSystem != null)
                _audioSystem.Initialize();
            if (_staticVisual != null)
                _staticVisual.Initialize();

            _observations = new GraphObservations(
                _graph, _memory, _perception, _ping, _suspicion, _locomotion, _body, _roomSensor,
                _neighborSlots, _maxNodeDistance, _revisitSaturation, _hiderVelocityScale, _rewardSystem.StagnationSteps);
        }

        protected override void OnEnable()
        {
            // Assina ANTES do base, como no SeekerManager: o base.OnEnable pode rodar o OnEpisodeBegin na hora.
            if (IsGameMode)
                GameManager.StateChanged += HandleGameState;

            base.OnEnable();

            if (IsGameMode && GameManager.Current != null)
                HandleGameState(GameManager.Current.State);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (IsGameMode)
                GameManager.StateChanged -= HandleGameState;
        }

        private void HandleGameState(GameState state)
        {
            _hunting = state == GameState.Playing;
            if (!_hunting && _locomotion != null)
                _locomotion.Stop();

            // Fim de jogo: tudo do monstro cala (estática e passos); dali em diante só o áudio da
            // PlayerCaughtSequence, como no SeekerManager.
            if ((state == GameState.Won || state == GameState.Lost) && _audioSystem != null)
                _audioSystem.Silence();
        }

        // ================================================================================
        // Episódio
        // ================================================================================

        public override void OnEpisodeBegin()
        {
            _elapsedSteps = 0;
            _episodeEnding = false;

            _arenaController.ResetEpisode();
            _settings = _arenaController.Settings;

            if (_arenaController.TryGetSpawn(out Vector3 position, out Quaternion rotation))
                transform.SetPositionAndRotation(position, rotation);
            else
                transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

            // Sem steer assist (v4.4): o corpo só anda para a frente e para na hora, a parede não vira trilho.
            _locomotion.ResetEpisode(transform.forward);
            if (_animationSystem != null)
                _animationSystem.ResetEpisode();
            if (_chaseState != null)
                _chaseState.ResetEpisode();
            if (_audioSystem != null)
                _audioSystem.ResetEpisode();

            // Depois do spawn: o hider nasce longe de onde o seeker nasceu.
            _arenaController.ResetHider(transform);

            _memory.ResetEpisode(_settings);
            Rooms.SetRoomCompletionRates(_arenaController.RoomCompletionRate);
            _body.ResetEpisode();
            _ping.ResetEpisode(_settings);
            _perception.ResetEpisode();
            _suspicion.ResetEpisode(_settings);

            // Registra o nó do spawn já: senão ele pagaria descoberta só por o agente ter nascido em cima.
            // Continua contando para a cobertura da sala.
            _memory.Tick(transform);
            _memory.ClearStepFlags();
        }

        // Sentir: amostrado a cada step de FÍSICA, não por decisão (com Decision Period > 1 o agente pode
        // cruzar um nó inteiro entre duas decisões). Ordem importa: memória (nós -> salas) -> visão -> ping ->
        // procura; cada um lê o estado do anterior neste step. A visão vem antes do ping (06/10): vendo o alvo, o
        // ping não existe e o calor que ele deixou some (era renda: a sala quente reabria e pagava de novo).
        private void FixedUpdate()
        {
            if (_episodeEnding || _graph == null)
                return;

            _memory.Tick(transform);

            _perception.Tick(transform);

            _ping.Tick(_memory.CurrentNodeIndex, _elapsedSteps, _perception.IsSeeing);
            if (_perception.IsSeeing)
                Rooms.ClearHeat();

            int started = _ping.ConsumeStarted();
            if (started >= 0)
            {
                Rooms.HeatRoom(_graph.RoomOf(started));
                _locomotion.NotifyHeard();
            }

            // Estado de alerta logo depois da visão e do ping: vendo = Perseguição, pista recente = Alerta.
            _locomotion.UpdateAwareness(_perception.IsSeeing);
            _suspicion.Tick(transform, _memory.CurrentNodeIndex);
        }

        // Modo de jogo não tem fim de episódio: a fração do tempo fica no meio, onde o treino mais a viu.
        public override void CollectObservations(VectorSensor sensor) =>
            _observations.Write(sensor, transform, IsGameMode ? 0.5f : (float)_elapsedSteps / Mathf.Max(1, _maxEpisodeSteps));

        // Avalia ANTES de agir: a posição já é o resultado da ação anterior. Com TakeActionsBetweenDecisions
        // roda todo step de física, e as flags de step são consumidas aqui.
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_episodeEnding)
                return;

            ActionSegment<float> continuous = actions.ContinuousActions;
            _body.BeginStep(new Vector2(continuous[0], continuous[1]), new Vector2(continuous[2], continuous[3]));

            // Fim do treino decidido ANTES de cobrar: o bônus terminal é termo do contexto. Modo de jogo não
            // paga nada (a captura é derrota do jogador) nem termina por cobertura.
            bool caught = _perception.Caught && !IsGameMode;
            bool covered = !caught && _settings.EndsOnCoverage && Rooms.CompletedFraction >= _settings.CoverageTarget;

            AddReward(_rewardSystem.EvaluateStep(BuildStepContext(caught, covered)));

            // Flags consumidas só depois de cobradas.
            _body.EndStep(_rigidbody);
            ClearStepFlags();

            // Agir: andar [0..1] e olhar [2..3], os dois no referencial do mundo. O corpo vai para onde anda;
            // o olhar só vira a cabeça (o cone de visão), até o limite do pescoço.
            Vector3 move = _hunting ? new Vector3(continuous[0], 0f, continuous[1]) : Vector3.zero;
            Vector3 look = _hunting ? new Vector3(continuous[2], 0f, continuous[3]) : Vector3.zero;
            _locomotion.Drive(move, look);
            if (_animationSystem != null)
                _animationSystem.Tick(move, _locomotion.State);

            // Luz, estática e áudio de perseguição do modelo (SeekerChaseState): o mesmo bool do SeekerManager.
            if (_chaseState != null)
                _chaseState.Tick(_perception.IsSeeing);

            // Cor da tela e das luzes da cabeça: azul vasculhando, amarelo em alerta, vermelho vendo o alvo.
            if (_staticVisual != null)
                _staticVisual.SetState(_locomotion.State == GraphLocomotion.Awareness.Chase,
                    _locomotion.State == GraphLocomotion.Awareness.Alert);
            _perception.SetViewDirection(_locomotion.ViewDirection);

            // Terminar.
            if (_perception.Caught && IsGameMode)
            {
                // Na preparação o jogador pode passar encostado: não vale, e não fica guardado para depois.
                if (_hunting)
                    CatchPlayer();
                else
                    _perception.ForgetCaught();
                return;
            }

            if (caught || covered)
            {
                FinishEpisode(success: true);
                return;
            }

            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps && !IsGameMode)
                FinishEpisode(success: false);
        }

        private GraphStepContext BuildStepContext(bool caught, bool covered) => new GraphStepContext
        {
            MaxEpisodeSteps = _maxEpisodeSteps,
            ElapsedSteps = _elapsedSteps,
            DiscoveryRewardScale = _settings.DiscoveryRewardScale,
            PingRewardScale = _settings.PingRewardScale,
            HiderCaught = caught,
            CoverageReached = covered,

            RoomNodeValue = Rooms.RoomNodeValue,
            RoomTailValue = Rooms.RoomTailValue,
            RoomCrumbValue = Rooms.RoomCrumbValue,
            RoomCompletedValue = Rooms.RoomCompletedValue,
            DoorCrossValue = Rooms.DoorCrossValue,
            RoomExitValue = Rooms.RoomExitValue,
            StepsSinceProgress = Rooms.StepsSinceProgress,
            EarlyRevisitArrivals = _memory.EarlyRevisitArrivals,
            EarlyRevisitStreak = _memory.EarlyRevisitStreak,

            IsTouchingWall = _body.IsTouchingWall,
            IsTouchingDoor = _body.IsTouchingDoor,
            WallHitIsDoor = _body.HitIsDoorOnly,
            WallHits = _body.HitsThisStep,
            RecentWallHits = _body.RecentHits,
            ActionChangeSq = _body.MoveChangeSq,
            LookChangeSq = _body.LookChangeSq,

            PingReached = _ping.Reached,
            PingReachedValue = _ping.ReachedValue,
            PingMissed = _ping.Missed,

            HiderSpotted = _perception.Spotted,
            HiderInView = _perception.IsSeeing,
            HasHiderApproach = _perception.HasApproach,
            HiderApproachDelta = _perception.ApproachDelta,
            SuspicionClearedMass = _suspicion.ClearedMass,
        };

        private void ClearStepFlags()
        {
            _memory.ClearStepFlags();
            _ping.ClearStepFlags();
            _perception.ClearStepFlags();
            _suspicion.ClearStepFlags();
        }

        // OnCollisionStay chega só no objeto do Rigidbody (este): o Manager tem que ficar nele. Parede por LAYER
        // (a máscara do grafo), nunca por tag: uma segunda fonte de verdade já deixou um termo de recompensa morto.
        private void OnCollisionStay(Collision collision)
        {
            int layerBit = 1 << collision.gameObject.layer;
            if (_graph == null || (_graph.WallLayer.value & layerBit) == 0)
                return;

            _body.MarkContact(door: (_doorLayer.value & layerBit) != 0);
        }

        // Modo de jogo: com GameManager, é derrota do jogador (ele recarrega a cena) e o seeker para onde
        // está; sem GameManager (cena de teste), o seeker renasce e caça de novo. É o único respawn do modo.
        private void CatchPlayer()
        {
            if (GameManager.Current != null)
            {
                _episodeEnding = true;
                _hunting = false;
                _locomotion.Stop();
                GameManager.Current.PlayerCaught();
                return;
            }

            FinishEpisode(success: true);
        }

        private void FinishEpisode(bool success)
        {
            _episodeEnding = true;
            _arenaController.ShowOutcome(success);
            RecordEpisodeStats();

            float delay = _arenaController.EpisodeEndDelay;
            if (delay <= 0f)
            {
                EndEpisode();
                return;
            }

            StartCoroutine(EndEpisodeAfterDelay(delay));
        }

        private IEnumerator EndEpisodeAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            EndEpisode();
        }

        /// <summary>
        /// Métricas do episódio no TensorBoard, separadas da recompensa (que muda a cada ajuste de peso):
        /// compare runs por elas.
        ///   Exploration/Coverage         fração das salas concluídas ao fim
        ///   Rooms/Completed              salas concluídas (inclui liberadas e refeitas)
        ///   Rooms/S00..S26               fração dos episódios em que cada sala foi concluída
        ///   Doors/Crossings              travessias de porta
        ///   Doors/RepeatFraction         fração das travessias por porta já usada (novidade &lt; 1)
        ///   Doors/UsedFraction           fração das portas do mapa atravessadas ao menos uma vez
        ///   Exploration/OffNodeFraction  fração dos steps fora de qualquer nó (alto = rode o NavGraphPlacer)
        ///   Exploration/EarlyRevisits    revisitas precoces (loop)
        ///   Exploration/AnchorFlicker    pisca-pisca de âncora (A-B-A em &lt; 2 s andando &lt; 1 m): borda de ladrilho
        ///   Exploration/WallContactFraction, WallHits; Movement/IdleFraction, ActionJitter, LookJitter (GraphBodyTracker)
        ///   Movement/ChaseFraction       fração do episódio em Perseguição (vendo o alvo, 10 m/s)
        ///   Movement/AlertFraction       fração em Alerta (ouviu ping ou perdeu de vista há pouco, 8 m/s)
        ///   Movement/MeanSpeed           velocidade média (m/s; patrulha 7, alerta 8.5, perseguição 10)
        ///   Ping/Started, Ping/Reached, Ping/Missed  pings no episódio: começaram / atendidos / expiraram
        ///   Ping/ReachedFraction         atendidos / (atendidos + expirados); com hider o rastro troca sem expirar
        ///   Ping/Silenced                pings apagados por o alvo estar à vista
        ///   Hunt/Seen, Hunt/Caught       só com hider: viu alguma vez / pegou
        ///   Search/Cleared               suspeita limpa que pagou (procura)
        /// </summary>
        private string[] _roomStatNames;

        private void RecordRoomStats(StatsRecorder stats)
        {
            int rooms = _graph.RoomCount;
            if (_roomStatNames == null || _roomStatNames.Length != rooms)
            {
                _roomStatNames = new string[rooms];
                for (int r = 0; r < rooms; r++)
                    _roomStatNames[r] = $"Rooms/S{r:00}";
            }

            for (int r = 0; r < rooms; r++)
            {
                if (!Rooms.IsRoomPrevisited(r))
                    stats.Add(_roomStatNames[r], Rooms.IsRoomCompleted(r) ? 1f : 0f);
            }
        }

        private void RecordEpisodeStats()
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            stats.Add("Exploration/Coverage", Rooms.CompletedFraction);
            stats.Add("Rooms/Completed", Rooms.RoomsCompletedTotal);

            // Por sala: média = fração dos episódios em que ela foi concluída (Rooms/S0..). Mostra QUAIS ficam
            // de fora. Depois vai para a média móvel da arena (spawn e valor de sala rara).
            RecordRoomStats(stats);
            _arenaController.RecordRoomOutcome(Rooms);
            stats.Add("Doors/Crossings", Rooms.Crossings);
            if (Rooms.Crossings > 0)
                stats.Add("Doors/RepeatFraction", (float)Rooms.RepeatCrossings / Rooms.Crossings);
            if (Rooms.DoorCount > 0)
                stats.Add("Doors/UsedFraction", (float)Rooms.DoorsUsed / Rooms.DoorCount);

            if (_memory.TickedSteps > 0)
                stats.Add("Exploration/OffNodeFraction", (float)_memory.OffNodeSteps / _memory.TickedSteps);

            stats.Add("Exploration/EarlyRevisits", _memory.EarlyRevisitCount);
            stats.Add("Exploration/AnchorFlicker", _memory.AnchorFlickers);

            _body.RecordStats(stats, _elapsedSteps);
            if (_elapsedSteps > 0)
            {
                stats.Add("Movement/ChaseFraction", _locomotion.ChaseFraction);
                stats.Add("Movement/AlertFraction", _locomotion.AlertFraction);
                stats.Add("Movement/MeanSpeed", _locomotion.MeanSpeed);
            }

            if (_ping.EpisodeStarted > 0)
            {
                stats.Add("Ping/Started", _ping.EpisodeStarted);
                stats.Add("Ping/Reached", _ping.EpisodeReached);
                stats.Add("Ping/Missed", _ping.EpisodeMissed);
                stats.Add("Ping/Silenced", _ping.EpisodeSilenced);
                int resolved = _ping.EpisodeReached + _ping.EpisodeMissed;
                if (resolved > 0)
                    stats.Add("Ping/ReachedFraction", (float)_ping.EpisodeReached / resolved);
            }

            if (_settings.HasHider)
            {
                stats.Add("Hunt/Seen", _perception.HasSeen ? 1f : 0f);
                stats.Add("Hunt/Caught", _perception.Caught ? 1f : 0f);
            }

            if (_suspicion.IsActive)
                stats.Add("Search/Cleared", _suspicion.EpisodeCleared);

            if (_logLoopNodes)
            {
                string top = _memory.TopLoopNodes(3);
                if (top.Length > 0)
                {
                    Debug.Log(
                        $"{name}: loops {_memory.EarlyRevisitCount}, pisca-pisca {_memory.AnchorFlickers} — " +
                        $"nós mais repetidos: {top}", this);
                }
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // Dirigir na mão: confere se os nós registram visita e se as ligações são percorríveis (olhe os gizmos).
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;

            // Modo de jogo: o WASD é do jogador, nunca do monstro (sem Model, ele fica parado em vez de obedecer).
            if (IsGameMode)
            {
                for (int i = 0; i < continuous.Length; i++)
                    continuous[i] = 0f;
                return;
            }

            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");

            // Na mão, olha para onde anda (parado, o olhar zero mantém a direção).
            continuous[2] = continuous[0];
            continuous[3] = continuous[1];
        }
#endif

        // Erro de wiring em ML-Agents é silencioso (treino que não converge): só loga. false = não dá para rodar.
        private bool ValidateSetup()
        {
            if (_memory == null || _rewardSystem == null || _movementSystem == null)
            {
                Debug.LogError(
                    $"{name}: falta GraphExplorationMemory, GraphRewardSystem ou SeekerMovementSystem no agente " +
                    "(neste objeto ou nos filhos).", this);
                return false;
            }

            if (_arenaController == null)
            {
                Debug.LogError($"{name}: GraphArenaController não encontrado nos pais.", this);
                return false;
            }

            _graph = _arenaController.Graph;
            if (_graph == null)
            {
                Debug.LogError($"{name}: a arena não tem NavGraph.", this);
                return false;
            }

            if (_rigidbody == null)
                Debug.LogError($"{name}: o Manager tem que ficar no objeto do Rigidbody (é lá que chega o OnCollisionStay).", this);

            if (_arenaController.GameMode)
            {
                if (_arenaController.PlayerTarget == null)
                    Debug.LogError($"{name}: modo de jogo sem jogador — nenhum objeto com a tag do jogador (GraphArenaController).", this);
                if (GameManager.Current == null)
                    Debug.LogWarning($"{name}: modo de jogo sem GameManager — pegar o jogador só faz o seeker renascer.", this);
            }

            // Layer de porta fora da máscara do NavGraph nem é parede (visão atravessa, grafo ignora, contato some).
            if ((_doorLayer.value & ~_graph.WallLayer.value) != 0)
            {
                Debug.LogWarning(
                    $"{name}: a layer de porta não está no Wall Layer do NavGraph — marque ela lá também, senão " +
                    "a porta não é parede para visão, grafo e contato.", this);
            }

            _graph.EnsureBaked();
            GraphObservations.Validate(_graph, GetComponent<BehaviorParameters>(), _neighborSlots, this);
            return true;
        }
    }
}
