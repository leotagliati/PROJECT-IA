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
        private bool[] _visited;
        private int _gridSize;
        private float[] _window;
        private bool _enteredNewCell;

        /// <summary>Janela local de células visitadas (1) ou não (0), em ordem linha por linha.</summary>
        public float[] Window => _window;

        public int WindowCellCount => _window?.Length ?? 0;

        /// <summary>Se o step atual levou o agente a uma célula onde ele ainda não tinha estado.</summary>
        public bool EnteredNewCell => _enteredNewCell;

        /// <summary>
        /// A grade é relativa à arena, não ao mundo: cada cópia do ambiente fica numa posição
        /// diferente da cena, então índices em coordenada de mundo misturariam as arenas.
        /// </summary>
        public void Configure(Transform arenaRoot)
        {
            _arenaRoot = arenaRoot;
            _gridSize = Mathf.Max(1, Mathf.CeilToInt(_arenaSize / _cellSize));
            _visited = new bool[_gridSize * _gridSize];

            int side = _windowRadius * 2 + 1;
            _window = new float[side * side];
        }

        public void ResetEpisode()
        {
            if (_visited == null)
                return;

            System.Array.Clear(_visited, 0, _visited.Length);
            _enteredNewCell = false;
        }

        public void Tick(Vector3 worldPosition)
        {
            if (_visited == null)
                return;

            ToCell(worldPosition, out int cellX, out int cellZ);

            _enteredNewCell = false;
            if (InBounds(cellX, cellZ))
            {
                int index = cellZ * _gridSize + cellX;
                _enteredNewCell = !_visited[index];
                _visited[index] = true;
            }

            FillWindow(cellX, cellZ);
        }

        // Fora dos limites conta como visitado: não há nada a ganhar ali, e marcar como
        // inexplorado transformaria as bordas da arena num atrativo.
        private void FillWindow(int cellX, int cellZ)
        {
            int i = 0;
            for (int dz = -_windowRadius; dz <= _windowRadius; dz++)
            {
                for (int dx = -_windowRadius; dx <= _windowRadius; dx++)
                {
                    int x = cellX + dx;
                    int z = cellZ + dz;
                    _window[i++] = InBounds(x, z) && !_visited[z * _gridSize + x] ? 0f : 1f;
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
