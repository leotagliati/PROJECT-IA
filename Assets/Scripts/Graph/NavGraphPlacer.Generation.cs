using System.Collections.Generic;
using UnityEngine;

// Os campos só são lidos pelos menus de editor; no build do jogo ficam sem uso (e sem custo).
#pragma warning disable CS0414

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Metade do <see cref="NavGraphPlacer"/> que decide ONDE e QUANTOS nós existem, e quem liga
    /// com quem — a outra metade só corrige nós que já existem, e a cobertura do chão (raios e
    /// auxiliares que fecham buracos) mora em NavGraphPlacer.Coverage.
    ///
    /// Tudo parte de um MAPA DO CHÃO ANDÁVEL da arena inteira (<see cref="WalkGrid"/>): grade
    /// no plano dos nós, cada célula marcada com "o corpo passa aqui?" (a mesma cápsula do
    /// NavGraph) e só as células conectadas a um ponto de dentro do prédio (flood fill). Em cima
    /// dela, três ideias:
    ///
    /// - DISTÂNCIA ANDANDO, não em linha reta. Duas salas separadas por uma parede estão a 30 cm
    ///   em linha reta e a 15 m andando; espaçar nós pela reta deixaria uma das salas sem nó.
    /// - REGIÃO de um nó = o chão que fica mais perto dele ANDANDO do que de qualquer outro.
    ///   Dois nós são vizinhos quando as regiões se encostam. É isso que liga dois nós de uma
    ///   sala grande mesmo a 20 m um do outro (a sala "quebrava no meio" porque nenhuma regra de
    ///   distância máxima os ligava) e nunca liga através de parede (região não atravessa
    ///   parede, porque a faixa em volta dela não é andável).
    /// - FOLGA de cada célula (transformada de distância): o centro de sala/corredor tem folga
    ///   alta. O primário novo vai para onde a folga é maior — fica no meio, longe dos móveis.
    ///
    /// As operações montam um RASCUNHO (<see cref="Draft"/>) e só no fim tocam na cena, num
    /// grupo de Undo só.
    /// </summary>
    public partial class NavGraphPlacer
    {
        [Header("-----Geração / ligação / limpeza-----")]
        // Resolução do mapa do chão. 0.3 m numa arena de ~100 x 80 m são ~90 mil células —
        // alguns segundos de física no editor. Menor é mais preciso nas portas estreitas.
        [SerializeField] private float _generationStep = 0.3f;

        // Altura dos nós GERADOS relativa a este GameObject, usada só quando o grafo está vazio
        // (com nós, vale a altura mediana deles). No NodeTraining é 0: os nós ficam no plano do
        // Graph, 1.84 m acima do chão.
        [SerializeField] private float _generationHeight = 0f;

        // Distância ANDANDO entre primários gerados. Medido no NodeTraining2 (réplica offline do
        // PlacePrimaries): 10 m -> 59 primários, 14 -> 41, 16 -> 31, 18 -> 27, 20 -> 23 (o número
        // do grafo feito à mão). Era 10: o escritório é quase todo aberto e corredor, e 10 m punha
        // um primário a cada trecho de corredor — cada um com raio apertado, sombra e posse, que
        // empurravam a cobertura para muitos auxiliares pequenos colados nele. 18 m deixa ~1 por
        // sala e 2 num salão, e o peso de cada um fica ~0.85 (orçamento 23 repartido).
        [SerializeField] private float _primarySpacing = 18f;

        // Teto de vizinhos por nó = _neighborSlots do GraphExplorerManager. Vizinho além disso
        // é cortado da observação em silêncio.
        [SerializeField, Range(2, 8)] private int _maxDegree = 8;

        // Pontos "de dentro do prédio" de onde o flood fill começa. Vazio = usa os nós que já
        // existem e o agente da arena. Só precisa preencher numa arena sem nó nenhum.
        [SerializeField] private Transform[] _generationSeeds;

        // Objetos cujo NOME contém isto são BATENTES DE PORTA (no kit do escritório,
        // "Wall_01_Door_Hole"): passagem, e não parede. Cada um ganha um auxiliar no meio do vão
        // (se ainda não houver nó a menos de 1 m), que a limpeza não apaga, e o relatório acusa
        // porta sem ligação atravessando. O vão tem 2.54 m e o corpo 1.70: só uma linha quase
        // perpendicular passa, então sem um nó NA porta as ligações diagonais batiam no batente
        // e a sala do outro lado ficava sem conexão. Vazio = desliga o tratamento de portas.
        [SerializeField] private string _doorNameContains = "Door_Hole";

#if UNITY_EDITOR
        private const int Unreached = int.MaxValue;

        private static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        // Leste, norte, nordeste, sudeste: a metade "para frente" das 8 direções.
        private static readonly int[] HalfStepX = { 1, 0, 1, 1 };
        private static readonly int[] HalfStepZ = { 0, 1, 1, -1 };

        // ================================================================================
        // Menus
        // ================================================================================

        [ContextMenu("5. Ligar vizinhos (por região)")]
        private void LinkByRegions()
        {
            RunStep("Ligar vizinhos", () =>
            {
                Draft draft = DraftFromScene();
                WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
                if (grid == null)
                    return;

                int before = draft.Count;
                LinkRegions(draft, grid);

                // Auxiliar de portal nasce com o raio padrão; o raio certo depende do chão em
                // volta dele (vazamento, posse do primário vizinho).
                FitRadiiDraft(draft, grid, NewIndices(draft, before));
                ApplyDraft(draft);
            });
        }

        [ContextMenu("6. Remover nós inúteis (sem sair da meta de cobertura)")]
        private void RemoveUseless()
        {
            if (!CanRemoveNodes())
                return;

            RunStep("Remover nós inúteis", () =>
            {
                Draft draft = DraftFromScene();
                WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
                if (grid == null)
                    return;

                PruneUseless(draft, grid);
                ApplyDraft(draft);
                ReportCoverage(grid, BuildCoverage(draft, grid, null));
            });
        }

        [ContextMenu("7. Gerar grafo do zero (apaga os nós atuais)")]
        private void GenerateFromScratch()
        {
            if (!CanRemoveNodes() || !RequireRadialGraph())
                return;

            int existing = ValidNodes().Count;
            if (existing > 0 && !UnityEditor.EditorUtility.DisplayDialog(
                    "Gerar grafo do zero",
                    $"Isto apaga os {existing} nós atuais e gera outros a partir do espaço livre. " +
                    "Ctrl+Z desfaz. Continuar?",
                    "Gerar", "Cancelar"))
                return;

            RunStep("Gerar grafo", () =>
            {
                Draft draft = DraftFromScene();
                WalkGrid grid = BuildWalkGrid(GridHeight(), draft);
                if (grid == null)
                    return;

                for (int i = 0; i < draft.Count; i++)
                    draft.Remove(i);

                PlacePrimaries(draft, grid);
                CoverFloor(draft, grid);
                ApplyDraft(draft);
                ReportCoverage(grid, BuildCoverage(draft, grid, null));
                ReportDoors(draft);
            });
        }

        // ================================================================================
        // Geração
        // ================================================================================

        /// <summary>
        /// Primários: varre as células em ordem de FOLGA decrescente (centro de salão primeiro,
        /// depois salas menores, depois corredores) e aceita a célula se não houver primário a
        /// menos de _primarySpacing ANDANDO. É amostragem de Poisson com prioridade: cada sala
        /// ganha o primário no ponto mais aberto dela, que é o melhor ponto de vantagem que a
        /// geometria sozinha sabe apontar.
        ///
        /// O PESO é o orçamento repartido igualmente (_primaryWeightBudget / quantidade): assim o
        /// teto de recompensa do mapa — e com ele os thresholds do currículo — não muda quando a
        /// geração produz mais ou menos primários.
        /// </summary>
        private void PlacePrimaries(Draft draft, WalkGrid grid)
        {
            int spacing = Mathf.CeilToInt(_primarySpacing / grid.Step);
            int[] distance = grid.NewDistanceField();
            var queue = new Queue<int>();
            var placed = new List<int>();

            // O vão de cada porta ganha um auxiliar (AddDoorNodes) que a limpeza não apaga; um
            // primário a menos de _minNodeSpacing dele seria um par colado que nada desfaz.
            List<DoorInfo> doors = FindDoors();

            foreach (int cell in grid.CellsByClearance(SpawnClearance))
            {
                if (distance[cell] < spacing)
                    continue;

                Vector3 position = grid.Position(cell);
                if (doors.Exists(door => PlanarDistance(door.Center, position) < _minNodeSpacing))
                    continue;

                if (!Graph.IsBodyClear(position, SpawnClearance))
                    continue;

                placed.Add(draft.Add(position, primary: true, source: null, Graph.DefaultPrimaryRadius, 1f));
                Relax(grid, cell, spacing, distance, queue);
            }

            float weight = placed.Count > 0 ? _primaryWeightBudget / placed.Count : 0f;
            foreach (int i in placed)
                draft.Weights[i] = weight;

            Debug.Log(
                $"{name}: {placed.Count} primário(s) gerado(s), peso {weight:0.###} cada (orçamento " +
                $"{_primaryWeightBudget:0.##}).", this);
        }

        // ================================================================================
        // Ligação por regiões
        // ================================================================================

        /// <summary>
        /// Liga todo par de nós cujas REGIÕES se encostam (ver cabeçalho). Só ADICIONA: ligação
        /// que você fez à mão nunca é removida aqui (a bloqueada é assunto do passo 3).
        ///
        /// Para cada par vizinho guarda o PORTAL — o ponto da fronteira entre as regiões com mais
        /// folga, que numa porta é o meio do vão. Se a reta entre os dois nós passa, liga direto;
        /// se não passa (a região contorna uma quina), põe um auxiliar no portal e liga pelos
        /// dois lados. Depois: junta pedaços soltos e corta o excesso de vizinhos.
        /// </summary>
        private void LinkRegions(Draft draft, WalkGrid grid)
        {
            int[] label = Regions(draft, grid, out _);
            var portals = new Dictionary<long, (Vector3 position, float clearance)>();

            for (int cell = 0; cell < grid.Count; cell++)
            {
                if (label[cell] < 0)
                    continue;

                int x = grid.X(cell);
                int z = grid.Z(cell);

                // Metade das 8 direções basta: cada par de células vizinhas é visto uma vez.
                for (int d = 0; d < HalfStepX.Length; d++)
                {
                    int nx = x + HalfStepX[d];
                    int nz = z + HalfStepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int other = grid.Index(nx, nz);
                    if (label[other] < 0 || label[other] == label[cell])
                        continue;

                    long key = Draft.Key(label[cell], label[other]);
                    float clearance = Mathf.Min(grid.Clearance[cell], grid.Clearance[other]);
                    if (!portals.TryGetValue(key, out var best) || clearance > best.clearance)
                        portals[key] = ((grid.Position(cell) + grid.Position(other)) * 0.5f, clearance);
                }
            }

            var added = new List<long>();
            int viaPortal = 0;
            int viaWalk = 0;
            int failed = 0;

            foreach (KeyValuePair<long, (Vector3 position, float clearance)> portal in portals)
            {
                if (draft.Edges.Contains(portal.Key))
                    continue;

                Draft.Split(portal.Key, out int a, out int b);
                Vector3 pa = draft.Positions[a];
                Vector3 pb = draft.Positions[b];

                if (IsSegmentClearBothWays(pa, pb))
                {
                    if (draft.AddEdge(a, b))
                        added.Add(portal.Key);
                    continue;
                }

                int via = FindOrCreatePortalNode(draft, portal.Value.position, a, b);
                if (via >= 0)
                {
                    if (draft.AddEdge(a, via))
                        added.Add(Draft.Key(a, via));
                    if (draft.AddEdge(via, b))
                        added.Add(Draft.Key(via, b));
                    viaPortal++;
                    continue;
                }

                // Nem reta nem um ponto só: o caminho faz curva (porta vista de lado, corredor em
                // L). Segue o CHÃO de uma região até a outra e põe auxiliares nas curvas. As duas
                // regiões são conexas e se encostam, então esse caminho sempre existe — era aqui
                // que a ligação sumia em silêncio antes.
                int labelA = a;
                int labelB = b;
                if (ConnectByWalking(draft, grid, a, b, cell => label[cell] == labelA || label[cell] == labelB, added))
                    viaWalk++;
                else
                    failed++;
            }

            int bridges = BridgeComponents(draft, grid, added);
            int pruned = PruneDegree(draft, added);

            Debug.Log(
                $"{name}: {added.Count} ligação(ões) nova(s) por região ({viaPortal} por um auxiliar de portal, " +
                $"{viaWalk} seguindo o chão com auxiliares nas curvas), {bridges} ponte(s) entre pedaços soltos, " +
                $"{pruned} cortada(s) por excesso de vizinhos.", this);

            if (failed > 0)
            {
                Debug.LogWarning(
                    $"{name}: {failed} par(es) de regiões vizinhas sem caminho de corpo inteiro entre os nós " +
                    "(centro do nó fora do chão andável?). A conexão geral é conferida logo abaixo.", this);
            }
        }

        // Reaproveita um nó que já está ali perto — de portal, de porta ou qualquer outro (três
        // regiões se encontrando numa porta pediriam três nós quase no mesmo lugar); senão cria um
        // no portal, desde que ele não fique a menos de _minNodeSpacing de nó nenhum.
        //
        // Antes só reaproveitava outro auxiliar de PORTAL: o portal de uma porta caía a 0.3–1.3 m
        // do nó da porta e virava um par colado (medido no NodeTraining2: 8 arestas assim).
        //
        // A folga exigida é a de PASSAGEM, não a de spawn: o portal de um vão estreito (porta de
        // 2 m, corredor de serviço) é exatamente onde não cabe a folga de 1.25 — e era ali que o
        // portal falhava e o par ficava sem ligação. Nó apertado não vira spawn (NavGraph.CanSpawnAt).
        private int FindOrCreatePortalNode(Draft draft, Vector3 portal, int a, int b)
        {
            Vector3 pa = draft.Positions[a];
            Vector3 pb = draft.Positions[b];

            // Raio de reaproveitamento = o do auxiliar padrão: um nó a 3 m do portal, com reta
            // livre para os dois lados, faz o mesmo papel que um nó novo no portal.
            float reuse = Mathf.Max(_minNodeSpacing, Graph.DefaultAuxiliaryRadius);
            int nearby = NearestUsable(draft, portal, reuse,
                i => i != a && i != b && IsSegmentClearBothWays(pa, draft.Positions[i]) && IsSegmentClearBothWays(draft.Positions[i], pb));
            if (nearby >= 0)
                return nearby;

            if (IsCrowded(draft, portal))
                return -1;

            if (!Graph.IsBodyClear(portal, Graph.LinkClearance) || ShadowsPrimary(draft, portal))
                return -1;

            if (!IsSegmentClearBothWays(pa, portal) || !IsSegmentClearBothWays(portal, pb))
                return -1;

            return draft.Add(portal, primary: false, source: null, Graph.DefaultAuxiliaryRadius, 0f);
        }

        /// <summary>
        /// Pedaço solto (nó sem caminho até o resto) torna a cobertura total impossível e deixa
        /// o agente preso numa ilha. Liga o menor pedaço ao resto, até sobrar um só:
        ///   1. pelo par de nós mais próximo com RETA livre;
        ///   2. sem reta (porta vista de lado, corredor em L): pelo caminho mais curto no CHÃO
        ///      até o nó mais perto de outro pedaço, com auxiliares nas curvas.
        /// Só falha se o chão em si não liga os dois pedaços — aí é geometria (porta estreita
        /// demais para o corpo, móvel fechando a passagem) e o Console diz onde.
        /// </summary>
        private int BridgeComponents(Draft draft, WalkGrid grid, List<long> added)
        {
            int bridges = 0;

            while (true)
            {
                int[] component = draft.Components(out int count);
                if (count <= 1)
                    return bridges;

                // O menor pedaço.
                var sizes = new int[count];
                for (int i = 0; i < draft.Count; i++)
                {
                    if (component[i] >= 0)
                        sizes[component[i]]++;
                }

                int smallest = 0;
                for (int c = 1; c < count; c++)
                {
                    if (sizes[c] < sizes[smallest])
                        smallest = c;
                }

                int bestA = -1;
                int bestB = -1;
                float bestDistance = float.MaxValue;

                for (int a = 0; a < draft.Count; a++)
                {
                    if (component[a] != smallest)
                        continue;

                    for (int b = 0; b < draft.Count; b++)
                    {
                        if (component[b] < 0 || component[b] == smallest)
                            continue;

                        float distance = PlanarDistance(draft.Positions[a], draft.Positions[b]);
                        if (distance < bestDistance && IsSegmentClearBothWays(draft.Positions[a], draft.Positions[b]))
                        {
                            bestDistance = distance;
                            bestA = a;
                            bestB = b;
                        }
                    }
                }

                if (bestA >= 0)
                {
                    if (draft.AddEdge(bestA, bestB))
                        added.Add(Draft.Key(bestA, bestB));
                    bridges++;
                    continue;
                }

                if (grid != null && BridgeByWalking(draft, grid, component, smallest, added))
                {
                    bridges++;
                    continue;
                }

                var loose = new List<string>();
                for (int i = 0; i < draft.Count && loose.Count < 6; i++)
                {
                    if (component[i] == smallest)
                    {
                        loose.Add(NodeLabel(draft, i));
                        if (draft.Source[i] != null)
                            _unresolved.Add(draft.Source[i]);
                    }
                }

                Debug.LogError(
                    $"{name}: {count} pedaços de grafo e NENHUM caminho de chão entre eles para o corpo (1.70 m de " +
                    $"largura). Pedaço solto: {string.Join(", ", loose)}. É geometria: uma porta estreita demais ou um " +
                    "móvel fechando a passagem — ou esses nós estão fora do prédio. X vermelho no gizmo.", this);
                return bridges;
            }
        }

        /// <summary>
        /// Nó com mais vizinhos que _maxDegree perde os excedentes na observação. Corta as
        /// ligações NOVAS mais longas desse nó, só enquanto o grafo continuar inteiro. Ligação
        /// feita à mão não é cortada; se ela sozinha estoura o teto, o aviso é seu.
        /// </summary>
        private int PruneDegree(Draft draft, List<long> candidates)
        {
            int pruned = 0;
            candidates.Sort((l, r) => EdgeLength(draft, r).CompareTo(EdgeLength(draft, l)));

            foreach (long edge in candidates)
            {
                Draft.Split(edge, out int a, out int b);
                if (!draft.Edges.Contains(edge) || (draft.Degree(a) <= _maxDegree && draft.Degree(b) <= _maxDegree))
                    continue;

                draft.Edges.Remove(edge);
                draft.Components(out int count);
                if (count > 1)
                {
                    draft.Edges.Add(edge);
                    continue;
                }

                pruned++;
            }

            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && draft.Degree(i) > _maxDegree)
                {
                    Debug.LogWarning(
                        $"{name}: um nó em {draft.Positions[i]} ficou com {draft.Degree(i)} vizinhos (máx. " +
                        $"{_maxDegree}) — os excedentes somem da observação. Pode ligações à mão.", this);
                }
            }

            return pruned;
        }

        private static float EdgeLength(Draft draft, long edge)
        {
            Draft.Split(edge, out int a, out int b);
            return PlanarDistance(draft.Positions[a], draft.Positions[b]);
        }

        // ================================================================================
        // Limpeza
        // ================================================================================

        /// <summary>
        /// Apaga AUXILIARES que não servem para nada. Primário nunca: ele carrega peso de
        /// recompensa, e apagar um muda o teto do episódio — isso é decisão sua (o Console avisa).
        ///
        /// Inútil, em ordem:
        ///   1. DENTRO de obstáculo ou fora do chão andável — o corpo nunca chega nele;
        ///   2. ISOLADO — sem ligação nenhuma;
        ///   3. REDUNDANTE — sem ele a COBERTURA DO CHÃO continua na meta, ou não piora se já
        ///      estava abaixo (mesma regra do FindNodeAt), e os vizinhos dele continuam
        ///      conectados (direto, ou por uma ligação nova de reta livre entre eles). Testados
        ///      do que menos chão cobre para o que mais cobre (RemoveRedundant).
        ///
        /// O critério antigo do 3 era "nenhum ponto a mais de 6 m ANDANDO de algum nó" — que não
        /// é cobertura: com áreas de ±3 m, o chão entre dois nós a 6 m já ficava fora de todas as
        /// áreas, e a limpeza apagava justamente os nós que davam área a esse chão.
        /// </summary>
        private void PruneUseless(Draft draft, WalkGrid grid)
        {
            int blocked = 0;
            int isolated = 0;
            int redundant = 0;

            for (int i = 0; i < draft.Count; i++)
            {
                bool inside = grid.Snap(draft.Positions[i], 0.5f) < 0 || !Graph.IsBodyClear(draft.Positions[i], Graph.LinkClearance);
                if (!inside)
                    continue;

                if (draft.Primary[i])
                {
                    Debug.LogWarning(
                        $"{draft.Source[i].name}: primário dentro de obstáculo. Não apago primário sozinho — mova " +
                        "(\"Reposicionar\") ou apague e refaça a conta de recompensa.", draft.Source[i]);
                    continue;
                }

                if (TryRemove(draft, grid, i, null))
                    blocked++;
            }

            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && !draft.Primary[i] && draft.Degree(i) == 0)
                {
                    draft.Remove(i);
                    isolated++;
                }
            }

            var order = new List<int>();
            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && !draft.Primary[i])
                    order.Add(i);
            }

            // Auxiliar de PORTA não sai: ele é o ponto por onde as ligações atravessam o batente
            // (ver _doorNameContains). Tirá-lo raramente perde cobertura, mas devolve o problema
            // das ligações diagonais batendo no batente.
            List<DoorInfo> doors = FindDoors();
            order.RemoveAll(i => IsDoorNode(doors, draft.Positions[i]));
            redundant = RemoveRedundant(draft, grid, order);

            Debug.Log(
                $"{name}: removidos {blocked} auxiliar(es) dentro de obstáculo, {isolated} isolado(s) e " +
                $"{redundant} redundante(s) (a cobertura do chão continua na meta, ou não piorou).", this);
        }

        /// <param name="coverage">
        /// Null = remove sem olhar cobertura (nó dentro de obstáculo). Senão, o mapa com o estado
        /// ATUAL: a remoção só vale se o chão ruim não aumentar OU se a cobertura continuar na meta
        /// (CoverageMeets); se valer, o mapa fica com o estado novo.
        /// </param>
        private bool TryRemove(Draft draft, WalkGrid grid, int node, CoverageMap coverage)
        {
            if (!draft.Alive(node))
                return false;

            draft.Components(out int before);
            int badBefore = coverage != null ? coverage.Uncovered + coverage.Wrong : 0;

            List<int> neighbors = draft.Neighbors(node);
            var removedEdges = new List<long>();
            foreach (int n in neighbors)
            {
                long key = Draft.Key(node, n);
                draft.Edges.Remove(key);
                removedEdges.Add(key);
            }

            draft.Remove(node);
            var bypass = new List<long>();

            // Reconecta os vizinhos entre si, pela reta livre mais curta, só se precisar.
            while (true)
            {
                int[] component = draft.Components(out int after);
                if (after <= before)
                    break;

                long best = -1;
                float bestLength = float.MaxValue;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    for (int j = i + 1; j < neighbors.Count; j++)
                    {
                        int a = neighbors[i];
                        int b = neighbors[j];
                        if (component[a] == component[b] || draft.Degree(a) >= _maxDegree || draft.Degree(b) >= _maxDegree)
                            continue;

                        float length = PlanarDistance(draft.Positions[a], draft.Positions[b]);
                        if (length < bestLength && IsSegmentClearBothWays(draft.Positions[a], draft.Positions[b]))
                        {
                            bestLength = length;
                            best = Draft.Key(a, b);
                        }
                    }
                }

                if (best < 0)
                {
                    Restore(draft, node, removedEdges, bypass);
                    return false;
                }

                draft.Edges.Add(best);
                bypass.Add(best);
            }

            // Pode piorar, desde que continue na meta (CoverageMeets). Antes a regra era "nenhuma
            // célula piora", e quase todo nó cobre alguma célula sozinho: a limpeza não tirava
            // quase nada (medido no NodeTraining2: 5 de 152 auxiliares).
            if (coverage != null)
            {
                BuildCoverage(draft, grid, coverage);
                if (coverage.Uncovered + coverage.Wrong > badBefore && !CoverageMeets(grid, coverage, out _))
                {
                    Restore(draft, node, removedEdges, bypass);
                    BuildCoverage(draft, grid, coverage);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Tenta tirar cada auxiliar de <paramref name="order"/> sem tirar a cobertura da meta (ou
        /// sem piorá-la, se já estava abaixo) nem partir o grafo. Sem lista: todos os auxiliares
        /// vivos que não são de porta. A ordem é do que MENOS chão vence no FindNodeAt para o que
        /// mais vence — o que cobre pouco sai primeiro e gasta pouco da folga até a meta; empate:
        /// o mais perto de outro nó. Devolve quantos saíram.
        /// </summary>
        private int RemoveRedundant(Draft draft, WalkGrid grid, List<int> order = null)
        {
            if (order == null)
            {
                List<DoorInfo> doors = FindDoors();
                order = new List<int>();
                for (int i = 0; i < draft.Count; i++)
                {
                    if (draft.Alive(i) && !draft.Primary[i] && !IsDoorNode(doors, draft.Positions[i]))
                        order.Add(i);
                }
            }

            // Um mapa de cobertura reaproveitado: cada teste reconstrói nele (só aloca uma vez).
            CoverageMap map = BuildCoverage(draft, grid, null);
            var owned = new Dictionary<int, int>();
            var nearest = new Dictionary<int, float>();
            foreach (int i in order)
            {
                owned[i] = map.Owned[i];
                nearest[i] = NearestOther(draft, i);
            }

            order.Sort((l, r) => owned[l] != owned[r] ? owned[l].CompareTo(owned[r]) : nearest[l].CompareTo(nearest[r]));
            int removed = 0;

            for (int k = 0; k < order.Count; k++)
            {
                UnityEditor.EditorUtility.DisplayProgressBar("Removendo nós redundantes", $"{k}/{order.Count}", (float)k / order.Count);
                if (TryRemove(draft, grid, order[k], map))
                    removed++;
            }

            return removed;
        }

        private static void Restore(Draft draft, int node, List<long> removedEdges, List<long> bypass)
        {
            foreach (long edge in bypass)
                draft.Edges.Remove(edge);

            draft.Restore(node);
            foreach (long edge in removedEdges)
                draft.Edges.Add(edge);
        }

        private static float NearestOther(Draft draft, int node)
        {
            float best = float.MaxValue;
            for (int i = 0; i < draft.Count; i++)
            {
                if (i != node && draft.Alive(i))
                    best = Mathf.Min(best, PlanarDistance(draft.Positions[i], draft.Positions[node]));
            }

            return best;
        }

        private static List<int> NewIndices(Draft draft, int from)
        {
            var created = new List<int>();
            for (int i = from; i < draft.Count; i++)
            {
                if (draft.Alive(i))
                    created.Add(i);
            }

            return created;
        }

        // ================================================================================
        // Mapa do chão
        // ================================================================================

        /// <summary>
        /// Região de cada célula (índice do nó mais perto ANDANDO, -1 se nenhum) por BFS de
        /// várias fontes. <paramref name="distance"/> sai em células (métrica de 8 vizinhos).
        /// </summary>
        private int[] Regions(Draft draft, WalkGrid grid, out int[] distance)
        {
            var label = new int[grid.Count];
            distance = grid.NewDistanceField();
            var queue = new Queue<int>();

            for (int i = 0; i < grid.Count; i++)
                label[i] = -1;

            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                int cell = grid.Snap(draft.Positions[i], 1.5f);
                if (cell < 0 || label[cell] >= 0)
                    continue;

                label[cell] = i;
                distance[cell] = 0;
                queue.Enqueue(cell);
            }

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int x = grid.X(cell);
                int z = grid.Z(cell);

                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (!grid.Walkable[next] || label[next] >= 0)
                        continue;

                    label[next] = label[cell];
                    distance[next] = distance[cell] + 1;
                    queue.Enqueue(next);
                }
            }

            return label;
        }

        // BFS de uma fonte que só desce valores do campo: rodada fonte a fonte, dá a distância
        // andando até a fonte mais próxima, sem nunca expandir além de limit.
        private static void Relax(WalkGrid grid, int source, int limit, int[] distance, Queue<int> queue)
        {
            if (distance[source] == 0)
                return;

            distance[source] = 0;
            queue.Clear();
            queue.Enqueue(source);

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int next = distance[cell] + 1;
                if (next > limit)
                    continue;

                int x = grid.X(cell);
                int z = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int neighbor = grid.Index(nx, nz);
                    if (!grid.Walkable[neighbor] || distance[neighbor] <= next)
                        continue;

                    distance[neighbor] = next;
                    queue.Enqueue(neighbor);
                }
            }
        }

        /// <summary>
        /// Mede o chão da arena: caixa de todos os colliders de parede ATIVOS dela, grade de
        /// "o corpo passa?", flood fill a partir de pontos de dentro, e folga por transformada de
        /// distância (chamfer 1 / raiz de 2) — sem física extra, a folga sai da própria grade.
        /// Null se o usuário cancelar ou faltar dado.
        /// </summary>
        private WalkGrid BuildWalkGrid(float height, Draft draft)
        {
            if (!TryGetArenaBounds(out Bounds bounds))
            {
                Debug.LogError($"{name}: nenhum collider de parede ativo na arena — não há mapa para medir.", this);
                return null;
            }

            float step = Mathf.Max(0.05f, _generationStep);
            int sizeX = Mathf.CeilToInt(bounds.size.x / step) + 1;
            int sizeZ = Mathf.CeilToInt(bounds.size.z / step) + 1;
            if ((long)sizeX * sizeZ > 4_000_000)
            {
                Debug.LogError($"{name}: grade de {sizeX} x {sizeZ} é grande demais. Aumente o Generation Step.", this);
                return null;
            }

            var grid = new WalkGrid(new Vector3(bounds.min.x, height, bounds.min.z), step, sizeX, sizeZ);
            var free = new bool[grid.Count];

            // Com a área cortada pela parede (NavGraph.AreasStopAtWalls), a cobertura precisa
            // saber o que BLOQUEIA A VISÃO na altura dos nós — a mesma altura do raycast do jogo.
            // Uma caixinha do tamanho da célula, fina, no plano dos nós. O alcance da visibilidade
            // é o maior raio que um nó pode ter nesta operação.
            bool clip = Graph.AreasStopAtWalls;
            if (clip)
            {
                grid.SightBlocked = new bool[grid.Count];
                float maxRadius = Mathf.Max(_maxAuxiliaryRadius, Mathf.Max(Graph.DefaultPrimaryRadius, Graph.DefaultAuxiliaryRadius));
                for (int i = 0; i < draft.Count; i++)
                    maxRadius = Mathf.Max(maxRadius, draft.Radii[i]);

                grid.VisReach = Mathf.CeilToInt(maxRadius / step) + 1;
            }

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            var sightExtents = new Vector3(step * 0.5f, 0.05f, step * 0.5f);

            for (int z = 0; z < sizeZ; z++)
            {
                if (UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                        "Medindo o chão", $"linha {z}/{sizeZ}", (float)z / sizeZ))
                {
                    Debug.LogWarning($"{name}: cancelado.", this);
                    return null;
                }

                for (int x = 0; x < sizeX; x++)
                {
                    int cell = grid.Index(x, z);
                    free[cell] = Graph.IsBodyClear(grid.Position(cell), Graph.LinkClearance);

                    if (clip)
                    {
                        grid.SightBlocked[cell] = physics.OverlapBox(
                            grid.Position(cell), sightExtents, _overlapBuffer, Quaternion.identity,
                            Graph.WallLayer, QueryTriggerInteraction.Ignore) > 0;
                    }
                }
            }

            // Flood fill a partir de pontos de dentro: o que está fora do prédio (livre, mas
            // inalcançável) não pode receber nó.
            var queue = new Queue<int>();
            foreach (Vector3 seed in Seeds(draft))
            {
                int cell = grid.SnapTo(free, seed, 2f);
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
                int x = grid.X(cell);
                int z = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (!free[next] || grid.Walkable[next])
                        continue;

                    grid.Walkable[next] = true;
                    queue.Enqueue(next);
                }
            }

            grid.ComputeClearance(Graph.LinkClearance);
            return grid;
        }

        private IEnumerable<Vector3> Seeds(Draft draft)
        {
            if (_generationSeeds != null)
            {
                foreach (Transform seed in _generationSeeds)
                {
                    if (seed != null)
                        yield return seed.position;
                }
            }

            for (int i = 0; i < draft.Count; i++)
                yield return draft.Positions[i];

            GraphExplorerManager agent = ArenaRoot.GetComponentInChildren<GraphExplorerManager>();
            if (agent != null)
                yield return agent.transform.position;
        }

        private bool TryGetArenaBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;

            foreach (Collider collider in ArenaRoot.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || (Graph.WallLayer.value & (1 << collider.gameObject.layer)) == 0)
                    continue;

                if (any)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    any = true;
                }
            }

            return any;
        }

        // Plano dos nós: a altura mediana dos que existem (os do NodeTraining variam 13 cm);
        // sem nó nenhum, a do Graph + _generationHeight.
        private float GridHeight()
        {
            var heights = new List<float>();
            foreach (NavNode node in ValidNodes())
                heights.Add(node.Position.y);

            if (heights.Count == 0)
                return transform.position.y + _generationHeight;

            heights.Sort();
            return heights[heights.Count / 2];
        }

        private Transform ArenaRoot
        {
            get
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                return arena != null ? arena.transform : transform.root;
            }
        }

        // Auxiliar a menos de MEIO RAIO de um primário eclipsa o primário no FindNodeAt (o
        // centro mais perto vence): o agente passaria pelo primário sem registrar a visita.
        private bool ShadowsPrimary(Draft draft, Vector3 position)
        {
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i) || !draft.Primary[i])
                    continue;

                if (Graph.AreaDistance(position, draft.Positions[i]) < draft.Radii[i] * 0.5f)
                    return true;
            }

            return false;
        }

        // Algum nó vivo a menos de _minNodeSpacing do ponto? Nó novo ali seria um par colado.
        private bool IsCrowded(Draft draft, Vector3 position)
        {
            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) && PlanarDistance(draft.Positions[i], position) < _minNodeSpacing)
                    return true;
            }

            return false;
        }

        // O nó vivo mais perto do ponto, a até maxDistance, que passa no teste; -1 se nenhum. É o
        // "reaproveite o que já está ali" do portal, das curvas do caminho e do desvio.
        private static int NearestUsable(Draft draft, Vector3 position, float maxDistance, System.Func<int, bool> usable)
        {
            var near = new List<(float distance, int node)>();
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                float distance = PlanarDistance(draft.Positions[i], position);
                if (distance < maxDistance)
                    near.Add((distance, i));
            }

            near.Sort((l, r) => l.distance.CompareTo(r.distance));
            foreach ((float _, int node) in near)
            {
                if (usable(node))
                    return node;
            }

            return -1;
        }

        // Apagar filho de uma instância de prefab não é permitido pelo Unity — e seria a forma
        // errada de mexer no NodeTraining de qualquer jeito (valeria só para aquela cópia).
        private bool CanRemoveNodes()
        {
            if (!UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject))
                return true;

            Debug.LogError(
                $"{name}: este grafo é uma instância de prefab — nós não podem ser apagados daqui. Abra o " +
                "prefab (duplo clique no NodeTraining) e rode no Prefab Mode.", this);
            return false;
        }

        // ================================================================================
        // Rascunho <-> cena
        // ================================================================================

        private Draft DraftFromScene()
        {
            List<NavNode> nodes = ValidNodes();
            var draft = new Draft();
            var index = new Dictionary<NavNode, int>();

            foreach (NavNode node in nodes)
            {
                // Ping entra no rascunho como "primário": para o placer os dois são nó de CHEGADA
                // (raio apertado, nunca apagado, posse protegida). O peso só vai para a soma do
                // orçamento quando é de exploração — o do ping é de outro prêmio.
                index[node] = draft.Add(node.Position, node.IsTarget, node, Graph.RadiusOf(node),
                    node.IsPrimary ? node.ExplorationWeight : 0f);
            }

            foreach (KeyValuePair<NavNode, List<NavNode>> entry in BuildAdjacency(nodes))
            {
                foreach (NavNode neighbor in entry.Value)
                    draft.AddEdge(index[entry.Key], index[neighbor]);
            }

            draft.SceneEdges.UnionWith(draft.Edges);
            return draft;
        }

        /// <summary>
        /// Leva o rascunho para a cena com a MENOR mudança possível: cria os nós novos, apaga os
        /// removidos e, nas listas de vizinhos, só tira o que saiu e acrescenta o que entrou —
        /// uma ligação que já existia fica declarada do lado em que você a declarou. O RAIO de
        /// cada nó vai para o override (0 quando dá o padrão do papel) e o PESO dos primários
        /// criados sai do rascunho (orçamento repartido).
        /// </summary>
        private void ApplyDraft(Draft draft)
        {
            var nodes = new NavNode[draft.Count];
            int created = 0;
            int destroyed = 0;

            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Source[i] != null)
                {
                    nodes[i] = draft.Source[i];
                    UnityEditor.Undo.RecordObject(nodes[i], "Grafo");
                    continue;
                }

                if (!draft.Alive(i))
                    continue;

                string kind = draft.Primary[i] ? "P" : "A";
                var go = new GameObject($"Node (auto {kind}{i})");
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Grafo");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = draft.Positions[i];

                nodes[i] = go.AddComponent<NavNode>();
                nodes[i].SetKind(draft.Primary[i] ? NodeKind.Primary : NodeKind.Auxiliary);
                nodes[i].SetExplorationWeight(draft.Primary[i] ? draft.Weights[i] : 0f);
                created++;
            }

            var alive = new Dictionary<NavNode, int>();
            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i))
                    alive[nodes[i]] = i;
            }

            // Tira o que saiu.
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i) || draft.Source[i] == null)
                    continue;

                int self = i;
                draft.Source[i].EditableNeighbors.RemoveAll(n =>
                    n == null || !alive.TryGetValue(n, out int other) || !draft.Edges.Contains(Draft.Key(self, other)));
            }

            // Põe o que entrou.
            foreach (long edge in draft.Edges)
            {
                Draft.Split(edge, out int a, out int b);
                if (!draft.Alive(a) || !draft.Alive(b))
                    continue;

                if (!nodes[a].IsNeighbor(nodes[b]) && !nodes[b].IsNeighbor(nodes[a]))
                    nodes[a].EditableNeighbors.Add(nodes[b]);
            }

            // Raio: o override só guarda a EXCEÇÃO. Perto do padrão do papel (5 cm) vira 0, para o
            // padrão do NavGraph continuar sendo o lugar em que se calibra o mapa inteiro.
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                float fallback = draft.Primary[i] ? Graph.DefaultPrimaryRadius : Graph.DefaultAuxiliaryRadius;
                float value = Mathf.Abs(draft.Radii[i] - fallback) < 0.05f ? 0f : Mathf.Round(draft.Radii[i] * 100f) / 100f;
                if (!Mathf.Approximately(nodes[i].RadiusOverride, value))
                    nodes[i].SetRadiusOverride(value);

                MarkModified(nodes[i]);
            }

            for (int i = 0; i < draft.Count; i++)
            {
                if (draft.Alive(i) || draft.Source[i] == null)
                    continue;

                UnityEditor.Undo.DestroyObjectImmediate(draft.Source[i].gameObject);
                destroyed++;
            }

            Graph.CollectChildNodes();
            Physics.SyncTransforms();
            LogSummary(draft, created, destroyed);
        }

        private void LogSummary(Draft draft, int created, int destroyed)
        {
            int primaries = 0;
            int auxiliaries = 0;
            float weight = 0f;
            int maxDegree = 0;
            float longest = 0f;

            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                if (draft.Primary[i])
                {
                    primaries++;
                    weight += draft.Weights[i];
                }
                else
                {
                    auxiliaries++;
                }

                maxDegree = Mathf.Max(maxDegree, draft.Degree(i));
            }

            foreach (long edge in draft.Edges)
            {
                Draft.Split(edge, out int a, out int b);
                if (draft.Alive(a) && draft.Alive(b))
                    longest = Mathf.Max(longest, EdgeLength(draft, edge));
            }

            float diameter = PathDiameter(draft);

            draft.Components(out int pieces);
            if (pieces > 1)
            {
                Debug.LogError(
                    $"{name}: o grafo terminou em {pieces} PEDAÇOS — algum nó não alcança os outros. Rode " +
                    "\"1. Diagnosticar\" para ver quais (X vermelho) e o motivo no Console.", this);
            }
            string budget = Mathf.Abs(weight - _primaryWeightBudget) > 0.05f
                ? $" DIFERENTE do orçamento ({_primaryWeightBudget:0.##}) — rode \"8. Normalizar pesos\" ou refaça a conta de recompensa"
                : " (= orçamento)";

            Debug.Log(
                $"{name}: grafo com {primaries} primários (peso total {weight:0.##}{budget}), {auxiliaries} auxiliares, " +
                $"{draft.Edges.Count} ligações, grau máx. {maxDegree}, maior aresta {longest:0.0} m, diâmetro " +
                $"{diameter:0} m pelo grafo — {created} nó(s) criado(s), {destroyed} apagado(s). Confira " +
                "_maxNodeDistance do GraphExplorerManager (~ maior aresta). O diâmetro é só informativo: o " +
                "agente normaliza as distâncias de caminho por ele sozinho (NavGraph.PathDiameter).", this);
        }

        // Maior caminho mais curto (em metros, pelas arestas do rascunho) entre dois nós vivos.
        // Dijkstra simples O(V²) por origem: roda uma vez por menu, e V fica na casa das centenas.
        private static float PathDiameter(Draft draft)
        {
            int n = draft.Count;
            var neighbors = new List<int>[n];
            for (int i = 0; i < n; i++)
                neighbors[i] = new List<int>();

            foreach (long edge in draft.Edges)
            {
                Draft.Split(edge, out int a, out int b);
                if (!draft.Alive(a) || !draft.Alive(b))
                    continue;

                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }

            var cost = new float[n];
            var done = new bool[n];
            float diameter = 0f;

            for (int source = 0; source < n; source++)
            {
                if (!draft.Alive(source))
                    continue;

                for (int i = 0; i < n; i++)
                {
                    cost[i] = float.MaxValue;
                    done[i] = false;
                }

                cost[source] = 0f;
                while (true)
                {
                    int current = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (!done[i] && cost[i] < float.MaxValue && (current < 0 || cost[i] < cost[current]))
                            current = i;
                    }

                    if (current < 0)
                        break;

                    done[current] = true;
                    diameter = Mathf.Max(diameter, cost[current]);

                    foreach (int next in neighbors[current])
                    {
                        float c = cost[current] + PlanarDistance(draft.Positions[current], draft.Positions[next]);
                        if (c < cost[next])
                            cost[next] = c;
                    }
                }
            }

            return diameter;
        }

        // ================================================================================
        // Caminho pelo chão
        // ================================================================================

        /// <summary>
        /// Liga <paramref name="a"/> a <paramref name="b"/> seguindo o chão (só por células em que
        /// <paramref name="allowed"/> vale), com auxiliares nas curvas do caminho.
        /// </summary>
        private bool ConnectByWalking(Draft draft, WalkGrid grid, int a, int b, System.Func<int, bool> allowed, List<long> added)
        {
            int start = grid.Snap(draft.Positions[a], 1.5f);
            int goal = grid.Snap(draft.Positions[b], 1.5f);
            if (start < 0 || goal < 0)
                return false;

            List<int> path = WalkPath(grid, new List<int> { start }, cell => cell == goal, allowed);
            return path != null && InsertChain(draft, grid, a, b, path, added);
        }

        // Caminho pelo chão do menor pedaço até o nó mais perto (andando) de qualquer outro pedaço.
        private bool BridgeByWalking(Draft draft, WalkGrid grid, int[] component, int smallest, List<long> added)
        {
            var nodeAt = new Dictionary<int, int>();
            var sources = new List<int>();
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                int cell = grid.Snap(draft.Positions[i], 1.5f);
                if (cell < 0 || nodeAt.ContainsKey(cell))
                    continue;

                nodeAt[cell] = i;
                if (component[i] == smallest)
                    sources.Add(cell);
            }

            if (sources.Count == 0)
                return false;

            List<int> path = WalkPath(grid, sources,
                cell => nodeAt.TryGetValue(cell, out int n) && component[n] >= 0 && component[n] != smallest,
                _ => true);
            if (path == null)
                return false;

            return InsertChain(draft, grid, nodeAt[path[0]], nodeAt[path[path.Count - 1]], path, added);
        }

        /// <summary>
        /// Transforma um caminho de células em ligações: "puxa a corda" (de cada ponto, vai até o
        /// ponto mais adiante do caminho ainda com reta livre para o corpo) e põe um auxiliar em
        /// cada curva. Como as células do caminho são todas andáveis, cada trecho curto passa —
        /// então a cadeia sempre fecha, até dentro de uma porta vista de lado.
        /// </summary>
        private bool InsertChain(Draft draft, WalkGrid grid, int a, int b, List<int> cells, List<long> added)
        {
            // Os pontos do caminho ficam na altura do nó de origem: a cadeia nova fica no mesmo
            // plano dos nós que ela liga.
            float y = draft.Positions[a].y;
            var points = new List<Vector3>(cells.Count + 2) { draft.Positions[a] };
            foreach (int cell in cells)
            {
                Vector3 center = grid.Position(cell);
                points.Add(new Vector3(center.x, y, center.z));
            }

            points.Add(draft.Positions[b]);

            var waypoints = new List<Vector3>();
            int last = points.Count - 1;
            int s = 0;
            while (!IsSegmentClearBothWays(points[s], points[last]))
            {
                int j = s + 1;
                if (!IsSegmentClearBothWays(points[s], points[j]))
                    return false;

                while (j + 1 < last && IsSegmentClearBothWays(points[s], points[j + 1]))
                    j++;

                waypoints.Add(points[j]);
                s = j;

                // Trava contra caminho degenerado: 64 curvas é um labirinto, não um mapa.
                if (waypoints.Count > 64)
                    return false;
            }

            // Cada curva reaproveita um nó que já esteja a menos de _minNodeSpacing dela, se ele
            // enxerga o trecho de trás e o da frente (inclusive o próprio nó anterior: aí a curva
            // some). Sem nó assim, cria — o caminho tem que fechar, então aqui o espaçamento é
            // preferência, não regra. Antes toda curva virava nó novo, e as curvas de uma porta
            // caíam coladas no nó da porta.
            int previous = a;
            for (int k = 0; k < waypoints.Count; k++)
            {
                Vector3 from = draft.Positions[previous];
                Vector3 next = k + 1 < waypoints.Count ? waypoints[k + 1] : draft.Positions[b];
                int node = NearestUsable(draft, waypoints[k], _minNodeSpacing,
                    i => IsSegmentClearBothWays(from, draft.Positions[i]) && IsSegmentClearBothWays(draft.Positions[i], next));

                if (node == previous)
                    continue;

                if (node == b)
                    break;

                if (node < 0)
                    node = draft.Add(waypoints[k], primary: false, source: null, Graph.DefaultAuxiliaryRadius, 0f);

                if (draft.AddEdge(previous, node))
                    added.Add(Draft.Key(previous, node));
                previous = node;
            }

            if (previous != b && draft.AddEdge(previous, b))
                added.Add(Draft.Key(previous, b));
            return true;
        }

        /// <summary>
        /// Caminho mais curto no chão andável (8 vizinhos) de qualquer célula de
        /// <paramref name="sources"/> até a primeira que satisfaz <paramref name="isGoal"/>, só por
        /// células permitidas. O custo cresce perto de parede (folga abaixo de _desiredClearance),
        /// então o caminho corre pelo meio de corredores e salas e só encosta onde precisa — numa
        /// porta. Devolve as células da origem ao destino, ou null.
        /// </summary>
        private List<int> WalkPath(WalkGrid grid, List<int> sources, System.Func<int, bool> isGoal, System.Func<int, bool> allowed)
        {
            var cost = new float[grid.Count];
            var parent = new int[grid.Count];
            for (int i = 0; i < grid.Count; i++)
            {
                cost[i] = float.MaxValue;
                parent[i] = -1;
            }

            var heap = new CellHeap();
            foreach (int source in sources)
            {
                cost[source] = 0f;
                heap.Push(source, 0f);
            }

            int found = -1;
            while (heap.Count > 0)
            {
                heap.Pop(out int cell, out float c);
                if (c > cost[cell])
                    continue;

                if (isGoal(cell))
                {
                    found = cell;
                    break;
                }

                int x = grid.X(cell);
                int z = grid.Z(cell);
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + StepX[d];
                    int nz = z + StepZ[d];
                    if (!grid.InBounds(nx, nz))
                        continue;

                    int next = grid.Index(nx, nz);
                    if (!grid.Walkable[next] || !allowed(next))
                        continue;

                    // 1 m perto da parede custa até 3 m no meio da sala.
                    float squeeze = Mathf.Max(0f, _desiredClearance - grid.Clearance[next]) / Mathf.Max(0.01f, _desiredClearance);
                    float step = (d < 4 ? 1f : 1.41421356f) * (1f + 2f * squeeze);
                    float nextCost = c + step;
                    if (nextCost >= cost[next])
                        continue;

                    cost[next] = nextCost;
                    parent[next] = cell;
                    heap.Push(next, nextCost);
                }
            }

            if (found < 0)
                return null;

            var path = new List<int>();
            for (int cell = found; cell >= 0; cell = parent[cell])
                path.Add(cell);

            path.Reverse();
            return path;
        }

        /// <summary>Heap mínimo (custo, célula) com remoção preguiçosa: WalkPath, profundidade do chão ruim e a fila da gulosa.</summary>
        private sealed class CellHeap
        {
            private readonly List<int> _cells = new List<int>();
            private readonly List<float> _costs = new List<float>();

            public int Count => _cells.Count;

            public float PeekCost => _costs[0];

            public void Push(int cell, float cost)
            {
                _cells.Add(cell);
                _costs.Add(cost);
                int i = _cells.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_costs[p] <= _costs[i])
                        break;

                    Swap(i, p);
                    i = p;
                }
            }

            public void Pop(out int cell, out float cost)
            {
                cell = _cells[0];
                cost = _costs[0];
                int last = _cells.Count - 1;
                Swap(0, last);
                _cells.RemoveAt(last);
                _costs.RemoveAt(last);

                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1;
                    if (l >= _cells.Count)
                        break;

                    int m = l + 1 < _cells.Count && _costs[l + 1] < _costs[l] ? l + 1 : l;
                    if (_costs[i] <= _costs[m])
                        break;

                    Swap(i, m);
                    i = m;
                }
            }

            private void Swap(int a, int b)
            {
                (_cells[a], _cells[b]) = (_cells[b], _cells[a]);
                (_costs[a], _costs[b]) = (_costs[b], _costs[a]);
            }
        }

        // ================================================================================
        // Portas
        // ================================================================================

        /// <summary>Um batente de porta da arena: meio do vão e a direção ao longo da parede.</summary>
        private struct DoorInfo
        {
            public Transform Source;
            public Vector3 Center;
            public Vector3 Along;
            public float HalfWidth;

            // Meia-espessura da PAREDE no vão (a do pilar mais fino), na normal da parede. É a
            // profundidade do ladrilho de porta (menu 9), que preenche o buraco exatamente.
            public float HalfDepth;
        }

        /// <summary>
        /// Batentes de porta ATIVOS da arena (nome contém _doorNameContains). O meio do vão é o
        /// ponto médio entre os dois colliders ALTOS mais distantes do objeto — os dois pilares do
        /// batente (a laje baixa do piso fica de fora por não ter 1 m de altura). Sem dois
        /// pilares, cai no centro dos colliders.
        /// </summary>
        private List<DoorInfo> FindDoors()
        {
            var doors = new List<DoorInfo>();
            if (string.IsNullOrEmpty(_doorNameContains))
                return doors;

            foreach (Transform t in ArenaRoot.GetComponentsInChildren<Transform>())
            {
                if (!t.name.Contains(_doorNameContains))
                    continue;

                var tall = new List<Bounds>();
                Bounds all = default;
                bool any = false;
                foreach (Collider collider in t.GetComponents<Collider>())
                {
                    if (!collider.enabled || collider.isTrigger)
                        continue;

                    if (any)
                        all.Encapsulate(collider.bounds);
                    else
                        all = collider.bounds;
                    any = true;

                    if (collider.bounds.size.y >= 1f)
                        tall.Add(collider.bounds);
                }

                var door = new DoorInfo
                {
                    Source = t, Center = any ? all.center : t.position, Along = t.right, HalfWidth = 1f,
                    HalfDepth = any ? Mathf.Max(0.05f, PlanarExtent(all, new Vector3(-t.right.z, 0f, t.right.x))) : 0.1f,
                };
                float widest = -1f;
                for (int i = 0; i < tall.Count; i++)
                {
                    for (int j = i + 1; j < tall.Count; j++)
                    {
                        float distance = PlanarDistance(tall[i].center, tall[j].center);
                        if (distance <= widest)
                            continue;

                        widest = distance;
                        door.Center = (tall[i].center + tall[j].center) * 0.5f;
                        Vector3 along = tall[j].center - tall[i].center;
                        along.y = 0f;
                        door.Along = along.normalized;

                        // Meia-largura do VÃO: do meio até a face interna do pilar.
                        float pillar = Mathf.Min(PlanarExtent(tall[i], door.Along), PlanarExtent(tall[j], door.Along));
                        door.HalfWidth = Mathf.Max(0.1f, distance * 0.5f - pillar);

                        var normal = new Vector3(-door.Along.z, 0f, door.Along.x);
                        door.HalfDepth = Mathf.Max(0.05f, Mathf.Min(PlanarExtent(tall[i], normal), PlanarExtent(tall[j], normal)));
                    }
                }

                doors.Add(door);
            }

            return doors;
        }

        private static float PlanarExtent(Bounds bounds, Vector3 direction) =>
            Mathf.Abs(direction.x) * bounds.extents.x + Mathf.Abs(direction.z) * bounds.extents.z;

        private static bool IsDoorNode(List<DoorInfo> doors, Vector3 position)
        {
            foreach (DoorInfo door in doors)
            {
                if (PlanarDistance(door.Center, position) < 1f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Um auxiliar no meio de cada vão de porta que ainda não tem nó a menos de 1 m. É o ponto
        /// por onde as ligações atravessam o batente de frente. Vão que não é chão andável (o corpo
        /// não cabe) é avisado: é porta estreita demais, não problema do grafo.
        /// </summary>
        private void AddDoorNodes(Draft draft, WalkGrid grid)
        {
            int created = 0;
            int existing = 0;
            int closed = 0;

            foreach (DoorInfo door in FindDoors())
            {
                var onPlane = new Vector3(door.Center.x, grid.Min.y, door.Center.z);
                int cell = grid.Snap(onPlane, 1f);
                if (cell < 0)
                {
                    closed++;
                    Debug.LogWarning(
                        $"{door.Source.name}: o vão desta porta não é chão andável para o corpo (1.70 m) — porta " +
                        "estreita demais, móvel no caminho ou fora do prédio. Nenhum nó foi posto nela.", door.Source);
                    continue;
                }

                Vector3 position = grid.Position(cell);
                bool has = false;
                for (int i = 0; i < draft.Count && !has; i++)
                    has = draft.Alive(i) && PlanarDistance(draft.Positions[i], position) < 1f;

                if (has)
                {
                    existing++;
                    continue;
                }

                draft.Add(position, primary: false, source: null, Graph.DefaultAuxiliaryRadius, 0f);
                created++;
            }

            if (created + existing + closed > 0)
            {
                Debug.Log(
                    $"{name}: portas — {created} auxiliar(es) novo(s) no vão, {existing} já tinham nó, {closed} fechada(s) " +
                    "para o corpo.", this);
            }
        }

        /// <summary>
        /// Confere que toda porta tem uma ligação ATRAVESSANDO o vão (e não só um nó perto dela).
        /// Porta sem travessia = as salas dos dois lados só se ligam dando a volta, ou nem isso.
        /// </summary>
        private int ReportDoors(Draft draft)
        {
            List<DoorInfo> doors = FindDoors();
            int missing = 0;

            foreach (DoorInfo door in doors)
            {
                if (DoorCrossed(draft, door))
                    continue;

                missing++;
                Debug.LogWarning(
                    $"{door.Source.name}: nenhuma ligação atravessa esta porta — as salas dos dois lados não se " +
                    "ligam por ela. \"4. Ajustar raios e cobrir o chão\" põe um nó no vão e liga pelo chão.", door.Source);
            }

            if (doors.Count > 0)
                Debug.Log($"{name}: {doors.Count - missing} de {doors.Count} porta(s) com ligação atravessando o vão.", this);

            return missing;
        }

        /// <summary>
        /// Alguma ligação atravessa o vão? Vale uma aresta cruzando a linha da porta, ou o NÓ DA
        /// PORTA (em cima da linha) com vizinhos dos dois lados — uma aresta que só encosta na
        /// linha, no nó da porta, não "cruza" no teste de segmentos, mas o caminho vizinho → porta
        /// → vizinho atravessa. Antes só a primeira forma contava.
        /// </summary>
        private bool DoorCrossed(Draft draft, DoorInfo door)
        {
            Vector3 a = door.Center - door.Along * door.HalfWidth;
            Vector3 b = door.Center + door.Along * door.HalfWidth;
            foreach (long edge in draft.Edges)
            {
                Draft.Split(edge, out int p, out int q);
                if (draft.Alive(p) && draft.Alive(q) && SegmentsCross(draft.Positions[p], draft.Positions[q], a, b))
                    return true;
            }

            int node = DoorNodeOf(draft, door);
            return node >= 0 && HasNeighborOnSide(draft, node, door, 1f) && HasNeighborOnSide(draft, node, door, -1f);
        }

        // O nó da porta: o vivo mais perto do meio do vão, a menos de 1 m (a regra do AddDoorNodes).
        private static int DoorNodeOf(Draft draft, DoorInfo door)
        {
            int best = -1;
            float bestDistance = 1f;
            for (int i = 0; i < draft.Count; i++)
            {
                if (!draft.Alive(i))
                    continue;

                float distance = PlanarDistance(draft.Positions[i], door.Center);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        // Distância com sinal do ponto até a linha da porta (a normal da parede). 0.5 m é "do
        // lado de lá": o nó da porta fica na linha (a grade o desloca até ~0.2 m).
        private static float DoorSide(DoorInfo door, Vector3 position)
        {
            var normal = new Vector3(-door.Along.z, 0f, door.Along.x);
            return Vector3.Dot(position - door.Center, normal);
        }

        private const float DoorSideMargin = 0.5f;

        private static bool HasNeighborOnSide(Draft draft, int node, DoorInfo door, float sign)
        {
            foreach (int neighbor in draft.Neighbors(node))
            {
                if (DoorSide(door, draft.Positions[neighbor]) * sign > DoorSideMargin)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Todo nó de porta ligado a um nó de CADA lado da parede. A ligação por região não
        /// garante isso: medido no NodeTraining2, duas portas tinham o nó do vão com uma ligação
        /// só — um lado só —, e as salas não se ligavam por ali. Para cada lado sem vizinho: o nó
        /// mais perto daquele lado com reta livre; sem reta, o caminho pelo chão (curvas viram
        /// auxiliares, reaproveitando os que já existem).
        /// </summary>
        private void LinkDoors(Draft draft, WalkGrid grid, List<long> added)
        {
            int linked = 0;
            int failed = 0;

            foreach (DoorInfo door in FindDoors())
            {
                int node = DoorNodeOf(draft, door);
                if (node < 0)
                    continue;

                foreach (float sign in new[] { 1f, -1f })
                {
                    if (HasNeighborOnSide(draft, node, door, sign))
                        continue;

                    var candidates = new List<(float distance, int other)>();
                    for (int i = 0; i < draft.Count; i++)
                    {
                        if (i != node && draft.Alive(i) && DoorSide(door, draft.Positions[i]) * sign > DoorSideMargin)
                            candidates.Add((PlanarDistance(draft.Positions[i], draft.Positions[node]), i));
                    }

                    candidates.Sort((l, r) => l.distance.CompareTo(r.distance));
                    int target = -1;
                    for (int k = 0; k < candidates.Count && k < 12 && target < 0; k++)
                    {
                        if (IsSegmentClearBothWays(draft.Positions[node], draft.Positions[candidates[k].other]))
                            target = candidates[k].other;
                    }

                    if (target >= 0)
                    {
                        if (draft.AddEdge(node, target))
                            added.Add(Draft.Key(node, target));
                        linked++;
                        continue;
                    }

                    if (ConnectToDoorSide(draft, grid, door, node, sign, added))
                        linked++;
                    else
                        failed++;
                }
            }

            if (linked > 0)
                Debug.Log($"{name}: {linked} lado(s) de porta que estavam sem vizinho foram ligados.", this);

            if (failed > 0)
            {
                Debug.LogWarning(
                    $"{name}: {failed} lado(s) de porta sem nenhum caminho de chão até um nó daquele lado — veja as " +
                    "portas acusadas no relatório abaixo.", this);
            }
        }

        // Caminho pelo chão do nó da porta até o nó mais perto (andando) do lado pedido.
        private bool ConnectToDoorSide(Draft draft, WalkGrid grid, DoorInfo door, int node, float sign, List<long> added)
        {
            int start = grid.Snap(draft.Positions[node], 1.5f);
            if (start < 0)
                return false;

            var nodeAt = new Dictionary<int, int>();
            for (int i = 0; i < draft.Count; i++)
            {
                if (i == node || !draft.Alive(i) || DoorSide(door, draft.Positions[i]) * sign <= DoorSideMargin)
                    continue;

                int cell = grid.Snap(draft.Positions[i], 1.5f);
                if (cell >= 0 && !nodeAt.ContainsKey(cell))
                    nodeAt[cell] = i;
            }

            if (nodeAt.Count == 0)
                return false;

            List<int> path = WalkPath(grid, new List<int> { start }, cell => nodeAt.ContainsKey(cell), _ => true);
            return path != null && InsertChain(draft, grid, node, nodeAt[path[path.Count - 1]], path, added);
        }

        /// <summary>
        /// Tira as ligações REDUNDANTES: A–B sai quando existe um C ligado aos dois com as duas
        /// pernas mais curtas que A–B (grafo de vizinhança relativa, restrito aos triângulos do
        /// próprio grafo). A ligação por região liga TODO par de regiões que se encostam, o que
        /// num grupo de nós vira uma triangulação — medido no NodeTraining2: 76 triângulos e 73
        /// ligações assim, contra 6 e 4 no grafo feito à mão. O caminho A–C–B continua existindo,
        /// então o grafo nunca parte (e cada remoção posterior tem a sua própria testemunha).
        ///
        /// Da mais longa para a mais curta. Não sai: ligação feita à mão (SceneEdges) nem uma
        /// cuja falta deixaria alguma porta sem travessia (DoorCrossed).
        /// </summary>
        private void PruneRedundantEdges(Draft draft)
        {
            List<DoorInfo> doors = FindDoors();
            var doorNode = new int[doors.Count];
            for (int d = 0; d < doors.Count; d++)
                doorNode[d] = DoorNodeOf(draft, doors[d]);

            var adjacency = new Dictionary<int, HashSet<int>>();
            var edges = new List<long>();
            foreach (long edge in draft.Edges)
            {
                Draft.Split(edge, out int a, out int b);
                if (!draft.Alive(a) || !draft.Alive(b))
                    continue;

                NeighborSet(adjacency, a).Add(b);
                NeighborSet(adjacency, b).Add(a);
                if (!draft.SceneEdges.Contains(edge))
                    edges.Add(edge);
            }

            edges.Sort((l, r) => EdgeLength(draft, r).CompareTo(EdgeLength(draft, l)));
            int pruned = 0;

            foreach (long edge in edges)
            {
                Draft.Split(edge, out int a, out int b);
                float length = EdgeLength(draft, edge);
                bool redundant = false;

                foreach (int c in adjacency[a])
                {
                    if (c == b || !adjacency[b].Contains(c))
                        continue;

                    float ac = PlanarDistance(draft.Positions[a], draft.Positions[c]);
                    float cb = PlanarDistance(draft.Positions[c], draft.Positions[b]);
                    if (Mathf.Max(ac, cb) < length - 0.01f)
                    {
                        redundant = true;
                        break;
                    }
                }

                if (!redundant)
                    continue;

                // As portas que esta ligação pode estar atravessando: a dela como nó de porta, ou
                // a que o segmento cruza. Tira, confere e devolve se alguma ficou sem travessia.
                var affected = new List<int>();
                for (int d = 0; d < doors.Count; d++)
                {
                    Vector3 da = doors[d].Center - doors[d].Along * doors[d].HalfWidth;
                    Vector3 db = doors[d].Center + doors[d].Along * doors[d].HalfWidth;
                    if (doorNode[d] == a || doorNode[d] == b || SegmentsCross(draft.Positions[a], draft.Positions[b], da, db))
                        affected.Add(d);
                }

                draft.Edges.Remove(edge);
                if (affected.Exists(d => !DoorCrossed(draft, doors[d])))
                {
                    draft.Edges.Add(edge);
                    continue;
                }

                adjacency[a].Remove(b);
                adjacency[b].Remove(a);
                pruned++;
            }

            if (pruned > 0)
                Debug.Log($"{name}: {pruned} ligação(ões) redundante(s) removida(s) (havia um caminho por um vizinho comum, com as duas pernas mais curtas).", this);
        }

        private static HashSet<int> NeighborSet(Dictionary<int, HashSet<int>> adjacency, int node)
        {
            if (!adjacency.TryGetValue(node, out HashSet<int> set))
            {
                set = new HashSet<int>();
                adjacency[node] = set;
            }

            return set;
        }

        // Os dois segmentos (no plano X/Z) se cruzam propriamente?
        private static bool SegmentsCross(Vector3 p, Vector3 q, Vector3 a, Vector3 b)
        {
            static float Side(Vector3 u, Vector3 v, Vector3 w) => (v.x - u.x) * (w.z - u.z) - (v.z - u.z) * (w.x - u.x);
            return Side(p, q, a) * Side(p, q, b) < 0f && Side(a, b, p) * Side(a, b, q) < 0f;
        }

        // ================================================================================
        // Estruturas
        // ================================================================================

        /// <summary>O grafo em edição: posições, papéis, raios, pesos, quem veio da cena e as arestas.</summary>
        private sealed class Draft
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<bool> Primary = new List<bool>();
            public readonly List<NavNode> Source = new List<NavNode>();

            // Raio EFETIVO (override ou padrão do papel). É o que a cobertura do chão mede, e o
            // ApplyDraft converte de volta em override.
            public readonly List<float> Radii = new List<float>();

            // Peso de exploração (só primário conta). Nó da cena: o dele; primário gerado: o
            // orçamento repartido.
            public readonly List<float> Weights = new List<float>();
            public readonly HashSet<long> Edges = new HashSet<long>();

            // Ligações que já estavam na cena quando o rascunho foi montado (autoria à mão). A
            // poda de redundantes não toca nelas: a regra do placer é só tirar o que ele criou.
            public readonly HashSet<long> SceneEdges = new HashSet<long>();
            private readonly List<bool> _removed = new List<bool>();

            public int Count => Positions.Count;

            public bool Alive(int i) => !_removed[i];

            public void Remove(int i) => _removed[i] = true;

            public void Restore(int i) => _removed[i] = false;

            public int Add(Vector3 position, bool primary, NavNode source, float radius, float weight)
            {
                Positions.Add(position);
                Primary.Add(primary);
                Source.Add(source);
                Radii.Add(radius);
                Weights.Add(weight);
                _removed.Add(false);
                return Positions.Count - 1;
            }

            public bool AddEdge(int a, int b) => a != b && Edges.Add(Key(a, b));

            public static long Key(int a, int b) =>
                a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            public static void Split(long key, out int a, out int b)
            {
                a = (int)(key >> 32);
                b = (int)(key & 0xFFFFFFFFL);
            }

            public int Degree(int node)
            {
                int degree = 0;
                foreach (long edge in Edges)
                {
                    Split(edge, out int a, out int b);
                    if ((a == node && Alive(b)) || (b == node && Alive(a)))
                        degree++;
                }

                return degree;
            }

            public List<int> Neighbors(int node)
            {
                var neighbors = new List<int>();
                foreach (long edge in Edges)
                {
                    Split(edge, out int a, out int b);
                    if (a == node && Alive(b))
                        neighbors.Add(b);
                    else if (b == node && Alive(a))
                        neighbors.Add(a);
                }

                return neighbors;
            }

            /// <summary>Componente de cada nó vivo (-1 nos removidos) e quantos há.</summary>
            public int[] Components(out int count)
            {
                var adjacency = new List<int>[Count];
                for (int i = 0; i < Count; i++)
                    adjacency[i] = new List<int>();

                foreach (long edge in Edges)
                {
                    Split(edge, out int a, out int b);
                    if (!Alive(a) || !Alive(b))
                        continue;

                    adjacency[a].Add(b);
                    adjacency[b].Add(a);
                }

                var component = new int[Count];
                for (int i = 0; i < Count; i++)
                    component[i] = -1;

                count = 0;
                var queue = new Queue<int>();
                for (int start = 0; start < Count; start++)
                {
                    if (!Alive(start) || component[start] >= 0)
                        continue;

                    component[start] = count;
                    queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        foreach (int next in adjacency[queue.Dequeue()])
                        {
                            if (component[next] >= 0)
                                continue;

                            component[next] = count;
                            queue.Enqueue(next);
                        }
                    }

                    count++;
                }

                return component;
            }
        }

        /// <summary>Mapa do chão andável da arena, no plano dos nós.</summary>
        private sealed class WalkGrid
        {
            public readonly Vector3 Min;
            public readonly float Step;
            public readonly int SizeX;
            public readonly int SizeZ;
            public readonly bool[] Walkable;
            public float[] Clearance;

            // O que bloqueia a VISÃO na altura dos nós (null = a área não é cortada pela parede).
            public bool[] SightBlocked;

            // Alcance, em células, da visibilidade guardada por origem.
            public int VisReach;

            private readonly Dictionary<int, bool[]> _visibility = new Dictionary<int, bool[]>();

            /// <summary>
            /// Células VISÍVEIS a partir de <paramref name="center"/>, num quadrado de VisReach em
            /// volta (índice local). Raios do centro até cada célula da borda do quadrado, andando
            /// meia célula por vez e parando na primeira célula que bloqueia a visão — a versão em
            /// grade do raycast do NavGraph.CanSeeFromNode. Guardado por origem: a mesma célula
            /// serve a um nó e a todos os candidatos testados ali.
            /// </summary>
            public bool[] VisibilityFrom(int center)
            {
                if (SightBlocked == null)
                    return null;

                if (_visibility.TryGetValue(center, out bool[] cached))
                    return cached;

                int r = VisReach;
                int size = r * 2 + 1;
                var visible = new bool[size * size];
                int cx = X(center);
                int cz = Z(center);
                visible[r * size + r] = true;

                for (int side = -r; side <= r; side++)
                {
                    March(visible, cx, cz, r, size, side, -r);
                    March(visible, cx, cz, r, size, side, r);
                    March(visible, cx, cz, r, size, -r, side);
                    March(visible, cx, cz, r, size, r, side);
                }

                _visibility[center] = visible;
                return visible;
            }

            private void March(bool[] visible, int cx, int cz, int r, int size, int tx, int tz)
            {
                int steps = Mathf.Max(Mathf.Abs(tx), Mathf.Abs(tz)) * 2;
                for (int s = 1; s <= steps; s++)
                {
                    int dx = Mathf.RoundToInt((float)tx * s / steps);
                    int dz = Mathf.RoundToInt((float)tz * s / steps);
                    if (!InBounds(cx + dx, cz + dz) || SightBlocked[Index(cx + dx, cz + dz)])
                        return;

                    visible[(dz + r) * size + dx + r] = true;
                }
            }

            /// <summary>A célula está visível a partir da origem? Sem corte por parede, sempre.</summary>
            public bool Sees(bool[] visible, int center, int cell)
            {
                if (SightBlocked == null)
                    return true;

                if (visible == null)
                    return false;

                int dx = X(cell) - X(center);
                int dz = Z(cell) - Z(center);
                int r = VisReach;
                return dx >= -r && dx <= r && dz >= -r && dz <= r && visible[(dz + r) * (r * 2 + 1) + dx + r];
            }

            public WalkGrid(Vector3 min, float step, int sizeX, int sizeZ)
            {
                Min = min;
                Step = step;
                SizeX = sizeX;
                SizeZ = sizeZ;
                Walkable = new bool[sizeX * sizeZ];
            }

            public int Count => SizeX * SizeZ;

            public int Index(int x, int z) => z * SizeX + x;

            public int X(int cell) => cell % SizeX;

            public int Z(int cell) => cell / SizeX;

            public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < SizeX && z < SizeZ;

            public Vector3 Position(int cell) => new Vector3(Min.x + X(cell) * Step, Min.y, Min.z + Z(cell) * Step);

            public int[] NewDistanceField()
            {
                var field = new int[Count];
                for (int i = 0; i < Count; i++)
                    field[i] = Unreached;

                return field;
            }

            /// <summary>Célula andável mais perto do ponto, a até maxDistance; -1 se nenhuma.</summary>
            public int Snap(Vector3 position, float maxDistance) => SnapTo(Walkable, position, maxDistance);

            public int SnapTo(bool[] mask, Vector3 position, float maxDistance)
            {
                int cx = Mathf.RoundToInt((position.x - Min.x) / Step);
                int cz = Mathf.RoundToInt((position.z - Min.z) / Step);
                int reach = Mathf.CeilToInt(maxDistance / Step);

                int best = -1;
                float bestDistance = float.MaxValue;
                for (int x = cx - reach; x <= cx + reach; x++)
                {
                    for (int z = cz - reach; z <= cz + reach; z++)
                    {
                        if (!InBounds(x, z) || !mask[Index(x, z)])
                            continue;

                        float distance = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = Index(x, z);
                        }
                    }
                }

                return bestDistance <= reach * reach ? best : -1;
            }

            /// <summary>Células que aceitam nó, da maior folga para a menor.</summary>
            public List<int> CellsByClearance(float minimum)
            {
                var cells = new List<int>();
                for (int i = 0; i < Count; i++)
                {
                    if (Walkable[i] && Clearance[i] >= minimum)
                        cells.Add(i);
                }

                cells.Sort((l, r) => Clearance[r] != Clearance[l] ? Clearance[r].CompareTo(Clearance[l]) : l.CompareTo(r));
                return cells;
            }

            /// <summary>
            /// Distância de cada célula andável até a não-andável mais próxima (chamfer de duas
            /// passadas, 1 e raiz de 2), convertida em folga do corpo: a borda do não-andável já
            /// está a bodyRadius do obstáculo, então folga = bodyRadius + distância.
            /// </summary>
            public void ComputeClearance(float bodyRadius)
            {
                const float diagonal = 1.41421356f;
                var d = new float[Count];
                for (int i = 0; i < Count; i++)
                    d[i] = Walkable[i] ? float.MaxValue : 0f;

                for (int z = 0; z < SizeZ; z++)
                {
                    for (int x = 0; x < SizeX; x++)
                    {
                        int i = Index(x, z);
                        if (d[i] == 0f)
                            continue;

                        d[i] = Mathf.Min(d[i], At(d, x - 1, z) + 1f, At(d, x, z - 1) + 1f,
                            Mathf.Min(At(d, x - 1, z - 1), At(d, x + 1, z - 1)) + diagonal);
                    }
                }

                for (int z = SizeZ - 1; z >= 0; z--)
                {
                    for (int x = SizeX - 1; x >= 0; x--)
                    {
                        int i = Index(x, z);
                        if (d[i] == 0f)
                            continue;

                        d[i] = Mathf.Min(d[i], At(d, x + 1, z) + 1f, At(d, x, z + 1) + 1f,
                            Mathf.Min(At(d, x + 1, z + 1), At(d, x - 1, z + 1)) + diagonal);
                    }
                }

                Clearance = new float[Count];
                for (int i = 0; i < Count; i++)
                    Clearance[i] = Walkable[i] ? bodyRadius + (d[i] - 0.5f) * Step : 0f;
            }

            // Fora da grade conta como obstáculo.
            private float At(float[] d, int x, int z) => InBounds(x, z) ? d[Index(x, z)] : 0f;
        }
#endif
    }
}
