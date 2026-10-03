using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Dono do ambiente de exploração: grafo da arena, spawns, feedback visual e leitura do
    /// currículo. Um por arena — cada cópia sorteia o próprio spawn e carrega o próprio estado.
    /// </summary>
    public class GraphArenaController : MonoBehaviour
    {
        [Header("-----Referências-----")]
        [SerializeField] private NavGraph _graph;
        [SerializeField] private Renderer _floorRenderer;
        [SerializeField] private Transform[] _spawnPoints;
        // Opcional: sem hider a arena é só exploração. Fallback por GetComponentInChildren.
        [SerializeField] private GraphHider _hider;

        [Header("-----Currículo-----")]
        // Fração das SALAS concluídas que encerra o episódio (GraphRoomMemory.CompletedFraction).
        // Vem do currículo; o valor aqui é o fallback quando você roda a cena sem trainer (Play no
        // editor, inferência). ACIMA DE 1 (ex.: 1.1) = sem fim por cobertura: o episódio vai até o
        // timeout. É o modo da patrulha (liberação ligada), onde nada fica concluído para sempre.
        [SerializeField] private string _coverageParameterName = "coverage_target";
        [SerializeField, Range(0.05f, 1.1f)] private float _defaultCoverageTarget = 0.35f;

        // Fração dos nós de uma sala que precisa ser pisada para ela contar como CONCLUÍDA (0.8:
        // "explorar 80% da sala"). Ver GraphRoomMemory. Sala de 1 nó conclui ao entrar.
        [SerializeField] private string _roomCompleteParameterName = "room_complete_threshold";
        [SerializeField, Range(0.05f, 1f)] private float _defaultRoomCompleteThreshold = 0.8f;

        // Fração das SALAS que já nasce concluída, sorteada a cada episódio (anti-decoreba: o
        // agente nasce no meio de uma exploração diferente, e o que serve em todas é a REGRA).
        // Sempre sobra uma sala por concluir.
        [SerializeField] private string _previsitedParameterName = "previsited_fraction";
        [SerializeField, Range(0f, 0.9f)] private float _defaultPrevisitedFraction = 0f;

        // LIBERAÇÃO (patrulha): com esta fração das portas usadas, a porta usada há mais tempo volta
        // a valer; com esta fração das salas concluídas, a sala concluída há mais tempo volta a ser
        // explorável. 0 = desligada (exploração pura). Use junto com coverage_target > 1.
        [SerializeField] private string _releaseParameterName = "release_fraction";
        [SerializeField, Range(0f, 1f)] private float _defaultReleaseFraction = 0f;

        // Intervalo entre PINGS, em steps de física (ver GraphPingSystem). 0 = sem ping. O
        // currículo liga o ping só depois de o agente saber explorar: antes disso ele seria
        // interrompido antes de aprender o que estava fazendo.
        [SerializeField] private string _pingIntervalParameterName = "ping_interval";
        [SerializeField, Min(0)] private int _defaultPingInterval = 0;

        // Modo do hider scriptado (ver GraphHider.Mode): 0 nenhum, 1 parado, 2 anda, 3 foge.
        // Com hider ligado, os pings vêm dos passos dele e o sorteio de ping_interval é ignorado.
        [SerializeField] private string _hiderModeParameterName = "hider_mode";
        [SerializeField, Range(0, 3)] private int _defaultHiderMode = 0;

        // Velocidade do hider (m/s) na lição. 0 = usa o padrão do GraphHider.
        [SerializeField] private string _hiderSpeedParameterName = "hider_speed";
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = 0f;

        // Chance de cada chegada do hider num nó de ping virar ping (GraphHider). 1 = toda chegada
        // (o de antes, quase um GPS); o config de procura desce até 0.25.
        [SerializeField] private string _hiderNoiseParameterName = "hider_noise";
        [SerializeField, Range(0f, 1f)] private float _defaultHiderNoise = 1f;

        // Escalas de recompensa por lição (GraphRewardSystem). 1 = o tuning do prefab.
        //   ping_reward_scale: 0 no config de procura — o ping vira só INFORMAÇÃO (a suspeita já
        //     paga ir aonde o barulho foi). Pago, o rastro do hider virava renda: o night_04
        //     aprendeu a colher ping em vez de pegar.
        //   discovery_reward_scale: 0.3 na procura — com a suspeita uniforme no começo, procurar
        //     já é explorar, e a descoberta cheia (~17 no mapa) competiria com a captura.
        [SerializeField] private string _pingRewardScaleParameterName = "ping_reward_scale";
        [SerializeField, Min(0f)] private float _defaultPingRewardScale = 1f;
        [SerializeField] private string _discoveryRewardScaleParameterName = "discovery_reward_scale";
        [SerializeField, Min(0f)] private float _defaultDiscoveryRewardScale = 1f;

        // PING POR SALA (S4): a cada episódio, um nó de cada sala vira nó de ping (onde o ping toca e
        // onde o hider faz barulho). Sala de UM nó só entra com esta chance — num armário de um
        // ladrilho o barulho seria sempre no mesmo ponto.
        [SerializeField] private string _pingSingleRoomChanceParameterName = "ping_single_room_chance";
        [SerializeField, Range(0f, 1f)] private float _defaultPingSingleRoomChance = 0.5f;

        // VER CONTA COMO EXPLORAR (S5): 1 = nós da sala atual que entram no cone de visão contam
        // como pisados (GraphRoomMemory). Olhar a sala da porta passa a valer.
        [SerializeField] private string _visionExploresParameterName = "vision_explores";
        [SerializeField, Range(0, 1)] private int _defaultVisionExplores = 0;

        // HIDER SOLTO (S6): 1 = o hider anda para pontos aleatórios dentro dos nós e se esconde
        // (GraphHider). 0 = de centro em centro, o hider "de trilho".
        [SerializeField] private string _hiderLooseParameterName = "hider_loose";
        [SerializeField, Range(0, 1)] private int _defaultHiderLoose = 0;

        // STEERING ASSISTIDO (SeekerMovementSystem.Steer): 0..1, fração da velocidade contra a
        // parede que é removida perto dela (1 = desliza, 0 = bate). A "rodinha de bicicleta":
        // alta no começo para a rede aprender a estratégia sem gastar milhões de steps aprendendo
        // a não bater, e baixa no fim para ela assumir o controle fino. O default é o valor da
        // última lição: é o que roda no jogo, sem trainer.
        [SerializeField] private string _steerAssistParameterName = "steer_assist";
        [SerializeField, Range(0f, 1f)] private float _defaultSteerAssist = 0.3f;

        [Header("-----Spawn-----")]
        // Nasce em cima de um nó ATIVO qualquer do grafo, sorteado por episódio, em vez de num
        // dos _spawnPoints. Dez pontos fixos são dez rotas para decorar; sessenta nós são
        // sessenta origens, e a origem deixa de identificar a rota. Os _spawnPoints continuam
        // sendo o fallback quando isto está desligado ou o grafo não tem nós.
        [SerializeField] private bool _spawnAtRandomNode = true;

        // Os nós ficam no chão; o corpo do agente nasce este tanto acima, para não nascer com
        // o collider dentro do piso. Da ordem da altura dos _spawnPoints originais (~0.13).
        [SerializeField] private float _nodeSpawnHeightOffset = 0.15f;

        // Spawn aleatório entre os pontos. Sortear é o que impede a política de decorar UMA
        // rota: com origem fixa, "explorar" e "executar aquela sequência de curvas" viram a
        // mesma coisa, e a segunda é muito mais fácil de aprender.
        [SerializeField] private bool _randomizeSpawn = true;

        [Header("-----Feedback-----")]
        // Só para debug visual. Mantenha 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;
        private bool _initialized;
        private int _lastSpawnIndex = -1;

        /// <summary>
        /// Resolvido sob demanda, e não no Awake: a ordem entre o Awake da arena e o Initialize
        /// do agente não é garantida entre objetos diferentes, e o agente lê isto no Initialize.
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
        /// Posiciona (ou desliga) o hider para o episódio. Chamar DEPOIS do spawn do seeker,
        /// porque o hider nasce a uma distância mínima dele.
        /// </summary>
        public void ResetHider(Vector3 seekerPosition)
        {
            if (_hider == null)
                _hider = GetComponentInChildren<GraphHider>(includeInactive: true);

            if (_hider != null)
                _hider.ResetEpisode(HiderMode, HiderSpeed, HiderNoise, seekerPosition, HiderLoose);
        }

        private void Awake() => EnsureInitialized();

        // Mesma razão do Graph: a cor original do chão precisa estar guardada antes do primeiro
        // ResetEpisode, que pode chegar antes do Awake daqui.
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
        /// Devolve um spawn. Retorna false quando nenhum ponto foi configurado — nesse caso o
        /// agente volta para a pose inicial que ele mesmo guardou.
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
                // Evita repetir o mesmo ponto duas vezes seguidas quando há alternativa: com
                // poucos spawns, a repetição por acaso concentra o treino numa região só.
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

        // Sorteia um nó de SPAWN válido (NavGraph.CanSpawnAt: ativo e com o corpo cabendo em
        // qualquer rotação). Auxiliares entram também: são justamente os pontos no meio dos
        // corredores, e nascer ali é o caso que os spawn points fixos nunca cobriam. Nó em
        // corredor estreito fica de fora — ele é âncora, não berço. A rotação é sorteada só por
        // variedade visual — as ações são no referencial do mundo.
        private bool TryGetNodeSpawn(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            NavGraph graph = Graph;
            if (graph == null || graph.NodeCount == 0)
                return false;

            graph.EnsureBaked();

            // SALA primeiro, por igual, e depois um nó dela: sorteando direto entre os nós, a sala
            // grande ganhava quase sempre (o corredor de 20 nós nascia 20x mais que um armário de 1)
            // e o começo do episódio ficava parecido. Com a sala sorteada, todo canto do mapa vira
            // ponto de partida com a mesma frequência — mais difícil de decorar.
            int node = RandomSpawnNodeByRoom(graph, -1);
            if (node < 0)
                return false;

            position = graph.NodePosition(node) + Vector3.up * _nodeSpawnHeightOffset;
            rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            return true;
        }

        /// <summary>
        /// Sorteia uma sala por igual (diferente de <paramref name="avoidRoom"/>, se houver outra) e um
        /// nó de spawn válido dela (NavGraph.CanSpawnAt: ativo e com o corpo cabendo). Porta nunca
        /// (é vão, não sala). -1 se nenhuma sala tem nó de spawn. Usado pelo spawn do agente e do hider.
        /// </summary>
        public static int RandomSpawnNodeByRoom(NavGraph graph, int avoidRoom)
        {
            int rooms = graph.RoomCount;
            if (rooms == 0)
                return -1;

            // Sorteio com rejeição: até 4 voltas no número de salas (sala sem nó com folga, ou a evitada).
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
            EnvironmentParameters parameters = Academy.Instance.EnvironmentParameters;

            // Sem Clamp01: acima de 1 é o "sem fim por cobertura" da patrulha (ver EndsOnCoverage).
            CoverageTarget = Mathf.Max(0.05f, parameters.GetWithDefault(_coverageParameterName, _defaultCoverageTarget));
            RoomCompleteThreshold = Mathf.Clamp(
                parameters.GetWithDefault(_roomCompleteParameterName, _defaultRoomCompleteThreshold), 0.05f, 1f);

            // Teto em 0.9 e não 1.0: a memória sempre deixa ao menos uma sala por concluir, mas
            // com quase tudo pré-concluído o episódio vira "ache a única sala que falta" — que é
            // outra tarefa, não exploração.
            PrevisitedFraction = Mathf.Clamp(
                parameters.GetWithDefault(_previsitedParameterName, _defaultPrevisitedFraction), 0f, 0.9f);

            ReleaseFraction = Mathf.Clamp01(parameters.GetWithDefault(_releaseParameterName, _defaultReleaseFraction));

            PingInterval = Mathf.Max(0, Mathf.RoundToInt(
                parameters.GetWithDefault(_pingIntervalParameterName, _defaultPingInterval)));

            HiderMode = (GraphHider.Mode)Mathf.Clamp(Mathf.RoundToInt(
                parameters.GetWithDefault(_hiderModeParameterName, _defaultHiderMode)), 0, 3);

            HiderSpeed = Mathf.Max(0f, parameters.GetWithDefault(_hiderSpeedParameterName, _defaultHiderSpeed));
            HiderNoise = Mathf.Clamp01(parameters.GetWithDefault(_hiderNoiseParameterName, _defaultHiderNoise));

            PingRewardScale = Mathf.Max(0f, parameters.GetWithDefault(_pingRewardScaleParameterName, _defaultPingRewardScale));
            DiscoveryRewardScale = Mathf.Max(0f,
                parameters.GetWithDefault(_discoveryRewardScaleParameterName, _defaultDiscoveryRewardScale));

            SteerAssist = Mathf.Clamp01(parameters.GetWithDefault(_steerAssistParameterName, _defaultSteerAssist));

            PingSingleRoomChance = Mathf.Clamp01(
                parameters.GetWithDefault(_pingSingleRoomChanceParameterName, _defaultPingSingleRoomChance));
            VisionExplores = parameters.GetWithDefault(_visionExploresParameterName, _defaultVisionExplores) >= 0.5f;
            HiderLoose = parameters.GetWithDefault(_hiderLooseParameterName, _defaultHiderLoose) >= 0.5f;
        }
    }
}
