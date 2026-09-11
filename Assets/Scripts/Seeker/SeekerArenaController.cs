using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Dono do ambiente, não do agente: spawn do hider e do seeker, feedback visual do resultado
    /// e leitura do currículo. Existe um por arena — quando as arenas forem duplicadas para treinar
    /// em paralelo, cada cópia carrega o próprio estado.
    /// </summary>
    public class SeekerArenaController : MonoBehaviour
    {
        [Header("-----Referências-----")]
        [SerializeField] private HiderAgent _hider;
        [SerializeField] private Renderer _floorRenderer;
        [SerializeField] private Transform[] _seekerSpawnPoints;

        [Header("-----Currículo-----")]
        [SerializeField] private GameObject _maze;
        [SerializeField] private string _mazeParameterName = "maze_enabled";

        [Header("-----Regime de recompensa na lição do labirinto-----")]
        // Os dois termos de shaping que funcionam em sala aberta atrapalham no labirinto.
        // Em vez de um parâmetro de currículo separado para cada um, a arena deriva o regime
        // do próprio maze_enabled — uma fonte de verdade só, e os pesos ficam tunáveis aqui.
        [SerializeField, Range(0f, 1f)] private float _mazeApproachScale = 0.25f;
        [SerializeField, Range(0f, 1f)] private float _mazeWallProximityScale = 0f;

        [Header("-----Feedback-----")]
        // Só para debug visual: durante o delay o agente segue recebendo decisões que não viram
        // experiência útil. Mantenha em 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;

        public float EpisodeEndDelay => _episodeEndDelay;

        // Atualizados a cada ResetEpisode e lidos pelo SeekerManager ao montar o step context.
        public float ApproachRewardScale { get; private set; } = 1f;

        public float WallProximityScale { get; private set; } = 1f;

        private void Awake()
        {
            if (_floorRenderer != null)
                _initialFloorColor = _floorRenderer.material.color;
        }

        public void ResetEpisode()
        {
            ApplyCurriculum();

            if (_floorRenderer != null)
                _floorRenderer.material.color = _initialFloorColor;

            if (_hider != null)
                _hider.Spawn();
        }

        public void ShowOutcome(bool won)
        {
            if (_floorRenderer != null)
                _floorRenderer.material.color = won ? Color.green : Color.red;
        }

        /// <summary>
        /// Devolve um spawn aleatório para o seeker. Retorna false quando nenhum ponto foi
        /// configurado — nesse caso o agente volta para a pose inicial que ele mesmo guardou.
        /// </summary>
        public bool TryGetSeekerSpawn(out Vector3 position, out Quaternion rotation)
        {
            if (_seekerSpawnPoints == null || _seekerSpawnPoints.Length == 0)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            Transform point = _seekerSpawnPoints[Random.Range(0, _seekerSpawnPoints.Length)];
            position = point.position;
            rotation = point.rotation;
            return true;
        }

        // Liga/desliga o labirinto conforme a lição atual do curriculum (config/seeker_curriculum.yaml)
        // e ajusta o regime de recompensa para combinar com ela.
        private void ApplyCurriculum()
        {
            float mazeProbability = Academy.Instance.EnvironmentParameters
                .GetWithDefault(_mazeParameterName, 0f);

            // maze_enabled é lido como PROBABILIDADE por episódio e por arena, não como flag
            // global. Com várias arenas na cena e 0.5, o mesmo batch carrega os dois regimes.
            // Sem isso, uma lição de labirinto puro tira a sala aberta do gradiente por
            // centenas de milhares de steps e a perseguição em espaço aberto degrada — o PPO
            // não preserva um comportamento que parou de dar recompensa.
            bool mazeOn = RollMaze(mazeProbability);

            // Fora do if do _maze de propósito: mesmo sem labirinto atribuído o regime de
            // recompensa precisa acompanhar a lição. Agora que o sorteio é por arena, cada
            // cópia usa o regime do PRÓPRIO layout no mesmo step — que é justamente o ponto:
            // a arena aberta continua cobrando aproximação em peso cheio enquanto a vizinha
            // com labirinto roda no regime reduzido.
            ApproachRewardScale = mazeOn ? _mazeApproachScale : 1f;
            WallProximityScale = mazeOn ? _mazeWallProximityScale : 1f;

            if (_maze != null)
                _maze.SetActive(mazeOn);
        }

        /// <summary>
        /// Sorteia o layout do episódio. Os extremos são tratados à parte porque
        /// UnityEngine.Random.value inclui o 1.0: uma comparação solta faria a lição "sempre
        /// labirinto" abrir a sala uma vez a cada alguns milhões de episódios — raro o
        /// bastante para nunca aparecer num teste e mesmo assim ser um bug.
        /// </summary>
        private static bool RollMaze(float probability)
        {
            if (probability <= 0f)
                return false;

            if (probability >= 1f)
                return true;

            return Random.value < probability;
        }
    }
}
