using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Dono do ambiente de UMA arena: grafo, spawns, feedback do chão e leitura do currículo.
    /// O GraphExplorerManager lê as propriedades daqui ao montar o contexto; o hider e o ping
    /// consomem o que é sorteado por episódio. Parâmetro novo de currículo = campo + default
    /// aqui em ApplyCurriculum + propriedade. Nunca guarde estado estático (há várias arenas).
    /// MODO DE JOGO (_gameMode): sem currículo (usa os defaults abaixo), hider desligado e o alvo do
    /// seeker passa a ser o jogador, achado pela tag; o GraphExplorerManager cuida da inferência e do fim.
    /// </summary>
    public class GraphArenaController : MonoBehaviour
    {
        [Header("-----Referências-----")]
        [SerializeField] private NavGraph _graph;
        [SerializeField] private Renderer _floorRenderer;
        [SerializeField] private Transform[] _spawnPoints;
        // Opcional: sem hider a arena é só exploração. Fallback por GetComponentInChildren.
        [SerializeField] private GraphHider _hider;

        [Header("-----Modo de jogo-----")]
        // Ligado: jogo de verdade (inferência, sem currículo, sem hider, caça o jogador, sem timeout).
        [SerializeField] private bool _gameMode = false;

        // Tag do jogador. "Goal" é a mesma convenção do Seeker antigo (o PlayerDummy já vem com ela).
        [SerializeField] private string _playerTag = "Goal";

        [Header("-----Currículo-----")]
        // Fração das SALAS concluídas que encerra o episódio. Acima de 1 = sem fim por cobertura
        // (patrulha). O default vale só sem trainer (Play no editor, inferência).
        [SerializeField] private string _coverageParameterName = "coverage_target";
        [SerializeField, Range(0.05f, 1.1f)] private float _defaultCoverageTarget = 0.35f;

        // Fração dos nós de uma sala que a conclui (0.8 = "explorar 80% da sala").
        [SerializeField] private string _roomCompleteParameterName = "room_complete_threshold";
        [SerializeField, Range(0.05f, 1f)] private float _defaultRoomCompleteThreshold = 0.8f;

        // Fração das SALAS que nasce concluída, sorteada a cada episódio (anti-decoreba). Sempre sobra uma.
        [SerializeField] private string _previsitedParameterName = "previsited_fraction";
        [SerializeField, Range(0f, 0.9f)] private float _defaultPrevisitedFraction = 0f;

        // Liberação (patrulha): com esta fração das portas/salas usadas, a mais antiga volta a valer.
        // 0 = desligada. Usar com coverage_target > 1.
        [SerializeField] private string _releaseParameterName = "release_fraction";
        [SerializeField, Range(0f, 1f)] private float _defaultReleaseFraction = 0f;

        // Intervalo entre pings, em steps de física (GraphPingSystem). 0 = sem ping.
        [SerializeField] private string _pingIntervalParameterName = "ping_interval";
        [SerializeField, Min(0)] private int _defaultPingInterval = 0;

        // Modo do hider (GraphHider.Mode): 0 nenhum, 1 parado, 2 anda, 3 foge. Com hider ligado,
        // os pings vêm dos passos dele e ping_interval é ignorado.
        [SerializeField] private string _hiderModeParameterName = "hider_mode";
        [SerializeField, Range(0, 3)] private int _defaultHiderMode = 0;

        // Velocidade do hider (m/s). 0 = padrão do GraphHider.
        [SerializeField] private string _hiderSpeedParameterName = "hider_speed";
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = 0f;

        // Chance de cada chegada do hider num nó de ping virar ping (GraphHider). 1 = toda chegada.
        [SerializeField] private string _hiderNoiseParameterName = "hider_noise";
        [SerializeField, Range(0f, 1f)] private float _defaultHiderNoise = 1f;

        // Escalas de recompensa por lição (GraphRewardSystem). 1 = o tuning do prefab; 0 em
        // ping_reward_scale faz o ping só informar.
        [SerializeField] private string _pingRewardScaleParameterName = "ping_reward_scale";
        [SerializeField, Min(0f)] private float _defaultPingRewardScale = 1f;
        [SerializeField] private string _discoveryRewardScaleParameterName = "discovery_reward_scale";
        [SerializeField, Min(0f)] private float _defaultDiscoveryRewardScale = 1f;

        // Chance de uma sala de UM nó só ter nó de ping no episódio (as demais sempre têm).
        [SerializeField] private string _pingSingleRoomChanceParameterName = "ping_single_room_chance";
        [SerializeField, Range(0f, 1f)] private float _defaultPingSingleRoomChance = 0.5f;

        // 1 = nós da sala atual dentro do cone de visão contam como pisados (GraphRoomMemory).
        [SerializeField] private string _visionExploresParameterName = "vision_explores";
        [SerializeField, Range(0, 1)] private int _defaultVisionExplores = 0;

        // 1 = hider anda para pontos aleatórios dentro dos nós e se esconde; 0 = de centro em centro.
        [SerializeField] private string _hiderLooseParameterName = "hider_loose";
        [SerializeField, Range(0, 1)] private int _defaultHiderLoose = 0;

        // Steering assistido (SeekerMovementSystem.Steer): fração da velocidade contra a parede
        // removida perto dela (1 = desliza, 0 = bate). Alto no começo, baixo no fim; o default é
        // o da última lição, que é o que roda no jogo.
        [SerializeField] private string _steerAssistParameterName = "steer_assist";
        [SerializeField, Range(0f, 1f)] private float _defaultSteerAssist = 0.3f;

        [Header("-----Spawn-----")]
        // Nasce em um nó ATIVO sorteado por episódio, em vez dos _spawnPoints (que ficam de
        // fallback): muitas origens impedem a política de decorar uma rota.
        [SerializeField] private bool _spawnAtRandomNode = true;

        // Altura acima do nó (que fica no chão) para o collider não nascer dentro do piso.
        [SerializeField] private float _nodeSpawnHeightOffset = 0.15f;

        // Sorteia entre os _spawnPoints; origem fixa deixa a política decorar a sequência de curvas.
        [SerializeField] private bool _randomizeSpawn = true;

        [Header("-----Feedback-----")]
        // Só debug visual. Mantenha 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;
        private bool _initialized;
        private int _lastSpawnIndex = -1;

        /// <summary>
        /// Resolvido sob demanda, não no Awake: o agente lê isto no Initialize, que pode vir antes
        /// do Awake da arena.
        /// </summary>
        public NavGraph Graph
        {
            get
            {
                if (_graph == null)
                    _graph = GetComponentInChildren<NavGraph>();

                return _graph;
            }
        }

        public float EpisodeEndDelay => _episodeEndDelay;

        public bool GameMode => _gameMode;

        private GraphPlayerTarget _playerTarget;

        /// <summary>
        /// O jogador como alvo (modo de jogo), achado pela tag; ganha um GraphPlayerTarget se não tiver.
        /// Null se não há objeto com a tag.
        /// </summary>
        public GraphPlayerTarget PlayerTarget
        {
            get
            {
                if (_playerTarget != null)
                    return _playerTarget;

                GameObject player = GameObject.FindWithTag(_playerTag);
                if (player == null)
                    return null;

                _playerTarget = player.GetComponent<GraphPlayerTarget>();
                if (_playerTarget == null)
                    _playerTarget = player.AddComponent<GraphPlayerTarget>();

                _playerTarget.Configure(Graph);
                return _playerTarget;
            }
        }

        // Atualizados a cada ResetEpisode e lidos pelo manager ao montar o step context.
        public float CoverageTarget { get; private set; }

        /// <summary>Fração dos nós de uma sala que a conclui (0..1).</summary>
        public float RoomCompleteThreshold { get; private set; }

        /// <summary>Fração das salas que nasce concluída neste episódio (0..0.9).</summary>
        public float PrevisitedFraction { get; private set; }

        /// <summary>Fração de portas/salas usadas que dispara a liberação; 0 = desligada.</summary>
        public float ReleaseFraction { get; private set; }

        /// <summary>Cobertura-alvo acima de 1: o episódio não termina por cobertura (patrulha).</summary>
        public bool EndsOnCoverage => CoverageTarget <= 1f;

        /// <summary>Chance (0..1) de uma sala de um nó só ter nó de ping no episódio.</summary>
        public float PingSingleRoomChance { get; private set; }

        /// <summary>O que o agente vê na sala atual conta como pisado.</summary>
        public bool VisionExplores { get; private set; }

        /// <summary>Hider solto (pontos dentro dos nós, esconderijos).</summary>
        public bool HiderLoose { get; private set; }

        /// <summary>Força do steering assistido neste episódio (0..1).</summary>
        public float SteerAssist { get; private set; }

        /// <summary>Pontos de spawn fixos (fallback). Lido pelo NavGraphPlacer para conferir se caem em chão coberto.</summary>
        internal Transform[] SpawnPoints => _spawnPoints;

        /// <summary>Steps de física entre pings neste episódio; 0 = sem ping.</summary>
        public int PingInterval { get; private set; }

        public GraphHider.Mode HiderMode { get; private set; }

        public float HiderSpeed { get; private set; }

        /// <summary>Chance (0..1) de cada chegada do hider virar ping.</summary>
        public float HiderNoise { get; private set; }

        public float PingRewardScale { get; private set; }

        public float DiscoveryRewardScale { get; private set; }

        /// <summary>
        /// Posiciona (ou desliga) o hider. Chamar DEPOIS do spawn do seeker: o hider nasce a uma
        /// distância mínima dele.
        /// </summary>
        public void ResetHider(Vector3 seekerPosition)
        {
            if (_hider == null)
                _hider = GetComponentInChildren<GraphHider>(includeInactive: true);

            if (_hider == null)
                return;

            // Modo de jogo: o hider sai de cena (None desativa o GameObject); quem foge é o jogador.
            if (_gameMode)
                _hider.ResetEpisode(GraphHider.Mode.None, 0f, 0f, seekerPosition, false);
            else
                _hider.ResetEpisode(HiderMode, HiderSpeed, HiderNoise, seekerPosition, HiderLoose);
        }

        private void Awake() => EnsureInitialized();

        // Guarda a cor do chão antes do primeiro ResetEpisode, que pode chegar antes do Awake.
        private void EnsureInitialized()
        {
            if (_initialized)
                return;

            _initialized = true;

            if (_floorRenderer != null)
                _initialFloorColor = _floorRenderer.material.color;
        }

        public void ResetEpisode()
        {
            EnsureInitialized();
            ApplyCurriculum();

            // Antes do hider e do ping do agente: os dois perguntam ao grafo onde o barulho pode ser.
            if (Graph != null)
                Graph.DrawEpisodePingNodes(PingSingleRoomChance);

            if (_floorRenderer != null)
                _floorRenderer.material.color = _initialFloorColor;
        }

        public void ShowOutcome(bool covered)
        {
            if (_floorRenderer != null)
                _floorRenderer.material.color = covered ? Color.green : Color.red;
        }

        /// <summary>
        /// Devolve um spawn; false se nenhum ponto foi configurado (o agente volta à pose inicial dele).
        /// </summary>
        public bool TryGetSpawn(out Vector3 position, out Quaternion rotation)
        {
            if (_spawnAtRandomNode && TryGetNodeSpawn(out position, out rotation))
                return true;

            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            int index;
            if (_randomizeSpawn)
            {
                // Evita repetir o ponto: com poucos spawns, a repetição concentra o treino numa região.
                index = Random.Range(0, _spawnPoints.Length);
                if (_spawnPoints.Length > 1 && index == _lastSpawnIndex)
                    index = (index + 1) % _spawnPoints.Length;
            }
            else
            {
                index = 0;
            }

            _lastSpawnIndex = index;
            Transform point = _spawnPoints[index];
            position = point.position;
            rotation = point.rotation;
            return true;
        }

        // Nó de spawn válido (NavGraph.CanSpawnAt), auxiliares incluídos; rotação só visual
        // (as ações são no referencial do mundo).
        private bool TryGetNodeSpawn(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            NavGraph graph = Graph;
            if (graph == null || graph.NodeCount == 0)
                return false;

            graph.EnsureBaked();

            // Sala primeiro, por igual: sortear entre nós fazia a sala grande nascer 20x mais que um armário.
            int node = RandomSpawnNodeByRoom(graph, -1);
            if (node < 0)
                return false;

            position = graph.NodePosition(node) + Vector3.up * _nodeSpawnHeightOffset;
            rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            return true;
        }

        /// <summary>
        /// Sorteia uma sala por igual (menos <paramref name="avoidRoom"/>, se houver outra) e um nó
        /// de spawn válido dela; nunca porta. -1 se nenhuma sala tem nó válido. Usado pelo spawn do
        /// agente e do hider.
        /// </summary>
        public static int RandomSpawnNodeByRoom(NavGraph graph, int avoidRoom)
        {
            int rooms = graph.RoomCount;
            if (rooms == 0)
                return -1;

            // Sorteio com rejeição, até 4 voltas no número de salas.
            for (int attempt = 0; attempt < rooms * 4; attempt++)
            {
                int room = Random.Range(0, rooms);
                if (room == avoidRoom && rooms > 1)
                    continue;

                int[] members = graph.NodesOfRoom(room);
                int start = Random.Range(0, members.Length);
                for (int k = 0; k < members.Length; k++)
                {
                    int node = members[(start + k) % members.Length];
                    if (graph.CanSpawnAt(node))
                        return node;
                }
            }

            return -1;
        }

        private void ApplyCurriculum()
        {
            // Modo de jogo não lê o trainer: valem os defaults do Inspector (a última lição).
            EnvironmentParameters parameters = _gameMode ? null : Academy.Instance.EnvironmentParameters;
            float Get(string key, float fallback) =>
                parameters != null ? parameters.GetWithDefault(key, fallback) : fallback;

            // Sem Clamp01: acima de 1 é o "sem fim por cobertura" da patrulha (EndsOnCoverage).
            CoverageTarget = Mathf.Max(0.05f, Get(_coverageParameterName, _defaultCoverageTarget));
            RoomCompleteThreshold = Mathf.Clamp(
                Get(_roomCompleteParameterName, _defaultRoomCompleteThreshold), 0.05f, 1f);

            // Teto 0.9: com quase tudo pré-concluído o episódio vira "ache a única sala que falta".
            PrevisitedFraction = Mathf.Clamp(
                Get(_previsitedParameterName, _defaultPrevisitedFraction), 0f, 0.9f);

            ReleaseFraction = Mathf.Clamp01(Get(_releaseParameterName, _defaultReleaseFraction));

            PingInterval = Mathf.Max(0, Mathf.RoundToInt(
                Get(_pingIntervalParameterName, _defaultPingInterval)));

            HiderMode = (GraphHider.Mode)Mathf.Clamp(Mathf.RoundToInt(
                Get(_hiderModeParameterName, _defaultHiderMode)), 0, 3);

            HiderSpeed = Mathf.Max(0f, Get(_hiderSpeedParameterName, _defaultHiderSpeed));
            HiderNoise = Mathf.Clamp01(Get(_hiderNoiseParameterName, _defaultHiderNoise));

            PingRewardScale = Mathf.Max(0f, Get(_pingRewardScaleParameterName, _defaultPingRewardScale));
            DiscoveryRewardScale = Mathf.Max(0f,
                Get(_discoveryRewardScaleParameterName, _defaultDiscoveryRewardScale));

            SteerAssist = Mathf.Clamp01(Get(_steerAssistParameterName, _defaultSteerAssist));

            PingSingleRoomChance = Mathf.Clamp01(
                Get(_pingSingleRoomChanceParameterName, _defaultPingSingleRoomChance));
            VisionExplores = Get(_visionExploresParameterName, _defaultVisionExplores) >= 0.5f;
            HiderLoose = Get(_hiderLooseParameterName, _defaultHiderLoose) >= 0.5f;

            if (_gameMode)
            {
                // Sem fim por cobertura, e o seeker supõe um alvo que foge (procura ligada); os pings
                // vêm dos passos do jogador, não do sorteio.
                CoverageTarget = Mathf.Max(CoverageTarget, 1.1f);
                if (HiderMode == GraphHider.Mode.None)
                    HiderMode = GraphHider.Mode.Flee;
                PingInterval = 0;
            }
        }
    }
}
