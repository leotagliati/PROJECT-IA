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

        [Header("-----Currículo-----")]
        // Fração do grafo que conta como episódio resolvido. Vem do currículo; o valor aqui é
        // o fallback quando você roda a cena sem trainer (Play no editor, inferência).
        [SerializeField] private string _coverageParameterName = "coverage_target";
        [SerializeField, Range(0.05f, 1f)] private float _defaultCoverageTarget = 0.35f;

        // Fração dos primários que já NASCE marcada como visitada, sorteada a cada episódio
        // (lida pela GraphExplorationMemory). É a variação de estado inicial: com 0, todo
        // episódio começa com o mapa inteiro por fazer e a sequência ótima a partir de cada
        // spawn é sempre a mesma — a política decora. Com 0.5, o agente nasce no meio de uma
        // exploração diferente a cada vez e a única coisa que serve em todas é a REGRA
        // ("vá para a saída não visitada"), que é o que queremos que ele aprenda.
        [SerializeField] private string _previsitedParameterName = "previsited_fraction";
        [SerializeField, Range(0f, 0.9f)] private float _defaultPrevisitedFraction = 0f;

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

        /// <summary>Fração dos primários que nasce visitada neste episódio (0..0.9).</summary>
        public float PrevisitedFraction { get; private set; }

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

        // Sorteia um nó ativo. Auxiliares entram também: são justamente os pontos no meio dos
        // corredores, e nascer ali é o caso que os spawn points fixos nunca cobriam. A rotação
        // é sorteada só por variedade visual — as ações são no referencial do mundo.
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
                if (!graph.IsNodeEnabled(index))
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

            CoverageTarget = Mathf.Clamp01(parameters.GetWithDefault(_coverageParameterName, _defaultCoverageTarget));
            // Teto em 0.9 e não 1.0: a memória sempre deixa ao menos um nó por descobrir, mas
            // com quase tudo pré-visitado o episódio vira "ache o único nó que falta" — que é
            // outra tarefa, não exploração.
            PrevisitedFraction = Mathf.Clamp(
                parameters.GetWithDefault(_previsitedParameterName, _defaultPrevisitedFraction), 0f, 0.9f);
        }
    }
}
