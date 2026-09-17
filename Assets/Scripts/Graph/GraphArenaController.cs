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

        [Header("-----Spawn-----")]
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

        private void ApplyCurriculum()
        {
            EnvironmentParameters parameters = Academy.Instance.EnvironmentParameters;

            CoverageTarget = Mathf.Clamp01(parameters.GetWithDefault(_coverageParameterName, _defaultCoverageTarget));
            FrontierHintScale = Mathf.Clamp01(parameters.GetWithDefault(_frontierParameterName, _defaultFrontierHint));

            // O currículo entrega float; a contagem é inteira.
            FrontierHintSteps = Mathf.Max(0, Mathf.RoundToInt(
                parameters.GetWithDefault(_frontierStepsParameterName, _defaultFrontierHintSteps)));
        }
    }
}
