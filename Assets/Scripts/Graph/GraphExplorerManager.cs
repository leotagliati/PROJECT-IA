using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Agente explorador: único que conhece os callbacks do ML-Agents. Ordem do step: sentir ->
    /// observar -> agir -> avaliar -> terminar; monta o <see cref="GraphStepContext"/> e delega a
    /// recompensa ao <see cref="GraphRewardSystem"/>. Fala com a arena (currículo, spawn), as memórias
    /// de nós e de salas, e ping/visão/procura (opcionais: sem eles o bloco da observação sai zerado).
    /// Observação EGOCÊNTRICA (nó atual, vizinhos, sala atual e suas portas) mais a planta de salas
    /// num BufferSensor, e não "todos os nós", que amarraria a rede a este mapa. Parede vem do Ray
    /// Perception Sensor 3D do prefab. Silencioso: ObservationSize tem que bater com o VectorObservationSize.
    /// </summary>
    public class GraphExplorerManager : Agent
    {
        // Por vizinho (10): [0..1] direção X/Z, [2] distância, [3] visitado, [4] quanto resta por essa saída
        // (dentro da sala), [5] vim daqui, [6] quantas vezes passei (satura em _revisitSaturation), [7] quão
        // perto está o que falta por essa saída (dentro da sala), [8] suspeita por essa saída, [9] válido.
        private const int FloatsPerNeighbor = 10;

        // Por PORTA da sala atual (9): [0..1] direção X/Z, [2] distância reta, [3] distância pelo grafo dentro
        // da sala, [4] novidade (1 = nunca atravessada), [5] entrei por aqui, [6] sala do outro lado concluída,
        // [7] calor (porta da sala do último ping), [8] válido. Ordem por ângulo em volta da sala
        // (GraphRoomMemory.DoorSlots); 8 slots, o máximo por sala no NodeTraining5 é 7.
        private const int FloatsPerDoor = 9;
        private const int DoorSlots = 8;

        // LAYOUT DAS OBSERVAÇÕES GLOBAIS (30):
        //   [0]      está dentro da área de algum nó
        //   [1..3]   direção X/Z + distância ao nó âncora
        //   [4..6]   direção X/Z + distância ao nó mais próximo COM LINHA LIVRE
        //   [7]      COBERTURA: fração das salas concluídas (a que encerra o episódio)
        //   [8]      encostado em parede, punida ou não (0/1)
        //   [9]      SALA ATUAL: progresso rumo à conclusão (1 = concluída)
        //   [10]     sala atual concluída (0/1)
        //   [11]     está num vão (a âncora é uma porta)
        //   [12]     nº de portas da sala atual / 8
        //   [13..15] PING: ativo, distância pelo grafo / diâmetro, quente/frio (-1/0/+1)
        //   [16..20] VISÃO: vendo, já viu, direção X/Z + distância à última posição vista do hider
        //   [21]     CALOR: a sala atual é a do último ping (0..1)
        //   [22..23] para onde o CORPO está virado (X/Z no mundo)
        //   [24..25] VELOCIDADE do hider enquanto vê (X/Z, / _hiderVelocityScale)
        //   [26]     força do STEER ASSIST na lição (0..1)
        //   [27]     PROCURA ativa (há hider no episódio)
        //   [28]     CERTEZA: maior suspeita de um nó (1 = sei onde ele está)
        //   [29]     tempo desde a última pista (ping ou visão) / 60 s; 1 = nenhuma
        // Depois: vizinhos (8 x 10) e portas (8 x 9). Total 30 + 80 + 72 = 182.
        private const int GlobalObservations = 30;

        // PLANTA DE SALAS (BufferSensor "Rooms", fora do VectorObservationSize): uma entrada por sala do mapa
        // (até MaxRooms); lista com atenção, para a rede não ficar amarrada a este mapa. Por sala (10):
        // [0..1] direção X/Z ao centro, [2] distância reta / diâmetro, [3] portas até ela / RoomHopsScale
        // (1 = inalcançável), [4] quanto já foi vista, [5] concluída, [6] quente (ping), [7] suspeita
        // (0 = média, 1 = teto), [8] é a atual, [9] nº de portas / 8.
        private const int RoomFeatures = 10;
        private const int MaxRooms = 32;
        private const float RoomHopsScale = 10f;
        private const string RoomSensorName = "Rooms";
        private BufferSensorComponent _roomSensor;
        private readonly float[] _roomBuffer = new float[RoomFeatures];


        [Header("-----Systems-----")]
        [SerializeField] private GraphExplorationMemory _memory;
        // Salas e portas. Sem ela no prefab, o Initialize cria uma com os valores padrão (e avisa).
        [SerializeField] private GraphRoomMemory _rooms;
        [SerializeField] private GraphRewardSystem _rewardSystem;
        // Reaproveitado do seeker: driver de Rigidbody sem regra de seeker (Move / ResetMovement).
        // Se ganhar lógica específica do seeker, copie para cá.
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphArenaController _arenaController;
        // Opcional: sem ele, o bloco de ping da observação emite zero e nada de ping é pago.
        [SerializeField] private GraphPingSystem _ping;
        // Opcional: sem ele, o bloco de visão emite zero e nada de visão é pago.
        [SerializeField] private GraphHiderPerception _perception;
        // Opcional: sem ele a procura fica desligada (observação zero, nada pago); o ValidateSetup avisa.
        [SerializeField] private GraphSuspicionMap _suspicion;

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
        // Em steps de FÍSICA, não decisões: 8000 = 160 s a 0.02 s, ou 1600 decisões com Decision Period 5
        // (o Episode Length do TensorBoard). Contato com parede e estagnação são cobrados por step: dobrar
        // isto dobra o teto deles (tabela no cabeçalho do GraphRewardSystem).
        [SerializeField] private int _maxEpisodeSteps = 8000;

        [Header("-----Parede sem punição-----")]
        // Layers que são PAREDE para visão, grafo, steering e observação [8], mas cujo contato NÃO é punido
        // (nem contínuo, nem batida); para os batentes, onde o corpo raspava. Tem que estar TAMBÉM no Wall
        // Layer do NavGraph (o ValidateSetup avisa). A parede inteira do Door_Hole vira grátis, não só o batente.
        [SerializeField] private LayerMask _penaltyFreeWallLayer;

        [Header("-----Diagnóstico-----")]
        // Loga no fim do episódio os 3 nós com mais loop/pisca-pisca. Desligado no treino (várias arenas
        // enchem o Console); ligue ao assistir um .onnx no Play.
        [SerializeField] private bool _logLoopNodes = false;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private NavGraph _graph;

        private int _elapsedSteps;
        private bool _episodeEnding;

        // Modo de jogo: só caça com o GameManager em Playing (parado na preparação e depois do fim).
        private bool _hunting = true;

        /// <summary>Modo de jogo: o seeker pegou o jogador.</summary>
        public event Action PlayerCaught;
        // Encostado em parede PUNIDA (custo contínuo + batida) e em parede SEM punição (batente,
        // ver _penaltyFreeWallLayer). As duas entram na observação [8]; só a primeira custa.
        private bool _touchingWall;
        private bool _touchingFreeWall;

        // Steps de física encostado em parede no episódio (métrica Exploration/WallContactFraction).
        private int _wallContactSteps;

        // Steps de física parado (< IdleSpeed, m/s) no episódio (Movement/IdleFraction). Só medição: parar é
        // permitido; a métrica pega o ótimo local "fico quieto e não perco nada".
        private const float IdleSpeed = 0.5f;
        private int _idleSteps;
        private Rigidbody _body;

        // Batidas em parede com janela recente (custo escalonado). Estado entre steps, fora do Manager.
        private readonly WallHitTracker _wallHits = new WallHitTracker();

        // Ação anterior, para o custo de suavidade. Sem ação anterior (1ª do episódio) não cobra.
        private Vector2 _lastAction;
        private Vector2 _lastLook;
        private bool _hasLastAction;
        private float _actionChangeSq;
        private float _lookChangeSq;

        // Soma de |Δação|² e |Δolhar|² e número de decisões no episódio (métricas
        // Movement/ActionJitter e Movement/LookJitter).
        private float _actionChangeSum;
        private float _lookChangeSum;
        private int _decisionCount;

        private readonly List<int> _neighborBuffer = new List<int>();
        private Comparison<int> _byDistanceFromNode;
        private Comparison<int> _byAngleFromNode;
        private Vector3 _sortOrigin;

        public int ObservationSize => GlobalObservations + _neighborSlots * FloatsPerNeighbor + DoorSlots * FloatsPerDoor;

        /// <summary>Normalizador de distância a nó. O NavGraphPlacer usa como alcance do teste "tem nó à vista?".</summary>
        public float MaxNodeDistance => _maxNodeDistance;

        private float CurrentCoverage => _rooms.CompletedFraction;

        // Cria o BufferSensor da planta de salas se o prefab não tiver, já com o tamanho certo (tamanho errado
        // no Inspector quebraria o treino em silêncio). Roda antes do Agent.OnEnable, que coleta os sensores.
        protected override void Awake()
        {
            _roomSensor = null;
            foreach (BufferSensorComponent sensor in GetComponents<BufferSensorComponent>())
            {
                if (sensor.SensorName == RoomSensorName)
                    _roomSensor = sensor;
            }

            if (_roomSensor == null)
                _roomSensor = gameObject.AddComponent<BufferSensorComponent>();

            _roomSensor.SensorName = RoomSensorName;
            _roomSensor.ObservableSize = RoomFeatures;
            _roomSensor.MaxNumObservables = MaxRooms;

            ApplyGameModeBehavior();

            base.Awake();
        }

        // Modo de jogo roda o .onnx sem trainer, e determinístico (a média da política, sem sorteio).
        // Antes do base.Awake/OnEnable, que criam a política a partir do Behavior Parameters.
        private void ApplyGameModeBehavior()
        {
            GraphArenaController arena = _arenaController != null
                ? _arenaController
                : GetComponentInParent<GraphArenaController>();
            if (arena == null || !arena.GameMode)
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

        private bool IsGameMode
        {
            get
            {
                if (_arenaController == null)
                    _arenaController = GetComponentInParent<GraphArenaController>();
                return _arenaController != null && _arenaController.GameMode;
            }
        }

        private void HandleGameState(GameState state)
        {
            _hunting = state == GameState.Playing;
            if (!_hunting && _movementSystem != null)
                _movementSystem.ResetMovement();
        }

        public override void Initialize()
        {
            // Checagem explícita, não ??= (fake null do Unity).
            if (_memory == null)
                _memory = GetComponentInChildren<GraphExplorationMemory>();

            if (_rooms == null)
                _rooms = GetComponentInChildren<GraphRoomMemory>();

            if (_rooms == null && _memory != null)
            {
                // Prefab de antes das salas: cria com os padrões em vez de quebrar o treino.
                _rooms = _memory.gameObject.AddComponent<GraphRoomMemory>();
                Debug.LogWarning(
                    $"{name}: sem GraphRoomMemory no prefab — criada em runtime com os valores padrão. " +
                    "Add Component > Graph Room Memory no agente para ajustar no Inspector.", this);
            }

            if (_rewardSystem == null)
                _rewardSystem = GetComponentInChildren<GraphRewardSystem>();

            if (_movementSystem == null)
                _movementSystem = GetComponentInChildren<SeekerMovementSystem>();

            if (_arenaController == null)
                _arenaController = GetComponentInParent<GraphArenaController>();

            if (_ping == null)
                _ping = GetComponentInChildren<GraphPingSystem>();

            if (_perception == null)
                _perception = GetComponentInChildren<GraphHiderPerception>();

            if (_suspicion == null)
                _suspicion = GetComponentInChildren<GraphSuspicionMap>();

            _byDistanceFromNode = CompareByDistance;
            _byAngleFromNode = CompareByAngle;

            _initialLocalPosition = transform.localPosition;
            _initialLocalRotation = transform.localRotation;
            _body = GetComponent<Rigidbody>();

            if (_arenaController != null)
                _graph = _arenaController.Graph;

            if (_graph != null && _memory != null)
            {
                _graph.EnsureBaked();
                _memory.Configure(_graph);
                _rooms.Configure(_graph, _memory, _perception, _suspicion);

                if (_ping != null)
                    _ping.Configure(_graph);

                if (_perception != null)
                    _perception.Configure(_graph);

                if (_suspicion != null)
                    _suspicion.Configure(_graph, _perception, _ping);

                if (_arenaController.GameMode)
                    TargetPlayer();
            }

            ValidateSetup();
        }

        // Modo de jogo: visão, procura e ping passam a olhar o jogador (achado pela tag) em vez do hider.
        private void TargetPlayer()
        {
            GraphPlayerTarget player = _arenaController.PlayerTarget;
            if (player == null)
                return;

            if (_perception != null)
                _perception.SetTarget(player);
            if (_ping != null)
                _ping.SetTarget(player);
            if (_suspicion != null)
                _suspicion.SetTarget(player);
        }

        public override void OnEpisodeBegin()
        {
            _elapsedSteps = 0;
            _episodeEnding = false;
            _touchingWall = false;
            _touchingFreeWall = false;

            _arenaController.ResetEpisode();

            if (_arenaController.TryGetSpawn(out Vector3 position, out Quaternion rotation))
                transform.SetPositionAndRotation(position, rotation);
            else
                transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

            _movementSystem.ResetMovement();

            // A arena já leu o currículo em ResetEpisode(); a parede é a mesma máscara do grafo.
            _movementSystem.ConfigureSteering(_arenaController.SteerAssist, _graph.WallLayer);

            // Depois do spawn: o hider nasce longe de onde o seeker nasceu.
            _arenaController.ResetHider(transform.position);

            // Nós antes de salas: a sala que nasce concluída marca os nós dela na memória de nós.
            _memory.ResetEpisode();
            _rooms.ResetEpisode(
                _arenaController.RoomCompleteThreshold,
                _arenaController.PrevisitedFraction,
                _arenaController.ReleaseFraction,
                _arenaController.VisionExplores);
            _wallContactSteps = 0;
            _idleSteps = 0;
            _wallHits.Reset();
            _hasLastAction = false;
            _actionChangeSq = 0f;
            _lookChangeSq = 0f;
            _actionChangeSum = 0f;
            _lookChangeSum = 0f;
            _decisionCount = 0;
            _rewardSystem.ResetEpisode();

            if (_ping != null)
                _ping.ResetEpisode(_arenaController.PingInterval);

            if (_perception != null)
                _perception.ResetEpisode();

            // Procura só com hider. Velocidade que o seeker SUPÕE: a da lição; parado = 0; 0 no currículo = padrão do mapa (-1).
            if (_suspicion != null)
            {
                GraphHider.Mode mode = _arenaController.HiderMode;
                float assumedSpeed = mode == GraphHider.Mode.Static ? 0f
                    : _arenaController.HiderSpeed > 0f ? _arenaController.HiderSpeed
                    : -1f;
                _suspicion.ResetEpisode(mode != GraphHider.Mode.None, assumedSpeed);
            }

            // Registra o nó do spawn já: senão ele pagaria descoberta só por o agente ter nascido em cima.
            // Continua contando para a cobertura da sala.
            _memory.Tick(transform.position);
            _rooms.Tick(transform);
            _memory.ClearStepFlags();
            _rooms.ClearStepFlags();

            RememberHider();
        }

        // Memória amostrada a cada step de FÍSICA, não por decisão: com Decision Period > 1 o agente pode
        // cruzar um nó inteiro entre duas decisões.
        private void FixedUpdate()
        {
            if (_episodeEnding || _graph == null)
                return;

            _memory.Tick(transform.position);

            // Ordem importa: nós -> salas -> ping -> visão -> procura; cada um lê o estado do anterior neste step.
            _rooms.Tick(transform);

            if (_ping != null)
            {
                _ping.Tick(_memory.CurrentNodeIndex, _elapsedSteps);

                int started = _ping.ConsumeStarted();
                if (started >= 0)
                    _rooms.HeatRoom(_graph.RoomOf(started));
            }

            if (_perception != null)
                _perception.Tick(transform);

            if (_suspicion != null)
                _suspicion.Tick(transform, _memory.CurrentNodeIndex);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector3 position = transform.position;
            int current = _memory.CurrentNodeIndex;

            // ---- Nó atual (4) ----
            sensor.AddObservation(_memory.IsAtNode);
            AddDirectionAndDistance(sensor, position, current >= 0 ? _graph.NodePosition(current) : position, current >= 0);

            // ---- Nó mais próximo alcançável (3) ----
            // Onde a malha está AGORA (a âncora diz de onde vim); sem isto, quem saiu do grafo só recebe a
            // direção de um nó que ficou para trás. Uma vez por DECISÃO, não por step de física: o CapsuleCast é caro.
            int nearest = _graph.FindNearestReachableNode(position);
            AddDirectionAndDistance(sensor, position, nearest >= 0 ? _graph.NodePosition(nearest) : position, nearest >= 0);

            // ---- Cobertura (1) + encostado em parede (1) ----
            // Salas concluídas no geral; e se o último step de física terminou em contato (OnCollisionStay roda
            // depois da decisão anterior, antes desta).
            sensor.AddObservation(CurrentCoverage);
            sensor.AddObservation(_touchingWall || _touchingFreeWall ? 1f : 0f);

            // ---- Sala atual (4) ----
            // Uma vez por decisão: portas da sala e quanto resta por saída.
            _rooms.Refresh();
            sensor.AddObservation(_rooms.CurrentRoomProgress);
            sensor.AddObservation(_rooms.CurrentRoomCompleted ? 1f : 0f);
            sensor.AddObservation(current >= 0 && _graph.IsDoor(current) ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(_rooms.CurrentRoomDoorCount / (float)DoorSlots));

            // ---- Ping (3) ----
            // Sem direção de propósito: a política descobre a saída lendo o quente/frio a cada troca de nó.
            bool pingActive = _ping != null && _ping.IsActive;
            sensor.AddObservation(pingActive ? 1f : 0f);
            sensor.AddObservation(pingActive ? Mathf.Clamp01(_ping.Distance / _graph.PathDiameter) : 0f);
            sensor.AddObservation(pingActive ? _ping.HotCold : 0f);

            // ---- Visão (5) ----
            // Direção + distância à ÚLTIMA POSIÇÃO VISTA (a atual enquanto vê; congelada ao perder).
            bool seeing = _perception != null && _perception.IsSeeing;
            bool hasSeen = _perception != null && _perception.HasSeen;
            sensor.AddObservation(seeing ? 1f : 0f);
            sensor.AddObservation(hasSeen ? 1f : 0f);
            AddDirectionAndDistance(sensor, position, hasSeen ? _perception.LastSeenPosition : position, hasSeen);

            // ---- Calor da sala atual (1): quão perto do último ping, 0..1 (esfria com o tempo) ----
            sensor.AddObservation(_rooms.CurrentRoomHeat);

            // ---- Corpo (5): para onde olha, velocidade do hider, força do assist ----
            Vector3 forward = transform.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);
            facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector2.up;
            sensor.AddObservation(facing.x);
            sensor.AddObservation(facing.y);

            Vector3 hiderVelocity = seeing ? _perception.HiderVelocity / _hiderVelocityScale : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.z, -1f, 1f));

            sensor.AddObservation(_arenaController.SteerAssist);

            // ---- Procura (3) ----
            bool searching = _suspicion != null && _suspicion.IsActive;
            sensor.AddObservation(searching ? 1f : 0f);
            sensor.AddObservation(searching ? _suspicion.Certainty : 0f);
            sensor.AddObservation(searching ? _suspicion.EvidenceAge : 0f);

            // ---- Vizinhos (FloatsPerNeighbor x _neighborSlots) ----
            FillNeighborBuffer(current);
            if (_suspicion != null)
                _suspicion.ScoreExits(current);

            for (int slot = 0; slot < _neighborSlots; slot++)
            {
                if (slot >= _neighborBuffer.Count)
                {
                    // Slot vazio: zeros e validade 0, para ser distinguível de um vizinho real exatamente na posição do agente.
                    for (int k = 0; k < FloatsPerNeighbor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int neighbor = _neighborBuffer[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(neighbor), true);
                sensor.AddObservation(_memory.VisitedObservation(neighbor));

                // O que há ATRÁS desta saída, dentro da sala (para na porta).
                sensor.AddObservation(_rooms.ExitRemainingScore(neighbor));

                // Contra loop: de onde vim e quantas vezes passei.
                sensor.AddObservation(neighbor == _memory.PreviousNodeIndex ? 1f : 0f);
                sensor.AddObservation(Mathf.Clamp01((float)_memory.VisitCountOf(neighbor) / _revisitSaturation));

                // Quão perto está o que falta por esta saída (1 = a mais perto). Campo de distância: seguir o 1 só
                // diminui, ao contrário do "quanto resta" (soma com desconto); ajuda contra loop e beco.
                sensor.AddObservation(_rooms.ExitProximityScore(neighbor));

                // Onde o hider provavelmente está, por esta saída (0 sem procura).
                sensor.AddObservation(_suspicion != null ? _suspicion.ExitScore(neighbor) : 0f);
                sensor.AddObservation(1f);
            }

            // ---- Planta de salas (BufferSensor, uma entrada por sala) ----
            AddRoomPlan(position);

            // ---- Portas da sala atual (FloatsPerDoor x DoorSlots) ----
            IReadOnlyList<int> doors = _rooms.DoorSlots;
            for (int slot = 0; slot < DoorSlots; slot++)
            {
                if (slot >= doors.Count)
                {
                    for (int k = 0; k < FloatsPerDoor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int door = doors[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(door), true);

                // Pelo grafo, dentro da sala: em linha reta a porta pode estar atrás de um móvel.
                float path = _rooms.DoorPathDistance(door);
                sensor.AddObservation(path >= 0f ? Mathf.Clamp01(path / _maxNodeDistance) : 1f);

                sensor.AddObservation(_rooms.DoorNovelty(door));
                sensor.AddObservation(door == _rooms.EntryDoor ? 1f : 0f);
                sensor.AddObservation(_rooms.OtherSideCompleted(door) ? 1f : 0f);
                sensor.AddObservation(_rooms.DoorHeat(door));
                sensor.AddObservation(1f);
            }
        }

        // Uma entrada por sala no BufferSensor "Rooms" (layout no topo).
        private void AddRoomPlan(Vector3 position)
        {
            if (_roomSensor == null)
                return;

            float diameter = _graph.PathDiameter;
            int current = _rooms.CurrentRoom;
            for (int room = 0; room < _graph.RoomCount && room < MaxRooms; room++)
            {
                Vector3 delta = _graph.RoomCentroid(room) - position;
                var planar = new Vector2(delta.x, delta.z);
                float distance = planar.magnitude;
                Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;
                int hops = _rooms.RoomHopsFromCurrent(room);

                _roomBuffer[0] = unit.x;
                _roomBuffer[1] = unit.y;
                _roomBuffer[2] = Mathf.Clamp01(distance / diameter);
                _roomBuffer[3] = hops >= 0 ? Mathf.Clamp01(hops / RoomHopsScale) : 1f;
                _roomBuffer[4] = _rooms.RoomProgress(room);
                _roomBuffer[5] = _rooms.IsRoomCompleted(room) ? 1f : 0f;
                _roomBuffer[6] = _rooms.RoomHeat(room);
                _roomBuffer[7] = _rooms.RoomSuspicion(room);
                _roomBuffer[8] = room == current ? 1f : 0f;
                _roomBuffer[9] = Mathf.Clamp01(_graph.DoorsOfRoom(room).Length / (float)DoorSlots);
                _roomSensor.AppendObservation(_roomBuffer);
            }
        }

        // Direção planar X/Z no referencial do MUNDO (o das ações) + distância normalizada; zeros se !valid.
        private void AddDirectionAndDistance(VectorSensor sensor, Vector3 from, Vector3 to, bool valid)
        {
            if (!valid)
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                return;
            }

            Vector3 delta = to - from;
            Vector2 planar = new(delta.x, delta.z);
            float distance = planar.magnitude;
            Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;

            sensor.AddObservation(unit.x);
            sensor.AddObservation(unit.y);
            sensor.AddObservation(Mathf.Clamp01(distance / _maxNodeDistance));
        }

        /// <summary>
        /// Vizinhos ativos do nó atual (até <see cref="_neighborSlots"/>) em ordem ESTÁVEL: por ângulo no
        /// mundo a partir do nó, para o mesmo vizinho cair sempre no mesmo slot (senão é ruído). Com excesso, corta os mais distantes.
        /// </summary>
        private void FillNeighborBuffer(int current)
        {
            _neighborBuffer.Clear();

            if (current < 0)
                return;

            _sortOrigin = _graph.NodePosition(current);

            foreach (int neighbor in _graph.GetNeighbors(current))
            {
                if (_graph.IsNodeEnabled(neighbor))
                    _neighborBuffer.Add(neighbor);
            }

            if (_neighborBuffer.Count > _neighborSlots)
            {
                _neighborBuffer.Sort(_byDistanceFromNode);
                _neighborBuffer.RemoveRange(_neighborSlots, _neighborBuffer.Count - _neighborSlots);
            }

            _neighborBuffer.Sort(_byAngleFromNode);
        }

        private int CompareByDistance(int a, int b)
        {
            float da = (_graph.NodePosition(a) - _sortOrigin).sqrMagnitude;
            float db = (_graph.NodePosition(b) - _sortOrigin).sqrMagnitude;
            return da.CompareTo(db);
        }

        private int CompareByAngle(int a, int b)
        {
            float angleA = AngleFromOrigin(a);
            float angleB = AngleFromOrigin(b);
            int comparison = angleA.CompareTo(angleB);

            // Desempate pelo índice (nós empilhados no mesmo ângulo).
            return comparison != 0 ? comparison : a.CompareTo(b);
        }

        private float AngleFromOrigin(int node)
        {
            Vector3 delta = _graph.NodePosition(node) - _sortOrigin;
            return Mathf.Atan2(delta.z, delta.x);
        }

        // Avalia ANTES de agir: a posição já é o resultado da ação anterior. Com TakeActionsBetweenDecisions
        // roda todo step de física, e os flags de step são consumidos aqui.
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_episodeEnding)
                return;

            // Antes do contexto: a batida e a mudança de ação DESTE step entram na conta dele.
            _wallHits.Step(_touchingWall);
            ActionSegment<float> continuous = actions.ContinuousActions;
            TrackActionChange(new Vector2(continuous[0], continuous[1]), new Vector2(continuous[2], continuous[3]));

            AddReward(_rewardSystem.EvaluateStep(BuildStepContext()));

            if (_touchingWall)
                _wallContactSteps++;

            if (_body != null)
            {
                Vector3 velocity = _body.linearVelocity;
                if (velocity.x * velocity.x + velocity.z * velocity.z < IdleSpeed * IdleSpeed)
                    _idleSteps++;
            }

            // Flags consumidas só depois de cobradas.
            RememberHider();
            _memory.ClearStepFlags();
            _rooms.ClearStepFlags();
            if (_ping != null)
                _ping.ClearStepFlags();
            if (_perception != null)
                _perception.ClearStepFlags();
            if (_suspicion != null)
                _suspicion.ClearStepFlags();
            _touchingWall = false;
            _touchingFreeWall = false;

            // Andar [0..1] e olhar [2..3], os dois no referencial do mundo.
            Vector3 direction = _hunting ? new Vector3(continuous[0], 0f, continuous[1]) : Vector3.zero;
            Vector3 look = _hunting ? new Vector3(continuous[2], 0f, continuous[3]) : Vector3.zero;
            _movementSystem.Move(direction, look);

            // Captura antes da cobertura: na caça é ela que explica o fim se as duas ocorrerem no mesmo step.
            if (_perception != null && _perception.Caught && _arenaController.GameMode)
            {
                // Na preparação o jogador pode passar encostado: não vale, e não fica guardado para depois.
                if (_hunting)
                    CatchPlayer();
                else
                    _perception.ForgetCaught();
                return;
            }

            if (_perception != null && _perception.Caught)
            {
                float remaining = 1f - (float)_elapsedSteps / Mathf.Max(1, _maxEpisodeSteps);
                AddReward(_rewardSystem.HiderCaughtReward(remaining));
                FinishEpisode(covered: true);
                return;
            }

            // Patrulha (alvo > 1): sem fim por cobertura, vai até o timeout.
            if (_arenaController.EndsOnCoverage && CurrentCoverage >= _arenaController.CoverageTarget)
            {
                AddReward(_rewardSystem.FullCoverageReward);
                FinishEpisode(covered: true);
                return;
            }

            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps && !_arenaController.GameMode)
                FinishEpisode(covered: false);
        }

        // |Δação|² e |Δolhar|² desde a ação anterior; zero entre decisões (a ação se repete), então a
        // suavidade só cobra onde a política escolhe.
        private void TrackActionChange(Vector2 action, Vector2 look)
        {
            _actionChangeSq = _hasLastAction ? (action - _lastAction).sqrMagnitude : 0f;
            _lookChangeSq = _hasLastAction ? (look - _lastLook).sqrMagnitude : 0f;
            if (_hasLastAction && (_actionChangeSq > 0f || _lookChangeSq > 0f))
            {
                _actionChangeSum += _actionChangeSq;
                _lookChangeSum += _lookChangeSq;
                _decisionCount++;
            }

            _lastAction = action;
            _lastLook = look;
            _hasLastAction = true;
        }

        private GraphStepContext BuildStepContext()
        {
            bool hiderComparable =
                _perception != null && _perception.IsSeeing && _wasSeeingAtLastDecision;

            float hiderDelta = hiderComparable ? _hiderDistanceAtLastDecision - _perception.CurrentDistance : 0f;

            return new GraphStepContext(
                _maxEpisodeSteps,
                _rooms.RoomNodeValue,
                _rooms.RoomTailValue,
                _rooms.RoomCompletedValue,
                _rooms.DoorCrossValue,
                _rooms.RoomExitValue,
                _rooms.StepsSinceProgress,
                _touchingWall,
                _ping != null && _ping.Reached,
                _ping != null ? _ping.ReachedValue : 0f,
                _ping != null && _ping.Missed,
                _perception != null && _perception.Spotted,
                hiderDelta,
                hiderComparable,
                _memory.EarlyRevisitArrivals,
                _memory.EarlyRevisitStreak,
                _wallHits.HitsThisStep,
                _wallHits.RecentHits,
                _actionChangeSq,
                _lookChangeSq,
                _arenaController.PingRewardScale,
                _arenaController.DiscoveryRewardScale,
                _perception != null && _perception.IsSeeing,
                _suspicion != null ? _suspicion.ClearedMass : 0f);
        }

        // Visão e distância na decisão anterior. O delta de aproximação só vale se VIA nas duas: ganhar ou
        // perder visão faz a distância saltar sem o agente andar.
        private bool _wasSeeingAtLastDecision;
        private float _hiderDistanceAtLastDecision;

        private void RememberHider()
        {
            _wasSeeingAtLastDecision = _perception != null && _perception.IsSeeing;
            _hiderDistanceAtLastDecision = _wasSeeingAtLastDecision ? _perception.CurrentDistance : 0f;
        }

        // OnCollisionStay dispara por collider a cada step: só marca a flag; quem cobra é o step, uma vez,
        // mesmo tocando três paredes numa quina.
        private void OnCollisionStay(Collision collision)
        {
            GameObject other = collision.gameObject;
            if (!IsWall(other))
                return;

            if (IsPenaltyFreeWall(other))
                _touchingFreeWall = true;
            else
                _touchingWall = true;
        }

        // Parede por LAYER (a máscara do grafo), nunca por tag: uma segunda fonte de verdade já deixou um
        // termo de recompensa morto e silencioso.
        private bool IsWall(GameObject other) =>
            _graph != null && (_graph.WallLayer.value & (1 << other.layer)) != 0;

        // Parede SEM punição (_penaltyFreeWallLayer); só vale se a layer também estiver no Wall Layer do NavGraph.
        private bool IsPenaltyFreeWall(GameObject other) =>
            (_penaltyFreeWallLayer.value & (1 << other.layer)) != 0;

        // Modo de jogo: com GameManager, é derrota do jogador (ele recarrega a cena) e o seeker para onde
        // está; sem GameManager (cena de teste), o seeker renasce e caça de novo. É o único respawn do modo.
        private void CatchPlayer()
        {
            PlayerCaught?.Invoke();

            if (GameManager.Current != null)
            {
                _episodeEnding = true;
                _hunting = false;
                _movementSystem.ResetMovement();
                GameManager.Current.PlayerCaught();
                return;
            }

            FinishEpisode(covered: true);
        }

        private void FinishEpisode(bool covered)
        {
            _episodeEnding = true;
            _arenaController.ShowOutcome(covered);
            RecordEpisodeStats();

            float delay = _arenaController.EpisodeEndDelay;
            if (delay <= 0f)
            {
                EndEpisode();
                return;
            }

            StartCoroutine(EndEpisodeAfterDelay(delay));
        }

        /// <summary>
        /// Métricas do episódio no TensorBoard, separadas da recompensa (que muda a cada ajuste de peso):
        /// compare runs por elas.
        ///   Exploration/Coverage         fração das salas concluídas ao fim
        ///   Rooms/Completed              salas concluídas (inclui liberadas e refeitas)
        ///   Doors/Crossings              travessias de porta
        ///   Doors/RepeatFraction         fração das travessias por porta já usada (novidade &lt; 1)
        ///   Doors/UsedFraction           fração das portas do mapa atravessadas ao menos uma vez
        ///   Exploration/OffNodeFraction  fração dos steps fora de qualquer nó (alto = rode o NavGraphPlacer)
        ///   Exploration/WallContactFraction  fração dos steps encostado em parede punida
        ///   Exploration/EarlyRevisits    revisitas precoces (loop)
        ///   Exploration/AnchorFlicker    pisca-pisca de âncora (A-B-A em &lt; 2 s andando &lt; 1 m): borda de ladrilho
        ///   Exploration/WallHits         batidas em parede (início de contato)
        ///   Movement/ActionJitter        média de |Δação|² por decisão (0 = sempre reto); LookJitter = o do olhar
        ///   Movement/IdleFraction        fração do episódio parado (&lt; 0.5 m/s)
        ///   Hunt/Seen, Hunt/Caught       só com hider: viu alguma vez / pegou
        ///   Search/Cleared               suspeita limpa que pagou (procura)
        /// </summary>
        private void RecordEpisodeStats()
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            stats.Add("Exploration/Coverage", CurrentCoverage);
            stats.Add("Rooms/Completed", _rooms.RoomsCompletedTotal);
            stats.Add("Doors/Crossings", _rooms.Crossings);
            if (_rooms.Crossings > 0)
                stats.Add("Doors/RepeatFraction", (float)_rooms.RepeatCrossings / _rooms.Crossings);
            if (_rooms.DoorCount > 0)
                stats.Add("Doors/UsedFraction", (float)_rooms.DoorsUsed / _rooms.DoorCount);

            if (_memory.TickedSteps > 0)
                stats.Add("Exploration/OffNodeFraction", (float)_memory.OffNodeSteps / _memory.TickedSteps);

            if (_elapsedSteps > 0)
            {
                stats.Add("Exploration/WallContactFraction", (float)_wallContactSteps / _elapsedSteps);
                stats.Add("Movement/IdleFraction", (float)_idleSteps / _elapsedSteps);
            }

            stats.Add("Exploration/EarlyRevisits", _memory.EarlyRevisitCount);
            stats.Add("Exploration/AnchorFlicker", _memory.AnchorFlickers);

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
            stats.Add("Exploration/WallHits", _wallHits.EpisodeHits);

            if (_decisionCount > 0)
            {
                stats.Add("Movement/ActionJitter", _actionChangeSum / _decisionCount);
                stats.Add("Movement/LookJitter", _lookChangeSum / _decisionCount);
            }

            if (_perception != null && _arenaController.HiderMode != GraphHider.Mode.None)
            {
                stats.Add("Hunt/Seen", _perception.HasSeen ? 1f : 0f);
                stats.Add("Hunt/Caught", _perception.Caught ? 1f : 0f);
            }

            if (_suspicion != null && _suspicion.IsActive)
                stats.Add("Search/Cleared", _suspicion.EpisodeCleared);
        }

        private IEnumerator EndEpisodeAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            EndEpisode();
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // Dirigir na mão: confere se os nós registram visita e se as ligações são percorríveis (olhe os gizmos).
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;
            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");

            // Na mão, olha para onde anda (parado, o olhar zero mantém a direção).
            continuous[2] = continuous[0];
            continuous[3] = continuous[1];
        }
#endif

        // Erro de wiring em ML-Agents é silencioso (treino que não converge): só loga.
        private void ValidateSetup()
        {
            if (_memory == null || _rooms == null || _rewardSystem == null || _movementSystem == null)
            {
                Debug.LogError($"{name}: sistema do explorador faltando — confira os componentes filhos.", this);
                return;
            }

            if (_arenaController == null)
            {
                Debug.LogError($"{name}: GraphArenaController não encontrado nos pais.", this);
                return;
            }

            if (_arenaController.GameMode)
            {
                if (_arenaController.PlayerTarget == null)
                    Debug.LogError($"{name}: modo de jogo sem jogador — nenhum objeto com a tag do jogador (GraphArenaController).", this);
                if (GameManager.Current == null)
                    Debug.LogWarning($"{name}: modo de jogo sem GameManager — pegar o jogador só faz o seeker renascer.", this);
            }

            if (_graph == null)
            {
                Debug.LogError($"{name}: a arena não tem NavGraph atribuído.", this);
                return;
            }

            // Nó com mais vizinhos que slots: os excedentes ficam invisíveis na observação.
            int worst = 0;
            NavNode worstNode = null;
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                int degree = _graph.GetNeighbors(i).Length;
                if (degree > worst)
                {
                    worst = degree;
                    worstNode = _graph.GetNode(i);
                }
            }

            if (worst > _neighborSlots)
            {
                Debug.LogWarning(
                    $"{name}: '{worstNode.name}' tem {worst} vizinhos e só há {_neighborSlots} slots de observação. " +
                    "Aumente _neighborSlots (e o VectorObservationSize junto) ou pode as ligações redundantes.",
                    worstNode);
            }

            if (_graph.RoomCount > MaxRooms)
            {
                Debug.LogWarning(
                    $"{name}: o mapa tem {_graph.RoomCount} salas e a planta (BufferSensor) só guarda {MaxRooms} — " +
                    "as excedentes ficam fora. Suba MaxRooms (e o treino recomeça do zero).", this);
            }

            // Sala com mais portas que slots: as excedentes ficam invisíveis.
            for (int room = 0; room < _graph.RoomCount; room++)
            {
                int doorCount = _graph.DoorsOfRoom(room).Length;
                if (doorCount > DoorSlots)
                {
                    Debug.LogWarning(
                        $"{name}: a sala S{room} tem {doorCount} portas e só há {DoorSlots} slots de porta na " +
                        "observação — as que sobram ficam invisíveis.", this);
                }
            }

            // Layer "sem punição" fora da máscara do NavGraph nem é parede (visão atravessa, grafo ignora).
            int freeOutsideWalls = _penaltyFreeWallLayer.value & ~_graph.WallLayer.value;
            if (freeOutsideWalls != 0)
            {
                Debug.LogWarning(
                    $"{name}: a layer de parede sem punição não está no Wall Layer do NavGraph — marque " +
                    "ela lá também, senão ela não é parede para visão, grafo e steering.", this);
            }

            if (_suspicion == null)
            {
                Debug.LogWarning(
                    $"{name}: sem GraphSuspicionMap no agente — a procura fica desligada (observação zero, " +
                    "nada pago). Add Component > Graph Suspicion Map antes das lições com hider.", this);
            }

            var behaviorParameters = GetComponent<BehaviorParameters>();
            if (behaviorParameters == null)
                return;

            int declared = behaviorParameters.BrainParameters.VectorObservationSize;
            if (declared != ObservationSize)
            {
                Debug.LogError(
                    $"{name}: VectorObservationSize = {declared} mas o agente emite {ObservationSize} observações " +
                    $"({GlobalObservations} + {_neighborSlots} vizinhos x {FloatsPerNeighbor} + {DoorSlots} portas x {FloatsPerDoor}). " +
                    "Ajuste no Behavior Parameters, senão o treino roda com o vetor truncado.", this);
            }

            int continuousActions = behaviorParameters.BrainParameters.ActionSpec.NumContinuousActions;
            if (continuousActions != 4)
            {
                Debug.LogError(
                    $"{name}: esperadas 4 ações contínuas (andar X/Z, olhar X/Z), encontradas {continuousActions}.", this);
            }
        }
    }
}
