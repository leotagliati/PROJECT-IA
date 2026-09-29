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
        // Fração do grafo que conta como episódio resolvido. Vem do currículo; o valor aqui é
        // o fallback quando você roda a cena sem trainer (Play no editor, inferência).
        // ACIMA DE 1 (ex.: 1.1) = sem fim por cobertura: o episódio vai até o timeout. É o modo
        // das lições de PATRULHA, onde o objetivo é manter o mapa vigiado, não terminá-lo.
        [SerializeField] private string _coverageParameterName = "coverage_target";
        [SerializeField, Range(0.05f, 1.1f)] private float _defaultCoverageTarget = 0.35f;

        // Peso da dica de fronteira. 1 = o agente vê para onde ir; 0 = ele tem que descobrir
        // sozinho a partir dos vizinhos e do que já visitou. Escala tanto a OBSERVAÇÃO quanto o
        // shaping de recompensa, de propósito: as duas são a mesma muleta, e desligar só uma
        // deixa metade da dependência de pé.
        [SerializeField] private string _frontierParameterName = "frontier_hint";
        [SerializeField, Range(0f, 1f)] private float _defaultFrontierHint = 1f;

        // DURAÇÃO da dica dentro de cada episódio, em steps de FÍSICA (8000 = episódio inteiro
        // com _maxEpisodeSteps = 8000; 1500 = os primeiros 30 s). 0 = sem limite, a dica dura o
        // episódio todo. Depois do limite a escala vai a ZERO — observação, shaping e gizmo.
        //
        // É o segundo eixo da muleta, independente da força acima: a força diz "quanto confiar
        // na seta", a duração diz "por quanto tempo ela existe". Dar a dica só no início do
        // episódio ensina o agente a se orientar com ela e a TERMINAR sem ela — que é a
        // situação do jogo final, onde não há seta nenhuma. Cortar a força direto para 0.0
        // numa lição (run 05) derrubou a recompensa de +15 para -3; cortar a duração deixa a
        // política ver os dois regimes no MESMO episódio, e a transição fica dentro do que ela
        // já sabe fazer.
        [SerializeField] private string _frontierStepsParameterName = "frontier_hint_steps";
        [SerializeField, Min(0)] private int _defaultFrontierHintSteps = 0;

        // Fração dos primários que já NASCE marcada como visitada, sorteada a cada episódio
        // (lida pela GraphExplorationMemory). É a variação de estado inicial: com 0, todo
        // episódio começa com o mapa inteiro por fazer e a sequência ótima a partir de cada
        // spawn é sempre a mesma — a política decora. Com 0.5, o agente nasce no meio de uma
        // exploração diferente a cada vez e a única coisa que serve em todas é a REGRA
        // ("vá para a saída não visitada"), que é o que queremos que ele aprenda.
        [SerializeField] private string _previsitedParameterName = "previsited_fraction";
        [SerializeField, Range(0f, 0.9f)] private float _defaultPrevisitedFraction = 0f;

        // Variação do PESO dos nós por episódio (lida pela GraphExplorationMemory): cada nó
        // vale autorado x U[1 - j, 1 + j]. Com 0, o mapa vale sempre o mesmo e a política pode
        // decorar "aquela sala paga mais"; com 0.5, o peso de um nó vai de metade ao dobro entre
        // episódios, e a única forma de ganhar é ler o peso do vizinho na observação.
        [SerializeField] private string _weightJitterParameterName = "weight_jitter";
        [SerializeField, Range(0f, 1f)] private float _defaultWeightJitter = 0f;

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

        // PATRULHA: segundos para um nó visitado recuperar o valor inteiro (ver
        // GraphExplorationMemory). 0 = desligado: nó visitado nunca mais paga no episódio (o
        // comportamento da fase de exploração).
        [SerializeField] private string _valueRecoveryParameterName = "value_recovery_seconds";
        [SerializeField, Min(0f)] private float _defaultValueRecoverySeconds = 0f;

        // TÉDIO DE SALA (NavNode._areaId): 1 liga, 0 desliga. Liga junto com a patrulha — na
        // exploração pura não faz sentido cansar de uma sala que ainda tem o que ver.
        [SerializeField] private string _areaBoredomParameterName = "area_boredom";
        [SerializeField, Range(0, 1)] private int _defaultAreaBoredom = 0;

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

        public float FrontierHintScale { get; private set; }

        /// <summary>Steps de física com a dica ligada por episódio; 0 = o episódio inteiro.</summary>
        public int FrontierHintSteps { get; private set; }

        /// <summary>Fração dos primários que nasce visitada neste episódio (0..0.9).</summary>
        public float PrevisitedFraction { get; private set; }

        /// <summary>Amplitude do sorteio de peso por nó neste episódio (0..1).</summary>
        public float WeightJitter { get; private set; }

        /// <summary>Cobertura-alvo acima de 1: o episódio não termina por cobertura (patrulha).</summary>
        public bool EndsOnCoverage => CoverageTarget <= 1f;

        /// <summary>Steps de física para um nó recuperar o valor; 0 = patrulha desligada.</summary>
        public int ValueRecoverySteps { get; private set; }

        public bool AreaBoredom { get; private set; }

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
                _hider.ResetEpisode(HiderMode, HiderSpeed, HiderNoise, seekerPosition);
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

            // Até NodeCount tentativas: com nós desligados por uma lição, sortear e rejeitar é
            // mais simples que manter uma lista de ativos em dia.
            for (int attempt = 0; attempt < graph.NodeCount; attempt++)
            {
                int index = Random.Range(0, graph.NodeCount);
                if (!graph.CanSpawnAt(index))
                    continue;

                position = graph.NodePosition(index) + Vector3.up * _nodeSpawnHeightOffset;
                rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                return true;
            }

            return false;
        }

        private void ApplyCurriculum()
        {
            EnvironmentParameters parameters = Academy.Instance.EnvironmentParameters;

            // Sem Clamp01: acima de 1 é o "sem fim por cobertura" da patrulha (ver EndsOnCoverage).
            CoverageTarget = Mathf.Max(0.05f, parameters.GetWithDefault(_coverageParameterName, _defaultCoverageTarget));
            FrontierHintScale = Mathf.Clamp01(parameters.GetWithDefault(_frontierParameterName, _defaultFrontierHint));

            // O currículo entrega float; a contagem é inteira.
            FrontierHintSteps = Mathf.Max(0, Mathf.RoundToInt(
                parameters.GetWithDefault(_frontierStepsParameterName, _defaultFrontierHintSteps)));

            // Teto em 0.9 e não 1.0: a memória sempre deixa ao menos um nó por descobrir, mas
            // com quase tudo pré-visitado o episódio vira "ache o único nó que falta" — que é
            // outra tarefa, não exploração.
            PrevisitedFraction = Mathf.Clamp(
                parameters.GetWithDefault(_previsitedParameterName, _defaultPrevisitedFraction), 0f, 0.9f);

            WeightJitter = Mathf.Clamp01(
                parameters.GetWithDefault(_weightJitterParameterName, _defaultWeightJitter));

            PingInterval = Mathf.Max(0, Mathf.RoundToInt(
                parameters.GetWithDefault(_pingIntervalParameterName, _defaultPingInterval)));

            HiderMode = (GraphHider.Mode)Mathf.Clamp(Mathf.RoundToInt(
                parameters.GetWithDefault(_hiderModeParameterName, _defaultHiderMode)), 0, 3);

            HiderSpeed = Mathf.Max(0f, parameters.GetWithDefault(_hiderSpeedParameterName, _defaultHiderSpeed));
            HiderNoise = Mathf.Clamp01(parameters.GetWithDefault(_hiderNoiseParameterName, _defaultHiderNoise));

            PingRewardScale = Mathf.Max(0f, parameters.GetWithDefault(_pingRewardScaleParameterName, _defaultPingRewardScale));
            DiscoveryRewardScale = Mathf.Max(0f,
                parameters.GetWithDefault(_discoveryRewardScaleParameterName, _defaultDiscoveryRewardScale));

            // Segundos no YAML (é como se pensa), steps de física na memória (é como ela conta).
            float recoverySeconds = Mathf.Max(0f, parameters.GetWithDefault(_valueRecoveryParameterName, _defaultValueRecoverySeconds));
            ValueRecoverySteps = Mathf.RoundToInt(recoverySeconds / Time.fixedDeltaTime);

            AreaBoredom = parameters.GetWithDefault(_areaBoredomParameterName, _defaultAreaBoredom) >= 0.5f;

            SteerAssist = Mathf.Clamp01(parameters.GetWithDefault(_steerAssistParameterName, _defaultSteerAssist));
        }
    }
}
