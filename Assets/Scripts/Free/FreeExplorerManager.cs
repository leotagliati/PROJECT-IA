using System.Collections.Generic;
using Assets.Scripts.Graph;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// Agente da v8 (explorador LIVRE): o ÚNICO que conhece os callbacks do ML-Agents. Não lê o mundo nem calcula
    /// recompensa; orquestra o step e delega. O CORPO é o do monstro da v5, sem mudança (pedido do Arthur): física,
    /// estado de alerta, inércia, olhar, raios de parede. O que muda é o cérebro: não há nó para seguir.
    ///   GraphLocomotion         estado de alerta -> velocidade, corpo e cabeça (+ SeekerMovementSystem)   | o da v5
    ///   GraphHiderPerception    visão do alvo, captura, aproximação                                       | o da v5
    ///   FreeTrackSense          ver as PEGADAS do alvo (FreeFootprintTrail, posto no alvo)                | estado de episódio
    ///   FreeExplorationMemory   pontos vistos, portas atravessadas, sala atual                            | estado de episódio
    ///   GraphBodyTracker        parede, batidas, suavidade (classe simples, a da v5)
    ///   FreeStuckTracker        PRESO na parede (classe simples)
    ///   FreeRewardSystem        recompensa (função pura do FreeStepContext)
    ///   FreeObservations        vetor (126) e planta de salas                                             | classe simples
    ///   FreeArenaController     mapa (FreeMap), currículo, spawn, presa (pelo GraphArenaController da arena)  | no pai
    ///   GraphAnimationSystem, SeekerChaseState/AudioSystem/StaticVisual: visual e áudio do modelo (opcionais)
    ///
    /// FOCO NA PRESA: CAÇANDO (vendo o alvo, procurando há < 20 s ou com pegada fresca) a exploração não paga e some da
    /// observação; ver uma pegada nova põe o monstro em ALERTA (9.5 m/s), ver o alvo em PERSEGUIÇÃO (11.5 m/s).
    ///
    /// ORDEM (a mesma da v5): SENTIR no FixedUpdate (a posição que a física deu ao corpo) -> OBSERVAR na decisão ->
    /// no OnActionReceived, AVALIAR o que a ação anterior causou -> terminar? -> AGIR.
    /// </summary>
    public class FreeExplorerManager : Agent
    {
        public const string BehaviorNameV8 = "FreeExplorer";

        // Andar X/Z + olhar X/Z, no referencial do mundo (o mesmo do radar e das direções observadas).
        private const int ContinuousActions = 4;

        [Header("-----Sistemas (vazio = acha neste objeto ou nos filhos)-----")]
        [SerializeField] private FreeExplorationMemory _memory;
        [SerializeField] private FreeRewardSystem _rewardSystem;
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphLocomotion _locomotion;
        [SerializeField] private GraphHiderPerception _perception;
        // Vazio = acha no agente, ou cria com os padrões.
        [SerializeField] private FreeTrackSense _trackSense;
        // Opcionais e só visuais/áudio do modelo do monstro: sem eles treina igual.
        [SerializeField] private GraphAnimationSystem _animationSystem;
        [SerializeField] private SeekerChaseState _chaseState;
        [SerializeField] private SeekerAudioSystem _audioSystem;
        [SerializeField] private SeekerStaticVisual _staticVisual;
        // Vazio = o FreeArenaController nos pais.
        [SerializeField] private FreeArenaController _arenaController;

        [Header("-----Episódio-----")]
        // Em steps de FÍSICA: 35000 = 700 s = 7000 decisões com Decision Period 5 (o Episode Length do TensorBoard).
        // O tempo da v5.1, que precisou dele para fazer o mapa inteiro. Mudar isto muda o teto dos custos por step
        // (cabeçalho do FreeRewardSystem); a existencial já se reescala sozinha.
        [SerializeField, Min(1)] private int _maxEpisodeSteps = 35000;

        [Header("-----Debug-----")]
        // Painel na tela (Game view) com cobertura, sala, portas, reward, parede e presa. Desligado no treino; o menu
        // PROJECT-IA > "v8 (em Play)" liga no agente assistido.
        [SerializeField] private bool _debugHud = false;

        private FreeMap _map;
        private Rigidbody _rigidbody;
        private FreeObservations _observations;
        private BufferSensorComponent _roomSensor;
        private readonly GraphBodyTracker _body = new GraphBodyTracker();
        private readonly FreeStuckTracker _stuck = new FreeStuckTracker();
        private FreeEpisodeSettings _settings;
        private FreeFootprintTrail _trail;
        private float _heightAboveFloor;
        private int _elapsedSteps;
        private bool _episodeEnding;
        private bool _ready;
        private float _lastStepReward;
        private string _lastEvent = "";

        /// <summary>Liga o painel de debug (menu "v8 (em Play)").</summary>
        public void SetDebugHud(bool on) => _debugHud = on;

        // ================================================================================
        // Montagem
        // ================================================================================

        // Antes do base.Awake: o sensor da planta tem que existir quando o Agent coleta os sensores, e o tamanho do
        // cérebro sai do código (VectorObservationSize errado no Inspector treinava em silêncio com o vetor truncado).
        protected override void Awake()
        {
            ResolveSystems();
            _roomSensor = FreeObservations.EnsureRoomSensor(gameObject);
            EnforceBrainShape(GetComponent<BehaviorParameters>(), warn: true, this);
            base.Awake();
        }

        private static void EnforceBrainShape(BehaviorParameters behavior, bool warn, Object context)
        {
            if (behavior == null)
                return;

            BrainParameters brain = behavior.BrainParameters;
            if (brain.VectorObservationSize != FreeObservations.Size)
            {
                if (warn)
                {
                    Debug.LogWarning(
                        $"{context.name}: VectorObservationSize estava {brain.VectorObservationSize}; ajustado para " +
                        $"{FreeObservations.Size}. Salve o prefab com o valor certo.", context);
                }

                brain.VectorObservationSize = FreeObservations.Size;
            }

            brain.NumStackedVectorObservations = 1;
            if (brain.ActionSpec.NumContinuousActions != ContinuousActions || brain.ActionSpec.NumDiscreteActions != 0)
                brain.ActionSpec = ActionSpec.MakeContinuous(ContinuousActions);
        }

        // Campo do Inspector > este objeto ou filhos. Checagem explícita, não ??= (fake null do Unity).
        private void ResolveSystems()
        {
            if (_arenaController == null)
                _arenaController = GetComponentInParent<FreeArenaController>();

            _memory = Find(_memory);
            _rewardSystem = Find(_rewardSystem);
            _movementSystem = Find(_movementSystem);
            _locomotion = Find(_locomotion);
            _perception = Find(_perception);
            _trackSense = Find(_trackSense);
            if (_trackSense == null)
                _trackSense = gameObject.AddComponent<FreeTrackSense>();
            _animationSystem = Find(_animationSystem);
            _chaseState = Find(_chaseState);
            _audioSystem = Find(_audioSystem);
            _staticVisual = Find(_staticVisual);
            _rigidbody = GetComponent<Rigidbody>();
        }

        private T Find<T>(T assigned) where T : Component =>
            assigned != null ? assigned : GetComponentInChildren<T>();

        public override void Initialize()
        {
            TrySetUp();
            if (_animationSystem != null)
                _animationSystem.Initialize();
            if (_audioSystem != null)
                _audioSystem.Initialize();
            if (_staticVisual != null)
                _staticVisual.Initialize();
        }

        // O mapa sai do NavMesh, que o NavMeshSurface da arena só carrega no OnEnable dele, e o Initialize do Agent
        // também roda no OnEnable: se vier antes, o mapa está vazio. Então tenta de novo a cada começo de episódio.
        private void TrySetUp()
        {
            if (_ready)
                return;

            _ready = ValidateSetup();
            if (!_ready)
                return;

            // A altura do corpo acima do piso é a do prefab (o corpo anda com a altura travada): o spawn repete.
            _heightAboveFloor = Mathf.Clamp(transform.position.y - _map.FloorY, 0f, 3f);

            GraphArenaController graphArena = _arenaController.GraphArena;
            _locomotion.Configure(_movementSystem);
            _perception.Configure(graphArena.Graph, graphArena.Target);
            _memory.Configure(_map);

            // As pegadas moram no ALVO (o hider aqui; o jogador no jogo): põe o rastro nele se faltar.
            if (graphArena.Target is Component target && target != null)
            {
                _trail = target.GetComponent<FreeFootprintTrail>();
                if (_trail == null)
                    _trail = target.gameObject.AddComponent<FreeFootprintTrail>();
            }

            _trackSense.Configure(_trail, _memory.WallLayer, _map.FloorY);
            _observations = new FreeObservations(
                _map, _memory, _locomotion, _body, _stuck, _perception, _trackSense, _roomSensor, _rewardSystem.StagnationSteps);
        }

        // Erro de wiring em ML-Agents é silencioso (treino que não converge): loga, e false = não dá para rodar.
        private bool ValidateSetup()
        {
            if (_memory == null || _rewardSystem == null || _movementSystem == null || _locomotion == null || _perception == null)
            {
                Debug.LogError(
                    $"{name}: falta FreeExplorationMemory, FreeRewardSystem, SeekerMovementSystem, GraphLocomotion ou " +
                    "GraphHiderPerception no agente (rode o FreeArenaController ⋮ \"v8: montar arena livre\").", this);
                return false;
            }

            if (_rigidbody == null)
            {
                Debug.LogError($"{name}: o Manager tem que ficar no objeto do Rigidbody (é lá que chega o OnCollisionStay).", this);
                return false;
            }

            if (_arenaController == null)
            {
                Debug.LogError($"{name}: FreeArenaController não encontrado nos pais (ele fica na raiz da arena).", this);
                return false;
            }

            if (_arenaController.GraphArena == null || _arenaController.GraphArena.Graph == null)
            {
                Debug.LogError($"{name}: a arena não tem GraphArenaController com NavGraph (a presa e a visão dela usam).", this);
                return false;
            }

            _map = _arenaController.Map;
            if (_map == null)
            {
                Debug.LogError($"{name}: a arena não tem FreeMap.", this);
                return false;
            }

            if (_map.PointCount == 0 || _map.RoomCount == 0)
            {
                Debug.LogError($"{name}: o FreeMap não gerou pontos (NavMesh assado e ativo na arena?).", this);
                return false;
            }

            if (GetComponent<DecisionRequester>() == null)
                Debug.LogWarning($"{name}: sem DecisionRequester — o agente nunca pede decisão.", this);

            FreeObservations.Validate(GetComponent<BehaviorParameters>(), ContinuousActions, this);
            return true;
        }

        // ================================================================================
        // Episódio
        // ================================================================================

        public override void OnEpisodeBegin()
        {
            TrySetUp();
            if (!_ready)
                return;

            _elapsedSteps = 0;
            _episodeEnding = false;

            _arenaController.ResetEpisode();
            _settings = _arenaController.Settings;

            if (_arenaController.TryGetSpawn(out Vector3 ground, out Quaternion rotation))
                transform.SetPositionAndRotation(ground + Vector3.up * _heightAboveFloor, rotation);

            _locomotion.ResetEpisode(transform.forward);
            if (_animationSystem != null)
                _animationSystem.ResetEpisode();
            if (_chaseState != null)
                _chaseState.ResetEpisode();
            if (_audioSystem != null)
                _audioSystem.ResetEpisode();

            // Depois do spawn: a presa nasce longe de onde o monstro nasceu. O rastro do episódio anterior some.
            _arenaController.ResetHider(transform);
            if (_trail != null)
                _trail.Clear();
            _trackSense.ResetEpisode();

            _memory.ResetEpisode();
            _body.ResetEpisode();
            _stuck.ResetEpisode();
            _perception.ResetEpisode();

            // O que se vê do spawn não paga: nascer olhando para uma sala aberta não é mérito. Continua contando
            // para a cobertura.
            _memory.Tick(transform.position, _locomotion.ViewDirection, forceVision: true);
            _memory.ClearStepFlags();
            _lastStepReward = 0f;
            _lastEvent = "";
        }

        // Sentir: a cada step de FÍSICA (com Decision Period 5 ele cruza uma porta entre duas decisões). Visão do
        // alvo antes do estado de alerta (vendo = Perseguição), e a memória com a posição que a física deu.
        private void FixedUpdate()
        {
            if (!_ready || _episodeEnding)
                return;

            _perception.Tick(transform);

            // Pegada nova vista = pista: Alerta (como ouvir o alvo na v5). Vendo o alvo ela nem é procurada.
            _trackSense.Tick(transform.position, _locomotion.ViewDirection, _perception.IsSeeing);
            if (_settings.HasHider && _trackSense.NewTrackThisStep)
                _locomotion.NotifyHeard();

            _locomotion.UpdateAwareness(_perception.IsSeeing);
            _memory.Tick(transform.position, _locomotion.ViewDirection);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!_ready)
            {
                for (int i = 0; i < FreeObservations.Size; i++)
                    sensor.AddObservation(0f);
                return;
            }

            _observations.Write(sensor, transform, (float)_elapsedSteps / _maxEpisodeSteps, _settings.CoverageTarget,
                _settings.HasHider, IsHunting);
        }

        // CAÇANDO: vendo o alvo, procurando (perdeu de vista há < 20 s) ou com pegada fresca na memória.
        private bool IsHunting =>
            _settings.HasHider && (_perception.IsSeeing || _perception.IsSearching || _trackSense.HasTrack);

        // Avalia ANTES de agir: a posição já é o resultado da ação anterior. Com TakeActionsBetweenDecisions roda todo
        // step de física, e as flags de step são consumidas aqui.
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!_ready || _episodeEnding)
                return;

            ActionSegment<float> continuous = actions.ContinuousActions;
            var move = new Vector2(continuous[0], continuous[1]);
            var look = new Vector2(continuous[2], continuous[3]);
            _body.BeginStep(move, look);
            _stuck.Tick(_body.IsTouchingAnyWall, _locomotion.IsMoving, _locomotion.Velocity.magnitude);

            // Fim decidido ANTES de cobrar: os bônus de término são termos do contexto. Com presa, a cobertura não
            // encerra (o episódio é dela).
            bool caught = _settings.HasHider && _perception.Caught;
            bool covered = !caught && !_settings.HasHider && _memory.SeenFraction >= _settings.CoverageTarget;

            FreeStepContext context = new FreeStepContext
            {
                MaxEpisodeSteps = _maxEpisodeSteps,
                ElapsedSteps = _elapsedSteps,
                PointCount = _map.PointCount,
                DiscoveryRewardScale = _settings.DiscoveryRewardScale,

                NewPoints = _memory.NewPointsThisStep,
                NewDoors = _memory.NewDoorsThisStep,
                StepsSinceProgress = _memory.StepsSinceProgress,
                CoverageReached = covered,

                IsTouchingWall = _body.IsTouchingAnyWall,
                WallHits = _body.HitsThisStep,
                RecentWallHits = _body.RecentHits,
                IsStuck = _stuck.IsStuck,
                MoveChangeSq = _body.MoveChangeSq,
                LookChangeSq = _body.LookChangeSq,

                Hunting = IsHunting,
                HiderSpotted = _settings.HasHider && _perception.Spotted,
                HiderApproachDelta = _settings.HasHider ? _perception.ApproachDelta : 0f,
                HiderCaught = caught,
            };
            _lastStepReward = _rewardSystem.EvaluateStep(context);
            AddReward(_lastStepReward);
            if (_debugHud)
                NoteEvent(context);

            // Flags consumidas só depois de cobradas.
            _body.EndStep(_rigidbody);
            _memory.ClearStepFlags();
            _perception.ClearStepFlags();
            _trackSense.ClearStepFlags();

            // Agir: andar [0..1] e olhar [2..3], no referencial do mundo. O corpo vai para onde anda; o olhar só vira
            // a cabeça (o cone de visão), até o limite do pescoço.
            var move3 = new Vector3(move.x, 0f, move.y);
            _locomotion.Drive(move3, new Vector3(look.x, 0f, look.y));
            _perception.SetViewDirection(_locomotion.ViewDirection);
            if (_animationSystem != null)
                _animationSystem.Tick(move3, _locomotion.State);
            if (_chaseState != null)
                _chaseState.Tick(_perception.IsSeeing);
            if (_staticVisual != null)
                _staticVisual.SetState(_locomotion.State == GraphLocomotion.Awareness.Chase,
                    _locomotion.State == GraphLocomotion.Awareness.Alert);

            // Terminar.
            if (caught || covered)
            {
                FinishEpisode(success: true);
                return;
            }

            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps)
                FinishEpisode(success: false);
        }

        // Último evento que mexeu na reward, para o painel (só com _debugHud).
        private void NoteEvent(in FreeStepContext context)
        {
            float t = _elapsedSteps * Time.fixedDeltaTime;
            if (context.NewDoors > 0)
                _lastEvent = $"{t:0}s  PORTA NOVA";
            else if (context.HiderCaught)
                _lastEvent = $"{t:0}s  PEGOU";
            else if (context.HiderSpotted)
                _lastEvent = $"{t:0}s  AVISTOU";
            else if (_trackSense.NewTrackThisStep)
                _lastEvent = $"{t:0}s  viu PEGADA";
            else if (context.CoverageReached)
                _lastEvent = $"{t:0}s  COBERTURA";
            else if (context.WallHits > 0)
                _lastEvent = $"{t:0}s  bateu na parede";
            else if (context.IsStuck)
                _lastEvent = $"{t:0}s  PRESA na parede";
            else if (context.NewPoints > 0)
                _lastEvent = $"{t:0}s  viu {context.NewPoints} ponto(s)";
        }

        private void OnGUI()
        {
            if (!_debugHud || !_ready)
                return;

            int room = _memory.CurrentRoom;
            string hunt = !_settings.HasHider ? "sem presa (lição Explorar)"
                : _perception.Caught ? "PEGOU" : _perception.IsSeeing ? "VENDO" : _perception.HasSeen ? "já viu, procurando" : "não viu";
            if (_settings.HasHider && _trackSense.HasTrack)
                hunt += $"   pegada a {Vector3.Distance(transform.position, _trackSense.Position):0} m ({_trackSense.Intensity:P0})";
            if (IsHunting)
                hunt += "   [CAÇANDO: exploração em 0]";
            string text =
                $"<b>v8 FreeExplorer</b>  {name}\n" +
                $"Tempo: {_elapsedSteps * Time.fixedDeltaTime:0} s / {_maxEpisodeSteps * Time.fixedDeltaTime:0} s\n" +
                $"Reward do episódio: {GetCumulativeReward():0.000}   (último step {_lastStepReward:+0.0000;-0.0000})\n" +
                $"Pontos vistos: {_memory.SeenCount}/{_map.PointCount} ({_memory.SeenFraction:P0}), meta {_settings.CoverageTarget:P0}\n" +
                $"Sala atual: {(room >= 0 ? $"S{room:00}" : "-")} ({_memory.RoomSeenFraction(room):P0} vista){(_memory.InDoorZone ? "  [na porta]" : "")}\n" +
                $"Salas vistas: {_memory.RoomsSeenCount}/{_map.RoomCount}   Portas novas: {_memory.DoorsCrossed}/{_map.DoorCount} (travessias {_memory.Crossings})\n" +
                $"Estado: {_locomotion.State}  {_locomotion.Velocity.magnitude:0.0} m/s\n" +
                $"Parede: batidas recentes {_body.RecentHits}   presa {(_stuck.IsStuck ? "SIM" : "não")} ({_stuck.Level:P0})\n" +
                $"Presa (hider): {hunt}\n" +
                $"Último evento: {_lastEvent}";

            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };
            GUI.Box(new Rect(10, 10, 560, 230), text, style);
        }

        // OnCollisionStay chega só no objeto do Rigidbody (este). Parede por LAYER (a máscara da memória), nunca por tag.
        private void OnCollisionStay(Collision collision)
        {
            if (_memory == null || (_memory.WallLayer.value & (1 << collision.gameObject.layer)) == 0)
                return;

            _body.MarkContact(door: false);
            if (IsDoorFrame(collision.collider))
                _body.MarkDoorFrame();
        }

        // Batente = collider com "Door_Hole" no nome dele ou de um pai. Só para a métrica Exploration/DoorContactFraction
        // (é onde o monstro da v5 empacava); cache por collider, porque o OnCollisionStay roda todo step.
        private readonly Dictionary<int, bool> _doorFrameCache = new Dictionary<int, bool>();

        private bool IsDoorFrame(Collider collider)
        {
            int id = collider.GetInstanceID();
            if (_doorFrameCache.TryGetValue(id, out bool isDoor))
                return isDoor;

            isDoor = false;
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                if (t.name.Contains("Door_Hole"))
                {
                    isDoor = true;
                    break;
                }
            }

            _doorFrameCache[id] = isDoor;
            return isDoor;
        }

        private void FinishEpisode(bool success)
        {
            _episodeEnding = true;
            _arenaController.ShowOutcome(success);
            RecordEpisodeStats(success);
            EndEpisode();
        }

        /// <summary>
        /// Métricas do episódio no TensorBoard (compare runs por elas, não pela reward, que muda a cada ajuste):
        ///   Exploration/Coverage        fração dos pontos vistos ao fim
        ///   Exploration/RoomsSeen       fração das salas vistas (>= _roomSeenThreshold dos pontos)
        ///   Exploration/Completed       1 se atingiu coverage_target (só sem presa)
        ///   Exploration/SecondsToCover  segundos até a meta (só episódios que chegaram)
        ///   Rooms/S00..                 fração dos episódios em que cada sala foi vista: mostra QUAIS ficam de fora
        ///   Doors/UsedFraction          portas diferentes atravessadas / portas do mapa
        ///   Doors/Crossings             travessias no total; Doors/RepeatFraction = as repetidas / total
        ///   Exploration/WallContactFraction, WallHits, DoorContactFraction (batente), DoorHits   (GraphBodyTracker)
        ///   Movement/StuckFraction      fração do episódio PRESA na parede
        ///   Movement/IdleFraction, ActionJitter, LookJitter, ChaseFraction, AlertFraction, MeanSpeed
        ///   Hunt/Seen, Caught, Sightings, LostSight, SightToCatchSeconds, TracksSeen (pegadas)   (só com presa)
        /// </summary>
        private string[] _roomStatNames;

        private void RecordEpisodeStats(bool success)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            stats.Add("Exploration/Coverage", _memory.SeenFraction);
            stats.Add("Exploration/RoomsSeen", _map.RoomCount > 0 ? (float)_memory.RoomsSeenCount / _map.RoomCount : 0f);
            if (!_settings.HasHider)
            {
                stats.Add("Exploration/Completed", success ? 1f : 0f);
                if (success)
                    stats.Add("Exploration/SecondsToCover", _elapsedSteps * Time.fixedDeltaTime);
            }

            int rooms = _map.RoomCount;
            if (_roomStatNames == null || _roomStatNames.Length != rooms)
            {
                _roomStatNames = new string[rooms];
                for (int r = 0; r < rooms; r++)
                    _roomStatNames[r] = $"Rooms/S{r:00}";
            }

            for (int r = 0; r < rooms; r++)
                stats.Add(_roomStatNames[r], _memory.IsRoomSeen(r) ? 1f : 0f);
            _arenaController.RecordRoomOutcome(_memory);

            stats.Add("Doors/UsedFraction", _memory.DoorsCrossedFraction);
            stats.Add("Doors/Crossings", _memory.Crossings);
            if (_memory.Crossings > 0)
                stats.Add("Doors/RepeatFraction", (float)_memory.RepeatCrossings / _memory.Crossings);

            _body.RecordStats(stats, _elapsedSteps);
            stats.Add("Movement/StuckFraction", _stuck.StuckFraction);
            if (_elapsedSteps > 0)
            {
                stats.Add("Movement/ChaseFraction", _locomotion.ChaseFraction);
                stats.Add("Movement/AlertFraction", _locomotion.AlertFraction);
                stats.Add("Movement/MeanSpeed", _locomotion.MeanSpeed);
            }

            if (_settings.HasHider)
            {
                stats.Add("Hunt/Seen", _perception.HasSeen ? 1f : 0f);
                stats.Add("Hunt/Caught", _perception.Caught ? 1f : 0f);
                stats.Add("Hunt/Sightings", _perception.EpisodeSightings);
                stats.Add("Hunt/LostSight", _perception.EpisodeLostSight);
                if (_perception.SightToCatchSeconds >= 0f)
                    stats.Add("Hunt/SightToCatchSeconds", _perception.SightToCatchSeconds);
                stats.Add("Hunt/TracksSeen", _trackSense.EpisodeTracksSeen);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // Dirigir na mão (Behavior Type = Heuristic Only): confere visão, portas, parede e recompensa (olhe os gizmos).
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;
            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");

            // Na mão, olha para onde anda.
            continuous[2] = continuous[0];
            continuous[3] = continuous[1];
        }
#endif

#if UNITY_EDITOR
        // Componentes do cérebro da v5 que saem do monstro copiado (o corpo, a visão do alvo e o visual ficam).
        private static readonly System.Type[] V5BrainComponents =
        {
            typeof(GraphExplorerManager), typeof(GraphExplorationMemory), typeof(GraphRewardSystem),
            typeof(GraphPingSystem), typeof(GraphSuspicionMap),
        };

        /// <summary>
        /// Cria o agente da v8 COPIANDO o monstro da v5 da arena (corpo, física, GraphLocomotion, visão do alvo, raios,
        /// modelo, animação, luz e áudio) e trocando só o cérebro. O original fica desligado ao lado. Chamado pelo
        /// FreeArenaController ⋮ "v8: montar arena livre" e pelo FreeV8Setup (batchmode).
        /// </summary>
        internal static FreeExplorerManager ConvertFromV5(GraphExplorerManager original, LayerMask wallLayer)
        {
            GameObject copy = Object.Instantiate(original.gameObject, original.transform.parent);
            copy.name = "SeekerAgentV2 - V8 livre";
            copy.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            copy.SetActive(true);
            UnityEditor.Undo.RegisterCreatedObjectUndo(copy, "Montar arena v8");
            UnityEditor.Undo.RecordObject(original.gameObject, "Montar arena v8");
            original.gameObject.SetActive(false);

            // O DecisionRequester exige um Agent: sai antes do manager antigo e volta depois do novo.
            foreach (DecisionRequester requester in copy.GetComponentsInChildren<DecisionRequester>(true))
                Object.DestroyImmediate(requester);

            foreach (System.Type type in V5BrainComponents)
            {
                foreach (Component component in copy.GetComponentsInChildren(type, true))
                    Object.DestroyImmediate(component);
            }

            // Os filhos que ficaram vazios (MemorySystem, RewardSystem) ficam: apagar "filho só com Transform" levaria
            // junto as pontas do esqueleto do modelo.
            FreeExplorationMemory memory = copy.AddComponent<FreeExplorationMemory>();
            memory.SetWallLayer(wallLayer);
            copy.AddComponent<FreeRewardSystem>();
            copy.AddComponent<FreeTrackSense>();
            FreeExplorerManager manager = copy.AddComponent<FreeExplorerManager>();
            manager.MaxStep = 0;

            DecisionRequester decisions = copy.AddComponent<DecisionRequester>();
            decisions.DecisionPeriod = 5;
            decisions.TakeActionsBetweenDecisions = true;

            BehaviorParameters behavior = copy.GetComponent<BehaviorParameters>();
            behavior.BehaviorName = BehaviorNameV8;
            behavior.Model = null;
            behavior.BehaviorType = BehaviorType.Default;
            EnforceBrainShape(behavior, warn: false, copy);

            // O sensor da planta da v5 (32 x 10) vira o da v8 (32 x 8).
            FreeObservations.EnsureRoomSensor(copy);
            return manager;
        }
#endif
    }
}
