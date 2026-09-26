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

        [Header("-----Grade de exploração-----")]
        // A memória de exploração do agente não sabe de parede: uma célula do outro lado dela
        // aparece como "não visitada" e vira atrativo — o agente empurra a parede para chegar
        // lá. A arena, que é quem conhece o layout, sonda a grade uma vez por layout e entrega
        // as células bloqueadas; a memória as trata como "nada a ganhar".
        [Tooltip("Altura, local à arena, em que as células são sondadas. Da ordem da altura dos colliders de parede, e acima do chão.")]
        [SerializeField] private float _occupancyProbeHeight = 0.5f;

        [Tooltip("Fração dos 9 pontos de sonda de uma célula dentro de parede para ela contar como bloqueada. 0.5: uma parede fina atravessando a célula NÃO bloqueia (ela é alcançável dos dois lados); célula engolida pela parede bloqueia.")]
        [SerializeField, Range(0.1f, 1f)] private float _blockedFraction = 0.5f;

        [Header("-----Feedback-----")]
        // Só para debug visual: durante o delay o agente segue recebendo decisões que não viram
        // experiência útil. Mantenha em 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;
        private bool _mazeOn;

        // Um mapa por layout: o labirinto liga e desliga por episódio, e sondar 2800 células
        // a cada reset seria desperdício. Reprocessa se a grade pedida mudar.
        private readonly OccupancyMap[] _occupancy = new OccupancyMap[2];

        private sealed class OccupancyMap
        {
            public float CellSize;
            public int GridSize;
            public int WallLayer;
            public bool[] Blocked;
        }

        public bool MazeEnabled => _mazeOn;

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

            _mazeOn = mazeOn;
        }

        /// <summary>
        /// Preenche <paramref name="blocked"/> (indexado como a memória: z * gridSize + x, grade
        /// centrada na arena) com as células engolidas por parede no layout ATUAL. Chamar depois
        /// de ResetEpisode, que é quando o labirinto do episódio já está ligado ou desligado.
        /// </summary>
        public void FillBlockedCells(float arenaSize, float cellSize, int gridSize, LayerMask wallLayer, bool[] blocked)
        {
            int key = _mazeOn ? 1 : 0;
            OccupancyMap map = _occupancy[key];

            if (map == null || map.CellSize != cellSize || map.GridSize != gridSize || map.WallLayer != wallLayer.value)
            {
                map = new OccupancyMap
                {
                    CellSize = cellSize,
                    GridSize = gridSize,
                    WallLayer = wallLayer.value,
                    Blocked = new bool[gridSize * gridSize],
                };
                BakeOccupancy(map, arenaSize, wallLayer);
                _occupancy[key] = map;
            }

            System.Array.Copy(map.Blocked, blocked, Mathf.Min(map.Blocked.Length, blocked.Length));
        }

        private void BakeOccupancy(OccupancyMap map, float arenaSize, LayerMask wallLayer)
        {
            // SetActive no labirinto registra os colliders na física na hora, mas transforms
            // movidos neste mesmo frame só chegam lá no próximo step — sincroniza antes de sondar.
            Physics.SyncTransforms();

            float half = arenaSize * 0.5f;
            float cell = map.CellSize;
            float radius = cell * 0.15f;
            int needed = Mathf.Max(1, Mathf.CeilToInt(_blockedFraction * 9f));
            int blockedCount = 0;

            for (int z = 0; z < map.GridSize; z++)
            {
                for (int x = 0; x < map.GridSize; x++)
                {
                    // Centro da célula no espaço da arena, mesma convenção de ToCell na memória.
                    float cx = -half + (x + 0.5f) * cell;
                    float cz = -half + (z + 0.5f) * cell;
                    int hits = 0;

                    // 3x3 pontos a ±cell/3 do centro: cobre a célula sem encostar nas vizinhas.
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            Vector3 local = new(cx + dx * cell / 3f, _occupancyProbeHeight, cz + dz * cell / 3f);
                            Vector3 world = transform.TransformPoint(local);

                            if (Physics.CheckSphere(world, radius, wallLayer, QueryTriggerInteraction.Ignore))
                                hits++;
                        }
                    }

                    bool isBlocked = hits >= needed;
                    map.Blocked[z * map.GridSize + x] = isBlocked;
                    if (isBlocked)
                        blockedCount++;
                }
            }

            // Esta contagem é o que calibra _newCellReward: (livres × recompensa) tem que ficar
            // bem abaixo do +5 de captura. Fica no log de propósito para não precisar adivinhar.
            int free = map.Blocked.Length - blockedCount;
            Debug.Log($"{name}: grade {map.GridSize}x{map.GridSize} ({(_mazeOn ? "labirinto" : "sala aberta")}): {free} células livres, {blockedCount} bloqueadas por parede.", this);
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
