using System.Collections.Generic;
using UnityEngine;

// Os campos só são lidos pelos menus de editor; no build do jogo ficam sem uso (e sem custo).
#pragma warning disable CS0414

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Ferramenta de AUTORIA do grafo (só editor, via menus de contexto; não roda no treino): encaixa os nós no espaço
    /// livre real (parede + mobília na layer de parede do <see cref="NavGraph"/>) e gera/liga/poda nós pelo mapa do chão,
    /// medindo com a física do jogo (NavGraph.IsBodyClear / IsSegmentClear).
    /// Menus: 1 Diagnosticar, 2 Reposicionar nós, 3 Consertar ligações, 4 Ajustar raios e cobrir o chão, 5 Ligar vizinhos,
    /// 6 Remover nós inúteis, 7 Gerar do zero, 9 Ladrilhar o chão, 11 Relatório de salas e portas; "Tudo" = 2 -> 3 -> 6 -> 4.
    /// Tudo tem Undo; rode no Prefab Mode do NodeTraining para valer nas cópias da arena.
    /// Parciais: este arquivo = campos, menus 1-3 e base de física; Generation = 5-7, rascunho, mapa do chão e portas;
    /// Coverage = 4 e 11 (raios e cobertura); Tiling = 9 (ladrilhos retangulares).
    /// "Primário" nos comentários = nó alvo (Porta/Ping, NavNode.IsTarget); os demais são auxiliares.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavGraph))]
    public partial class NavGraphPlacer : MonoBehaviour
    {
        [Header("-----Corpo-----")]
        // A folga de spawn vem do NavGraph (_spawnClearance); o placer só lê.

        // Folga "boa" (m) até o obstáculo; acima dela não pontua, para o nó não ir ao centro do salão.
        [SerializeField] private float _desiredClearance = 2f;

        [Header("-----Reposicionamento-----")]
        // Deslocamento máximo (m) de um nó; o que não couber é listado como sem solução.
        [SerializeField] private float _maxDisplacement = 3f;

        // Passo (m) da grade grossa e do refinamento final.
        [SerializeField] private float _sampleStep = 0.2f;
        [SerializeField] private float _refineStep = 0.05f;

        // Custo de 1 m de deslocamento em metros de folga (0.3: aceita ~3 m para ganhar 1 m).
        [SerializeField] private float _displacementCost = 0.3f;

        // Ligado: só mexe em nó obstruído. Desligado: também centraliza os nós livres.
        [SerializeField] private bool _moveOnlyObstructed = true;

        [Header("-----Ligações-----")]
        // Margem (m) da busca de ponto de desvio em volta de uma aresta bloqueada.
        [SerializeField] private float _detourMargin = 4f;

        [Header("-----Raios-----")]
        // Vazamento máximo: fração do chão livre da área de chegada que o corpo não alcança sem sair dela,
        // medida no mapa do chão (Coverage). Mais tolerante no auxiliar.
        [SerializeField, Range(0f, 1f)] private float _maxLeakPrimary = 0.1f;
        [SerializeField, Range(0f, 1f)] private float _maxLeakAuxiliary = 0.25f;

        // Raio mínimo (m) do alvo e do auxiliar.
        [SerializeField] private float _minPrimaryRadius = 1.2f;
        [SerializeField] private float _minAuxiliaryRadius = 1.5f;

        // Teto do raio (m) do AUXILIAR (a área é cortada pela parede, então um nó grande cobre uma sala).
        // O alvo não usa isto: continua apertado, para "visitado" significar "estive lá".
        [SerializeField] private float _maxAuxiliaryRadius = 8f;

        // Passo (m) com que o raio do alvo encolhe enquanto vaza.
        [SerializeField] private float _radiusStep = 0.1f;

        // Posse mínima de um alvo: fração do chão livre da área dele em que ele vence no FindNodeAt.
        [SerializeField, Range(0f, 1f)] private float _minPrimaryOwnership = 0.75f;

        [Header("-----Gizmos-----")]
        // Desenha o relatório da última execução: linha de onde o nó saiu e X vermelho nos sem solução.
        [SerializeField] private bool _drawReport = true;

        private NavGraph _graph;

        // Relatório da última execução (da sessão; não serializado).
        private readonly List<Vector3> _movedFrom = new List<Vector3>();
        private readonly List<NavNode> _movedNodes = new List<NavNode>();
        private readonly List<NavNode> _unresolved = new List<NavNode>();

        private readonly Collider[] _overlapBuffer = new Collider[1];

        private NavGraph Graph
        {
            get
            {
                if (_graph == null)
                    _graph = GetComponent<NavGraph>();

                return _graph;
            }
        }

#if UNITY_EDITOR
        // ===== Menus =====

        [ContextMenu("1. Diagnosticar (não altera nada)")]
        private void Diagnose()
        {
            if (!Prepare() || !RequireRadialGraph())
                return;

            _unresolved.Clear();
            ClearCoverageReport();

            try
            {
                DiagnoseStep();
            }
            finally
            {
                UnityEditor.EditorUtility.ClearProgressBar();
            }
        }

        private void DiagnoseStep()
        {
            int stuck = 0;
            int tight = 0;

            foreach (NavNode node in ValidNodes())
            {
                if (!Graph.IsBodyClear(node.Position, Graph.LinkClearance))
                {
                    stuck++;
                    _unresolved.Add(node);
                    Debug.LogError($"{node.name}: DENTRO de obstáculo — o corpo não cabe nem passando.", node);
                    continue;
                }

                // Nó apertado não é erro: só não vira spawn (NavGraph.CanSpawnAt).
                if (!Graph.IsBodyClear(node.Position, SpawnClearance))
                    tight++;
            }

            int blocked = 0;
            foreach ((NavNode a, NavNode b) in Edges())
            {
                if (IsEdgeClear(a, b))
                    continue;

                blocked++;
                Debug.LogWarning($"{a.name} <-> {b.name}: ligação bloqueada para o corpo.", a);
            }

            // Sobreposição entre alvos (todos os pares); auxiliar x alvo é a posse, medida no mapa do chão.
            int overlaps = 0;
            List<NavNode> nodes = ValidNodes();
            for (int i = 0; i < nodes.Count; i++)
            {
                NavNode p = nodes[i];
                if (!p.IsTarget || !p.IsEnabled)
                    continue;

                for (int j = i + 1; j < nodes.Count; j++)
                {
                    NavNode q = nodes[j];
                    if (!q.IsTarget || !q.IsEnabled)
                        continue;

                    if (Graph.AreaDistance(p.Position, q.Position) < Graph.RadiusOf(p) + Graph.RadiusOf(q))
                    {
                        overlaps++;
                        Debug.LogWarning($"{p.name} <-> {q.name}: áreas de primários se sobrepõem.", p);
                    }
                }
            }

            int isolated = ReportPieces(nodes) - 1;

            // Medido no mapa do chão (alguns segundos): vazamento, posse, cobertura, nó à vista e spawns.
            int leaking = 0;
            int robbed = 0;
            int blind = 0;
            int badSpawns = 0;
            int doorsMissing = 0;
            bool coverageOk = false;

            Draft draft = DraftFromScene();
            WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
            if (grid != null)
            {
                CoverageMap map = BuildCoverage(draft, grid, null);

                for (int i = 0; i < draft.Count; i++)
                {
                    NavNode node = draft.Source[i];
                    float leak = LeakOf(map, i);
                    float maxLeak = draft.Primary[i] ? _maxLeakPrimary : _maxLeakAuxiliary;
                    if (leak > maxLeak)
                    {
                        leaking++;
                        Debug.LogWarning(
                            $"{node.name}: {leak:P0} da área de chegada fica atrás de parede/móvel " +
                            $"(máx. {maxLeak:P0}) — chegada registrada sem ter chegado.", node);
                    }

                    float ownership = OwnershipOf(map, i);
                    if (draft.Primary[i] && ownership < _minPrimaryOwnership)
                    {
                        robbed++;
                        Debug.LogWarning(
                            $"{node.name}: só é dono de {ownership:P0} da própria área (mín. {_minPrimaryOwnership:P0}) — " +
                            "um nó vizinho registra a chegada no lugar dele.", node);
                    }
                }

                coverageOk = ReportCoverage(grid, map);
                blind = CheckOrientation(draft, grid);
                badSpawns = CheckSpawnPoints(grid, map);
            }

            doorsMissing = ReportDoors(draft);

            Debug.Log(
                $"{name}: diagnóstico — {stuck} nó(s) dentro de obstáculo, {tight} sem folga de spawn (não viram " +
                $"spawn), {blocked} ligação(ões) bloqueada(s), {overlaps} par(es) de primários sobrepostos, " +
                $"{isolated + 1} pedaço(s) de grafo, {leaking} área(s) vazando, {robbed} primário(s) com área " +
                $"roubada, {blind} ponto(s) sem nó à vista, {badSpawns} spawn point(s) fora de chão coberto, " +
                $"{doorsMissing} porta(s) sem ligação atravessando. " +
                $"Cobertura do chão: {(grid == null ? "não medida" : coverageOk ? "OK" : "abaixo da meta")}.", this);
        }

        [ContextMenu("2. Reposicionar nós")]
        private void RelocateNodes() => RunStep("Reposicionar nós", RelocateNodesStep);

        [ContextMenu("3. Consertar ligações bloqueadas")]
        private void RepairLinks() => RunStep("Consertar ligações", RepairLinksStep);

        /// <summary>
        /// Arruma um grafo existente: 2, 3, 6 e 4 por último, porque o raio certo depende de onde estão os outros nós.
        /// </summary>
        [ContextMenu("Tudo (2 -> 3 -> 6 -> 4)")]
        private void RunAll()
        {
            if (!CanRemoveNodes())
                return;

            RunStep("Posicionar grafo", () =>
            {
                RelocateNodesStep();
                Physics.SyncTransforms();

                RepairLinksStep();
                Physics.SyncTransforms();

                Draft draft = DraftFromScene();
                WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
                if (grid == null)
                    return;

                PruneUseless(draft, grid);
                CoverFloor(draft, grid);
                ApplyDraft(draft);
                ReportCoverage(grid, BuildCoverage(draft, grid, null));
                ReportDoors(draft);
            });
        }

        /// <summary>
        /// Conta os pedaços conexos do grafo; com mais de um, loga os nós dos menores e marca X vermelho.
        /// </summary>
        private int ReportPieces(List<NavNode> nodes)
        {
            Dictionary<NavNode, List<NavNode>> adjacency = BuildAdjacency(nodes);
            var pieces = new List<List<NavNode>>();
            var seen = new HashSet<NavNode>();

            foreach (NavNode start in nodes)
            {
                if (!start.IsEnabled || !seen.Add(start))
                    continue;

                var piece = new List<NavNode>();
                var queue = new Queue<NavNode>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    NavNode current = queue.Dequeue();
                    piece.Add(current);
                    foreach (NavNode next in adjacency[current])
                    {
                        if (next.IsEnabled && seen.Add(next))
                            queue.Enqueue(next);
                    }
                }

                pieces.Add(piece);
            }

            if (pieces.Count <= 1)
                return pieces.Count;

            pieces.Sort((a, b) => b.Count.CompareTo(a.Count));
            for (int p = 1; p < pieces.Count; p++)
            {
                var names = new List<string>();
                foreach (NavNode node in pieces[p])
                {
                    _unresolved.Add(node);
                    if (names.Count < 6)
                        names.Add(node.name);
                }

                Debug.LogError(
                    $"{name}: pedaço SOLTO com {pieces[p].Count} nó(s), sem caminho até o resto do grafo: " +
                    $"{string.Join(", ", names)}{(pieces[p].Count > 6 ? ", ..." : "")}. Rode \"5. Ligar vizinhos\" ou " +
                    "\"4. Ajustar raios e cobrir o chão\" (ligam pelo chão, com auxiliares nas curvas).", pieces[p][0]);
            }

            Debug.LogError(
                $"{name}: grafo partido em {pieces.Count} pedaços (o maior tem {pieces[0].Count} nós). X vermelho " +
                "no gizmo nos nós soltos.", this);
            return pieces.Count;
        }

        [ContextMenu("Zerar raios ajustados")]
        private void ClearRadiusOverrides()
        {
            RunStep("Zerar raios", () =>
            {
                foreach (NavNode node in ValidNodes())
                {
                    UnityEditor.Undo.RecordObject(node, "Zerar raios");
                    node.SetRadiusOverride(0f);
                    MarkModified(node);
                }
            }, radialOnly: false);
        }

        // Um passo = um grupo de Undo + barra de progresso que sempre fecha. radialOnly: recusa grafo ladrilhado (usa raio).
        private void RunStep(string label, System.Action step, bool radialOnly = true)
        {
            if (!Prepare() || (radialOnly && !RequireRadialGraph()))
                return;

            UnityEditor.Undo.IncrementCurrentGroup();
            int group = UnityEditor.Undo.GetCurrentGroup();
            UnityEditor.Undo.SetCurrentGroupName(label);

            _movedFrom.Clear();
            _movedNodes.Clear();
            _unresolved.Clear();
            ClearCoverageReport();

            try
            {
                step();
            }
            finally
            {
                UnityEditor.EditorUtility.ClearProgressBar();
                UnityEditor.Undo.CollapseUndoOperations(group);
            }
        }

        // ===== 2. Reposicionar =====

        private void RelocateNodesStep()
        {
            List<NavNode> nodes = ValidNodes();
            Dictionary<NavNode, List<NavNode>> adjacency = BuildAdjacency(nodes);
            int relaxed = 0;

            for (int i = 0; i < nodes.Count; i++)
            {
                NavNode node = nodes[i];
                UnityEditor.EditorUtility.DisplayProgressBar("Reposicionar nós", node.name, (float)i / nodes.Count);

                List<NavNode> neighbors = adjacency[node];
                if (_moveOnlyObstructed && !IsObstructed(node, neighbors))
                    continue;

                if (!TryFindPlacement(node, neighbors, out Vector3 best, out bool usedRelaxed))
                {
                    _unresolved.Add(node);
                    Debug.LogError(
                        $"{node.name}: nenhum ponto livre a até {_maxDisplacement} m. Mova na mão, ou apague o " +
                        "nó se a área virou móvel.", node);
                    continue;
                }

                if (usedRelaxed)
                {
                    relaxed++;
                    Debug.LogWarning(
                        $"{node.name}: só coube com a folga de passagem ({Graph.LinkClearance:0.00}), não com " +
                        $"a de spawn ({SpawnClearance:0.00}). Corredor estreito ou canto apertado: ele não vira spawn.", node);
                }

                Vector3 from = node.Position;
                if ((best - from).sqrMagnitude < 1e-6f)
                    continue;

                UnityEditor.Undo.RecordObject(node.transform, "Reposicionar nós");
                node.transform.position = best;
                MarkModified(node.transform);

                // Os próximos casts precisam ver este nó já no lugar novo.
                Physics.SyncTransforms();

                _movedFrom.Add(from);
                _movedNodes.Add(node);
            }

            Debug.Log(
                $"{name}: {_movedNodes.Count} nó(s) movido(s), {relaxed} com folga reduzida, " +
                $"{_unresolved.Count} sem solução.", this);
        }

        private bool IsObstructed(NavNode node, List<NavNode> neighbors)
        {
            if (!Graph.IsBodyClear(node.Position, SpawnClearance))
                return true;

            foreach (NavNode neighbor in neighbors)
            {
                if (!IsEdgeClear(node, neighbor))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Melhor ponto a até _maxDisplacement: grade de "o corpo cabe?" restrita ao componente conexo da origem (não pula parede fina),
        /// pontuada por folga - deslocamento - penalidades e refinada. Sem folga de spawn, usa a de passagem.
        /// </summary>
        private bool TryFindPlacement(NavNode node, List<NavNode> neighbors, out Vector3 best, out bool usedRelaxed)
        {
            usedRelaxed = false;
            if (TryFindPlacement(node, neighbors, SpawnClearance, out best))
                return true;

            usedRelaxed = true;
            return TryFindPlacement(node, neighbors, Graph.LinkClearance, out best);
        }

        private bool TryFindPlacement(NavNode node, List<NavNode> neighbors, float bodyRadius, out Vector3 best)
        {
            Vector3 origin = node.Position;
            best = origin;

            int half = Mathf.CeilToInt(_maxDisplacement / _sampleStep);
            int size = half * 2 + 1;
            var free = new bool[size, size];
            float nearest = float.MaxValue;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    Vector3 cell = CellPosition(origin, x - half, z - half, _sampleStep);
                    float distance = PlanarDistance(origin, cell);
                    if (distance > _maxDisplacement || !Graph.IsBodyClear(cell, bodyRadius))
                        continue;

                    free[x, z] = true;
                    nearest = Mathf.Min(nearest, distance);
                }
            }

            if (nearest == float.MaxValue)
                return false;

            // Sementes: células livres mais próximas da origem (+0.5 m, para sair de dentro de uma mesa por qualquer lado).
            var seeds = new List<Vector2Int>();
            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    if (free[x, z] && PlanarDistance(origin, CellPosition(origin, x - half, z - half, _sampleStep)) <= nearest + 0.5f)
                        seeds.Add(new Vector2Int(x, z));
                }
            }

            bool[,] reachable = FloodFill(free, seeds);
            bool isPrimary = node.IsTarget;

            // Pré-placar barato em toda célula; só os 32 melhores pagam os casts das ligações.
            var candidates = new List<(Vector3 position, float score)>();
            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    if (!reachable[x, z])
                        continue;

                    Vector3 cell = CellPosition(origin, x - half, z - half, _sampleStep);
                    candidates.Add((cell, BaseScore(origin, cell)));
                }
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            float bestScore = float.MinValue;
            int limit = Mathf.Min(candidates.Count, 32);
            for (int i = 0; i < limit; i++)
            {
                float score = candidates[i].score + PlacementPenalty(node, isPrimary, candidates[i].position, neighbors);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidates[i].position;
                }
            }

            Vector3 coarse = best;
            int fine = Mathf.CeilToInt(_sampleStep / _refineStep);
            for (int x = -fine; x <= fine; x++)
            {
                for (int z = -fine; z <= fine; z++)
                {
                    Vector3 cell = CellPosition(coarse, x, z, _refineStep);
                    if (PlanarDistance(origin, cell) > _maxDisplacement || !Graph.IsBodyClear(cell, bodyRadius))
                        continue;

                    float score = BaseScore(origin, cell) + PlacementPenalty(node, isPrimary, cell, neighbors);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cell;
                    }
                }
            }

            return true;
        }

        private float BaseScore(Vector3 origin, Vector3 cell)
        {
            return Mathf.Min(Clearance(cell), _desiredClearance) - _displacementCost * PlanarDistance(origin, cell);
        }

        // Penalidades: ligação bloqueada -100 (só se perde uma se nenhum ponto mantiver todas); -10 por alvo invadindo
        // a área de outro alvo, ou auxiliar a menos de meio raio de um alvo (as regras de área do bake do NavGraph).
        private float PlacementPenalty(NavNode node, bool isPrimary, Vector3 cell, List<NavNode> neighbors)
        {
            float penalty = 0f;

            foreach (NavNode neighbor in neighbors)
            {
                if (!IsSegmentClearBothWays(cell, neighbor.Position))
                    penalty -= 100f;
            }

            float radius = Graph.RadiusOf(node);
            foreach (NavNode other in Graph.Nodes)
            {
                if (other == null || other == node || (!isPrimary && !other.IsTarget))
                    continue;

                float distance = Graph.AreaDistance(cell, other.Position);

                if (isPrimary && other.IsTarget)
                {
                    if (distance < radius + Graph.RadiusOf(other))
                        penalty -= 10f;
                }
                else
                {
                    float primaryRadius = isPrimary ? radius : Graph.RadiusOf(other);
                    if (distance < primaryRadius * 0.5f)
                        penalty -= 10f;
                }
            }

            return penalty;
        }

        // ===== 3. Consertar ligações =====

        /// <summary>
        /// Para cada aresta bloqueada: desvia por um nó existente ou auxiliar novo; senão remove, se o grafo segue conexo;
        /// senão acusa erro (pede um nó à mão).
        /// </summary>
        private void RepairLinksStep()
        {
            List<NavNode> nodes = ValidNodes();
            var blockedEdges = new List<(NavNode a, NavNode b)>();
            foreach ((NavNode a, NavNode b) edge in Edges())
            {
                if (!IsEdgeClear(edge.a, edge.b))
                    blockedEdges.Add(edge);
            }

            int detoured = 0;
            int removed = 0;
            int kept = 0;

            for (int i = 0; i < blockedEdges.Count; i++)
            {
                (NavNode a, NavNode b) = blockedEdges[i];
                UnityEditor.EditorUtility.DisplayProgressBar("Consertar ligações", $"{a.name} <-> {b.name}", (float)i / blockedEdges.Count);

                if (TryFindExistingDetour(nodes, a, b, out NavNode via))
                {
                    Unlink(a, b);
                    UnityEditor.Undo.RecordObject(via, "Consertar ligações");
                    if (!via.IsNeighbor(a) && !a.IsNeighbor(via))
                        via.EditableNeighbors.Add(a);
                    if (!via.IsNeighbor(b) && !b.IsNeighbor(via))
                        via.EditableNeighbors.Add(b);
                    MarkModified(via);
                    detoured++;
                    Debug.Log($"{a.name} <-> {b.name}: desvio pelo nó que já existia '{via.name}'.", via);
                    continue;
                }

                if (TryFindDetour(a, b, out Vector3 point))
                {
                    NavNode detour = CreateAuxiliary(a, b, point);
                    Unlink(a, b);
                    UnityEditor.Undo.RecordObject(detour, "Consertar ligações");
                    detour.EditableNeighbors.Add(a);
                    detour.EditableNeighbors.Add(b);
                    MarkModified(detour);
                    nodes.Add(detour);
                    detoured++;
                    Debug.Log($"{a.name} <-> {b.name}: desvio por '{detour.name}'.", detour);
                    continue;
                }

                if (IsConnectedWithout(nodes, a, b))
                {
                    Unlink(a, b);
                    removed++;
                    Debug.LogWarning($"{a.name} <-> {b.name}: sem desvio possível — ligação removida (grafo continua conexo).", a);
                    continue;
                }

                kept++;
                _unresolved.Add(a);
                Debug.LogError(
                    $"{a.name} <-> {b.name}: bloqueada, sem desvio, e é a única ponte entre duas partes do " +
                    "grafo. Ponha um nó à mão contornando o obstáculo.", a);
            }

            if (detoured > 0)
                Graph.CollectChildNodes();

            Debug.Log(
                $"{name}: {blockedEdges.Count} ligação(ões) bloqueada(s) — {detoured} com desvio, " +
                $"{removed} removida(s), {kept} sem solução.", this);
        }

        /// <summary>
        /// Desvio por um nó que JÁ EXISTE (menor caminho A -> nó -> B); vem antes de criar auxiliar, para não gerar par colado.
        /// </summary>
        private bool TryFindExistingDetour(List<NavNode> nodes, NavNode a, NavNode b, out NavNode via)
        {
            via = null;
            float direct = PlanarDistance(a.Position, b.Position);
            var candidates = new List<(float cost, NavNode node)>();
            foreach (NavNode node in nodes)
            {
                if (node == a || node == b || !node.IsEnabled)
                    continue;

                float cost = PlanarDistance(a.Position, node.Position) + PlanarDistance(node.Position, b.Position);
                if (cost <= direct + 2f * _detourMargin)
                    candidates.Add((cost, node));
            }

            candidates.Sort((l, r) => l.cost.CompareTo(r.cost));
            foreach ((float _, NavNode node) in candidates)
            {
                if (IsSegmentClearBothWays(a.Position, node.Position) && IsSegmentClearBothWays(node.Position, b.Position))
                {
                    via = node;
                    return true;
                }
            }

            return false;
        }

        // Folga de spawn, depois a de passagem, depois sem espaçamento (_minNodeSpacing): desvio apertado vale mais
        // que remover a ligação ou deixá-la atravessando o móvel.
        private bool TryFindDetour(NavNode a, NavNode b, out Vector3 point) =>
            TryFindDetour(a, b, SpawnClearance, true, out point)
            || TryFindDetour(a, b, Graph.LinkClearance, true, out point)
            || TryFindDetour(a, b, Graph.LinkClearance, false, out point);

        private bool TryFindDetour(NavNode a, NavNode b, float clearance, bool spaced, out Vector3 point)
        {
            point = Vector3.zero;

            Vector3 pa = a.Position;
            Vector3 pb = b.Position;
            float minX = Mathf.Min(pa.x, pb.x) - _detourMargin;
            float maxX = Mathf.Max(pa.x, pb.x) + _detourMargin;
            float minZ = Mathf.Min(pa.z, pb.z) - _detourMargin;
            float maxZ = Mathf.Max(pa.z, pb.z) + _detourMargin;
            float y = (pa.y + pb.y) * 0.5f;

            var candidates = new List<(Vector3 position, float cost)>();
            for (float x = minX; x <= maxX; x += _sampleStep)
            {
                for (float z = minZ; z <= maxZ; z += _sampleStep)
                {
                    var cell = new Vector3(x, y, z);
                    if ((spaced && NearAnyNode(cell)) || !Graph.IsBodyClear(cell, clearance) || ShadowsPrimary(cell))
                        continue;

                    // Menor caminho, empurrado para longe dos móveis (1 m a menos de folga = +0.5 m de caminho).
                    float cost = PlanarDistance(pa, cell) + PlanarDistance(cell, pb)
                                 + 0.5f * (_desiredClearance - Mathf.Min(Clearance(cell), _desiredClearance));
                    candidates.Add((cell, cost));
                }
            }

            candidates.Sort((l, r) => l.cost.CompareTo(r.cost));

            foreach (var (position, _) in candidates)
            {
                if (IsSegmentClearBothWays(pa, position) && IsSegmentClearBothWays(position, pb))
                {
                    point = position;
                    return true;
                }
            }

            return false;
        }

        private bool NearAnyNode(Vector3 position)
        {
            foreach (NavNode other in Graph.Nodes)
            {
                if (other != null && PlanarDistance(position, other.Position) < _minNodeSpacing)
                    return true;
            }

            return false;
        }

        // Auxiliar a menos de meio raio de um alvo eclipsa o alvo no FindNodeAt.
        private bool ShadowsPrimary(Vector3 position)
        {
            foreach (NavNode other in Graph.Nodes)
            {
                if (other != null && other.IsTarget && Graph.AreaDistance(position, other.Position) < Graph.RadiusOf(other) * 0.5f)
                    return true;
            }

            return false;
        }

        private NavNode CreateAuxiliary(NavNode a, NavNode b, Vector3 position)
        {
            var go = new GameObject($"Node (desvio {a.name} - {b.name})");
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Consertar ligações");

            // Irmão dos nós existentes, para "Coletar nós filhos" achá-lo.
            Transform parent = a.transform.parent != null ? a.transform.parent : Graph.transform;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.position = position;

            NavNode node = UnityEditor.Undo.AddComponent<NavNode>(go);
            node.SetKind(NodeKind.Auxiliary);
            return node;
        }

        private void Unlink(NavNode a, NavNode b)
        {
            UnityEditor.Undo.RecordObject(a, "Consertar ligações");
            UnityEditor.Undo.RecordObject(b, "Consertar ligações");
            a.EditableNeighbors.RemoveAll(n => n == b);
            b.EditableNeighbors.RemoveAll(n => n == a);
            MarkModified(a);
            MarkModified(b);
        }

        private bool IsConnectedWithout(List<NavNode> nodes, NavNode cutA, NavNode cutB)
        {
            Dictionary<NavNode, List<NavNode>> adjacency = BuildAdjacency(nodes);
            var enabled = nodes.FindAll(n => n.IsEnabled);
            if (enabled.Count == 0)
                return true;

            var seen = new HashSet<NavNode> { enabled[0] };
            var queue = new Queue<NavNode>();
            queue.Enqueue(enabled[0]);

            while (queue.Count > 0)
            {
                NavNode current = queue.Dequeue();
                foreach (NavNode next in adjacency[current])
                {
                    bool isCut = (current == cutA && next == cutB) || (current == cutB && next == cutA);
                    if (isCut || !next.IsEnabled || !seen.Add(next))
                        continue;

                    queue.Enqueue(next);
                }
            }

            return seen.Count == enabled.Count;
        }

        // ===== Base =====

        /// <summary>
        /// Os menus de raio (1-7) estragariam um grafo ladrilhado (Rectangle, menu 9), então recusam.
        /// </summary>
        private bool RequireRadialGraph()
        {
            if (Graph.Shape != NodeShape.Rectangle)
                return true;

            Debug.LogError(
                $"{name}: este grafo é LADRILHADO (Node Shape = Rectangle). Os menus 1 a 7 trabalham com raio e não " +
                "servem para ele: rode \"9. Ladrilhar o chão\" de novo, ou troque o Node Shape do NavGraph para " +
                "Circle/Square antes.", this);
            return false;
        }

        private bool Prepare()
        {
            if (Graph == null)
                return false;

            if (Graph.WallLayer.value == 0)
            {
                Debug.LogError($"{name}: o NavGraph está sem Wall Layer — sem ela não há obstáculo para medir.", this);
                return false;
            }

            // Edição de transform no editor não sincroniza a física; sem isto o placer mede contra a posição antiga dos móveis.
            Physics.SyncTransforms();
            WarnAboutInactiveObstacles();
            return true;
        }

        // Collider desligado não existe para a física: com Map_Objects desativados o placer aprova nós que cairão dentro dos móveis.
        private void WarnAboutInactiveObstacles()
        {
            Transform root = ArenaRoot;
            int inactive = 0;
            Collider example = null;

            foreach (Collider collider in root.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                bool isWall = (Graph.WallLayer.value & (1 << collider.gameObject.layer)) != 0;
                if (!isWall || (collider.enabled && collider.gameObject.activeInHierarchy))
                    continue;

                inactive++;
                if (example == null)
                    example = collider;
            }

            if (inactive > 0)
            {
                Debug.LogWarning(
                    $"{name}: {inactive} collider(es) de parede DESATIVADO(s) nesta arena (ex.: '{example.name}'). " +
                    "Eles não contam — ative os Map_Objects antes, se o mapa final vai tê-los.", example);
            }
        }

        private List<NavNode> ValidNodes()
        {
            var nodes = new List<NavNode>();
            foreach (NavNode node in Graph.Nodes)
            {
                if (node != null)
                    nodes.Add(node);
            }

            return nodes;
        }

        // Adjacência não dirigida: fora do Play não há bake, e a autoria declara cada aresta de um lado só.
        private static Dictionary<NavNode, List<NavNode>> BuildAdjacency(List<NavNode> nodes)
        {
            var adjacency = new Dictionary<NavNode, List<NavNode>>();
            foreach (NavNode node in nodes)
                adjacency[node] = new List<NavNode>();

            foreach (NavNode node in nodes)
            {
                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null || neighbor == node || !adjacency.ContainsKey(neighbor))
                        continue;

                    if (!adjacency[node].Contains(neighbor))
                        adjacency[node].Add(neighbor);

                    if (!adjacency[neighbor].Contains(node))
                        adjacency[neighbor].Add(node);
                }
            }

            return adjacency;
        }

        private IEnumerable<(NavNode a, NavNode b)> Edges()
        {
            List<NavNode> nodes = ValidNodes();
            Dictionary<NavNode, List<NavNode>> adjacency = BuildAdjacency(nodes);
            var index = new Dictionary<NavNode, int>();
            for (int i = 0; i < nodes.Count; i++)
                index[nodes[i]] = i;

            foreach (NavNode node in nodes)
            {
                foreach (NavNode neighbor in adjacency[node])
                {
                    if (index[neighbor] > index[node])
                        yield return (node, neighbor);
                }
            }
        }

        // Os dois sentidos: o cast ignora o collider que envolve a origem, então um nó DENTRO de uma mesa passaria só de um lado.
        private bool IsEdgeClear(NavNode a, NavNode b) => IsSegmentClearBothWays(a.Position, b.Position);

        private bool IsSegmentClearBothWays(Vector3 a, Vector3 b) => Graph.IsSegmentClear(a, b) && Graph.IsSegmentClear(b, a);

        /// <summary>
        /// Folga (Chebyshev, m) até o obstáculo mais próximo, saturada em _desiredClearance; 0 = dentro de algo.
        /// Usa CAIXA, não cápsula: a cápsula vira esfera acima de 1.7 m e deixa de ver móvel baixo.
        /// </summary>
        private float Clearance(Vector3 position)
        {
            if (IsBoxClear(position, _desiredClearance))
                return _desiredClearance;

            float low = 0f;
            float high = _desiredClearance;
            for (int i = 0; i < 7; i++)
            {
                float mid = (low + high) * 0.5f;
                if (IsBoxClear(position, mid))
                    low = mid;
                else
                    high = mid;
            }

            return low;
        }

        private bool IsBoxClear(Vector3 position, float halfWidth)
        {
            float bottom = Graph.BodyBottom;
            float top = Graph.BodyTop;
            Vector3 center = position + Vector3.up * ((bottom + top) * 0.5f);
            var extents = new Vector3(halfWidth, (top - bottom) * 0.5f, halfWidth);

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            return physics.OverlapBox(center, extents, _overlapBuffer, Quaternion.identity, Graph.WallLayer, QueryTriggerInteraction.Ignore) == 0;
        }

        private static bool[,] FloodFill(bool[,] free, List<Vector2Int> seeds)
        {
            int sizeX = free.GetLength(0);
            int sizeZ = free.GetLength(1);
            var reached = new bool[sizeX, sizeZ];
            var queue = new Queue<Vector2Int>();

            foreach (Vector2Int seed in seeds)
            {
                if (!free[seed.x, seed.y] || reached[seed.x, seed.y])
                    continue;

                reached[seed.x, seed.y] = true;
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int x = cell.x + dx;
                        int z = cell.y + dz;
                        if (x < 0 || z < 0 || x >= sizeX || z >= sizeZ || reached[x, z] || !free[x, z])
                            continue;

                        reached[x, z] = true;
                        queue.Enqueue(new Vector2Int(x, z));
                    }
                }
            }

            return reached;
        }

        private static Vector3 CellPosition(Vector3 origin, int x, int z, float step) =>
            new Vector3(origin.x + x * step, origin.y, origin.z + z * step);

        private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        // Em instância de prefab, mudança por script só vira override se for registrada.
        private static void MarkModified(Object target)
        {
            UnityEditor.EditorUtility.SetDirty(target);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(target))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }
#endif

        private void OnDrawGizmosSelected()
        {
            if (!_drawReport)
                return;

            DrawCoverageReport();

            for (int i = 0; i < _movedNodes.Count; i++)
            {
                NavNode node = _movedNodes[i];
                if (node == null)
                    continue;

                // Cor do tipo do nó: linha de onde ele veio.
                Gizmos.color = NavNode.ColorOf(node.Kind);
                Gizmos.DrawLine(_movedFrom[i], node.Position);
                Gizmos.DrawWireSphere(_movedFrom[i], 0.12f);
            }

            Gizmos.color = Color.red;
            foreach (NavNode node in _unresolved)
            {
                if (node == null)
                    continue;

                Vector3 p = node.Position + Vector3.up * 0.1f;
                Gizmos.DrawLine(p + new Vector3(-0.6f, 0f, -0.6f), p + new Vector3(0.6f, 0f, 0.6f));
                Gizmos.DrawLine(p + new Vector3(-0.6f, 0f, 0.6f), p + new Vector3(0.6f, 0f, -0.6f));
            }
        }
    }
}
