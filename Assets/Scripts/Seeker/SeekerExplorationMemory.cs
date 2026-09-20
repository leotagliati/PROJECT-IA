using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Memória espacial explícita: uma grade sobre a arena marcando onde o agente já passou.
    ///
    /// Existe porque a política é uma função pura da observação atual — dois trechos de corredor
    /// produzem leituras idênticas, então sem isso o agente não tem como saber de onde veio nem
    /// qual bifurcação já tentou, e oscila em vez de explorar. Em vez de esperar que a rede
    /// aprenda a construir um mapa a partir de um histórico, o mapa é entregue pronto.
    ///
    /// Células engolidas por parede (mapa da <see cref="SeekerArenaController"/>) contam como
    /// "nada a ganhar": 1 na janela e sem recompensa de célula nova. Sem isso a célula do outro
    /// lado de uma parede parece alcançável e o agente aprende a empurrar a parede para chegar lá.
    ///
    /// A janela sozinha tem um buraco: cercado de visitadas e paredes ela vira toda 1 — uma
    /// observação CONSTANTE, sem nenhuma informação de para onde fica o que falta. É nesse
    /// estado que o agente gira ou empurra parede. A fronteira (BFS na grade, respeitando as
    /// paredes, até a célula livre não visitada mais próxima) dá o gradiente global que falta:
    /// "o caminho começa para lá".
    /// </summary>
    public class SeekerExplorationMemory : MonoBehaviour
    {
        [Header("-----Grade-----")]
        // Lado da arena em unidades: os Wall Bound fecham um quadrado de 10.4.
        [SerializeField] private float _arenaSize = 10.4f;

        // Da ordem da largura de um corredor. Muito maior e tudo vira "visitado" de imediato;
        // muito menor e a janela de observação cobre pouco espaço para ser útil.
        [SerializeField] private float _cellSize = 0.8f;

        // Raio da janela observada. 2 => 5x5 => 25 observações.
        [SerializeField] private int _windowRadius = 2;

        private Transform _arenaRoot;
        private SeekerArenaController _arena;
        private LayerMask _wallLayer;
        private bool[] _visited;
        private bool[] _blocked;
        private bool[] _windowBlocked;
        private int _freeCount;
        private int _lastCellX;
        private int _lastCellZ;

        // BFS da fronteira. Arrays de trabalho com carimbo em vez de Clear: o BFS roda toda vez
        // que o agente muda de célula (ou pisa numa nova), e limpar 2800 células a cada vez
        // custaria mais que a busca.
        private int[] _bfsQueue;
        private int[] _bfsSeen;
        private int[] _bfsFirstStep;
        private int _bfsStamp;
        private int _cachedCellX = int.MinValue;
        private int _cachedCellZ = int.MinValue;
        private int _cachedVisitedCount = -1;

        private bool _hasFrontier;
        private int _frontierIndex = -1;
        private int _frontierDistance;
        private Vector3 _frontierStepLocal;

        // 4-vizinhos, e não 8: na diagonal o BFS atravessaria quinas entre duas paredes.
        private static readonly int[] StepDx = { 0, 1, 0, -1 };
        private static readonly int[] StepDz = { 1, 0, -1, 0 };
        private int _gridSize;
        private float[] _window;
        private bool _enteredNewCell;
        private int _visitedCount;

        /// <summary>Janela local de células visitadas (1) ou não (0), em ordem linha por linha.</summary>
        public float[] Window => _window;

        public int WindowCellCount => _window?.Length ?? 0;

        /// <summary>Se o step atual levou o agente a uma célula onde ele ainda não tinha estado.</summary>
        public bool EnteredNewCell => _enteredNewCell;

        /// <summary>Células distintas pisadas no episódio. Telemetria: o agente não observa isto.</summary>
        public int VisitedCellCount => _visitedCount;

        /// <summary>Células alcançáveis (fora as bloqueadas por parede) no layout atual.</summary>
        public int CellCount => _freeCount;

        /// <summary>Se existe alguma célula livre não visitada alcançável a partir de onde o agente está.</summary>
        public bool HasFrontier => _hasFrontier;

        /// <summary>
        /// Primeiro passo do caminho até a fronteira, em MUNDO (mesmo referencial das ações e do
        /// vetor do hider). Unitário; zero sem fronteira.
        /// </summary>
        public Vector3 FrontierStepDirectionWorld =>
            _hasFrontier ? (_arenaRoot != null ? _arenaRoot.TransformDirection(_frontierStepLocal) : _frontierStepLocal) : Vector3.zero;

        /// <summary>Comprimento do caminho até a fronteira, em células (4-vizinhos). Zero sem fronteira.</summary>
        public int FrontierDistanceCells => _hasFrontier ? _frontierDistance : 0;

        /// <summary>Distância normalizada pelo lado da grade, para observação.</summary>
        public float FrontierDistanceNormalized => _hasFrontier ? Mathf.Clamp01((float)_frontierDistance / _gridSize) : 0f;

        /// <summary>Índice da célula-alvo da fronteira, ou -1. Muda quando a fronteira troca de célula.</summary>
        public int FrontierIndex => _hasFrontier ? _frontierIndex : -1;

        /// <summary>Telemetria: centro em mundo da célula-alvo da fronteira.</summary>
        public Vector3 FrontierCellWorldCenter => CellWorldCenter(_frontierIndex % _gridSize, _frontierIndex / _gridSize);

        /// <summary>Telemetria: se a célula i da janela está bloqueada por parede (a rede vê só o 1).</summary>
        public bool IsWindowCellBlocked(int index) => _windowBlocked != null && index >= 0 && index < _windowBlocked.Length && _windowBlocked[index];

        public float CellSize => _cellSize;

        /// <summary>
        /// Se a posição cai numa célula bloqueada por parede. Fora da arena também conta como
        /// bloqueado: empurrar a borda é tão inútil quanto empurrar uma parede interna.
        /// </summary>
        public bool IsBlockedAtWorld(Vector3 worldPosition)
        {
            if (_blocked == null)
                return false;

            ToCell(worldPosition, out int cellX, out int cellZ);
            return !InBounds(cellX, cellZ) || _blocked[cellZ * _gridSize + cellX];
        }

        /// <summary>Telemetria: centro, em mundo, da célula i da janela (do último Tick).</summary>
        public Vector3 WindowCellWorldCenter(int index)
        {
            int side = _windowRadius * 2 + 1;
            int x = _lastCellX + index % side - _windowRadius;
            int z = _lastCellZ + index / side - _windowRadius;
            return CellWorldCenter(x, z);
        }

        /// <summary>
        /// A grade é relativa à arena, não ao mundo: cada cópia do ambiente fica numa posição
        /// diferente da cena, então índices em coordenada de mundo misturariam as arenas.
        /// </summary>
        public void Configure(SeekerArenaController arena, LayerMask wallLayer)
        {
            _arena = arena;
            _arenaRoot = arena != null ? arena.transform : null;
            _wallLayer = wallLayer;
            _gridSize = Mathf.Max(1, Mathf.CeilToInt(_arenaSize / _cellSize));
            _visited = new bool[_gridSize * _gridSize];
            _blocked = new bool[_gridSize * _gridSize];
            _freeCount = _blocked.Length;

            int side = _windowRadius * 2 + 1;
            _window = new float[side * side];
            _windowBlocked = new bool[side * side];

            int cells = _gridSize * _gridSize;
            _bfsQueue = new int[cells];
            _bfsSeen = new int[cells];
            _bfsFirstStep = new int[cells];
        }

        public void ResetEpisode()
        {
            if (_visited == null)
                return;

            System.Array.Clear(_visited, 0, _visited.Length);
            _enteredNewCell = false;
            _visitedCount = 0;
            _cachedVisitedCount = -1;
            _hasFrontier = false;

            // Depois do ResetEpisode da arena, que é quem liga ou desliga o labirinto do episódio.
            if (_arena != null)
            {
                _arena.FillBlockedCells(_arenaSize, _cellSize, _gridSize, _wallLayer, _blocked);

                _freeCount = 0;
                foreach (bool blocked in _blocked)
                    if (!blocked) _freeCount++;
            }
        }

        public void Tick(Vector3 worldPosition)
        {
            if (_visited == null)
                return;

            ToCell(worldPosition, out int cellX, out int cellZ);
            _lastCellX = cellX;
            _lastCellZ = cellZ;

            _enteredNewCell = false;
            if (InBounds(cellX, cellZ))
            {
                int index = cellZ * _gridSize + cellX;

                // Pisar numa célula bloqueada (borda de parede) marca, mas não paga.
                _enteredNewCell = !_visited[index] && !_blocked[index];
                _visited[index] = true;

                if (_enteredNewCell)
                    _visitedCount++;
            }

            FillWindow(cellX, cellZ);

            // A fronteira só muda quando o agente troca de célula ou marca uma nova.
            if (cellX != _cachedCellX || cellZ != _cachedCellZ || _visitedCount != _cachedVisitedCount)
            {
                _cachedCellX = cellX;
                _cachedCellZ = cellZ;
                _cachedVisitedCount = _visitedCount;
                FindFrontier(cellX, cellZ);
            }
        }

        /// <summary>
        /// BFS a partir da célula do agente, só por células livres, até a primeira não visitada.
        /// Guarda a direção do PRIMEIRO passo do caminho (propagada de vizinho em vizinho), não
        /// a direção reta até o alvo — a reta atravessa parede, o passo não.
        /// </summary>
        private void FindFrontier(int startX, int startZ)
        {
            _hasFrontier = false;

            if (!InBounds(startX, startZ))
                return;

            _bfsStamp++;
            int head = 0, tail = 0;
            int start = startZ * _gridSize + startX;

            _bfsQueue[tail++] = start;
            _bfsSeen[start] = _bfsStamp;
            _bfsFirstStep[start] = -1;

            // Distância em células por camada: a fila é FIFO, então dá para contar a camada
            // pelo tamanho dela em vez de guardar um array de distâncias.
            int distance = 0;
            int layerEnd = tail;

            while (head < tail)
            {
                if (head == layerEnd)
                {
                    distance++;
                    layerEnd = tail;
                }

                int current = _bfsQueue[head++];
                int cx = current % _gridSize;
                int cz = current / _gridSize;

                if (current != start && !_visited[current])
                {
                    _hasFrontier = true;
                    _frontierIndex = current;
                    _frontierDistance = distance;

                    int step = _bfsFirstStep[current];
                    _frontierStepLocal = new Vector3(StepDx[step], 0f, StepDz[step]);
                    return;
                }

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + StepDx[d];
                    int nz = cz + StepDz[d];
                    if (!InBounds(nx, nz))
                        continue;

                    int next = nz * _gridSize + nx;
                    if (_bfsSeen[next] == _bfsStamp || _blocked[next])
                        continue;

                    _bfsSeen[next] = _bfsStamp;
                    _bfsFirstStep[next] = current == start ? d : _bfsFirstStep[current];
                    _bfsQueue[tail++] = next;
                }
            }
        }

        private Vector3 CellWorldCenter(int x, int z)
        {
            float half = _arenaSize * 0.5f;
            Vector3 local = new(-half + (x + 0.5f) * _cellSize, 0f, -half + (z + 0.5f) * _cellSize);
            return _arenaRoot != null ? _arenaRoot.TransformPoint(local) : local;
        }

        // Fora dos limites e parede contam como visitado: não há nada a ganhar ali, e marcar
        // como inexplorado transformaria bordas e paredes num atrativo.
        private void FillWindow(int cellX, int cellZ)
        {
            int i = 0;
            for (int dz = -_windowRadius; dz <= _windowRadius; dz++)
            {
                for (int dx = -_windowRadius; dx <= _windowRadius; dx++)
                {
                    int x = cellX + dx;
                    int z = cellZ + dz;
                    bool inBounds = InBounds(x, z);
                    int index = inBounds ? z * _gridSize + x : -1;
                    bool blocked = inBounds && _blocked[index];

                    _windowBlocked[i] = blocked;
                    _window[i++] = inBounds && !blocked && !_visited[index] ? 0f : 1f;
                }
            }
        }

        private void ToCell(Vector3 worldPosition, out int cellX, out int cellZ)
        {
            Vector3 local = _arenaRoot != null
                ? _arenaRoot.InverseTransformPoint(worldPosition)
                : worldPosition;

            float half = _arenaSize * 0.5f;
            cellX = Mathf.FloorToInt((local.x + half) / _cellSize);
            cellZ = Mathf.FloorToInt((local.z + half) / _cellSize);
        }

        private bool InBounds(int cellX, int cellZ) =>
            cellX >= 0 && cellX < _gridSize && cellZ >= 0 && cellZ < _gridSize;

        private void OnDrawGizmosSelected()
        {
            if (_visited == null || _arenaRoot == null)
                return;

            float half = _arenaSize * 0.5f;
            for (int z = 0; z < _gridSize; z++)
            {
                for (int x = 0; x < _gridSize; x++)
                {
                    if (!_visited[z * _gridSize + x])
                        continue;

                    Vector3 local = new(
                        (x + 0.5f) * _cellSize - half,
                        0.05f,
                        (z + 0.5f) * _cellSize - half);

                    Gizmos.color = new Color(0f, 0.7f, 1f, 0.25f);
                    Gizmos.DrawCube(_arenaRoot.TransformPoint(local), new Vector3(_cellSize, 0.02f, _cellSize));
                }
            }
        }
    }
}
