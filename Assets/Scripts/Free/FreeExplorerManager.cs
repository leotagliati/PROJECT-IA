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
    ///   FreeObservations        vetor (200), planta de salas, chaves e eventos                            | classe simples
    ///   FreeCloneAbility        o CLONE (v9): ramo discreto 0, canalização, recarga                       | estado de episódio
    ///   FreeEventLog            acontecimentos recentes (som, chave levada, clone viu) para o sensor       | classe simples
    ///   FreeArenaController     mapa (FreeMap), currículo, spawn, presa (FreeHider), alvo, modo de jogo    | no pai
    /// Nada aqui usa NavGraph nem nó: o mapa, a presa e as rotas saem do NavMesh.
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

        // v9: ramos discretos. 0 = clone (nada / andarilho / vigia). 1 = "habilidade" (nada + 6) e 2 = "alvo" (nada +
        // 12 slots de porta) são RESERVADOS: sempre mascarados até uma habilidade nova ganhar uso (aí o treino segue
        // com --resume e uma lição nova, sem mudar o cérebro). Ordem e tamanhos são o contrato com os .onnx.
        public const int AbilityBranchSize = 7;
        public const int TargetBranchSize = FreeObservations.DoorSlots + 1;
        private static readonly int[] DiscreteBranches = { FreeCloneAbility.BranchSize, AbilityBranchSize, TargetBranchSize };

        [Header("-----Sistemas (vazio = acha neste objeto ou nos filhos)-----")]
        [SerializeField] private FreeExplorationMemory _memory;
        [SerializeField] private FreeRewardSystem _rewardSystem;
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphLocomotion _locomotion;
        [SerializeField] private GraphHiderPerception _perception;
        // Vazio = acha no agente, ou cria com os padrões.
        [SerializeField] private FreeTrackSense _trackSense;
        [SerializeField] private FreeCloneAbility _clone;
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

        [Header("-----Modo de jogo (FreeArenaController._gameMode)-----")]
        // Fração do episódio que a rede vê no jogo, onde não há fim por tempo. 0.05 = ~35 s: onde a caça do treino
        // viveu (episódios da HiderFoge de ~80 s = 0 a 0.11). O 0.5 da v5 ela quase nunca viu caçando.
        [SerializeField, Range(0f, 1f)] private float _gameModeEpisodeFraction = 0.05f;

        // Nasce a pelo menos isto (m, em linha reta) do jogador, no Play e depois de pegá-lo sem GameManager.
        [SerializeField, Min(0f)] private float _gameModeSpawnDistance = 25f;

        [Header("-----Chaves (v8.1)-----")]
        // Sensor das chaves (BufferSensor "Keys"): o cérebro passa a ter uma entrada a mais, então os .onnx sem ele (o
        // v8.0 inteiro) não rodam com isto ligado, e os com ele não rodam desligado. Ligado só na cena/build da v8.1.
        [SerializeField] private bool _keySensor = false;

        [Header("-----Debug-----")]
        // Painel na tela (Game view) com cobertura, sala, portas, reward, parede e presa. Desligado no treino; o menu
        // PROJECT-IA > "v8 (em Play)" liga no agente assistido.
        [SerializeField] private bool _debugHud = false;

        private FreeMap _map;
        private Rigidbody _rigidbody;
        private FreeObservations _observations;
        private BufferSensorComponent _roomSensor;
        private BufferSensorComponent _keySensorComponent;
        private BufferSensorComponent _eventSensorComponent;
        private FreeKeyField _keys;

        // Acontecimentos dos últimos 10 s, os 8 mais novos (o sensor "Events").
        private readonly FreeEventLog _events = new FreeEventLog(FreeObservations.MaxEvents, 10f);

        // O corpo do alvo (cabeça e peito para a visão do clone) e onde ele estava no step anterior (para onde ia).
        private Collider _targetBody;
        private Vector3 _lastTargetPosition;
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

        // Modo de jogo: o alvo é o jogador (achado pela tag), roda o .onnx sem trainer, sem fim por tempo, e pegar é
        // derrota (GameManager). Só se mexe com o GameManager em Playing; sem GameManager (cena de teste), sempre.
        private bool IsGameMode => _arenaController != null && _arenaController.GameMode;
        private bool _matchRunning = true;

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
            if (_keySensor)
                _keySensorComponent = FreeObservations.EnsureKeySensor(gameObject);
            _eventSensorComponent = FreeObservations.EnsureEventSensor(gameObject);
            EnforceBrainShape(GetComponent<BehaviorParameters>(), warn: true, this);
            ApplyGameModeBehavior();
            base.Awake();
        }

        // Modo de jogo roda o .onnx sem trainer, determinístico (a média da política, sem sorteio). Antes do
        // base.Awake, que cria a política.
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
                Debug.LogError($"{name}: modo de jogo sem Model no Behavior Parameters — o monstro não vai se mexer.", this);
        }

        protected override void OnEnable()
        {
            // Assina ANTES do base, como na v5: o base.OnEnable pode rodar o OnEpisodeBegin na hora.
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
            _matchRunning = state == GameState.Playing;
            if (!_matchRunning && _locomotion != null)
                _locomotion.Stop();

            // Fim de jogo: o monstro cala; dali em diante só o áudio da PlayerCaughtSequence.
            if ((state == GameState.Won || state == GameState.Lost) && _audioSystem != null)
                _audioSystem.Silence();
        }

        /// <summary>Acerta vetor e ações do Behavior Parameters (o FreeV9Setup grava isto no prefab).</summary>
        public static void EnforceBrainShape(BehaviorParameters behavior, bool warn, Object context)
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
            ActionSpec spec = brain.ActionSpec;
            bool branchesOk = spec.BranchSizes != null && spec.BranchSizes.Length == DiscreteBranches.Length;
            for (int b = 0; branchesOk && b < DiscreteBranches.Length; b++)
                branchesOk = spec.BranchSizes[b] == DiscreteBranches[b];
            if (spec.NumContinuousActions != ContinuousActions || !branchesOk)
                brain.ActionSpec = new ActionSpec(ContinuousActions, (int[])DiscreteBranches.Clone());
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
            _clone = Find(_clone);
            if (_clone == null)
                _clone = gameObject.AddComponent<FreeCloneAbility>();
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

            // Sem grafo: a parede da visão do alvo e da captura vem da arena.
            IGraphTarget arenaTarget = _arenaController.Target;
            _locomotion.Configure(_movementSystem);
            _perception.Configure(null, arenaTarget, _arenaController.WallLayer);
            _memory.Configure(_map);

            // As pegadas moram no ALVO (o hider aqui; o jogador no jogo): põe o rastro nele se faltar.
            if (arenaTarget is Component target && target != null)
            {
                _trail = target.GetComponent<FreeFootprintTrail>();
                if (_trail == null)
                    _trail = target.gameObject.AddComponent<FreeFootprintTrail>();
                _targetBody = target.GetComponentInChildren<Collider>();
            }

            _trackSense.Configure(_trail, _memory.WallLayer, _map.FloorY);
            _keys = _arenaController.Keys;
            _observations = new FreeObservations(
                _map, _memory, _locomotion, _body, _stuck, _perception, _trackSense, _roomSensor, _rewardSystem.StagnationSteps,
                _keys, _keySensorComponent, _clone, _events, _eventSensorComponent);
        }

        // Erro de wiring em ML-Agents é silencioso (treino que não converge): loga, e false = não dá para rodar.
        private bool ValidateSetup()
        {
            if (_memory == null || _rewardSystem == null || _movementSystem == null || _locomotion == null || _perception == null)
            {
                Debug.LogError(
                    $"{name}: falta FreeExplorationMemory, FreeRewardSystem, SeekerMovementSystem, GraphLocomotion ou " +
                    "GraphHiderPerception no agente (o prefab Arena - V8 já vem com eles).", this);
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

            // Sem jogador o alvo é nulo e a visão do alvo não tem o que olhar: não roda (tenta de novo no próximo episódio).
            if (IsGameMode)
            {
                if (_arenaController.PlayerTarget == null)
                {
                    Debug.LogError($"{name}: modo de jogo sem jogador — nenhum objeto ATIVO com a tag do jogador (FreeArenaController).", this);
                    return false;
                }

                if (GameManager.Current == null)
                    Debug.LogWarning($"{name}: modo de jogo sem GameManager — pegar o jogador só faz o monstro renascer.", this);
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

            // No jogo o jogador não é reposicionado: o monstro nasce longe dele.
            Vector3? avoid = null;
            if (IsGameMode && GraphTarget.IsLive(_arenaController.Target))
                avoid = _arenaController.Target.Position;

            if (_arenaController.TryGetSpawn(out Vector3 ground, out Quaternion rotation, avoid, _gameModeSpawnDistance))
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
            _clone.ResetEpisode(_settings.CloneEnabled && _settings.HasHider);
            _events.Clear();
            if (GraphTarget.IsLive(_arenaController.Target))
                _lastTargetPosition = _arenaController.Target.Position;

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

            // Clone (v9): canaliza, anda/varre e olha. Vendo o alvo (e o monstro não), entrega uma PISTA forte: onde o
            // alvo está e para onde ia. Antes do Tick da pista, para valer já neste step.
            IGraphTarget target = _arenaController.Target;
            bool targetLive = GraphTarget.IsLive(target);
            if (_clone.Tick(transform.position, target, _targetBody, _arenaController.WallLayer)
                && _settings.HasHider && targetLive && !_perception.IsSeeing)
            {
                _trackSense.Report(target.Position, target.Position - _lastTargetPosition, _clone.ReportStrength, _clone.ReportLifetime);
                _events.Add(FreeEventLog.Type.CloneSaw, target.Position);
            }

            if (targetLive)
                _lastTargetPosition = target.Position;

            // Pista nova (pegada vista, som ouvido ou o clone) = Alerta. Vendo o alvo nenhuma delas é procurada.
            _trackSense.Tick(transform.position, _locomotion.ViewDirection, _perception.IsSeeing);
            if (_settings.HasHider && _trackSense.NewTrackThisStep)
                _locomotion.NotifyHeard();
            if (_trackSense.HeardThisStep)
                _events.Add(FreeEventLog.Type.Noise, _trackSense.Position);

            // Chave levada que o monstro acabou de saber (com atraso) = pista: Alerta, como ouvir o alvo.
            if (_keys != null && _keys.Count > 0)
            {
                Vector3 ground = _memory.Ground;
                var eye = new Vector3(ground.x, _map.FloorY + _memory.EyeHeight, ground.z);
                _keys.Tick(ground, eye, _locomotion.ViewDirection, _memory.CosHalfAngle);
                if (_keys.NewlyKnownThisStep > 0)
                {
                    _locomotion.NotifyHeard();
                    _events.Add(FreeEventLog.Type.KeyTaken, _keys.LastKnownPosition);
                }
            }

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

            float elapsed = IsGameMode ? _gameModeEpisodeFraction : (float)_elapsedSteps / _maxEpisodeSteps;
            _observations.Write(sensor, transform, elapsed, _settings.CoverageTarget, _settings.HasHider, IsHunting);
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

            // Clone: o ramo 0 pede; a máscara já barrou o que não pode, e o TryActivate confere de novo (entre decisões
            // a mesma ação chega a cada step de física e só a 1ª começa a canalização). Canalizando, o monstro fica
            // parado: o andar vale zero (também para a suavidade, senão parar custaria como um tranco).
            if (actions.DiscreteActions.Length > 0 && actions.DiscreteActions[0] > 0 && _matchRunning)
                _clone.TryActivate((FreeCloneAbility.Mode)actions.DiscreteActions[0], transform.position, _locomotion.ViewDirection);
            if (_clone.IsChanneling)
                move = Vector2.zero;
            _body.BeginStep(move, look);
            _stuck.Tick(_body.IsTouchingAnyWall, _locomotion.IsMoving, _locomotion.Velocity.magnitude);

            // Fim decidido ANTES de cobrar: os bônus de término são termos do contexto. Com presa, a cobertura não
            // encerra (o episódio é dela). No jogo pegar não paga: é derrota do jogador (tratada lá embaixo).
            bool caught = _settings.HasHider && _perception.Caught && !IsGameMode;
            bool covered = !caught && !_settings.HasHider && _memory.SeenFraction >= _settings.CoverageTarget;

            // A presa levou todas as chaves (o monstro soube da última): escapou, encerra. No jogo quem decide é o jogo.
            bool escaped = !caught && !IsGameMode && _keys != null && _keys.Escaped;

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

                KeyChecks = _keys != null ? _keys.ChecksThisStep : 0,
                KeysLost = _keys != null ? _keys.NewlyKnownThisStep : 0,
                HiderEscaped = escaped,
                Camping = _keys != null && _keys.IsCamping && !IsHunting,
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
            if (_keys != null)
                _keys.ClearStepFlags();

            // Agir: andar [0..1] e olhar [2..3], no referencial do mundo. O corpo vai para onde anda; o olhar só vira
            // a cabeça (o cone de visão), até o limite do pescoço.
            // Fora da partida (preparação, fim de jogo) o monstro fica parado.
            var move3 = _matchRunning ? new Vector3(move.x, 0f, move.y) : Vector3.zero;
            var look3 = _matchRunning ? new Vector3(look.x, 0f, look.y) : Vector3.zero;
            _locomotion.Drive(move3, look3);
            _perception.SetViewDirection(_locomotion.ViewDirection);
            // Animação pela velocidade real do corpo (só visual; no treino o Animator fica desligado).
            if (_animationSystem != null)
            {
                _animationSystem.TickBySpeed(_locomotion.Speed, _locomotion.PatrolSpeed, _locomotion.MaxSpeed);
                _animationSystem.SetHeadYaw(_locomotion.HeadYaw);
            }
            if (_chaseState != null)
                _chaseState.Tick(_perception.IsSeeing);
            if (_staticVisual != null)
                _staticVisual.SetState(_locomotion.State == GraphLocomotion.Awareness.Chase,
                    _locomotion.State == GraphLocomotion.Awareness.Alert);

            // Terminar.
            if (IsGameMode && _perception.Caught)
            {
                // Na preparação o jogador pode passar encostado: não vale, e não fica guardado para depois.
                if (_matchRunning)
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

            if (escaped)
            {
                FinishEpisode(success: false);
                return;
            }

            // O jogo não tem fim por tempo (o contador segue só para o painel).
            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps && !IsGameMode)
                FinishEpisode(success: false);
        }

        // Ramo 0 (clone) só com o clone pronto; ramos 1 e 2 RESERVADOS: só a opção 0 ("nada") até ganharem uso.
        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            bool cloneReady = _ready && _clone != null && _clone.IsReady && _matchRunning;
            for (int option = 1; option < FreeCloneAbility.BranchSize; option++)
                actionMask.SetActionEnabled(0, option, cloneReady);
            for (int option = 1; option < AbilityBranchSize; option++)
                actionMask.SetActionEnabled(1, option, false);
            for (int option = 1; option < TargetBranchSize; option++)
                actionMask.SetActionEnabled(2, option, false);
        }

        // Com GameManager é derrota do jogador (ele recarrega a cena) e o monstro para onde está; sem GameManager
        // (cena de teste), o monstro renasce longe do jogador e caça de novo. É o único respawn do modo de jogo.
        private void CatchPlayer()
        {
            if (GameManager.Current != null)
            {
                _episodeEnding = true;
                _matchRunning = false;
                _locomotion.Stop();
                GameManager.Current.PlayerCaught();
                return;
            }

            FinishEpisode(success: true);
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
                _lastEvent = _trackSense.Heading == Vector3.zero ? $"{t:0}s  OUVIU" : $"{t:0}s  viu PEGADA";
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
            string clone = !_clone.Allowed ? "desligado nesta lição"
                : _clone.IsChanneling ? $"CANALIZANDO {_clone.ChannelProgress:P0}"
                : _clone.IsActive ? $"{_clone.CurrentMode} ({_clone.LifeRemaining:P0} de vida){(_clone.SeesTarget ? "  VENDO O ALVO" : "")}"
                : _clone.IsReady ? "pronto (C = andarilho, V = vigia)" : $"recarga {_clone.CooldownRemaining:P0}";
            string text =
                $"<b>v9 FreeExplorer</b>  {name}\n" +
                $"Tempo: {_elapsedSteps * Time.fixedDeltaTime:0} s / {_maxEpisodeSteps * Time.fixedDeltaTime:0} s\n" +
                $"Reward do episódio: {GetCumulativeReward():0.000}   (último step {_lastStepReward:+0.0000;-0.0000})\n" +
                $"Pontos vistos: {_memory.SeenCount}/{_map.PointCount} ({_memory.SeenFraction:P0}), meta {_settings.CoverageTarget:P0}\n" +
                $"Sala atual: {(room >= 0 ? $"S{room:00}" : "-")} ({_memory.RoomSeenFraction(room):P0} vista){(_memory.InDoorZone ? "  [na porta]" : "")}\n" +
                $"Salas vistas: {_memory.RoomsSeenCount}/{_map.RoomCount}   Portas novas: {_memory.DoorsCrossed}/{_map.DoorCount} (travessias {_memory.Crossings})\n" +
                $"Estado: {_locomotion.State}  {_locomotion.Velocity.magnitude:0.0} m/s\n" +
                $"Parede: batidas recentes {_body.RecentHits}   presa {(_stuck.IsStuck ? "SIM" : "não")} ({_stuck.Level:P0})\n" +
                $"Presa (hider): {hunt}\n" +
                $"Clone: {clone}   eventos: {_events.Count}\n" +
                $"Último evento: {_lastEvent}";

            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };
            GUI.Box(new Rect(10, 10, 560, 250), text, style);
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
        ///   Hunt/Seen, Caught, Sightings, LostSight, SightToCatchSeconds, TracksSeen (pegadas), NoisesHeard (sons)   (só com presa)
        ///   Clone/Uses, Clone/SawTarget, Clone/Reports                                  (só com o clone liberado)
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
                // Separado por tipo de presa: na lição Misto o Caught junta os três tipos e esconde quem vai mal.
                string prey = _settings.HiderHides ? "Hunt/CaughtHidden" : _settings.HiderFast ? "Hunt/CaughtFast" : "Hunt/CaughtFleeing";
                stats.Add(prey, _perception.Caught ? 1f : 0f);
                stats.Add("Hunt/Sightings", _perception.EpisodeSightings);
                stats.Add("Hunt/LostSight", _perception.EpisodeLostSight);
                if (_perception.SightToCatchSeconds >= 0f)
                    stats.Add("Hunt/SightToCatchSeconds", _perception.SightToCatchSeconds);
                stats.Add("Hunt/TracksSeen", _trackSense.EpisodeTracksSeen);
                stats.Add("Hunt/NoisesHeard", _trackSense.EpisodeNoisesHeard);
            }

            // Clone (só episódios em que a lição o libera): usos, se viu o alvo, pistas entregues.
            if (_clone.Allowed)
            {
                stats.Add("Clone/Uses", _clone.EpisodeUses);
                stats.Add("Clone/SawTarget", _clone.EpisodeSawTarget ? 1f : 0f);
                stats.Add("Clone/Reports", _clone.EpisodeReports);
            }

            // Chaves (só episódios com chaves): quantas a presa levou, se escapou, rondas e tempo campando.
            if (_keys != null && _keys.Count > 0)
            {
                stats.Add("Keys/Taken", _keys.Count - _keys.Remaining);
                stats.Add("Keys/Escaped", _keys.Escaped ? 1f : 0f);
                stats.Add("Keys/Checks", _keys.EpisodeChecks);
                if (_elapsedSteps > 0)
                    stats.Add("Keys/CampFraction", (float)_keys.EpisodeCampSteps / _elapsedSteps);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // Dirigir na mão (Behavior Type = Heuristic Only): confere visão, portas, parede e recompensa (olhe os gizmos).
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;

            // Modo de jogo: o WASD é do jogador, nunca do monstro (sem Model, ele fica parado em vez de obedecer).
            if (IsGameMode)
            {
                for (int i = 0; i < continuous.Length; i++)
                    continuous[i] = 0f;
                ActionSegment<int> idle = actionsOut.DiscreteActions;
                for (int i = 0; i < idle.Length; i++)
                    idle[i] = 0;
                return;
            }

            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");

            // Na mão, olha para onde anda.
            continuous[2] = continuous[0];
            continuous[3] = continuous[1];

            // C = clone andarilho, V = vigia (na mão a máscara não vale: o TryActivate ignora se não estiver pronto).
            // Ramos reservados: sempre 0.
            ActionSegment<int> discrete = actionsOut.DiscreteActions;
            for (int i = 0; i < discrete.Length; i++)
                discrete[i] = 0;
            if (discrete.Length > 0)
                discrete[0] = Input.GetKey(KeyCode.C) ? 1 : Input.GetKey(KeyCode.V) ? 2 : 0;
        }
#endif
    }
}
