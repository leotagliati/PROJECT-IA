using System.Collections.Generic;
using UnityEngine;

// Os campos só são lidos pelos menus de editor; no build do jogo ficam sem uso (e sem custo).
#pragma warning disable CS0414

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// LADRILHAMENTO (menu 9): cobre o chão da arena inteiro com nós RETANGULARES que não se
    /// sobrepõem, em vez de discos com raio. É a outra resposta ao mesmo problema da cobertura
    /// (NavGraphPlacer.Coverage): lá os discos se cruzam e o FindNodeAt desempata pelo centro mais
    /// perto; aqui cada ponto do chão está em EXATAMENTE um nó, por construção.
    ///
    /// O que sai:
    ///   - um nó AUXILIAR por ladrilho de chão onde o corpo vai (a forma do NavGraph vira
    ///     Retângulo). Quais viram exploração ou ping é autoria sua — o ladrilhamento só não
    ///     esquece os que já eram (_tilingKeepKinds);
    ///   - CORREDOR = uma FILA de ladrilhos, cada um de parede a parede: nunca dois lado a lado
    ///     na largura (ver SectionsOf);
    ///   - PORTA = um ladrilho que preenche exatamente o buraco da parede (pilar a pilar, na
    ///     espessura da parede);
    ///   - VERMELHO (NavBlockedArea, não é nó) = chão livre onde o corpo NÃO CABE (vão entre
    ///     móveis, nicho estreito, sala de porta estreita demais).
    ///
    /// COMO, numa grade de _tileStep no plano dos nós (mesma física do resto do placer):
    ///   CHÃO     célula com a coluna do corpo livre (caixa do tamanho da célula);
    ///   ANDÁVEL  onde o CENTRO do corpo pode estar (NavGraph.IsBodyClear), conectado aos pontos
    ///            de dentro do prédio;
    ///   ACESSÍVEL o chão que o corpo ENCOSTA: andável dilatado por meia largura do corpo (um
    ///            quadrado, como a caixa do corpo) — é ele que é ladrilhado, e é por isso que os
    ///            ladrilhos vão até a parede;
    ///   chão que não é acessível = vermelho (ou fora do prédio, quando encosta na borda do mapa).
    /// Os ladrilhos: porta primeiro, depois seções transversais de corredor empilhadas, depois o
    /// resto por "maior retângulo livre" (gulosa); junta o que se completa num retângulo, corta o
    /// que passa de _maxTileSize e junta de novo os pedaços que ficaram sem chão andável.
    /// O NÓ de cada ladrilho fica no ponto andável mais perto do centro dele (é dele que saem as
    /// ligações e a direção na observação), e o retângulo guarda o chão (NavNode._areaSize).
    /// Dois ladrilhos são vizinhos quando o centro do corpo passa de um para o outro.
    /// </summary>
    public partial class NavGraphPlacer
    {
        [Header("-----Ladrilhos (menu 9)-----")]
        // Resolução da grade do ladrilhamento. As bordas dos ladrilhos caem nas bordas das
        // células, então é também o erro máximo de um ladrilho contra a parede. 0.25 numa arena
        // de ~100 x 80 m são ~130 mil células — alguns segundos de física no editor.
        [SerializeField, Min(0.1f)] private float _tileStep = 0.25f;

        // Maior lado de um ladrilho. Ladrilho maior que isto é cortado em pedaços IGUAIS (uma
        // sala de 12 m vira 3 de 4 m, não 5 + 5 + 2). É o espaçamento dos nós: a observação de
        // vizinhos é medida a partir do nó, e um ladrilho de 20 m diria pouco sobre onde o agente
        // está. 5 m ~ 1 s de caminhada a 5 m/s.
        [SerializeField, Min(1f)] private float _maxTileSize = 5f;

        // Um trecho mais estreito que isto e mais comprido do que largo é CORREDOR: vira uma fila
        // de ladrilhos de parede a parede e nunca é cortado na largura (mesmo se for mais largo
        // que _maxTileSize). 4 m cobre os corredores do escritório (~3 m) com folga.
        [SerializeField, Min(1f)] private float _corridorMaxWidth = 4f;

        // Quanto a largura de um corredor pode variar (em cada lado) e continuar no MESMO
        // ladrilho. Um pilar ou rodapé de 20 cm não pode partir a fila em ladrilhos de 30 cm. O
        // que sobra de fora (o pilar) fica dentro do retângulo — é obstáculo, o agente não pisa.
        [SerializeField, Min(0f)] private float _corridorTolerance = 0.3f;

        // Área mínima de uma mancha VERMELHA. Menor que isto é arredondamento de grade (o canto
        // de uma sala que a dilatação não alcança) e volta a ser chão comum.
        [SerializeField, Min(0f)] private float _minBlockedArea = 0.5f;

        // Ladrilho menor que isto é juntado ao vizinho quando a união é um retângulo. Não sendo
        // possível, fica (ainda é chão onde o agente pisa) — o relatório conta quantos.
        [SerializeField, Min(0f)] private float _minTileArea = 0.75f;

        // Ligado: nó de exploração ou de ping que já existia passa o tipo e o peso para o
        // ladrilho que contém a posição dele. Re-ladrilhar (mudou um móvel, mudou o tamanho) não
        // apaga a sua marcação. Desligado: tudo volta a ser auxiliar.
        [SerializeField] private bool _tilingKeepKinds = true;

#if UNITY_EDITOR
        private const string BlockedContainerName = "Bloqueados (sem passagem)";

        // CorridorX = corredor que CORRE ao longo de X (a seção transversal é uma coluna em Z).
        private enum TileKind
        {
            Room,
            CorridorX,
            CorridorZ,
            Door,
        }

        private sealed class Tile
        {
            // Células, inclusivas.
            public int X0, Z0, X1, Z1;
            public TileKind Kind;
            public int Walkable;

            // Retângulo no mundo (calculado no fim; o da porta vem da geometria dela).
            public float MinX, MaxX, MinZ, MaxZ;
            public Vector3 Position;

            // Tipo e peso herdados de um nó antigo (_tilingKeepKinds).
            public NodeKind NodeKind = NodeKind.Auxiliary;
            public float Weight;
            public bool Inherited;

            public int Width => X1 - X0 + 1;
            public int Depth => Z1 - Z0 + 1;
            public int Area => Width * Depth;

            public Tile Copy() => (Tile)MemberwiseClone();
        }

        private sealed class TileGrid
        {
            public readonly Vector3 Min;
            public readonly float Step;
            public readonly int SizeX;
            public readonly int SizeZ;
            public readonly bool[] Floor;
            public readonly bool[] Walkable;
            public readonly bool[] Access;
            public readonly bool[] Red;

            // Ladrilho de porta que reservou a célula; -1 = nenhum; -2 = porta fechada (vermelha).
            public readonly int[] Door;

            public TileGrid(Vector3 min, float step, int sizeX, int sizeZ)
            {
                Min = min;
                Step = step;
                SizeX = sizeX;
                SizeZ = sizeZ;
                Floor = new bool[sizeX * sizeZ];
                Walkable = new bool[sizeX * sizeZ];
                Access = new bool[sizeX * sizeZ];
                Red = new bool[sizeX * sizeZ];
                Door = new int[sizeX * sizeZ];
                for (int i = 0; i < Door.Length; i++)
                    Door[i] = -1;
            }

            public int Count => SizeX * SizeZ;

            public int Index(int x, int z) => z * SizeX + x;

            public int X(int cell) => cell % SizeX;

            public int Z(int cell) => cell / SizeX;

            public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < SizeX && z < SizeZ;

            public Vector3 Position(int cell) => Position(X(cell), Z(cell));

            public Vector3 Position(int x, int z) => new Vector3(Min.x + x * Step, Min.y, Min.z + z * Step);

            public float CellArea => Step * Step;
        }

        // ================================================================================
        // Menu
        // ================================================================================

        [ContextMenu("9. Ladrilhar o chão com nós retangulares (apaga os nós atuais)")]
        private void TileFloor()
        {
            if (!CanRemoveNodes())
                return;

            int existing = ValidNodes().Count;
            if (existing > 0 && !UnityEditor.EditorUtility.DisplayDialog(
                    "Ladrilhar o chão",
                    $"Isto apaga os {existing} nós atuais e cobre o chão com nós retangulares (auxiliares" +
                    (_tilingKeepKinds ? "; os de exploração e ping passam o tipo para o ladrilho em que estão" : "") +
                    "). O NavGraph passa para a forma Retângulo. Ctrl+Z desfaz. Continuar?",
                    "Ladrilhar", "Cancelar"))
                return;

            RunStep("Ladrilhar o chão", TileFloorStep, radialOnly: false);
        }

        private void TileFloorStep()
        {
            float height = GridHeight();
            TileGrid grid = BuildTileGrid(height);
            if (grid == null)
                return;

            var tiles = new List<Tile>();
            var blocked = new List<Tile>();

            ReserveDoors(grid, tiles, blocked, height);
            MarkBlocked(grid);

            // Chão ainda sem dono: acessível, fora de porta.
            var pool = new bool[grid.Count];
            for (int i = 0; i < grid.Count; i++)
                pool[i] = grid.Access[i] && grid.Door[i] == -1;

            var swallowed = new bool[grid.Count];
            BuildCorridors(grid, pool, swallowed, tiles);
            BuildRooms(grid, pool, tiles);

            int[] owner = new int[grid.Count];
            PaintOwners(grid, tiles, owner);

            // Junta o que forma retângulo, corta o que passou do tamanho, junta de novo os pedaços
            // sem chão andável (faixa de parede que o corte isolou) e descarta o que sobrar assim.
            MergeTiles(grid, tiles, owner, afterSplit: false);
            tiles = SplitTiles(grid, tiles);
            PaintOwners(grid, tiles, owner);
            MergeTiles(grid, tiles, owner, afterSplit: true);
            float droppedArea = DropUnwalkable(grid, tiles);
            PaintOwners(grid, tiles, owner);

            if (tiles.Count == 0)
            {
                Debug.LogError($"{name}: nenhum ladrilho sobrou — confira a Wall Layer e os pontos de partida.", this);
                return;
            }

            PlaceTileNodes(grid, tiles);
            ComputeWorldRects(grid, tiles);

            HashSet<long> edges = TileEdges(grid, tiles, owner);
            List<long> keptBlocked = SettleTileEdges(grid, tiles, edges);

            BuildBlockedRects(grid, blocked);

            List<(Vector3 position, NodeKind kind, float weight, string name)> marked = _tilingKeepKinds
                ? MarkedNodes()
                : new List<(Vector3, NodeKind, float, string)>();
            int inherited = InheritKinds(tiles, marked);

            NavNode[] nodes = ApplyTiles(tiles, edges, blocked);

            // Relatório no gizmo: chão andável sem ladrilho (marrom) e nós de ligação que o corpo
            // não atravessa em linha reta (X vermelho).
            _reportCellSize = grid.Step;
            for (int i = 0; i < grid.Count; i++)
            {
                if (grid.Walkable[i] && owner[i] < 0)
                    _uncoveredCells.Add(grid.Position(i));
            }

            foreach (long edge in keptBlocked)
            {
                Draft.Split(edge, out int a, out int b);
                _unresolved.Add(nodes[a]);
                _unresolved.Add(nodes[b]);
            }

            ReportTiling(grid, tiles, edges, blocked, owner, droppedArea, keptBlocked.Count, inherited, marked.Count);
        }

        // ================================================================================
        // Grade
        // ================================================================================

        private TileGrid BuildTileGrid(float height)
        {
            if (!TryGetArenaBounds(out Bounds bounds))
            {
                Debug.LogError($"{name}: nenhum collider de parede ativo na arena — não há mapa para medir.", this);
                return null;
            }

            float step = Mathf.Max(0.1f, _tileStep);
            int sizeX = Mathf.CeilToInt(bounds.size.x / step) + 1;
            int sizeZ = Mathf.CeilToInt(bounds.size.z / step) + 1;
            if ((long)sizeX * sizeZ > 4_000_000)
            {
                Debug.LogError($"{name}: grade de {sizeX} x {sizeZ} é grande demais. Aumente o Tile Step.", this);
                return null;
            }

            var grid = new TileGrid(new Vector3(bounds.min.x, height, bounds.min.z), step, sizeX, sizeZ);
            var bodyFree = new bool[grid.Count];

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            float bottom = Graph.BodyBottom;
            float top = Graph.BodyTop;
            Vector3 up = Vector3.up * ((bottom + top) * 0.5f);

            // A caixa da célula (um fio menor, para a parede exatamente na borda não contar dos
            // dois lados), na coluna inteira do corpo: CHÃO é onde nada ocupa essa coluna.
            var extents = new Vector3(step * 0.49f, (top - bottom) * 0.5f, step * 0.49f);

            for (int z = 0; z < sizeZ; z++)
            {
                if (UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                        "Ladrilhar: medindo o chão", $"linha {z}/{sizeZ}", (float)z / sizeZ))
                {
                    Debug.LogWarning($"{name}: cancelado.", this);
                    return null;
                }

                for (int x = 0; x < sizeX; x++)
                {
                    int cell = grid.Index(x, z);
                    Vector3 position = grid.Position(x, z);
                    grid.Floor[cell] = physics.OverlapBox(position + up, extents, _overlapBuffer, Quaternion.identity,
                        Graph.WallLayer, QueryTriggerInteraction.Ignore) == 0;
                    bodyFree[cell] = grid.Floor[cell] && Graph.IsBodyClear(position, Graph.LinkClearance);
                }
            }

            // ANDÁVEL: flood fill do centro do corpo a partir de pontos de dentro do prédio.
            var queue = new Queue<int>();
            foreach (Vector3 seed in Seeds(DraftFromScene()))
            {
                int cell = SnapTile(grid, bodyFree, seed, 2f);
                if (cell < 0 || grid.Walkable[cell])
                    continue;

                grid.Walkable[cell] = true;
                queue.Enqueue(cell);
            }

            if (queue.Count == 0)
            {
                Debug.LogError(
                    $"{name}: nenhum ponto de partida em chão livre. Preencha Generation Seeds com um ponto " +
                    "dentro do prédio.", this);
                return null;
            }

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int cx = grid.X(cell);
                int cz = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + StepX[d];
                    int nz = cz + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (!bodyFree[next] || grid.Walkable[next])
                        continue;

                    grid.Walkable[next] = true;
                    queue.Enqueue(next);
                }
            }

            // ACESSÍVEL: andável dilatado por um QUADRADO de meia largura do corpo + meia célula
            // (a caixa do corpo encostada na parede cobre até ela; a meia célula é a folga da
            // grade). Separável: primeiro em X, depois em Z, com soma de prefixos.
            int k = Mathf.CeilToInt((Graph.LinkClearance + step * 0.5f) / step);
            bool[] dilated = DilateAlong(grid, grid.Walkable, k, alongX: true);
            dilated = DilateAlong(grid, dilated, k, alongX: false);
            for (int i = 0; i < grid.Count; i++)
                grid.Access[i] = grid.Floor[i] && dilated[i];

            return grid;
        }

        private static bool[] DilateAlong(TileGrid grid, bool[] source, int k, bool alongX)
        {
            var result = new bool[grid.Count];
            int lines = alongX ? grid.SizeZ : grid.SizeX;
            int length = alongX ? grid.SizeX : grid.SizeZ;
            var prefix = new int[length + 1];

            for (int line = 0; line < lines; line++)
            {
                for (int i = 0; i < length; i++)
                {
                    int cell = alongX ? grid.Index(i, line) : grid.Index(line, i);
                    prefix[i + 1] = prefix[i] + (source[cell] ? 1 : 0);
                }

                for (int i = 0; i < length; i++)
                {
                    int lo = Mathf.Max(0, i - k);
                    int hi = Mathf.Min(length - 1, i + k);
                    if (prefix[hi + 1] - prefix[lo] > 0)
                        result[alongX ? grid.Index(i, line) : grid.Index(line, i)] = true;
                }
            }

            return result;
        }

        private static int SnapTile(TileGrid grid, bool[] mask, Vector3 position, float maxDistance)
        {
            int cx = Mathf.RoundToInt((position.x - grid.Min.x) / grid.Step);
            int cz = Mathf.RoundToInt((position.z - grid.Min.z) / grid.Step);
            int reach = Mathf.CeilToInt(maxDistance / grid.Step);

            int best = -1;
            int bestDistance = int.MaxValue;
            for (int x = cx - reach; x <= cx + reach; x++)
            {
                for (int z = cz - reach; z <= cz + reach; z++)
                {
                    if (!grid.InBounds(x, z) || !mask[grid.Index(x, z)])
                        continue;

                    int distance = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = grid.Index(x, z);
                    }
                }
            }

            return bestDistance <= reach * reach ? best : -1;
        }

        // ================================================================================
        // Portas e vermelho
        // ================================================================================

        /// <summary>
        /// Um ladrilho por batente (FindDoors), do tamanho exato do buraco: de pilar a pilar ao
        /// longo da parede, e a espessura da parede na normal. As células cujo centro cai nele
        /// (pelo menos uma fileira, mesmo com parede mais fina que a célula) ficam reservadas.
        /// Porta por onde o centro do corpo não passa vira vermelha. Só portas alinhadas aos
        /// eixos: um ladrilho é um retângulo alinhado ao mundo.
        /// </summary>
        private void ReserveDoors(TileGrid grid, List<Tile> tiles, List<Tile> blocked, float height)
        {
            float step = grid.Step;
            foreach (DoorInfo door in FindDoors())
            {
                bool alongX = Mathf.Abs(door.Along.x) >= 0.98f;
                bool alongZ = Mathf.Abs(door.Along.z) >= 0.98f;
                if (!alongX && !alongZ)
                {
                    Debug.LogWarning(
                        $"{door.Source.name}: porta fora dos eixos do mundo — ladrilho é retângulo alinhado aos eixos. " +
                        "O vão entra no ladrilhamento comum.", door.Source);
                    continue;
                }

                float halfX = alongX ? door.HalfWidth : door.HalfDepth;
                float halfZ = alongX ? door.HalfDepth : door.HalfWidth;
                float reserveX = alongX ? halfX : Mathf.Max(halfX, step * 0.5f);
                float reserveZ = alongX ? Mathf.Max(halfZ, step * 0.5f) : halfZ;

                int x0 = Mathf.Max(0, Mathf.CeilToInt((door.Center.x - reserveX - grid.Min.x) / step));
                int x1 = Mathf.Min(grid.SizeX - 1, Mathf.FloorToInt((door.Center.x + reserveX - grid.Min.x) / step));
                int z0 = Mathf.Max(0, Mathf.CeilToInt((door.Center.z - reserveZ - grid.Min.z) / step));
                int z1 = Mathf.Min(grid.SizeZ - 1, Mathf.FloorToInt((door.Center.z + reserveZ - grid.Min.z) / step));
                if (x0 > x1 || z0 > z1)
                    continue;

                bool taken = false;
                bool open = false;
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = grid.Index(x, z);
                        taken |= grid.Door[cell] != -1;
                        open |= grid.Walkable[cell];
                    }
                }

                if (taken)
                {
                    Debug.LogWarning($"{door.Source.name}: vão sobreposto a outra porta — ignorado.", door.Source);
                    continue;
                }

                var tile = new Tile
                {
                    X0 = x0, X1 = x1, Z0 = z0, Z1 = z1,
                    Kind = TileKind.Door,
                    MinX = door.Center.x - halfX, MaxX = door.Center.x + halfX,
                    MinZ = door.Center.z - halfZ, MaxZ = door.Center.z + halfZ,
                    Position = new Vector3(door.Center.x, height, door.Center.z),
                };

                int id = open ? tiles.Count : -2;
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = grid.Index(x, z);
                        grid.Door[cell] = id;
                        if (open && grid.Floor[cell])
                            grid.Access[cell] = true;
                    }
                }

                if (!open)
                {
                    blocked.Add(tile);
                    Debug.LogWarning(
                        $"{door.Source.name}: o corpo não passa por esta porta (vão de {door.HalfWidth * 2f:0.00} m, móvel " +
                        "no caminho ou sala sem ponto de partida) — ladrilho VERMELHO.", door.Source);
                    continue;
                }

                // O nó da porta fica no meio do vão; se o corpo não couber exatamente ali, na
                // célula andável do vão mais perto do meio.
                if (!Graph.IsBodyClear(tile.Position, Graph.LinkClearance))
                    tile.Position = NearestWalkable(grid, tile, tile.Position);

                tiles.Add(tile);
            }
        }

        /// <summary>
        /// Chão (coluna livre) que o corpo não encosta vira VERMELHO, por mancha 4-conexa. Mancha
        /// que toca a borda da grade é o lado de fora do prédio (nada); mancha menor que
        /// _minBlockedArea é arredondamento (volta a ser chão comum).
        /// </summary>
        private void MarkBlocked(TileGrid grid)
        {
            var seen = new bool[grid.Count];
            var queue = new Queue<int>();
            var cells = new List<int>();

            for (int start = 0; start < grid.Count; start++)
            {
                if (seen[start] || !IsBlockedCandidate(grid, start))
                    continue;

                cells.Clear();
                bool border = false;
                seen[start] = true;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    cells.Add(cell);
                    int cx = grid.X(cell);
                    int cz = grid.Z(cell);
                    border |= cx == 0 || cz == 0 || cx == grid.SizeX - 1 || cz == grid.SizeZ - 1;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + StepX[d];
                        int nz = cz + StepZ[d];
                        if (!grid.InBounds(nx, nz))
                            continue;

                        int next = grid.Index(nx, nz);
                        if (seen[next] || !IsBlockedCandidate(grid, next))
                            continue;

                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }

                if (border)
                    continue;

                bool small = cells.Count * grid.CellArea < _minBlockedArea;
                foreach (int cell in cells)
                {
                    if (small)
                        grid.Access[cell] = true;
                    else
                        grid.Red[cell] = true;
                }
            }
        }

        private static bool IsBlockedCandidate(TileGrid grid, int cell) =>
            grid.Floor[cell] && !grid.Access[cell] && grid.Door[cell] == -1;

        /// <summary>Retângulos vermelhos: gulosa de maior retângulo sobre as manchas.</summary>
        private void BuildBlockedRects(TileGrid grid, List<Tile> blocked)
        {
            var mask = (bool[])grid.Red.Clone();
            var heights = new int[grid.SizeX];
            var stack = new int[grid.SizeX + 1];
            int minCells = Mathf.Max(1, Mathf.CeilToInt(_minBlockedArea * 0.2f / grid.CellArea));

            while (LargestRectangle(grid.SizeX, grid.SizeZ, mask, heights, stack, out int x0, out int z0, out int x1, out int z1, out int area)
                   && area >= minCells)
            {
                ClearRect(grid, mask, x0, z0, x1, z1);
                var tile = new Tile { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Kind = TileKind.Room };
                CellRectToWorld(grid, tile);
                blocked.Add(tile);
            }
        }

        // ================================================================================
        // Corredores
        // ================================================================================

        // Uma seção transversal: na linha Row, as células Start..End (na direção transversal).
        private struct Section
        {
            public int Row;
            public int Start;
            public int End;
        }

        /// <summary>
        /// Corredores viram FILAS: cada seção transversal (de parede a parede) de um trecho
        /// estreito e comprido é inteira de um ladrilho só, e seções seguidas com a mesma largura
        /// (± _corridorTolerance) empilham no mesmo ladrilho. Assim um ladrilho nunca divide a
        /// largura do corredor com outro — o que a gulosa de retângulos faria sempre que um
        /// armário estreitasse um lado.
        /// </summary>
        private void BuildCorridors(TileGrid grid, bool[] pool, bool[] swallowed, List<Tile> tiles)
        {
            int[] runX = RunLengths(grid, pool, alongX: true);
            int[] runZ = RunLengths(grid, pool, alongX: false);

            // alongZ = corredor que corre ao longo de Z (seção = trecho em X numa linha z).
            List<Section> alongZ = SectionsOf(grid, pool, runX, runZ, corridorAlongZ: true);
            List<Section> alongX = SectionsOf(grid, pool, runX, runZ, corridorAlongZ: false);

            // Célula nas duas orientações = cruzamento ou sala quase quadrada: nenhuma das duas
            // leituras é confiável, e as duas seções voltam para a gulosa de salas.
            var inZ = new bool[grid.Count];
            var inX = new bool[grid.Count];
            MarkSections(grid, alongZ, true, inZ);
            MarkSections(grid, alongX, false, inX);
            alongZ.RemoveAll(s => SectionTouches(grid, s, true, inX));
            alongX.RemoveAll(s => SectionTouches(grid, s, false, inZ));

            StackSections(grid, pool, swallowed, alongZ, true, tiles);
            StackSections(grid, pool, swallowed, alongX, false, tiles);
        }

        private static int[] RunLengths(TileGrid grid, bool[] pool, bool alongX)
        {
            var run = new int[grid.Count];
            int lines = alongX ? grid.SizeZ : grid.SizeX;
            int length = alongX ? grid.SizeX : grid.SizeZ;

            for (int line = 0; line < lines; line++)
            {
                int i = 0;
                while (i < length)
                {
                    if (!pool[alongX ? grid.Index(i, line) : grid.Index(line, i)])
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    while (i < length && pool[alongX ? grid.Index(i, line) : grid.Index(line, i)])
                        i++;

                    for (int k = start; k < i; k++)
                        run[alongX ? grid.Index(k, line) : grid.Index(line, k)] = i - start;
                }
            }

            return run;
        }

        // Célula (linha, coluna) na orientação do corredor: ao longo de Z a linha é z e a coluna x.
        private static int At(TileGrid grid, bool corridorAlongZ, int row, int col) =>
            corridorAlongZ ? grid.Index(col, row) : grid.Index(row, col);

        /// <summary>
        /// Seções de corredor numa orientação: trechos transversais com largura até
        /// _corridorMaxWidth em que a maioria das células se estende MAIS ao longo do corredor
        /// do que a própria largura (é comprido, não quadrado).
        /// </summary>
        private List<Section> SectionsOf(TileGrid grid, bool[] pool, int[] runX, int[] runZ, bool corridorAlongZ)
        {
            var sections = new List<Section>();
            int widthCells = Mathf.Max(1, Mathf.FloorToInt(_corridorMaxWidth / grid.Step));
            int rows = corridorAlongZ ? grid.SizeZ : grid.SizeX;
            int cols = corridorAlongZ ? grid.SizeX : grid.SizeZ;
            int[] along = corridorAlongZ ? runZ : runX;

            for (int row = 0; row < rows; row++)
            {
                int col = 0;
                while (col < cols)
                {
                    if (!pool[At(grid, corridorAlongZ, row, col)])
                    {
                        col++;
                        continue;
                    }

                    int start = col;
                    while (col < cols && pool[At(grid, corridorAlongZ, row, col)])
                        col++;

                    int end = col - 1;
                    int width = end - start + 1;
                    if (width > widthCells)
                        continue;

                    int longer = 0;
                    for (int k = start; k <= end; k++)
                    {
                        if (along[At(grid, corridorAlongZ, row, k)] > width)
                            longer++;
                    }

                    if (longer * 2 >= width)
                        sections.Add(new Section { Row = row, Start = start, End = end });
                }
            }

            return sections;
        }

        private static void MarkSections(TileGrid grid, List<Section> sections, bool corridorAlongZ, bool[] mark)
        {
            foreach (Section s in sections)
            {
                for (int k = s.Start; k <= s.End; k++)
                    mark[At(grid, corridorAlongZ, s.Row, k)] = true;
            }
        }

        private static bool SectionTouches(TileGrid grid, Section s, bool corridorAlongZ, bool[] mark)
        {
            for (int k = s.Start; k <= s.End; k++)
            {
                if (mark[At(grid, corridorAlongZ, s.Row, k)])
                    return true;
            }

            return false;
        }

        private void StackSections(TileGrid grid, bool[] pool, bool[] swallowed, List<Section> sections, bool corridorAlongZ, List<Tile> tiles)
        {
            int tolerance = Mathf.RoundToInt(_corridorTolerance / grid.Step);
            var byRow = new Dictionary<int, List<int>>();
            for (int i = 0; i < sections.Count; i++)
            {
                if (!byRow.TryGetValue(sections[i].Row, out List<int> list))
                    byRow[sections[i].Row] = list = new List<int>();
                list.Add(i);
            }

            var used = new bool[sections.Count];
            var stack = new List<Section>();

            for (int i = 0; i < sections.Count; i++)
            {
                if (used[i])
                    continue;

                used[i] = true;
                stack.Clear();
                stack.Add(sections[i]);
                int lo = sections[i].Start;
                int hi = sections[i].End;

                while (byRow.TryGetValue(stack[stack.Count - 1].Row + 1, out List<int> next))
                {
                    int found = -1;
                    foreach (int j in next)
                    {
                        Section s = sections[j];
                        if (!used[j] && Mathf.Abs(s.Start - lo) <= tolerance && Mathf.Abs(s.End - hi) <= tolerance)
                        {
                            found = j;
                            break;
                        }
                    }

                    if (found < 0)
                        break;

                    int newLo = Mathf.Min(lo, sections[found].Start);
                    int newHi = Mathf.Max(hi, sections[found].End);
                    stack.Add(sections[found]);
                    if (!StackFits(grid, swallowed, stack, newLo, newHi, corridorAlongZ))
                    {
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    }

                    used[found] = true;
                    lo = newLo;
                    hi = newHi;
                }

                // Obstáculos engolidos pelo retângulo (pilar, recorte da parede) ficam marcados:
                // outro corredor não pode engoli-los também, senão os retângulos se sobrepõem.
                foreach (Section s in stack)
                {
                    for (int col = lo; col <= hi; col++)
                    {
                        int cell = At(grid, corridorAlongZ, s.Row, col);
                        if (col >= s.Start && col <= s.End)
                            pool[cell] = false;
                        else
                            swallowed[cell] = true;
                    }
                }

                int firstRow = stack[0].Row;
                int lastRow = stack[stack.Count - 1].Row;
                tiles.Add(corridorAlongZ
                    ? new Tile { X0 = lo, X1 = hi, Z0 = firstRow, Z1 = lastRow, Kind = TileKind.CorridorZ }
                    : new Tile { X0 = firstRow, X1 = lastRow, Z0 = lo, Z1 = hi, Kind = TileKind.CorridorX });
            }
        }

        // O retângulo lo..hi x linhas da pilha só pode conter as seções dela e OBSTÁCULO (célula
        // sem chão) ainda não engolido por outro corredor. Chão de outro dono, vermelho ou porta
        // ali dentro = os retângulos se sobreporiam.
        private static bool StackFits(TileGrid grid, bool[] swallowed, List<Section> stack, int lo, int hi, bool corridorAlongZ)
        {
            foreach (Section s in stack)
            {
                for (int col = lo; col <= hi; col++)
                {
                    if (col >= s.Start && col <= s.End)
                        continue;

                    int cell = At(grid, corridorAlongZ, s.Row, col);
                    if (grid.Floor[cell] || swallowed[cell] || grid.Door[cell] != -1)
                        return false;
                }
            }

            return true;
        }

        // ================================================================================
        // Salas
        // ================================================================================

        /// <summary>
        /// O que sobrou (salas, cruzamentos, recortes) vira retângulos pela gulosa do MAIOR
        /// retângulo livre: pega o maior, tira do mapa, repete até não sobrar chão.
        /// </summary>
        private void BuildRooms(TileGrid grid, bool[] pool, List<Tile> tiles)
        {
            var heights = new int[grid.SizeX];
            var stack = new int[grid.SizeX + 1];
            int guard = 0;

            while (LargestRectangle(grid.SizeX, grid.SizeZ, pool, heights, stack, out int x0, out int z0, out int x1, out int z1, out int area))
            {
                if ((++guard & 63) == 0)
                    UnityEditor.EditorUtility.DisplayProgressBar("Ladrilhar: salas", $"{tiles.Count} ladrilhos", 0.5f);

                ClearRect(grid, pool, x0, z0, x1, z1);
                tiles.Add(new Tile { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Kind = TileKind.Room });
            }
        }

        /// <summary>
        /// Maior retângulo de células verdadeiras da máscara (histograma por linha + pilha),
        /// O(células). Falso quando a máscara está vazia.
        /// </summary>
        private static bool LargestRectangle(int sizeX, int sizeZ, bool[] mask, int[] heights, int[] stack,
            out int bx0, out int bz0, out int bx1, out int bz1, out int best)
        {
            bx0 = bz0 = bx1 = bz1 = 0;
            best = 0;
            System.Array.Clear(heights, 0, heights.Length);

            for (int z = 0; z < sizeZ; z++)
            {
                int rowStart = z * sizeX;
                for (int x = 0; x < sizeX; x++)
                    heights[x] = mask[rowStart + x] ? heights[x] + 1 : 0;

                int top = 0;
                for (int x = 0; x <= sizeX; x++)
                {
                    int h = x < sizeX ? heights[x] : 0;
                    while (top > 0 && heights[stack[top - 1]] >= h)
                    {
                        int height = heights[stack[--top]];
                        int left = top > 0 ? stack[top - 1] + 1 : 0;
                        int area = height * (x - left);
                        if (area > best)
                        {
                            best = area;
                            bx0 = left;
                            bx1 = x - 1;
                            bz1 = z;
                            bz0 = z - height + 1;
                        }
                    }

                    if (x < sizeX)
                        stack[top++] = x;
                }
            }

            return best > 0;
        }

        private static void ClearRect(TileGrid grid, bool[] mask, int x0, int z0, int x1, int z1)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                    mask[grid.Index(x, z)] = false;
            }
        }

        // ================================================================================
        // Juntar, cortar, descartar
        // ================================================================================

        private static void PaintOwners(TileGrid grid, List<Tile> tiles, int[] owner)
        {
            for (int i = 0; i < owner.Length; i++)
                owner[i] = -1;

            for (int t = 0; t < tiles.Count; t++)
                Paint(grid, tiles[t], t, owner);
        }

        private static void Paint(TileGrid grid, Tile tile, int id, int[] owner)
        {
            for (int z = tile.Z0; z <= tile.Z1; z++)
            {
                for (int x = tile.X0; x <= tile.X1; x++)
                    owner[grid.Index(x, z)] = id;
            }
        }

        private static int CountWalkable(TileGrid grid, Tile tile)
        {
            int count = 0;
            for (int z = tile.Z0; z <= tile.Z1; z++)
            {
                for (int x = tile.X0; x <= tile.X1; x++)
                {
                    if (grid.Walkable[grid.Index(x, z)])
                        count++;
                }
            }

            return count;
        }

        private bool IsWeak(TileGrid grid, Tile tile) =>
            tile.Walkable == 0 || tile.Area * grid.CellArea < _minTileArea;

        /// <summary>
        /// Junta dois vizinhos cuja união é exatamente um retângulo (mesma extensão no lado
        /// comum). Antes do corte: mesmo tipo, ou um deles fraco (pequeno ou sem chão andável) —
        /// duas metades de sala viram uma sala, que o corte reparte em pedaços iguais. Depois do
        /// corte: só para salvar o fraco, e sem passar de 1.5 x o tamanho máximo. Porta nunca.
        /// </summary>
        private void MergeTiles(TileGrid grid, List<Tile> tiles, int[] owner, bool afterSplit)
        {
            foreach (Tile tile in tiles)
                tile.Walkable = CountWalkable(grid, tile);

            int maxCells = Mathf.Max(1, Mathf.FloorToInt(_maxTileSize / grid.Step));
            int limit = Mathf.Max(Mathf.CeilToInt(maxCells * 1.5f), Mathf.FloorToInt(_corridorMaxWidth / grid.Step));

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < tiles.Count; i++)
                {
                    Tile a = tiles[i];
                    if (a == null || a.Kind == TileKind.Door)
                        continue;

                    for (int side = 0; side < 4; side++)
                    {
                        int j = NeighborAcross(grid, a, side, owner);
                        if (j < 0 || j == i || tiles[j] == null || tiles[j].Kind == TileKind.Door)
                            continue;

                        Tile b = tiles[j];
                        if (!UnionIsRectangle(a, b))
                            continue;

                        bool weakA = IsWeak(grid, a);
                        bool weakB = IsWeak(grid, b);
                        bool allowed = afterSplit ? weakA || weakB : a.Kind == b.Kind || weakA || weakB;
                        if (!allowed)
                            continue;

                        int width = Mathf.Max(a.X1, b.X1) - Mathf.Min(a.X0, b.X0) + 1;
                        int depth = Mathf.Max(a.Z1, b.Z1) - Mathf.Min(a.Z0, b.Z0) + 1;
                        if (afterSplit && (width > limit || depth > limit))
                            continue;

                        if (weakA && !weakB)
                            a.Kind = b.Kind;

                        a.X0 = Mathf.Min(a.X0, b.X0);
                        a.X1 = Mathf.Max(a.X1, b.X1);
                        a.Z0 = Mathf.Min(a.Z0, b.Z0);
                        a.Z1 = Mathf.Max(a.Z1, b.Z1);
                        a.Walkable += b.Walkable;
                        tiles[j] = null;
                        Paint(grid, a, i, owner);
                        changed = true;
                        break;
                    }
                }
            }

            // Os índices do owner apontam para a lista com buracos; quem chama repinta.
            tiles.RemoveAll(t => t == null);
            PaintOwners(grid, tiles, owner);
        }

        // Dono da célula logo além do lado (0 = +X, 1 = -X, 2 = +Z, 3 = -Z), no canto de baixo.
        private static int NeighborAcross(TileGrid grid, Tile tile, int side, int[] owner)
        {
            int x = side == 0 ? tile.X1 + 1 : side == 1 ? tile.X0 - 1 : tile.X0;
            int z = side == 2 ? tile.Z1 + 1 : side == 3 ? tile.Z0 - 1 : tile.Z0;
            return grid.InBounds(x, z) ? owner[grid.Index(x, z)] : -1;
        }

        private static bool UnionIsRectangle(Tile a, Tile b)
        {
            if (a.X0 == b.X0 && a.X1 == b.X1)
                return a.Z1 + 1 == b.Z0 || b.Z1 + 1 == a.Z0;

            if (a.Z0 == b.Z0 && a.Z1 == b.Z1)
                return a.X1 + 1 == b.X0 || b.X1 + 1 == a.X0;

            return false;
        }

        /// <summary>
        /// Ladrilho com lado maior que _maxTileSize vira pedaços IGUAIS. Corredor só é cortado ao
        /// longo do comprimento — a largura é sempre de um ladrilho só (a fila). Porta nunca.
        /// </summary>
        private List<Tile> SplitTiles(TileGrid grid, List<Tile> tiles)
        {
            int maxCells = Mathf.Max(1, Mathf.FloorToInt(_maxTileSize / grid.Step));
            var result = new List<Tile>(tiles.Count * 2);

            foreach (Tile tile in tiles)
            {
                int nx = tile.Kind == TileKind.Door || tile.Kind == TileKind.CorridorZ ? 1 : Mathf.CeilToInt(tile.Width / (float)maxCells);
                int nz = tile.Kind == TileKind.Door || tile.Kind == TileKind.CorridorX ? 1 : Mathf.CeilToInt(tile.Depth / (float)maxCells);
                if (nx <= 1 && nz <= 1)
                {
                    result.Add(tile);
                    continue;
                }

                for (int i = 0; i < nx; i++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        Tile piece = tile.Copy();
                        piece.X0 = tile.X0 + tile.Width * i / nx;
                        piece.X1 = tile.X0 + tile.Width * (i + 1) / nx - 1;
                        piece.Z0 = tile.Z0 + tile.Depth * k / nz;
                        piece.Z1 = tile.Z0 + tile.Depth * (k + 1) / nz - 1;
                        result.Add(piece);
                    }
                }
            }

            return result;
        }

        // Ladrilho sem nenhuma célula andável (faixa de parede que o corte isolou num canto) não
        // vira nó: o centro do corpo nunca estaria nele. Devolve a área de chão descartada.
        private static float DropUnwalkable(TileGrid grid, List<Tile> tiles)
        {
            float dropped = 0f;
            tiles.RemoveAll(tile =>
            {
                if (tile.Kind == TileKind.Door || CountWalkable(grid, tile) > 0)
                    return false;

                for (int z = tile.Z0; z <= tile.Z1; z++)
                {
                    for (int x = tile.X0; x <= tile.X1; x++)
                    {
                        if (grid.Access[grid.Index(x, z)])
                            dropped += grid.CellArea;
                    }
                }

                return true;
            });

            return dropped;
        }

        // ================================================================================
        // Nós e ligações
        // ================================================================================

        /// <summary>
        /// O nó de cada ladrilho: a célula ANDÁVEL mais perto do centro do retângulo (o centro
        /// pode cair na faixa da parede, onde o corpo não chega). O da porta já vem do vão.
        /// </summary>
        private static void PlaceTileNodes(TileGrid grid, List<Tile> tiles)
        {
            foreach (Tile tile in tiles)
            {
                if (tile.Kind == TileKind.Door)
                    continue;

                Vector3 center = grid.Position(0, 0) + new Vector3((tile.X0 + tile.X1) * 0.5f * grid.Step, 0f, (tile.Z0 + tile.Z1) * 0.5f * grid.Step);
                tile.Position = NearestWalkable(grid, tile, center);
            }
        }

        private static Vector3 NearestWalkable(TileGrid grid, Tile tile, Vector3 target)
        {
            Vector3 best = target;
            float bestDistance = float.MaxValue;
            for (int z = tile.Z0; z <= tile.Z1; z++)
            {
                for (int x = tile.X0; x <= tile.X1; x++)
                {
                    if (!grid.Walkable[grid.Index(x, z)])
                        continue;

                    Vector3 position = grid.Position(x, z);
                    float distance = PlanarDistance(position, target);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = position;
                    }
                }
            }

            return best;
        }

        private static void CellRectToWorld(TileGrid grid, Tile tile)
        {
            tile.MinX = grid.Min.x + (tile.X0 - 0.5f) * grid.Step;
            tile.MaxX = grid.Min.x + (tile.X1 + 0.5f) * grid.Step;
            tile.MinZ = grid.Min.z + (tile.Z0 - 0.5f) * grid.Step;
            tile.MaxZ = grid.Min.z + (tile.Z1 + 0.5f) * grid.Step;
        }

        /// <summary>
        /// Retângulo no mundo: bordas das células; a porta tem o dela (exato). O lado de um
        /// ladrilho que encosta numa porta vai até a FACE da porta — a face da porta é a face da
        /// parede, então o lado inteiro fica certo, e o ladrilho não invade nem deixa fresta no vão.
        /// </summary>
        private static void ComputeWorldRects(TileGrid grid, List<Tile> tiles)
        {
            var doors = tiles.FindAll(t => t.Kind == TileKind.Door);
            float minSize = grid.Step * 0.25f;

            foreach (Tile tile in tiles)
            {
                if (tile.Kind == TileKind.Door)
                    continue;

                CellRectToWorld(grid, tile);

                foreach (Tile door in doors)
                {
                    bool overlapZ = tile.Z0 <= door.Z1 && door.Z0 <= tile.Z1;
                    bool overlapX = tile.X0 <= door.X1 && door.X0 <= tile.X1;

                    if (overlapZ && tile.X1 + 1 == door.X0 && door.MinX - tile.MinX > minSize)
                        tile.MaxX = door.MinX;
                    else if (overlapZ && door.X1 + 1 == tile.X0 && tile.MaxX - door.MaxX > minSize)
                        tile.MinX = door.MaxX;

                    if (overlapX && tile.Z1 + 1 == door.Z0 && door.MinZ - tile.MinZ > minSize)
                        tile.MaxZ = door.MinZ;
                    else if (overlapX && door.Z1 + 1 == tile.Z0 && tile.MaxZ - door.MaxZ > minSize)
                        tile.MinZ = door.MaxZ;
                }
            }
        }

        /// <summary>
        /// Vizinhos = ladrilhos entre os quais o CENTRO do corpo passa: duas células andáveis
        /// lado a lado (4-vizinhança), uma em cada ladrilho.
        /// </summary>
        private static HashSet<long> TileEdges(TileGrid grid, List<Tile> tiles, int[] owner)
        {
            var edges = new HashSet<long>();
            for (int z = 0; z < grid.SizeZ; z++)
            {
                for (int x = 0; x < grid.SizeX; x++)
                {
                    int cell = grid.Index(x, z);
                    if (!grid.Walkable[cell] || owner[cell] < 0)
                        continue;

                    if (x + 1 < grid.SizeX)
                        AddTileEdge(edges, grid, owner, cell, grid.Index(x + 1, z));
                    if (z + 1 < grid.SizeZ)
                        AddTileEdge(edges, grid, owner, cell, grid.Index(x, z + 1));
                }
            }

            return edges;
        }

        private static void AddTileEdge(HashSet<long> edges, TileGrid grid, int[] owner, int a, int b)
        {
            if (!grid.Walkable[b] || owner[b] < 0 || owner[a] == owner[b])
                return;

            edges.Add(Draft.Key(owner[a], owner[b]));
        }

        /// <summary>
        /// A ligação é a RETA entre os dois nós, e é ela que o corpo segue. Ladrilhos vizinhos
        /// podem ter a reta raspando numa quina (o nó da sala fica no meio dela e a porta está
        /// num canto). Três voltas: o nó de um ladrilho com ligação bloqueada procura, dentro do
        /// próprio ladrilho, o ponto andável que libera mais ligações (empate: o mais central) —
        /// na prática ele desliza para a frente da porta. A ligação que continua bloqueada sai se
        /// o grafo não parte sem ela; senão fica, e é devolvida para o relatório (X vermelho).
        /// </summary>
        private List<long> SettleTileEdges(TileGrid grid, List<Tile> tiles, HashSet<long> edges)
        {
            var neighbors = new List<int>[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
                neighbors[i] = new List<int>();

            foreach (long edge in edges)
            {
                Draft.Split(edge, out int a, out int b);
                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }

            for (int round = 0; round < 3; round++)
            {
                bool any = false;
                for (int t = 0; t < tiles.Count; t++)
                {
                    if (tiles[t].Kind == TileKind.Door)
                        continue;

                    int clear = ClearEdges(tiles, neighbors[t], tiles[t].Position);
                    if (clear == neighbors[t].Count)
                        continue;

                    any = true;
                    UnityEditor.EditorUtility.DisplayProgressBar("Ladrilhar: ligações", $"volta {round + 1}, ladrilho {t}", (float)t / tiles.Count);

                    Vector3 center = new Vector3((tiles[t].MinX + tiles[t].MaxX) * 0.5f, grid.Min.y, (tiles[t].MinZ + tiles[t].MaxZ) * 0.5f);
                    foreach (Vector3 candidate in Candidates(grid, tiles[t], center))
                    {
                        int score = ClearEdges(tiles, neighbors[t], candidate);
                        if (score > clear)
                        {
                            clear = score;
                            tiles[t].Position = candidate;
                            if (clear == neighbors[t].Count)
                                break;
                        }
                    }
                }

                if (!any)
                    break;
            }

            var kept = new List<long>();
            foreach (long edge in new List<long>(edges))
            {
                Draft.Split(edge, out int a, out int b);
                if (IsSegmentClearBothWays(tiles[a].Position, tiles[b].Position))
                    continue;

                edges.Remove(edge);
                if (TilesConnected(tiles.Count, edges, a, b))
                    continue;

                edges.Add(edge);
                kept.Add(edge);
            }

            return kept;
        }

        private int ClearEdges(List<Tile> tiles, List<int> neighbors, Vector3 from)
        {
            int clear = 0;
            foreach (int n in neighbors)
            {
                if (IsSegmentClearBothWays(from, tiles[n].Position))
                    clear++;
            }

            return clear;
        }

        // Até ~48 pontos andáveis do ladrilho, em grade espaçada, do mais central para fora.
        private static List<Vector3> Candidates(TileGrid grid, Tile tile, Vector3 center)
        {
            int walkable = CountWalkable(grid, tile);
            int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(walkable / 48f)));
            var candidates = new List<Vector3>();

            for (int z = tile.Z0; z <= tile.Z1; z += stride)
            {
                for (int x = tile.X0; x <= tile.X1; x += stride)
                {
                    if (grid.Walkable[grid.Index(x, z)])
                        candidates.Add(grid.Position(x, z));
                }
            }

            candidates.Sort((l, r) => PlanarDistance(l, center).CompareTo(PlanarDistance(r, center)));
            return candidates;
        }

        private static bool TilesConnected(int count, HashSet<long> edges, int from, int to)
        {
            var adjacency = new List<int>[count];
            for (int i = 0; i < count; i++)
                adjacency[i] = new List<int>();

            foreach (long edge in edges)
            {
                Draft.Split(edge, out int a, out int b);
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }

            var seen = new bool[count];
            var queue = new Queue<int>();
            seen[from] = true;
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (current == to)
                    return true;

                foreach (int next in adjacency[current])
                {
                    if (seen[next])
                        continue;

                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }

            return false;
        }

        // ================================================================================
        // Tipos herdados
        // ================================================================================

        // Os nós de exploração e ping que existem AGORA (antes de o ladrilhamento apagá-los).
        private List<(Vector3 position, NodeKind kind, float weight, string name)> MarkedNodes()
        {
            var marked = new List<(Vector3, NodeKind, float, string)>();
            foreach (NavNode node in NodesUnderGraph())
            {
                if (node.Kind != NodeKind.Auxiliary)
                    marked.Add((node.Position, node.Kind, node.ExplorationWeight, node.name));
            }

            return marked;
        }

        /// <summary>
        /// Cada nó marcado passa tipo e peso para o ladrilho que CONTÉM a posição dele (ou, fora
        /// de todos, para o nó de ladrilho mais perto a até 3 m). Dois marcados no mesmo
        /// ladrilho: fica o primeiro, e o Console avisa — o ladrilho ficou maior que a distância
        /// entre eles, e você decide qual vale.
        /// </summary>
        private int InheritKinds(List<Tile> tiles, List<(Vector3 position, NodeKind kind, float weight, string name)> marked)
        {
            int inherited = 0;
            foreach ((Vector3 position, NodeKind kind, float weight, string nodeName) in marked)
            {
                Tile target = null;
                foreach (Tile tile in tiles)
                {
                    if (position.x >= tile.MinX && position.x <= tile.MaxX && position.z >= tile.MinZ && position.z <= tile.MaxZ)
                    {
                        target = tile;
                        break;
                    }
                }

                if (target == null)
                {
                    float best = 3f;
                    foreach (Tile tile in tiles)
                    {
                        float distance = PlanarDistance(tile.Position, position);
                        if (distance < best)
                        {
                            best = distance;
                            target = tile;
                        }
                    }
                }

                if (target == null)
                {
                    Debug.LogWarning($"{name}: '{nodeName}' ({kind}) não caiu em ladrilho nenhum — o tipo dele se perdeu.", this);
                    continue;
                }

                if (target.Inherited)
                {
                    Debug.LogWarning(
                        $"{name}: '{nodeName}' ({kind}) caiu num ladrilho que já herdou outro nó marcado — ficou o primeiro. " +
                        "Marque o outro ladrilho à mão, ou diminua o Max Tile Size.", this);
                    continue;
                }

                target.NodeKind = kind;
                target.Weight = weight;
                target.Inherited = true;
                inherited++;
            }

            return inherited;
        }

        // ================================================================================
        // Cena
        // ================================================================================

        // Os nós da lista E os filhos do Graph que ainda não foram coletados: um nó solto que
        // sobrevivesse ao ladrilhamento seria coletado junto com os ladrilhos, por cima deles.
        private List<NavNode> NodesUnderGraph()
        {
            var nodes = new HashSet<NavNode>(ValidNodes());
            nodes.UnionWith(GetComponentsInChildren<NavNode>(includeInactive: true));
            return new List<NavNode>(nodes);
        }

        /// <summary>
        /// Apaga os nós e as áreas vermelhas antigas, cria um nó por ladrilho (filho do Graph),
        /// as ligações (declaradas de um lado só, como sempre) e as áreas vermelhas num objeto
        /// separado, e passa o NavGraph para a forma Retângulo. Um grupo de Undo (RunStep).
        /// </summary>
        private NavNode[] ApplyTiles(List<Tile> tiles, HashSet<long> edges, List<Tile> blocked)
        {
            foreach (NavNode old in NodesUnderGraph())
            {
                if (old != null)
                    UnityEditor.Undo.DestroyObjectImmediate(old.gameObject);
            }

            foreach (NavBlockedArea old in GetComponentsInChildren<NavBlockedArea>(includeInactive: true))
            {
                if (old != null)
                    UnityEditor.Undo.DestroyObjectImmediate(old.gameObject);
            }

            Transform oldContainer = transform.Find(BlockedContainerName);
            if (oldContainer != null)
                UnityEditor.Undo.DestroyObjectImmediate(oldContainer.gameObject);

            var nodes = new NavNode[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                string label = tile.Kind == TileKind.Door ? "porta" : tile.Kind == TileKind.Room ? "sala" : "corredor";
                var go = new GameObject($"Ladrilho {i:000} ({label})");
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Ladrilhar o chão");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = tile.Position;

                NavNode node = go.AddComponent<NavNode>();
                node.SetKind(tile.NodeKind);
                node.SetExplorationWeight(tile.Inherited ? tile.Weight : 1f);

                var center = new Vector2((tile.MinX + tile.MaxX) * 0.5f, (tile.MinZ + tile.MaxZ) * 0.5f);
                node.SetArea(center - new Vector2(tile.Position.x, tile.Position.z), new Vector2(tile.MaxX - tile.MinX, tile.MaxZ - tile.MinZ));
                nodes[i] = node;
            }

            foreach (long edge in edges)
            {
                Draft.Split(edge, out int a, out int b);
                nodes[Mathf.Min(a, b)].EditableNeighbors.Add(nodes[Mathf.Max(a, b)]);
            }

            foreach (NavNode node in nodes)
                UnityEditor.EditorUtility.SetDirty(node);

            if (blocked.Count > 0)
            {
                var container = new GameObject(BlockedContainerName);
                UnityEditor.Undo.RegisterCreatedObjectUndo(container, "Ladrilhar o chão");
                container.transform.SetParent(transform, worldPositionStays: false);

                float y = tiles[0].Position.y;
                for (int i = 0; i < blocked.Count; i++)
                {
                    Tile rect = blocked[i];
                    var go = new GameObject($"Bloqueado {i:000}");
                    go.transform.SetParent(container.transform, worldPositionStays: false);
                    go.transform.position = new Vector3((rect.MinX + rect.MaxX) * 0.5f, y, (rect.MinZ + rect.MaxZ) * 0.5f);
                    go.AddComponent<NavBlockedArea>().SetSize(new Vector2(rect.MaxX - rect.MinX, rect.MaxZ - rect.MinZ));
                }
            }

            UnityEditor.Undo.RecordObject(Graph, "Ladrilhar o chão");
            Graph.SetShape(NodeShape.Rectangle);
            MarkModified(Graph);
            Graph.CollectChildNodes();
            Physics.SyncTransforms();
            return nodes;
        }

        private void ReportTiling(TileGrid grid, List<Tile> tiles, HashSet<long> edges, List<Tile> blocked, int[] owner,
            float droppedArea, int keptBlocked, int inherited, int marked)
        {
            int doors = 0;
            int corridors = 0;
            int rooms = 0;
            int small = 0;
            foreach (Tile tile in tiles)
            {
                if (tile.Kind == TileKind.Door)
                    doors++;
                else if (tile.Kind == TileKind.Room)
                    rooms++;
                else
                    corridors++;

                if (tile.Kind != TileKind.Door && tile.Area * grid.CellArea < _minTileArea)
                    small++;
            }

            var degree = new int[tiles.Count];
            foreach (long edge in edges)
            {
                Draft.Split(edge, out int a, out int b);
                degree[a]++;
                degree[b]++;
            }

            int maxDegree = 0;
            int overDegree = 0;
            int isolated = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                maxDegree = Mathf.Max(maxDegree, degree[i]);
                if (degree[i] > _maxDegree)
                    overDegree++;
                if (degree[i] == 0)
                    isolated++;
            }

            int walkable = 0;
            int covered = 0;
            for (int i = 0; i < grid.Count; i++)
            {
                if (!grid.Walkable[i])
                    continue;

                walkable++;
                if (owner[i] >= 0)
                    covered++;
            }

            float blockedArea = 0f;
            foreach (Tile rect in blocked)
                blockedArea += (rect.MaxX - rect.MinX) * (rect.MaxZ - rect.MinZ);

            Debug.Log(
                $"{name}: LADRILHADO — {tiles.Count} nós ({rooms} de sala, {corridors} de corredor em filas, {doors} de porta), " +
                $"{edges.Count} ligações, grau máx. {maxDegree}. Chão andável dentro de um ladrilho: " +
                $"{(walkable > 0 ? (float)covered / walkable : 0f):P1}. {blocked.Count} área(s) VERMELHA(S) ({blockedArea:0.#} m²) " +
                $"onde o corpo não cabe. {droppedArea:0.#} m² de faixa de parede sem ladrilho (o centro do corpo não chega lá). " +
                $"{small} ladrilho(s) menor(es) que {_minTileArea:0.##} m².", this);

            if (overDegree > 0)
            {
                Debug.LogWarning(
                    $"{name}: {overDegree} nó(s) com mais de {_maxDegree} vizinhos — o que passar dos slots da observação " +
                    "é cortado. Diminua o Max Tile Size (ladrilhos longos encostam em muitos).", this);
            }

            if (keptBlocked > 0)
            {
                Debug.LogWarning(
                    $"{name}: {keptBlocked} ligação(ões) cuja reta o corpo não atravessa, mas que são a única passagem entre " +
                    "dois lados (X vermelho). Mova um dos nós à mão, dentro do ladrilho dele.", this);
            }

            if (isolated > 0)
                Debug.LogWarning($"{name}: {isolated} ladrilho(s) sem ligação nenhuma.", this);

            if (marked > 0 || inherited > 0)
                Debug.Log($"{name}: {inherited} de {marked} nó(s) de exploração/ping passaram o tipo para o ladrilho.", this);

            if (inherited == 0)
            {
                Debug.LogWarning(
                    $"{name}: todos os nós são AUXILIARES. Antes de treinar, marque quais ladrilhos são de Exploração " +
                    "(pagam ao descobrir) e de Ping (podem tocar), e ajuste a pontuação de cada tipo no NavGraph.", this);
            }
        }
#endif
    }
}
