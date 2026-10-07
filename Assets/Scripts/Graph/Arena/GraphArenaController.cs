using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O que vale NESTE episódio, lido do currículo (ou dos defaults da arena) no início dele. Uma fonte de
    /// verdade: o Manager repassa isto a quem precisa (memória, ping, procura, hider, contexto da recompensa)
    /// em vez de cada um perguntar à arena. Struct de dados preenchida pela arena; só ela escreve.
    /// </summary>
    public struct GraphEpisodeSettings
    {
        /// <summary>Fração das SALAS concluídas que encerra o episódio; acima de 1 = sem fim por cobertura (patrulha).</summary>
        public float CoverageTarget;

        /// <summary>Fração dos nós de uma sala que a conclui (0..1).</summary>
        public float RoomCompleteThreshold;

        /// <summary>Fração das salas que nasce concluída (0..0.9).</summary>
        public float PrevisitedFraction;

        /// <summary>Fração de portas/salas usadas que dispara a liberação; 0 = desligada.</summary>
        public float ReleaseFraction;

        /// <summary>Steps de física entre pings aleatórios; 0 = sem ping (com hider, quem toca é ele).</summary>
        public int PingInterval;

        /// <summary>Chance (0..1) de uma sala de um nó só ter nó de ping no episódio.</summary>
        public float PingSingleRoomChance;

        /// <summary>Nós dentro do cone de visão contam como pisados.</summary>
        public bool VisionExplores;

        public GraphHider.Mode HiderMode;

        /// <summary>m/s do hider CORRENDO; 0 = padrão do GraphHider.</summary>
        public float HiderSpeed;

        /// <summary>Segundos de corrida do hider; 0 = padrão do GraphHider.</summary>
        public float HiderStamina;

        /// <summary>Chance (0..1) de cada chegada do hider num nó de ping virar ping.</summary>
        public float HiderNoise;

        /// <summary>Hider solto (pontos dentro dos nós, esconderijos).</summary>
        public bool HiderLoose;

        /// <summary>Escala dos termos de ping (0 = o ping só informa).</summary>
        public float PingRewardScale;

        /// <summary>Escala da recompensa de exploração.</summary>
        public float DiscoveryRewardScale;

        public bool EndsOnCoverage => CoverageTarget <= 1f;

        public bool HasHider => HiderMode != GraphHider.Mode.None;

        /// <summary>
        /// Velocidade que a PROCURA supõe para o hider: a da lição (a de corrida, o limite de cima); parado = 0;
        /// -1 = o padrão do GraphSuspicionMap (lição sem hider_speed).
        /// </summary>
        public float AssumedHiderSpeed =>
            HiderMode == GraphHider.Mode.Static ? 0f
            : HiderSpeed > 0f ? HiderSpeed
            : -1f;
    }

    /// <summary>
    /// Dono do ambiente de UMA arena: grafo, spawns, feedback do chão, currículo e o ALVO do seeker.
    /// No início de cada episódio lê o currículo para <see cref="Settings"/>; o Manager repassa. Parâmetro
    /// novo de currículo = campo no GraphEpisodeSettings + default aqui + uma linha no ApplyCurriculum.
    /// Nunca guarde estado estático (há várias arenas).
    /// MODO DE JOGO (_gameMode): sem currículo (valem os defaults abaixo), hider desligado e o alvo passa a
    /// ser o jogador, achado pela tag; o GraphExplorerManager cuida da inferência e do fim.
    /// </summary>
    public class GraphArenaController : MonoBehaviour
    {
        [Header("-----Referências-----")]
        // Os dois têm fallback por GetComponentInChildren (o grafo e o hider são filhos da arena).
        [SerializeField] private NavGraph _graph;
        [SerializeField] private GraphHider _hider;
        [SerializeField] private Renderer _floorRenderer;
        [SerializeField] private Transform[] _spawnPoints;

        [Header("-----Modo de jogo-----")]
        // Ligado: jogo de verdade (inferência, sem currículo, sem hider, caça o jogador, sem timeout).
        [SerializeField] private bool _gameMode = false;

        // Tag do jogador. "Goal" é a mesma convenção do Seeker antigo (o PlayerDummy já vem com ela).
        [SerializeField] private string _playerTag = "Goal";

        // Defaults do currículo: valem só sem trainer (Play no editor, modo de jogo). O padrão do código é a
        // ÚLTIMA lição da v5 (HiderJogador, config/graph_v5.0_zero.yaml): o .onnx treinado roda no ambiente em
        // que terminou. Menu de contexto "Usar padrões do treino (v5)" reaplica sem dar Reset na arena (que
        // apagaria spawns e chão). O nome do parâmetro no YAML está ao lado de cada um em ApplyCurriculum.
        [Header("-----Currículo (defaults sem trainer)-----")]
        [SerializeField, Range(0.05f, 1.1f)] private float _defaultCoverageTarget = V5.CoverageTarget;
        // 1 = a sala só conclui com TODOS os nós vistos ou pisados (v5; era 0.8).
        [SerializeField, Range(0.05f, 1f)] private float _defaultRoomCompleteThreshold = V5.RoomCompleteThreshold;
        [SerializeField, Range(0f, 0.9f)] private float _defaultPrevisitedFraction = V5.PrevisitedFraction;
        [SerializeField, Range(0f, 1f)] private float _defaultReleaseFraction = V5.ReleaseFraction;
        [SerializeField, Min(0)] private int _defaultPingInterval = V5.PingInterval;
        [SerializeField, Range(0f, 1f)] private float _defaultPingSingleRoomChance = V5.PingSingleRoomChance;
        [SerializeField, Range(0, 1)] private int _defaultVisionExplores = V5.VisionExplores;
        [SerializeField, Range(0, 3)] private int _defaultHiderMode = V5.HiderMode;
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = V5.HiderSpeed;
        [SerializeField, Min(0f)] private float _defaultHiderStamina = V5.HiderStamina;
        [SerializeField, Range(0f, 1f)] private float _defaultHiderNoise = V5.HiderNoise;
        [SerializeField, Range(0, 1)] private int _defaultHiderLoose = V5.HiderLoose;
        [SerializeField, Min(0f)] private float _defaultPingRewardScale = V5.PingRewardScale;
        [SerializeField, Min(0f)] private float _defaultDiscoveryRewardScale = V5.DiscoveryRewardScale;

        // A última lição da v5 (HiderJogador). Mudou o currículo final: mude aqui junto.
        private static class V5
        {
            public const float CoverageTarget = 1.1f;
            public const float RoomCompleteThreshold = 1f;
            public const float PrevisitedFraction = 0f;
            public const float ReleaseFraction = 0f;
            public const int PingInterval = 0;
            public const float PingSingleRoomChance = 0.5f;
            public const int VisionExplores = 1;
            public const int HiderMode = 3;
            public const float HiderSpeed = 10.2f;
            public const float HiderStamina = 10f;
            public const float HiderNoise = 0.3f;
            public const int HiderLoose = 1;
            public const float PingRewardScale = 0f;
            public const float DiscoveryRewardScale = 0.5f;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Põe na layer Wall todo objeto com collider da arena: paredes, móveis e as peças Door_Hole (custo cheio).
        /// Até 06/10 as Door_Hole iam para a layer Door, com contato a 25% do da parede; o monstro aprendeu a raspar o
        /// batente e ficava preso na porta de vez em quando, então a porta voltou a ser parede como outra qualquer.
        /// Ficam de fora: chão e teto (pelo nome, ou placa fina e larga: na layer de parede o teste de corpo do
        /// NavGraph encostaria neles e bloquearia tudo), o monstro, o hider, os nós e triggers. Com Undo; rode no
        /// Prefab Mode da arena e depois "Validar ligações" no NavGraph (móvel que virou parede pode cortar ligação).
        /// </summary>
        [ContextMenu("Ajustar layers das paredes (Wall)")]
        private void AssignWallLayers()
        {
            int wall = LayerMask.NameToLayer("Wall");
            if (wall < 0)
            {
                Debug.LogError($"{name}: falta a layer \"Wall\" no Tags and Layers.", this);
                return;
            }

            int toWall = 0, unchanged = 0;
            var skipped = new System.Collections.Generic.SortedSet<string>();
            var done = new System.Collections.Generic.HashSet<GameObject>();
            foreach (Collider collider in GetComponentsInChildren<Collider>(includeInactive: true))
            {
                GameObject go = collider.gameObject;
                if (!done.Add(go) || collider.isTrigger)
                    continue;

                if (go.GetComponentInParent<GraphExplorerManager>(true) != null || go.GetComponentInParent<GraphHider>(true) != null
                    || go.GetComponentInParent<NavNode>(true) != null)
                    continue;

                if (IsFloorOrCeiling(collider))
                {
                    skipped.Add(go.name);
                    continue;
                }

                if (go.layer == wall)
                {
                    unchanged++;
                    continue;
                }

                UnityEditor.Undo.RecordObject(go, "Ajustar layers das paredes");
                go.layer = wall;
                if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(go);
                toWall++;
            }

            Debug.Log($"{name}: layers ajustadas — {toWall} para Wall, {unchanged} já estavam certos. " +
                      $"Fora (chão/teto): {string.Join(", ", skipped)}", this);
        }

        // Chão e teto: pelo nome, ou placa fina (< 0.3 m de altura) e larga (> 2 m) — nunca podem ser parede.
        private static bool IsFloorOrCeiling(Collider collider)
        {
            string lower = collider.gameObject.name.ToLowerInvariant();
            if (lower.Contains("floor") || lower.Contains("ceiling") || lower.Contains("chao") || lower.Contains("chão") || lower.Contains("teto"))
                return true;

            Vector3 size = collider.bounds.size;
            return size.y < 0.3f && Mathf.Max(size.x, size.z) > 2f;
        }

        [ContextMenu("Usar padrões do treino (v5)")]
        private void ApplyTrainingDefaults()
        {
            UnityEditor.Undo.RecordObject(this, "Padrões do treino (v5)");
            _defaultCoverageTarget = V5.CoverageTarget;
            _defaultRoomCompleteThreshold = V5.RoomCompleteThreshold;
            _defaultPrevisitedFraction = V5.PrevisitedFraction;
            _defaultReleaseFraction = V5.ReleaseFraction;
            _defaultPingInterval = V5.PingInterval;
            _defaultPingSingleRoomChance = V5.PingSingleRoomChance;
            _defaultVisionExplores = V5.VisionExplores;
            _defaultHiderMode = V5.HiderMode;
            _defaultHiderSpeed = V5.HiderSpeed;
            _defaultHiderStamina = V5.HiderStamina;
            _defaultHiderNoise = V5.HiderNoise;
            _defaultHiderLoose = V5.HiderLoose;
            _defaultPingRewardScale = V5.PingRewardScale;
            _defaultDiscoveryRewardScale = V5.DiscoveryRewardScale;
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }
#endif

        [Header("-----Spawn-----")]
        // Nasce em um nó ATIVO sorteado por episódio, em vez dos _spawnPoints (que ficam de
        // fallback): muitas origens impedem a política de decorar uma rota.
        [SerializeField] private bool _spawnAtRandomNode = true;

        // Altura acima do nó (que fica no chão) para o collider não nascer dentro do piso.
        [SerializeField] private float _nodeSpawnHeightOffset = 0.15f;

        // Sorteia entre os _spawnPoints; origem fixa deixa a política decorar a sequência de curvas.
        [SerializeField] private bool _randomizeSpawn = true;

        [Header("-----Salas esquecidas-----")]
        // Taxa de conclusão de cada sala nos episódios desta arena (média móvel, peso deste episódio). 0.1 = ~os
        // últimos 10 episódios. Alimenta o spawn e o valor de sala rara (GraphRoomMemory._rarityValueGain).
        [SerializeField, Range(0.01f, 1f)] private float _roomRateSmoothing = 0.1f;

        // Spawn por sala com peso piso + (1 - taxa de conclusão): nasce mais onde quase nunca conclui. O run
        // v5.1_zero_01 fazia a parte de cima do mapa (salas interligadas) e nunca descia: sem nascer lá, nunca
        // treinava lá. O piso mantém as salas comuns no sorteio. Muda só ONDE ele treina, nada dentro do episódio.
        [SerializeField] private bool _spawnFavorsRareRooms = true;
        [SerializeField, Min(0f)] private float _rareSpawnFloor = 0.2f;

        [Header("-----Feedback-----")]
        // Só debug visual. Mantenha 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;
        private bool _initialized;
        private int _lastSpawnIndex = -1;
        private GraphPlayerTarget _playerTarget;
        private float[] _roomCompletionRate;   // por sala, média móvel entre episódios; começa em 1 (ver EnsureRoomStats)
        private float[] _spawnWeights;

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

        public bool GameMode => _gameMode;

        /// <summary>
        /// Taxa de conclusão de cada sala nos últimos episódios desta arena (0..1, média móvel). Null até o grafo
        /// existir. Só leitura: quem atualiza é <see cref="RecordRoomOutcome"/>.
        /// </summary>
        public float[] RoomCompletionRate
        {
            get
            {
                EnsureRoomStats();
                return _roomCompletionRate;
            }
        }

        /// <summary>
        /// Fim de episódio: soma na média móvel quais salas foram concluídas. Salas pré-concluídas ficam de fora
        /// (o mérito não foi do agente e a taxa delas não diz nada).
        /// </summary>
        public void RecordRoomOutcome(GraphRoomMemory rooms)
        {
            EnsureRoomStats();
            if (_roomCompletionRate == null)
                return;

            for (int r = 0; r < _roomCompletionRate.Length; r++)
            {
                if (rooms.IsRoomPrevisited(r))
                    continue;

                float outcome = rooms.IsRoomCompleted(r) ? 1f : 0f;
                _roomCompletionRate[r] = Mathf.Lerp(_roomCompletionRate[r], outcome, _roomRateSmoothing);
            }
        }

        private void EnsureRoomStats()
        {
            NavGraph graph = Graph;
            if (graph == null)
                return;

            graph.EnsureBaked();
            if (_roomCompletionRate != null && _roomCompletionRate.Length == graph.RoomCount)
                return;

            _roomCompletionRate = new float[graph.RoomCount];
            _spawnWeights = new float[graph.RoomCount];
            // Começa em 1 ("sempre concluída" = x1, peso mínimo no spawn), não em 0.5: a taxa vive só na memória do
            // build, e com 0.5 todo --resume fazia TODA sala valer x1.5 nos ~10 primeiros episódios. A reward
            // inflada passou o Completo (critério 27) em 17.76M com a S24 ainda em 0. Assim só as salas que ele
            // realmente pula sobem para x2, aos poucos.
            for (int r = 0; r < _roomCompletionRate.Length; r++)
                _roomCompletionRate[r] = 1f;
        }

        public float EpisodeEndDelay => _episodeEndDelay;

        /// <summary>O que vale neste episódio; atualizado a cada <see cref="ResetEpisode"/>.</summary>
        public GraphEpisodeSettings Settings { get; private set; }

        /// <summary>Pontos de spawn fixos (fallback). Lido pelo NavGraphPlacer para conferir se caem em chão coberto.</summary>
        internal Transform[] SpawnPoints => _spawnPoints;

        private GraphHider Hider
        {
            get
            {
                if (_hider == null)
                    _hider = GetComponentInChildren<GraphHider>(includeInactive: true);

                return _hider;
            }
        }

        /// <summary>
        /// O alvo do seeker, único para visão, ping e procura: o hider no treino, o jogador no modo de jogo.
        /// Null se não há (arena só de exploração, ou jogador sem a tag).
        /// </summary>
        public IGraphTarget Target => _gameMode ? PlayerTarget : Hider;

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

        /// <summary>Lê o currículo para <see cref="Settings"/>, sorteia os nós de ping e limpa o chão.</summary>
        public void ResetEpisode()
        {
            EnsureInitialized();
            Settings = ApplyCurriculum();

            // Antes do hider e do ping do agente: os dois perguntam ao grafo onde o barulho pode ser.
            if (Graph != null)
                Graph.DrawEpisodePingNodes(Settings.PingSingleRoomChance);

            if (_floorRenderer != null)
                _floorRenderer.material.color = _initialFloorColor;
        }

        /// <summary>
        /// Posiciona (ou desliga) o hider. Chamar DEPOIS do spawn do seeker: o hider nasce longe dele e foge dele.
        /// </summary>
        public void ResetHider(Transform seeker)
        {
            if (Hider == null)
                return;

            // Modo de jogo: o hider sai de cena; quem foge é o jogador. (Settings.HiderMode é Foge aqui só para a
            // procura supor um alvo que foge.)
            if (_gameMode)
                Hider.Deactivate();
            else
                Hider.ResetEpisode(Settings, seeker);
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

        // Nó de spawn válido (NavGraph.RandomSpawnNode); rotação só visual (as ações são no referencial do mundo).
        private bool TryGetNodeSpawn(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            NavGraph graph = Graph;
            if (graph == null || graph.NodeCount == 0)
                return false;

            int node = graph.RandomSpawnNode(RareRoomSpawnWeights());
            if (node < 0)
                return false;

            position = graph.NodePosition(node) + Vector3.up * _nodeSpawnHeightOffset;
            rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            return true;
        }

        // Null = sorteio por igual (desligado ou modo de jogo, onde o spawn não deve depender do histórico).
        private float[] RareRoomSpawnWeights()
        {
            if (!_spawnFavorsRareRooms || _gameMode)
                return null;

            EnsureRoomStats();
            if (_roomCompletionRate == null)
                return null;

            for (int r = 0; r < _spawnWeights.Length; r++)
                _spawnWeights[r] = _rareSpawnFloor + (1f - _roomCompletionRate[r]);

            return _spawnWeights;
        }

        private GraphEpisodeSettings ApplyCurriculum()
        {
            // Modo de jogo não lê o trainer: valem os defaults do Inspector (a última lição).
            EnvironmentParameters parameters = _gameMode ? null : Academy.Instance.EnvironmentParameters;
            float Get(string key, float fallback) =>
                parameters != null ? parameters.GetWithDefault(key, fallback) : fallback;

            var settings = new GraphEpisodeSettings
            {
                // Sem Clamp01: acima de 1 é o "sem fim por cobertura" da patrulha (EndsOnCoverage).
                CoverageTarget = Mathf.Max(0.05f, Get("coverage_target", _defaultCoverageTarget)),
                RoomCompleteThreshold = Mathf.Clamp(Get("room_complete_threshold", _defaultRoomCompleteThreshold), 0.05f, 1f),

                // Teto 0.9: com quase tudo pré-concluído o episódio vira "ache a única sala que falta".
                PrevisitedFraction = Mathf.Clamp(Get("previsited_fraction", _defaultPrevisitedFraction), 0f, 0.9f),
                ReleaseFraction = Mathf.Clamp01(Get("release_fraction", _defaultReleaseFraction)),
                PingInterval = Mathf.Max(0, Mathf.RoundToInt(Get("ping_interval", _defaultPingInterval))),
                PingSingleRoomChance = Mathf.Clamp01(Get("ping_single_room_chance", _defaultPingSingleRoomChance)),
                VisionExplores = Get("vision_explores", _defaultVisionExplores) >= 0.5f,
                HiderMode = (GraphHider.Mode)Mathf.Clamp(Mathf.RoundToInt(Get("hider_mode", _defaultHiderMode)), 0, 3),
                HiderSpeed = Mathf.Max(0f, Get("hider_speed", _defaultHiderSpeed)),
                HiderStamina = Mathf.Max(0f, Get("hider_stamina", _defaultHiderStamina)),
                HiderNoise = Mathf.Clamp01(Get("hider_noise", _defaultHiderNoise)),
                HiderLoose = Get("hider_loose", _defaultHiderLoose) >= 0.5f,
                PingRewardScale = Mathf.Max(0f, Get("ping_reward_scale", _defaultPingRewardScale)),
                DiscoveryRewardScale = Mathf.Max(0f, Get("discovery_reward_scale", _defaultDiscoveryRewardScale)),
            };

            if (_gameMode)
            {
                // Sem fim por cobertura, e o seeker supõe um alvo que foge (procura ligada); os pings
                // vêm dos passos do jogador, não do sorteio.
                settings.CoverageTarget = Mathf.Max(settings.CoverageTarget, 1.1f);
                if (settings.HiderMode == GraphHider.Mode.None)
                    settings.HiderMode = GraphHider.Mode.Flee;
                settings.PingInterval = 0;
            }

            return settings;
        }
    }
}
