using System.Collections.Generic;
using UnityEngine;

// Os campos só são lidos pelos menus de editor; no build do jogo ficam sem uso (e sem custo).
#pragma warning disable CS0414

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// COBERTURA DO CHÃO (menus 4 e 7, nós em disco/quadrado): todo ponto andável tem que cair na área do nó que o
    /// FindNodeAt escolheria ali e alcançar o centro dele sem sair da área. Cada célula do mapa do chão é OK, NÓ ERRADO
    /// (área vazada por parede/móvel) ou SEM NÓ; o placer ajusta raios e cria auxiliares nos buracos até a meta.
    /// Quando as regras de raio brigam, a saída é mais nós, nunca relaxar a regra.
    /// </summary>
    public partial class NavGraphPlacer
    {
        [Header("-----Cobertura do chão-----")]
        // Meta: fração do chão andável no estado OK. É onde a gulosa para e o teto de gasto da limpeza (RemoveRedundant).
        // 97% e não 98%: o último 1% custa ~10 nós de 2-3 m² cada, e o resto fica raso por causa de _maxHoleDepth.
        [SerializeField, Range(0.5f, 1f)] private float _coverageTarget = 0.97f;

        // Profundidade máxima (m) do chão ruim (sem nó ou no nó errado): distância andando até o chão OK mais perto.
        // 1 m ~ 10 steps de física a 5 m/s.
        [SerializeField, Min(0f)] private float _maxHoleDepth = 1f;

        // Espaçamento (m) da malha de candidatos a auxiliar em volta de cada buraco; o custo da busca cai com o quadrado dele.
        [SerializeField, Min(0.1f)] private float _candidateStep = 0.6f;

        // Passo (m) entre os raios testados num auxiliar, do teto ao piso (piso e padrão entram sempre); cada raio custa
        // um flood fill por candidato.
        [SerializeField, Min(0.05f)] private float _auxiliaryRadiusStep = 1f;

        // Trava de segurança, não limite de projeto: bater nisso indica mapa errado (ex.: Wall Layer faltando).
        [SerializeField, Min(1)] private int _maxNewNodes = 600;

        // Distância mínima (m, no plano) entre um nó NOVO, de qualquer origem, e qualquer nó; quem nasceria mais perto
        // reaproveita o que já está ali. 3 m = a menor aresta do grafo feito à mão.
        [SerializeField, Min(0f)] private float _minNodeSpacing = 3f;

        // Ganho mínimo (m² de chão que passa a OK) para a gulosa criar um auxiliar; o chão FUNDO (além de
        // _maxHoleDepth) é fechado de qualquer jeito, sem esse piso.
        [SerializeField, Min(0f)] private float _minNodeGain = 4f;

        // Custo, por célula, de tomar chão que já era OK em outro nó (descobrir célula nova vale 1); faz o raio parar
        // onde começa a área do vizinho.
        [SerializeField, Range(0f, 1f)] private float _overlapPenalty = 0.15f;

        // Relatório da última execução (só nesta sessão do editor), desenhado no gizmo.
        private readonly List<Vector3> _uncoveredCells = new List<Vector3>();
        private readonly List<Vector3> _wrongCells = new List<Vector3>();
        private readonly List<Vector3> _blindPoints = new List<Vector3>();
        private float _reportCellSize = 0.3f;

        // Marrom (cor livre nos gizmos): cheio = chão SEM nó; contorno = chão no NÓ ERRADO; esfera = ponto sem nó à vista.
        private static readonly Color UncoveredColor = new Color(0.55f, 0.33f, 0.12f, 0.8f);

        private float SpawnClearance => Graph.SpawnClearance;

        private void ClearCoverageReport()
        {
            _uncoveredCells.Clear();
            _wrongCells.Clear();
            _blindPoints.Clear();
        }

#if UNITY_EDITOR
        // ================================================================================
        // Menus
        // ================================================================================

        /// <summary>Ajusta raios e cobre com auxiliares o chão sem nó ou no nó errado. Cria nós: só no Prefab Mode.</summary>
        [ContextMenu("4. Ajustar raios e cobrir o chão")]
        private void FitRadiiAndCover()
        {
            if (!CanRemoveNodes())
                return;

            RunStep("Ajustar raios e cobrir o chão", () =>
            {
                Draft draft = DraftFromScene();
                WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
                if (grid == null)
                    return;

                CoverFloor(draft, grid);
                ApplyDraft(draft);
                ReportCoverage(grid, BuildCoverage(draft, grid, null));
                ReportDoors(draft);
            });
        }

        /// <summary>Loga as salas do NavGraph (nós e portas de cada uma) e avisa porta que não liga duas salas.</summary>
        [ContextMenu("11. Relatório de salas e portas")]
        private void ReportRooms()
        {
            if (Graph == null)
            {
                Debug.LogWarning($"{name}: sem NavGraph neste objeto.", this);
                return;
            }

            Graph.LogRoomReport();
        }

        // ================================================================================
        // Pipeline
        // ================================================================================

        // Menu 4: nós de porta -> raios -> auxiliares nos buracos -> ligação por região, em até 3 voltas (repete só se criou
        // nó); depois limpeza até a meta, portas ligadas dos dois lados e poda de ligações redundantes.
        private void CoverFloor(Draft draft, WalkGrid grid)
        {
            AddDoorNodes(draft, grid);
            FitRadiiDraft(draft, grid, null);
            CoverageMap map = null;

            for (int round = 0; round < 3; round++)
            {
                map = BuildCoverage(draft, grid, map);
                int filled = FillCoverage(draft, grid, map);

                int beforeLink = draft.Count;
                LinkRegions(draft, grid);
                List<int> portals = NewIndices(draft, beforeLink);
                if (portals.Count > 0)
                    FitRadiiDraft(draft, grid, portals);

                if (filled == 0 && portals.Count == 0)
                    break;
            }

            // Última passada: tira o auxiliar que ficou sobrando sem piorar a cobertura nem partir o grafo.
            int removed = RemoveRedundant(draft, grid);
            if (removed > 0)
                Debug.Log($"{name}: {removed} auxiliar(es) que sobraram depois da cobertura foram removidos.", this);

            // A limpeza pode ter tirado o nó do outro lado de uma porta: religa só ligações (e curvas de caminho pelo chão).
            int beforeDoors = draft.Count;
            LinkDoors(draft, grid, new List<long>());
            FitRadiiDraft(draft, grid, NewIndices(draft, beforeDoors));
            PruneRedundantEdges(draft);
        }

        // ================================================================================
        // Mapa de cobertura
        // ================================================================================

        private enum CellState : byte
        {
            Blocked,
            Ok,
            Wrong,
            Uncovered,
        }

        /// <summary>Estado de cada célula do chão e, por nó: células da área, alcançadas por dentro e vencidas.</summary>
        /// </summary>
        private sealed class CoverageMap
        {
            public readonly int[] Winner;
            public readonly float[] WinnerDistance;
            public readonly CellState[] State;

            // Carimbo do flood fill (evita limpar a cada busca) e a fila dele; depois de FloodArea, Buffer[0..n) = alcançadas.
            public readonly int[] Marks;
            public readonly int[] Buffer;
            public int Stamp;

            public readonly List<int> AreaCells = new List<int>();
            public readonly List<int> Reached = new List<int>();
            public readonly List<int> Owned = new List<int>();

            // Rascunho da avaliação de candidato: células tomadas de cada primário.
            public int[] Taken = new int[0];
            public readonly List<int> Touched = new List<int>();

            public int Walkable;
            public int Ok;
            public int Wrong;
            public int Uncovered;

            public CoverageMap(int cells)
            {
                Winner = new int[cells];
                WinnerDistance = new float[cells];
                State = new CellState[cells];
                Marks = new int[cells];
                Buffer = new int[cells];
            }

            public void EnsureNode(int node)
            {
                while (AreaCells.Count <= node)
                {
                    AreaCells.Add(0);
                    Reached.Add(0);
                    Owned.Add(0);
                }

                if (Taken.Length <= node)
                    System.Array.Resize(ref Taken, Mathf.Max(node + 1, Taken.Length * 2));
            }

            public void Count(CellState state, int delta)
            {
                if (state == CellState.Ok)
                    Ok += delta;
                else if (state == CellState.Wrong)
                    Wrong += delta;
                else if (state == CellState.Uncovered)
                    Uncovered += delta;
            }
        }

        private static int StateValue(CellState state) =>
            state == CellState.Ok ? 1 : state == CellState.Wrong ? -1 : 0;

        /// <summary>Monta (ou remonta em <paramref name="map"/>) a cobertura do rascunho atual.</summary>
        private CoverageMap BuildCoverage(Draft draft, WalkGrid grid, CoverageMap map)
        {
            if (map == null)
                map = new CoverageMap(grid.Count);

            for (int cell = 0; cell < grid.Count; cell++)
            {
                map.Winner[cell] = -1;
                map.WinnerDistance[cell] = float.MaxValue;
                map.State[cell] = CellState.Blocked;
            }

            map.EnsureNode(draft.Count - 1);
            for (int i = 0; i < map.AreaCells.Count; i++)
            {
                map.AreaCells[i] = 0;
                map.Reached[i] = 0;
                map.Owned[i] = 0;
            }

            // Vencedor da célula: mesma regra do NavGraph.FindNodeAt (no raio e visível do centro; vence o centro mais perto).
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                Vector3 center = draft.Positions[i];
                float radius = draft.Radii[i];
                bool[] visible = AreaVisibility(grid, center, out int origin);
                AreaRange(grid, center, radius, out int x0, out int x1, out int z0, out int z1);

                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = grid.Index(x, z);
                        if (!grid.Walkable[cell])
                            continue;

                        float distance = Graph.AreaDistance(center, grid.Position(cell));
                        if (distance > radius || !grid.Sees(visible, origin, cell))
                            continue;

                        map.AreaCells[i]++;
                        if (distance < map.WinnerDistance[cell])
                        {
                            map.WinnerDistance[cell] = distance;
                            map.Winner[cell] = i;
                        }
                    }
                }
            }

            // Alcance por dentro da área: o que o nó vence E alcança é OK.
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                int reached = FloodArea(grid, map, draft.Positions[i], draft.Radii[i]);
                map.Reached[i] = reached;
                for (int k = 0; k < reached; k++)
                {
                    int cell = map.Buffer[k];
                    if (map.Winner[cell] == i)
                        map.State[cell] = CellState.Ok;
                }
            }

            map.Walkable = 0;
            map.Ok = 0;
            map.Wrong = 0;
            map.Uncovered = 0;

            for (int cell = 0; cell < grid.Count; cell++)
            {
                if (!grid.Walkable[cell])
                    continue;

                map.Walkable++;
                if (map.Winner[cell] < 0)
                    map.State[cell] = CellState.Uncovered;
                else if (map.State[cell] != CellState.Ok)
                    map.State[cell] = CellState.Wrong;

                if (map.Winner[cell] >= 0)
                    map.Owned[map.Winner[cell]]++;

                map.Count(map.State[cell], 1);
            }

            return map;
        }

        // Célula de origem do nó na grade e o que se vê dela (null sem corte por parede).
        private static bool[] AreaVisibility(WalkGrid grid, Vector3 center, out int origin)
        {
            origin = grid.Snap(center, grid.Step * 1.5f);
            return origin >= 0 ? grid.VisibilityFrom(origin) : null;
        }

        private static void AreaRange(WalkGrid grid, Vector3 center, float radius, out int x0, out int x1, out int z0, out int z1)
        {
            x0 = Mathf.Max(0, Mathf.FloorToInt((center.x - radius - grid.Min.x) / grid.Step));
            x1 = Mathf.Min(grid.SizeX - 1, Mathf.CeilToInt((center.x + radius - grid.Min.x) / grid.Step));
            z0 = Mathf.Max(0, Mathf.FloorToInt((center.z - radius - grid.Min.z) / grid.Step));
            z1 = Mathf.Min(grid.SizeZ - 1, Mathf.CeilToInt((center.z + radius - grid.Min.z) / grid.Step));
        }

        /// <summary>Flood fill do centro por células andáveis da área; alcançadas ficam em Buffer[0..n), n devolvido.</summary>
        private int FloodArea(WalkGrid grid, CoverageMap map, Vector3 center, float radius)
        {
            int stamp = ++map.Stamp;
            bool[] visible = AreaVisibility(grid, center, out int start);
            if (start < 0 || Graph.AreaDistance(center, grid.Position(start)) > radius)
                return 0;

            int head = 0;
            int tail = 0;
            map.Buffer[tail++] = start;
            map.Marks[start] = stamp;

            while (head < tail)
            {
                int cell = map.Buffer[head++];
                int x = grid.X(cell);
                int z = grid.Z(cell);

                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (map.Marks[next] == stamp || !grid.Walkable[next])
                        continue;

                    if (Graph.AreaDistance(center, grid.Position(next)) > radius || !grid.Sees(visible, start, next))
                        continue;

                    map.Marks[next] = stamp;
                    map.Buffer[tail++] = next;
                }
            }

            return tail;
        }

        private static float LeakOf(CoverageMap map, int node) =>
            map.AreaCells[node] > 0 ? 1f - (float)map.Reached[node] / map.AreaCells[node] : 0f;

        private static float OwnershipOf(CoverageMap map, int node) =>
            map.AreaCells[node] > 0 ? (float)map.Owned[node] / map.AreaCells[node] : 1f;

        // ================================================================================
        // Candidato a auxiliar
        // ================================================================================

        // Ganho de um auxiliar no mapa atual: +1 por célula que vira OK, -1 por nó errado (estado anterior descontado).
        // Inválido se eclipsa nó de porta (< meio raio), vaza > _maxLeakAuxiliary, tira posse de primário ou não resolve seed.
        private bool TryEvaluate(Draft draft, WalkGrid grid, CoverageMap map, Vector3 position, float radius, int seed, out float score)
        {
            score = 0f;

            if (seed >= 0 && Graph.AreaDistance(position, grid.Position(seed)) >= map.WinnerDistance[seed])
                return false;

            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && draft.Primary[i] && Graph.AreaDistance(position, draft.Positions[i]) < draft.Radii[i] * 0.5f)
                    return false;
            }

            int reached = FloodArea(grid, map, position, radius);
            int stamp = map.Stamp;
            if (reached == 0 || (seed >= 0 && map.Marks[seed] != stamp))
                return false;

            bool[] visible = AreaVisibility(grid, position, out int origin);
            AreaRange(grid, position, radius, out int x0, out int x1, out int z0, out int z1);
            int area = 0;
            float gain = 0f;
            map.Touched.Clear();

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = grid.Index(x, z);
                    if (!grid.Walkable[cell])
                        continue;

                    float distance = Graph.AreaDistance(position, grid.Position(cell));
                    if (distance > radius || !grid.Sees(visible, origin, cell))
                        continue;

                    area++;
                    if (distance >= map.WinnerDistance[cell])
                        continue;

                    CellState after = map.Marks[cell] == stamp ? CellState.Ok : CellState.Wrong;
                    gain += StateValue(after) - StateValue(map.State[cell]);
                    if (map.State[cell] == CellState.Ok)
                        gain -= _overlapPenalty;

                    int owner = map.Winner[cell];
                    if (owner >= 0 && draft.Primary[owner])
                    {
                        if (map.Taken[owner] == 0)
                            map.Touched.Add(owner);
                        map.Taken[owner]++;
                    }
                }
            }

            bool valid = area > 0 && 1f - (float)reached / area <= _maxLeakAuxiliary;
            foreach (int primary in map.Touched)
            {
                if (map.Owned[primary] - map.Taken[primary] < _minPrimaryOwnership * map.AreaCells[primary])
                    valid = false;

                map.Taken[primary] = 0;
            }

            score = gain;
            return valid;
        }

        /// <summary>Grava o auxiliar <paramref name="node"/> (já no rascunho) no mapa, sem remontar tudo.</summary>
        private void ApplyToCoverage(Draft draft, WalkGrid grid, CoverageMap map, int node)
        {
            map.EnsureNode(node);
            Vector3 position = draft.Positions[node];
            float radius = draft.Radii[node];

            int reached = FloodArea(grid, map, position, radius);
            int stamp = map.Stamp;
            map.Reached[node] = reached;
            map.AreaCells[node] = 0;
            map.Owned[node] = 0;

            bool[] visible = AreaVisibility(grid, position, out int origin);
            AreaRange(grid, position, radius, out int x0, out int x1, out int z0, out int z1);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = grid.Index(x, z);
                    if (!grid.Walkable[cell])
                        continue;

                    float distance = Graph.AreaDistance(position, grid.Position(cell));
                    if (distance > radius || !grid.Sees(visible, origin, cell))
                        continue;

                    map.AreaCells[node]++;
                    if (distance >= map.WinnerDistance[cell])
                        continue;

                    if (map.Winner[cell] >= 0)
                        map.Owned[map.Winner[cell]]--;

                    map.Winner[cell] = node;
                    map.WinnerDistance[cell] = distance;
                    map.Owned[node]++;

                    CellState after = map.Marks[cell] == stamp ? CellState.Ok : CellState.Wrong;
                    map.Count(map.State[cell], -1);
                    map.Count(after, 1);
                    map.State[cell] = after;
                }
            }
        }

        // Raios candidatos, do maior ao menor. O piso e o padrão do papel entram sempre (corredor estreito só cabe no mínimo).
        private List<float> AuxiliaryRadii()
        {
            var radii = new List<float>();
            for (float r = _maxAuxiliaryRadius; r >= _minAuxiliaryRadius - 1e-3f; r -= _auxiliaryRadiusStep)
                radii.Add(r);

            if (!radii.Exists(r => Mathf.Abs(r - _minAuxiliaryRadius) < 0.01f))
                radii.Add(_minAuxiliaryRadius);

            float fallback = Graph.DefaultAuxiliaryRadius;
            if (fallback >= _minAuxiliaryRadius && fallback <= _maxAuxiliaryRadius && !radii.Exists(r => Mathf.Abs(r - fallback) < 0.01f))
                radii.Add(fallback);

            radii.Sort((a, b) => b.CompareTo(a));
            return radii;
        }

        // ================================================================================
        // Raios
        // ================================================================================

        // Raio de cada nó (de only, ou todos): primários (nós de porta) apertados, até metade da distância ao vizinho primário;
        // auxiliares, o que mais cobre sem vazar nem roubar posse. Encolher pode abrir buraco, que FillCoverage fecha.
        private void FitRadiiDraft(Draft draft, WalkGrid grid, List<int> only)
        {
            if (only != null && only.Count == 0)
                return;

            var set = only != null ? new HashSet<int>(only) : null;
            CoverageMap map = new CoverageMap(grid.Count);
            int total = 0;
            int done = 0;
            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && (set == null || set.Contains(i)))
                    total++;
            }

            var primaryOverrides = new List<float>();
            var auxiliaryOverrides = new List<float>();
            int primaryCount = 0;
            int auxiliaryCount = 0;
            List<float> radii = AuxiliaryRadii();

            for (int pass = 0; pass < 2; pass++)
            {
                bool primaryPass = pass == 0;

                for (int i = 0; i < draft.Count; i++)
                {
                    if (!draft.Alive(i) || draft.Primary[i] != primaryPass || (set != null && !set.Contains(i)))
                        continue;

                    UnityEditor.EditorUtility.DisplayProgressBar("Ajustando raios", $"{done}/{total}", (float)done++ / Mathf.Max(1, total));

                    if (primaryPass)
                    {
                        primaryCount++;
                        draft.Radii[i] = FitPrimaryRadius(draft, grid, map, i);
                    }
                    else
                    {
                        auxiliaryCount++;
                        draft.Radii[i] = FitAuxiliaryRadius(draft, grid, map, i, radii);
                    }

                    float fallback = primaryPass ? Graph.DefaultPrimaryRadius : Graph.DefaultAuxiliaryRadius;
                    if (Mathf.Abs(draft.Radii[i] - fallback) >= 0.05f)
                        (primaryPass ? primaryOverrides : auxiliaryOverrides).Add(draft.Radii[i]);
                }
            }

            if (only == null)
            {
                SuggestDefault("primários", primaryOverrides, primaryCount, Graph.DefaultPrimaryRadius);
                SuggestDefault("auxiliares", auxiliaryOverrides, auxiliaryCount, Graph.DefaultAuxiliaryRadius);
                Debug.Log(
                    $"{name}: raios ajustados — {primaryOverrides.Count} primário(s) e {auxiliaryOverrides.Count} " +
                    "auxiliar(es) fora do padrão do papel.", this);
            }
        }

        private float FitPrimaryRadius(Draft draft, WalkGrid grid, CoverageMap map, int node)
        {
            Vector3 center = draft.Positions[node];
            float radius = Graph.DefaultPrimaryRadius;
            int closest = -1;

            for (int i = 0; i < draft.Count; i++)
            {
                if (i == node || !draft.Alive(i) || !draft.Primary[i])
                    continue;

                float half = Graph.AreaDistance(center, draft.Positions[i]) * 0.5f;
                if (half < radius)
                {
                    radius = half;
                    closest = i;
                }
            }

            if (radius < _minPrimaryRadius)
            {
                Debug.LogWarning(
                    $"{NodeLabel(draft, node)} e {NodeLabel(draft, closest)}: primários a {radius * 2f:0.0} m — nem no " +
                    $"raio mínimo ({_minPrimaryRadius}) as áreas deixam de se sobrepor. Junte os dois num primário só " +
                    "(somando o peso) ou afaste-os.", NodeContext(draft, node));
                radius = _minPrimaryRadius;
            }

            float leak = LeakAt(grid, map, center, radius);
            while (leak > _maxLeakPrimary && radius - _radiusStep >= _minPrimaryRadius)
            {
                radius -= _radiusStep;
                leak = LeakAt(grid, map, center, radius);
            }

            if (leak > _maxLeakPrimary)
            {
                Debug.LogWarning(
                    $"{NodeLabel(draft, node)}: mesmo com raio {radius:0.0} a área vaza {leak:P0}. Afaste o nó da " +
                    "parede/móvel.", NodeContext(draft, node));
            }

            return radius;
        }

        private float FitAuxiliaryRadius(Draft draft, WalkGrid grid, CoverageMap map, int node, List<float> radii)
        {
            // O mapa SEM este nó: o raio certo é o que cobre o que só ele cobre.
            draft.Remove(node);
            BuildCoverage(draft, grid, map);
            draft.Restore(node);

            Vector3 position = draft.Positions[node];
            float fallback = Graph.DefaultAuxiliaryRadius;
            float bestRadius = -1f;
            float bestScore = float.MinValue;

            foreach (float radius in radii)
            {
                if (!TryEvaluate(draft, grid, map, position, radius, -1, out float score))
                    continue;

                bool better = score > bestScore + 0.5f
                              || (score > bestScore - 0.5f && Mathf.Abs(radius - fallback) < Mathf.Abs(bestRadius - fallback));
                if (better)
                {
                    bestScore = score;
                    bestRadius = radius;
                }
            }

            if (bestRadius > 0f)
                return bestRadius;

            // Nenhum raio respeita as regras (perto de primário ou encostado em parede): fica no piso e o Console avisa.
            Debug.LogWarning(
                $"{NodeLabel(draft, node)}: nenhum raio entre {_minAuxiliaryRadius} e {_maxAuxiliaryRadius} evita vazar " +
                $"mais de {_maxLeakAuxiliary:P0} ou roubar área de primário — ficou no mínimo. Afaste-o do primário/parede " +
                "ou rode \"6. Remover nós inúteis\".", NodeContext(draft, node));
            return _minAuxiliaryRadius;
        }

        private float LeakAt(WalkGrid grid, CoverageMap map, Vector3 center, float radius)
        {
            int reached = FloodArea(grid, map, center, radius);
            bool[] visible = AreaVisibility(grid, center, out int origin);
            AreaRange(grid, center, radius, out int x0, out int x1, out int z0, out int z1);
            int area = 0;
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = grid.Index(x, z);
                    if (grid.Walkable[cell] && Graph.AreaDistance(center, grid.Position(cell)) <= radius
                        && grid.Sees(visible, origin, cell))
                        area++;
                }
            }

            return area == 0 ? 0f : 1f - (float)reached / area;
        }

        private void SuggestDefault(string role, List<float> overrides, int total, float current)
        {
            if (total == 0 || overrides.Count * 2 <= total)
                return;

            overrides.Sort();
            float median = overrides[overrides.Count / 2];
            Debug.LogWarning(
                $"{name}: {overrides.Count}/{total} {role} ficaram fora do padrão (mediana {median:0.00}, padrão " +
                $"{current:0.00}). Considere mudar o padrão no NavGraph e rodar \"4. Ajustar raios\" de novo.", this);
        }

        // ================================================================================
        // Auxiliares nos buracos
        // ================================================================================

        // Cria auxiliares até a meta (CoverageMeets). Fase 1: o de maior ganho (gulosa preguiçosa, >= _minNodeGain); fase 2:
        // fecha célula a célula o chão mais fundo que _maxHoleDepth. O que nenhum candidato resolve vai ao relatório (marrom).
        private int FillCoverage(Draft draft, WalkGrid grid, CoverageMap map)
        {
            List<float> radii = AuxiliaryRadii();
            float minGain = _minNodeGain / (grid.Step * grid.Step);
            int stride = Mathf.Max(1, Mathf.RoundToInt(_candidateStep / grid.Step));
            int added = 0;
            bool cancelled = false;

            var crowded = new bool[grid.Count];
            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i))
                    MarkCrowded(grid, crowded, draft.Positions[i]);
            }

            // ---- Fase 1: ganho máximo ----
            var heap = new CellHeap();
            if (!CoverageMeets(grid, map, out _))
            {
                // Pula candidato sem chão ruim bastante no quadrado do maior raio (soma de prefixos), sem pagar a avaliação.
                int[] bad = BadPrefix(grid, map);
                int reach = Mathf.CeilToInt(_maxAuxiliaryRadius / grid.Step);

                for (int z = 0; z < grid.SizeZ && !cancelled; z += stride)
                {
                    cancelled = UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                        "Cobrindo o chão", $"avaliando candidatos, linha {z}/{grid.SizeZ}", (float)z / grid.SizeZ);

                    for (int x = 0; x < grid.SizeX && !cancelled; x += stride)
                    {
                        int cell = grid.Index(x, z);
                        if (!grid.Walkable[cell] || crowded[cell] || BadInSquare(grid, bad, x, z, reach) < minGain)
                            continue;

                        if (BestRadiusAt(draft, grid, map, cell, radii, out _, out float score) && score >= minGain)
                            heap.Push(cell, -score);
                    }
                }
            }

            int iterations = 0;
            int candidates = heap.Count;
            while (!cancelled && heap.Count > 0)
            {
                if (added >= _maxNewNodes)
                {
                    Debug.LogError(
                        $"{name}: parou em {_maxNewNodes} auxiliares novos. Mapa sem Wall Layer certo ou arena " +
                        "\"vazando\" para fora do prédio? Confira e rode de novo.", this);
                    break;
                }

                float okFraction = (float)map.Ok / Mathf.Max(1, map.Walkable);
                if ((iterations++ & 7) == 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                        "Cobrindo o chão", $"{added} auxiliar(es) novo(s), {okFraction:P1} do chão OK", okFraction))
                {
                    cancelled = true;
                    break;
                }

                if (CoverageMeets(grid, map, out _))
                    break;

                heap.Pop(out int cell, out float _);
                if (crowded[cell])
                    continue;

                if (!BestRadiusAt(draft, grid, map, cell, radii, out float radius, out float score) || score < minGain)
                    continue;

                // Reavaliado, ficou atrás de outro candidato: volta para a fila.
                if (heap.Count > 0 && score < -heap.PeekCost)
                {
                    heap.Push(cell, -score);
                    continue;
                }

                AddAuxiliary(draft, grid, map, crowded, cell, radius);
                added++;
            }

            int byGain = added;

            // ---- Fase 2: chão fundo ----
            int unresolved = 0;
            var skip = new bool[grid.Count];
            while (!cancelled && added < _maxNewNodes)
            {
                float[] depth = BadDepth(grid, map, out float deepest);
                if (deepest <= _maxHoleDepth + 1e-3f)
                    break;

                int seed = -1;
                for (int cell = 0; cell < grid.Count; cell++)
                {
                    if (depth[cell] > _maxHoleDepth + 1e-3f && !skip[cell]
                        && (seed < 0 || grid.Clearance[cell] < grid.Clearance[seed]))
                        seed = cell;
                }

                if (seed < 0)
                    break;

                if (UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                        "Cobrindo o chão", $"chão fundo: {added - byGain} auxiliar(es), maior profundidade {deepest:0.0} m", 1f))
                {
                    cancelled = true;
                    break;
                }

                if (BestCandidateFor(draft, grid, map, crowded, seed, radii, out int bestCell, out float bestRadius))
                {
                    AddAuxiliary(draft, grid, map, crowded, bestCell, bestRadius);
                    added++;
                    continue;
                }

                // Sem solução: pula a célula e o entorno (mesma malha de candidatos) para não repetir a busca inteira.
                unresolved++;
                SkipAround(grid, skip, seed, stride);
            }

            if (cancelled)
                Debug.LogWarning($"{name}: cobertura cancelada — o que já foi criado fica.", this);

            BadDepth(grid, map, out float finalDepth);
            string depthText = $"chão ruim a até {finalDepth:0.0} m do OK";
            Debug.Log(
                $"{name}: {added} auxiliar(es) novo(s) para cobrir o chão ({byGain} por ganho máximo entre {candidates} " +
                $"candidato(s), {added - byGain} para o chão fundo); {(float)map.Ok / Mathf.Max(1, map.Walkable):P1} do chão OK, " +
                $"{depthText}; {unresolved} ponto(s) fundo(s) sem candidato que os resolva sem ferir vazamento/posse/" +
                "sombra/espaçamento (marrom no gizmo).", this);
            return added;
        }

        private void AddAuxiliary(Draft draft, WalkGrid grid, CoverageMap map, bool[] crowded, int cell, float radius)
        {
            int node = draft.Add(grid.Position(cell), primary: false, source: null, radius);
            ApplyToCoverage(draft, grid, map, node);
            MarkCrowded(grid, crowded, draft.Positions[node]);
        }

        /// <summary>Melhor raio de um auxiliar na célula (empate: o mais perto do padrão); false se nenhum é válido.</summary>
        private bool BestRadiusAt(Draft draft, WalkGrid grid, CoverageMap map, int cell, List<float> radii, out float bestRadius, out float bestScore)
        {
            Vector3 position = grid.Position(cell);
            float fallback = Graph.DefaultAuxiliaryRadius;
            bestRadius = -1f;
            bestScore = float.MinValue;

            foreach (float radius in radii)
            {
                if (!TryEvaluate(draft, grid, map, position, radius, -1, out float score))
                    continue;

                bool better = score > bestScore + 0.5f
                              || (score > bestScore - 0.5f && Mathf.Abs(radius - fallback) < Mathf.Abs(bestRadius - fallback));
                if (better)
                {
                    bestScore = score;
                    bestRadius = radius;
                }
            }

            return bestRadius > 0f;
        }

        // Melhor auxiliar que resolve a célula seed: candidatos na malha em volta, em cada raio. Vence a maior pontuação positiva
        // (empate: mais folga, depois raio mais perto do padrão).
        private bool BestCandidateFor(Draft draft, WalkGrid grid, CoverageMap map, bool[] crowded, int seed, List<float> radii,
            out int bestCell, out float bestRadius)
        {
            int stride = Mathf.Max(1, Mathf.RoundToInt(_candidateStep / grid.Step));
            int reach = Mathf.CeilToInt(_maxAuxiliaryRadius / grid.Step);
            float fallback = Graph.DefaultAuxiliaryRadius;
            int sx = grid.X(seed);
            int sz = grid.Z(seed);
            Vector3 seedPosition = grid.Position(seed);

            bestCell = -1;
            bestRadius = 0f;
            float bestScore = 0f;
            float bestClearance = 0f;

            for (int dz = -reach; dz <= reach; dz += stride)
            {
                for (int dx = -reach; dx <= reach; dx += stride)
                {
                    int cx = sx + dx;
                    int cz = sz + dz;
                    if (!grid.InBounds(cx, cz))
                        continue;

                    int cell = grid.Index(cx, cz);
                    if (!grid.Walkable[cell] || crowded[cell])
                        continue;

                    Vector3 position = grid.Position(cell);
                    float toSeed = Graph.AreaDistance(position, seedPosition);

                    foreach (float radius in radii)
                    {
                        // Raios em ordem decrescente: se este não alcança a célula, os menores também não.
                        if (toSeed > radius)
                            break;

                        if (!TryEvaluate(draft, grid, map, position, radius, seed, out float score) || score <= 0f)
                            continue;

                        float clearance = grid.Clearance[cell];
                        bool better = score > bestScore + 0.5f
                                      || (score > bestScore - 0.5f && bestCell >= 0
                                          && (clearance > bestClearance + 0.05f
                                              || (Mathf.Abs(clearance - bestClearance) <= 0.05f
                                                  && Mathf.Abs(radius - fallback) < Mathf.Abs(bestRadius - fallback))));
                        if (bestCell < 0 || better)
                        {
                            bestCell = cell;
                            bestRadius = radius;
                            bestScore = score;
                            bestClearance = clearance;
                        }
                    }
                }
            }

            return bestCell >= 0;
        }

        private static void SkipAround(WalkGrid grid, bool[] skip, int seed, int radius)
        {
            int sx = grid.X(seed);
            int sz = grid.Z(seed);
            for (int z = sz - radius; z <= sz + radius; z++)
            {
                for (int x = sx - radius; x <= sx + radius; x++)
                {
                    if (grid.InBounds(x, z))
                        skip[grid.Index(x, z)] = true;
                }
            }
        }

        // Soma de prefixos 2D do chão ruim (sem nó ou no nó errado): contar o chão ruim de um
        // quadrado vira O(1). Índice (z + 1) * (SizeX + 1) + (x + 1).
        private static int[] BadPrefix(WalkGrid grid, CoverageMap map)
        {
            int w = grid.SizeX + 1;
            var prefix = new int[w * (grid.SizeZ + 1)];
            for (int z = 0; z < grid.SizeZ; z++)
            {
                int row = 0;
                for (int x = 0; x < grid.SizeX; x++)
                {
                    int cell = grid.Index(x, z);
                    if (grid.Walkable[cell] && map.State[cell] != CellState.Ok)
                        row++;

                    prefix[(z + 1) * w + x + 1] = prefix[z * w + x + 1] + row;
                }
            }

            return prefix;
        }

        private static int BadInSquare(WalkGrid grid, int[] prefix, int x, int z, int reach)
        {
            int w = grid.SizeX + 1;
            int x0 = Mathf.Max(0, x - reach);
            int z0 = Mathf.Max(0, z - reach);
            int x1 = Mathf.Min(grid.SizeX - 1, x + reach) + 1;
            int z1 = Mathf.Min(grid.SizeZ - 1, z + reach) + 1;
            return prefix[z1 * w + x1] - prefix[z0 * w + x1] - prefix[z1 * w + x0] + prefix[z0 * w + x0];
        }

        /// <summary>Profundidade da célula ruim: distância andando (m) até a OK mais perto; deepest = a maior.</summary>
        private static float[] BadDepth(WalkGrid grid, CoverageMap map, out float deepest)
        {
            var depth = new float[grid.Count];
            var heap = new CellHeap();
            float diagonal = grid.Step * 1.41421356f;

            for (int cell = 0; cell < grid.Count; cell++)
            {
                if (!grid.Walkable[cell] || map.State[cell] == CellState.Ok)
                    continue;

                depth[cell] = float.MaxValue;
                int x = grid.X(cell);
                int z = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (grid.Walkable[next] && map.State[next] == CellState.Ok)
                        depth[cell] = Mathf.Min(depth[cell], d < 4 ? grid.Step : diagonal);
                }

                if (depth[cell] < float.MaxValue)
                    heap.Push(cell, depth[cell]);
            }

            while (heap.Count > 0)
            {
                heap.Pop(out int cell, out float cost);
                if (cost > depth[cell])
                    continue;

                int x = grid.X(cell);
                int z = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (!grid.Walkable[next] || map.State[next] == CellState.Ok)
                        continue;

                    float nextCost = cost + (d < 4 ? grid.Step : diagonal);
                    if (nextCost >= depth[next])
                        continue;

                    depth[next] = nextCost;
                    heap.Push(next, nextCost);
                }
            }

            deepest = 0f;
            for (int cell = 0; cell < grid.Count; cell++)
                deepest = Mathf.Max(deepest, depth[cell]);

            return depth;
        }

        // Meta: fração OK >= _coverageTarget e chão ruim a até _maxHoleDepth do OK (critério único da gulosa e do relatório).
        // deepest fica em float.MaxValue se a fração não bate (a profundidade nem é medida).
        private bool CoverageMeets(WalkGrid grid, CoverageMap map, out float deepest)
        {
            deepest = float.MaxValue;
            if (map.Ok < _coverageTarget * map.Walkable)
                return false;

            BadDepth(grid, map, out deepest);
            return deepest <= _maxHoleDepth + 1e-3f;
        }

        private void MarkCrowded(WalkGrid grid, bool[] crowded, Vector3 position)
        {
            if (_minNodeSpacing <= 0f)
                return;

            AreaRange(grid, position, _minNodeSpacing, out int x0, out int x1, out int z0, out int z1);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = grid.Index(x, z);
                    if (PlanarDistance(position, grid.Position(cell)) < _minNodeSpacing)
                        crowded[cell] = true;
                }
            }
        }

        // ================================================================================
        // Relatório
        // ================================================================================

        /// <summary>Loga a cobertura (OK, nó errado, sem nó, profundidade), preenche o gizmo; devolve se bateu a meta.</summary>
        private bool ReportCoverage(WalkGrid grid, CoverageMap map)
        {
            ClearCoverageReport();
            _reportCellSize = grid.Step;
            Vector3 lift = Vector3.up * 0.05f;

            for (int cell = 0; cell < grid.Count; cell++)
            {
                if (map.State[cell] == CellState.Uncovered)
                    _uncoveredCells.Add(grid.Position(cell) + lift);
                else if (map.State[cell] == CellState.Wrong)
                    _wrongCells.Add(grid.Position(cell) + lift);
            }

            float walkable = Mathf.Max(1, map.Walkable);
            float ok = map.Ok / walkable;
            LargestPatch(grid, map, CellState.Uncovered, out float holeArea, out float holeSide);
            LargestPatch(grid, map, CellState.Wrong, out float wrongArea, out float wrongSide);
            BadDepth(grid, map, out float deepest);
            bool meets = ok >= _coverageTarget && deepest <= _maxHoleDepth + 1e-3f;

            string message =
                $"{name}: cobertura do chão — {ok:P1} no nó certo, {map.Wrong / walkable:P1} no nó ERRADO (área vazada), " +
                $"{map.Uncovered / walkable:P1} SEM nó; o chão ruim fica a até {deepest:0.0} m do chão OK. Maior mancha sem " +
                $"nó: {holeArea:0.0} m² (lado {holeSide:0.0} m); maior mancha de nó errado: {wrongArea:0.0} m² (lado " +
                $"{wrongSide:0.0} m). Meta: ≥ {_coverageTarget:P0} e chão ruim a até {_maxHoleDepth:0.0} m do OK — " +
                $"{(meets ? "OK" : "NÃO BATIDA (marrom no gizmo; \"4. Ajustar raios e cobrir o chão\")")}.";

            if (meets)
                Debug.Log(message, this);
            else
                Debug.LogWarning(message, this);

            return meets;
        }

        // Maior componente (8 vizinhos) de células no estado: área (m²) e maior lado do retângulo que a envolve (m).
        private static void LargestPatch(WalkGrid grid, CoverageMap map, CellState state, out float area, out float side)
        {
            area = 0f;
            side = 0f;
            var seen = new bool[grid.Count];
            var queue = new Queue<int>();

            for (int start = 0; start < grid.Count; start++)
            {
                if (seen[start] || map.State[start] != state)
                    continue;

                int count = 0;
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                seen[start] = true;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    int x = grid.X(cell);
                    int z = grid.Z(cell);
                    count++;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minZ = Mathf.Min(minZ, z);
                    maxZ = Mathf.Max(maxZ, z);

                    for (int d = 0; d < 8; d++)
                    {
                        int nx = x + StepX[d];
                        int nz = z + StepZ[d];
                        if (!grid.InBounds(nx, nz))
                            continue;

                        int next = grid.Index(nx, nz);
                        if (seen[next] || map.State[next] != state)
                            continue;

                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }

                area = Mathf.Max(area, count * grid.Step * grid.Step);
                side = Mathf.Max(side, (Mathf.Max(maxX - minX, maxZ - minZ) + 1) * grid.Step);
            }
        }

        // De todo ponto do chão, algum dos 6 nós mais próximos tem reta livre a até _maxNodeDistance; senão a observação
        // [4..6] aponta através da parede. Amostra de ~1 m (um cast por teste); devolve quantos pontos ficaram sem nó.
        private int CheckOrientation(Draft draft, WalkGrid grid)
        {
            GraphExplorerManager agent = ArenaRoot.GetComponentInChildren<GraphExplorerManager>();
            float range = agent != null ? agent.MaxNodeDistance : 25f;
            int stride = Mathf.Max(1, Mathf.RoundToInt(1f / grid.Step));
            var nearest = new List<(float distance, int node)>();
            int blind = 0;
            int samples = 0;

            for (int z = 0; z < grid.SizeZ; z += stride)
            {
                if ((z & 7) == 0)
                    UnityEditor.EditorUtility.DisplayProgressBar("Nó à vista?", $"linha {z}/{grid.SizeZ}", (float)z / grid.SizeZ);

                for (int x = 0; x < grid.SizeX; x += stride)
                {
                    int cell = grid.Index(x, z);
                    if (!grid.Walkable[cell])
                        continue;

                    samples++;
                    Vector3 position = grid.Position(cell);
                    nearest.Clear();
                    for (int i = 0; i < draft.Count; i++)
                    {
                        if (!draft.Alive(i))
                            continue;

                        float distance = PlanarDistance(position, draft.Positions[i]);
                        if (distance <= range)
                            nearest.Add((distance, i));
                    }

                    nearest.Sort((a, b) => a.distance.CompareTo(b.distance));
                    bool seen = false;
                    for (int k = 0; k < nearest.Count && k < 6 && !seen; k++)
                        seen = Graph.IsSegmentClear(position, draft.Positions[nearest[k].node]);

                    if (!seen)
                    {
                        blind++;
                        _blindPoints.Add(position + Vector3.up * 0.1f);
                    }
                }
            }

            if (blind > 0)
            {
                Debug.LogWarning(
                    $"{name}: {blind} de {samples} ponto(s) do chão sem nenhum nó com reta livre a até {range:0} m " +
                    "(esfera marrom no gizmo) — ali a observação \"nó mais próximo\" aponta através da parede.", this);
            }

            return blind;
        }

        // Spawn point fixo (fallback do spawn aleatório) tem que cair em chão OK.
        private int CheckSpawnPoints(WalkGrid grid, CoverageMap map)
        {
            GraphArenaController arena = GetComponentInParent<GraphArenaController>();
            if (arena == null || arena.SpawnPoints == null)
                return 0;

            int bad = 0;
            foreach (Transform point in arena.SpawnPoints)
            {
                if (point == null)
                    continue;

                var onPlane = new Vector3(point.position.x, grid.Min.y, point.position.z);
                int cell = grid.Snap(onPlane, 0.5f);
                if (cell >= 0 && map.State[cell] == CellState.Ok)
                    continue;

                bad++;
                Debug.LogWarning(
                    $"{point.name}: spawn point fora de chão coberto (sem nó, no nó errado ou dentro de obstáculo). " +
                    "Só importa com _spawnAtRandomNode desligado.", point);
            }

            return bad;
        }

        private static string NodeLabel(Draft draft, int node)
        {
            if (node < 0)
                return "?";

            return draft.Source[node] != null ? draft.Source[node].name : $"nó novo em {draft.Positions[node]}";
        }

        private Object NodeContext(Draft draft, int node) =>
            node >= 0 && draft.Source[node] != null ? draft.Source[node] : this;
#endif

        private void DrawCoverageReport()
        {
            if (_uncoveredCells.Count == 0 && _wrongCells.Count == 0 && _blindPoints.Count == 0)
                return;

            // Teto de cubos desenhados: dezenas de milhares travam a Scene view.
            const int limit = 40000;
            float size = _reportCellSize * 0.9f;
            var cube = new Vector3(size, 0.02f, size);

            Gizmos.color = UncoveredColor;
            for (int i = 0; i < _uncoveredCells.Count && i < limit; i++)
                Gizmos.DrawCube(_uncoveredCells[i], cube);

            for (int i = 0; i < _wrongCells.Count && i < limit; i++)
                Gizmos.DrawWireCube(_wrongCells[i], cube);

            foreach (Vector3 point in _blindPoints)
                Gizmos.DrawWireSphere(point, 0.3f);
        }
    }
}
