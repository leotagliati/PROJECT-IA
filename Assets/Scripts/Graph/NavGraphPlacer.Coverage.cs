using System.Collections.Generic;
using UnityEngine;

// Os campos só são lidos pelos menus de editor; no build do jogo ficam sem uso (e sem custo).
#pragma warning disable CS0414

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// COBERTURA DO CHÃO: a parte do <see cref="NavGraphPlacer"/> que garante que o agente nunca
    /// fica sem nó. É a correção do handoff de 23/09/2026 (docs/graph/handoff-cobertura-de-nos.md).
    ///
    /// O QUE PRECISA VALER: todo ponto em que o corpo consegue estar (célula andável do mapa do
    /// chão) fica dentro da área de chegada do nó que o JOGO escolheria ali — e alcança o centro
    /// desse nó sem sair da área. A regra do jogo (NavGraph.FindNodeAt) é "entre as áreas que
    /// contêm o ponto, vence o centro mais perto", então cada célula cai num de três estados:
    ///   OK          o vencedor alcança a célula por dentro da própria área;
    ///   NÓ ERRADO   o vencedor só a contém ATRAVÉS de parede/móvel (área vazada) — o agente
    ///               "chega" num nó do outro lado da parede, e a observação mente;
    ///   SEM NÓ      nenhuma área contém a célula — a observação fica presa na âncora antiga.
    ///
    /// A garantia antiga ("todo ponto a menos de 6 m ANDANDO de um nó") não é essa: com áreas de
    /// ±3 m, o chão entre dois nós a 6 m já ficava sem nó por construção, e corredor com menos de
    /// ~2.5 m livres nunca recebia nó (só se testava onde cabia a folga de spawn).
    ///
    /// COMO: tudo em cima do mesmo mapa do chão da geração (WalkGrid, 0.3 m), sem física nova:
    ///   - raio de cada nó escolhido pelo que ele COBRE, respeitando vazamento, posse do primário
    ///     e sombra (FitRadiiDraft);
    ///   - buraco que sobra vira auxiliar novo, escolhido por cobertura de conjuntos gulosa: o
    ///     candidato (posição + raio) que mais transforma chão ruim em chão OK sem piorar nada
    ///     (FillCoverage), até a META (_coverageTarget e _maxHoleDepth) — não até 100%, que
    ///     enchia os cantos entre móveis de nós pequenos. Candidato é QUALQUER célula onde o corpo
    ///     passa, inclusive corredor estreito; nó apertado não vira spawn (NavGraph.CanSpawnAt).
    ///   - nenhum nó novo, de origem nenhuma, a menos de _minNodeSpacing de outro.
    /// Quando as regras brigam (raio grande cobre mais, mas vaza e rouba área de primário), a
    /// saída é MAIS NÓS, nunca relaxar a regra.
    /// </summary>
    public partial class NavGraphPlacer
    {
        [Header("-----Cobertura do chão-----")]
        // Meta da cobertura: fração do chão andável no estado OK. É também onde a gulosa PARA
        // (FillCoverage) e o quanto a limpeza pode gastar (RemoveRedundant) — antes era só
        // relatório, e a gulosa perseguia 100%: cada cantinho entre móveis virava um nó.
        // 97% e não 98%: medido no NodeTraining2 (réplica offline do mapa do chão), o último 1%
        // custa uns 10 nós de 2–3 m² cada, e o grafo à mão (a referência de "enxuto") cobre só
        // 78% com os móveis ligados. O que sobra fica raso por causa de _maxHoleDepth.
        [SerializeField, Range(0.5f, 1f)] private float _coverageTarget = 0.97f;

        // PROFUNDIDADE máxima do chão ruim (sem nó ou no nó errado): distância andando de
        // qualquer célula ruim até o chão OK mais perto, em metros. 1 m ~ 10 steps de física do
        // agente a 5 m/s: ele sai dali antes de a observação envelhecer.
        //
        // Substitui _maxHoleSide (lado do retângulo que envolve o buraco): uma faixa de 0.3 m ao
        // longo de uma parede de 14 m contava como "buraco de 14 m" e pedia nó, embora o agente
        // nela esteja a um passo do chão coberto. A profundidade mede o que o agente sente.
        // Nome novo porque a unidade mudou de significado (ver CLAUDE.md).
        [SerializeField, Min(0f)] private float _maxHoleDepth = 1f;

        // Espaçamento da malha de CANDIDATOS a auxiliar em volta de cada buraco. 0.6 m = 2
        // células da grade: fino o bastante para achar o meio de um corredor de 2 m, grosso o
        // bastante para a busca não levar minutos (o custo cai com o quadrado deste valor).
        [SerializeField, Min(0.1f)] private float _candidateStep = 0.6f;

        // Passo entre os raios testados num auxiliar (do teto ao piso; o piso e o padrão do papel
        // entram sempre). 1 m = 8 raios entre 1.5 e 8. Era 0.5 com teto 5: com o teto em 8, meio
        // metro dobraria o número de raios, e cada raio custa um flood fill por candidato — a
        // cobertura muda pouco entre 6 e 6.5 m numa sala que a parede já recorta.
        [SerializeField, Min(0.05f)] private float _auxiliaryRadiusStep = 1f;

        // Teto de auxiliares que um passo de cobertura cria. Trava de segurança contra mapa
        // errado (layer de parede faltando = o prédio inteiro "vaza"), não um limite de projeto.
        [SerializeField, Min(1)] private int _maxNewNodes = 600;

        // Distância mínima (m, no plano) entre um nó NOVO, de QUALQUER origem (gulosa, portal da
        // ligação, curva do caminho pelo chão, desvio), e qualquer nó. Quem nasceria mais perto
        // que isso reaproveita o nó que já está ali. Sem ela os nós se empilhavam: dois vizinhos
        // a 50 cm dizem a mesma coisa na observação.
        //
        // Era 2 e só valia para a gulosa. Medido no NodeTraining2: 38 das 53 arestas com menos
        // de 3 m eram um auxiliar a 2.0–2.9 m de um primário — o primário fica no ponto de maior
        // folga da sala, e o auxiliar que cobre a sala queria o mesmo ponto. E as 8 arestas de
        // 0.3–1.3 m eram portais criados em cima do nó de porta. 3 m = a menor aresta do grafo
        // feito à mão (3.2 m).
        [SerializeField, Min(0f)] private float _minNodeSpacing = 3f;

        // Ganho mínimo (m² de chão que passa a OK) para a gulosa criar um auxiliar. Abaixo disso
        // o nó cobre um canto entre móveis que o agente mal usa, e custa um vizinho a mais na
        // observação e uma âncora a mais no grafo. 4 m² = um quadrado de 2 m. O chão FUNDO
        // (além de _maxHoleDepth) é fechado de qualquer jeito, sem esse piso.
        [SerializeField, Min(0f)] private float _minNodeGain = 4f;

        // Custo, por célula, de TOMAR chão que já estava OK em outro nó (sobreposição). Descobrir
        // uma célula nova vale 1: com 0.15, crescer o raio só compensa se cada ~7 células de
        // sobreposição trouxerem 1 de chão novo. É o que faz o raio parar onde a área do vizinho
        // começa, em vez de engolir o vizinho inteiro.
        [SerializeField, Range(0f, 1f)] private float _overlapPenalty = 0.15f;

        [Header("-----Pesos-----")]
        // SOMA dos pesos dos primários que o mapa deve ter. É o teto de recompensa de cobertura
        // (soma x _nodeCoverageReward) e é nele que os thresholds do currículo foram calculados:
        // 23 = os 23 primários de peso 1 do NodeTraining quando a conta foi feita
        // (config/graph_explorer_v2.yaml). A geração reparte este valor entre os primários que
        // criar, e o menu "8. Normalizar pesos" reescala os que já existem — assim regenerar o
        // grafo nunca muda o orçamento, e o YAML continua valendo.
        [SerializeField, Min(0.1f)] private float _primaryWeightBudget = 23f;

        // Relatório da última execução (só nesta sessão do editor), desenhado no gizmo.
        private readonly List<Vector3> _uncoveredCells = new List<Vector3>();
        private readonly List<Vector3> _wrongCells = new List<Vector3>();
        private readonly List<Vector3> _blindPoints = new List<Vector3>();
        private float _reportCellSize = 0.3f;

        // MARROM: é a única cor ainda livre no vocabulário dos gizmos (vermelho = problema de
        // geometria; verde, laranja, amarelo, magenta, branco, rosa, azuis e cinzas já têm dono).
        // Cheio = chão SEM nó; contorno = chão no NÓ ERRADO; esfera = ponto sem nó à vista.
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

        /// <summary>
        /// Raio de cada nó escolhido pela cobertura, e o chão que ainda ficar sem nó (ou no nó
        /// errado) recebe auxiliares novos, já ligados por região. Pode CRIAR nós, por isso roda
        /// só no Prefab Mode — encolher um raio para ele parar de vazar abre buraco, e o buraco
        /// tem que ser fechado no mesmo passo, senão o menu troca um problema por outro.
        /// </summary>
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

        /// <summary>
        /// Reescala os pesos dos primários para a soma dar _primaryWeightBudget, mantendo a
        /// PROPORÇÃO entre eles (a sala que você marcou como 2x continua valendo 2x). É o que
        /// mantém o teto de recompensa do mapa igual ao da conta do currículo.
        /// </summary>
        [ContextMenu("8. Normalizar pesos dos primários (soma = orçamento)")]
        private void NormalizeWeights()
        {
            RunStep("Normalizar pesos", () =>
            {
                var primaries = ValidNodes().FindAll(n => n.IsPrimary);
                if (primaries.Count == 0)
                {
                    Debug.LogWarning($"{name}: nenhum primário para normalizar.", this);
                    return;
                }

                float sum = 0f;
                foreach (NavNode node in primaries)
                    sum += node.ExplorationWeight;

                foreach (NavNode node in primaries)
                {
                    float weight = sum > 1e-4f
                        ? node.ExplorationWeight * _primaryWeightBudget / sum
                        : _primaryWeightBudget / primaries.Count;

                    UnityEditor.Undo.RecordObject(node, "Normalizar pesos");
                    node.SetExplorationWeight(Mathf.Round(weight * 1000f) / 1000f);
                    MarkModified(node);
                }

                Debug.Log(
                    $"{name}: {primaries.Count} primário(s), soma dos pesos {sum:0.##} -> {_primaryWeightBudget:0.##}.", this);
            }, radialOnly: false);
        }

        // ================================================================================
        // Pipeline
        // ================================================================================

        /// <summary>
        /// Nó no vão de cada porta -> raios -> auxiliares nos buracos -> ligação por região (com
        /// caminho pelo chão quando a reta não passa) -> raio dos auxiliares que a ligação criou.
        /// Repete (no máximo 3 voltas) só se a volta anterior criou nó: um auxiliar novo pode
        /// vazar e abrir "nó errado" que a volta seguinte fecha. Depois: limpeza (até a meta),
        /// porta ligada dos dois lados e poda das ligações redundantes.
        /// </summary>
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

            // A gulosa decide nó a nó, e um nó posto cedo pode ter ficado sobrando depois que os
            // vizinhos chegaram. Última passada: tira todo auxiliar sem o qual a cobertura continua
            // na meta (ou não piora) e o grafo não parte.
            int removed = RemoveRedundant(draft, grid);
            if (removed > 0)
                Debug.Log($"{name}: {removed} auxiliar(es) que sobraram depois da cobertura foram removidos.", this);

            // A limpeza pode ter tirado o nó do outro lado de uma porta; as duas passadas finais
            // só mexem em ligações (nenhum nó novo, a não ser curvas de um caminho pelo chão, que
            // ganham o raio certo como qualquer auxiliar novo).
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

        /// <summary>
        /// Estado de cobertura de cada célula do mapa do chão, e por nó do rascunho: quantas
        /// células andáveis a área dele tem, quantas ele alcança por dentro e em quantas ele
        /// vence o FindNodeAt. Reaproveitável: <see cref="BuildCoverage"/> reconstrói em cima.
        /// </summary>
        private sealed class CoverageMap
        {
            public readonly int[] Winner;
            public readonly float[] WinnerDistance;
            public readonly CellState[] State;

            // Carimbo do flood fill (evita limpar a cada busca) e a fila dele. Depois de um
            // FloodArea, Buffer[0..n) são as células alcançadas.
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

            // Vencedor de cada célula: a MESMA regra do NavGraph.FindNodeAt (no raio E, com a área
            // cortada pela parede, visível do centro; entre os que contêm, o centro mais perto).
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

        /// <summary>
        /// Flood fill a partir do centro, só por células andáveis DENTRO da área (no raio e, com o
        /// corte por parede, visíveis do centro). Carimba as alcançadas com um Stamp novo e as
        /// deixa em Buffer[0..n). Centro fora do chão andável (nó dentro de obstáculo) alcança zero.
        /// </summary>
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

        /// <summary>
        /// Quanto um AUXILIAR em <paramref name="position"/> com <paramref name="radius"/>
        /// melhoraria o chão, contra o mapa ATUAL (em que ele não existe). Pontos por célula que
        /// ele passaria a vencer: +1 por célula que vira OK, -1 por célula que vira NÓ ERRADO,
        /// com o estado de antes descontado (tirar uma célula OK de outro nó e alcançá-la vale 0).
        ///
        /// Inválido (false) quando fere uma regra que NÃO se troca por cobertura:
        ///   - sombra: a menos de meio raio de um primário (eclipsa a visita dele);
        ///   - vazamento acima de _maxLeakAuxiliary;
        ///   - posse: algum primário ficaria dono de menos de _minPrimaryOwnership da própria área.
        /// Com <paramref name="seed"/> &gt;= 0, também exige que o candidato resolva aquela célula.
        /// </summary>
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

        // Raios testados num auxiliar, do maior ao menor, sempre incluindo o padrão do papel
        // (para um nó que já está bom não ganhar override por arredondamento da escada).
        private List<float> AuxiliaryRadii()
        {
            var radii = new List<float>();
            for (float r = _maxAuxiliaryRadius; r >= _minAuxiliaryRadius - 1e-3f; r -= _auxiliaryRadiusStep)
                radii.Add(r);

            // O piso sempre entra: com passo 1 a escada 8, 7, ..., 2 pararia antes de 1.5, e o
            // corredor estreito é justamente onde só o raio mínimo cabe.
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

        /// <summary>
        /// Raio de cada nó (de <paramref name="only"/>, ou de todos), gravado no rascunho. Duas
        /// passadas, porque o limite do auxiliar depende do raio FINAL dos primários:
        ///
        /// PRIMÁRIOS — apertados de propósito ("visitado" = "estive lá"):
        ///   - nunca passam do padrão; encolhem até a METADE da distância para o primário mais
        ///     próximo (duas áreas que se sobrepõem registram a chegada no meio do caminho);
        ///   - encolhem enquanto a área vaza mais que _maxLeakPrimary (vazar é visita de graça).
        /// AUXILIARES — o raio que MAIS COBRE o chão (TryEvaluate com o nó fora do mapa), entre os
        ///   que não vazam além de _maxLeakAuxiliary nem roubam a posse de um primário. Empate:
        ///   o mais perto do padrão, para não encher o mapa de override.
        ///
        /// Encolher um raio aqui pode abrir buraco — é de propósito: vazamento e roubo são "nó
        /// errado", que mente para a observação. O buraco é fechado logo depois pelo FillCoverage.
        /// </summary>
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

            // Nenhum raio respeita as regras: o nó está perto demais de um primário ou encostado
            // em parede. Fica no piso e o Console avisa — "Remover nós inúteis" costuma resolver.
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

        /// <summary>
        /// Cobertura de conjuntos GULOSA, em duas fases.
        ///
        /// 1. GANHO MÁXIMO. Cada candidato (malha de _candidateStep sobre o chão andável, com o
        ///    raio que mais cobre ali) é avaliado uma vez (TryEvaluate) e entra numa fila de
        ///    prioridade. A cada passo sai o de maior ganho; ele é reavaliado contra o mapa atual e,
        ///    se continua na frente, vira auxiliar — senão volta para a fila com o valor novo. O
        ///    ganho de um candidato quase só cai conforme os vizinhos chegam, então o topo
        ///    reavaliado costuma ser o melhor de verdade (a gulosa "preguiçosa" clássica) e cada
        ///    passo custa poucas avaliações. Para quando bate a meta (CoverageMeets) ou quando o
        ///    melhor ganho fica abaixo de _minNodeGain.
        ///
        ///    Antes a gulosa andava de célula ruim em célula ruim, da mais apertada para a mais
        ///    aberta, punha o melhor nó QUE RESOLVESSE AQUELA CÉLULA e só parava quando acabavam as
        ///    células: cada canto entre móveis ganhava o seu nó. Medido no NodeTraining2 (réplica
        ///    offline): 152 auxiliares onde uma gulosa de ganho máximo com as mesmas regras de área
        ///    chega a 98% com ~60.
        ///
        /// 2. CHÃO FUNDO. O que ainda ficar a mais de _maxHoleDepth do chão OK — corredor estreito,
        ///    que tem poucos candidatos e ganho pequeno — é fechado célula a célula, da mais
        ///    apertada para a mais aberta, sem o piso de ganho. É a garantia antiga ("nenhum
        ///    corredor sem nó"), agora só para o chão que o agente sente.
        ///
        /// Célula funda que nenhum candidato resolve (nó errado colado num primário, canto entre
        /// móveis) fica no relatório — marrom no gizmo — para ser resolvida à mão.
        /// </summary>
        /// <returns>Quantos auxiliares foram criados.</returns>
        private int FillCoverage(Draft draft, WalkGrid grid, CoverageMap map)
        {
            List<float> radii = AuxiliaryRadii();
            float minGain = _minNodeGain / (grid.Step * grid.Step);
            int stride = Mathf.Max(1, Mathf.RoundToInt(_candidateStep / grid.Step));
            int added = 0;
            bool cancelled = false;

            // Células a menos de _minNodeSpacing de algum nó não recebem nó novo.
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
                // Candidato sem chão ruim bastante no quadrado do maior raio não tem como ganhar
                // _minNodeGain: pula sem pagar a avaliação (soma de prefixos do chão ruim).
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

                // Ficou para trás de outro candidato com o valor novo: volta para a fila.
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

                // Sem solução: a célula e o entorno imediato (a malha de candidatos é a mesma para
                // eles) saem da lista, senão cada célula do mesmo buraco pagaria a busca inteira.
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
            int node = draft.Add(grid.Position(cell), primary: false, source: null, radius, 0f);
            ApplyToCoverage(draft, grid, map, node);
            MarkCrowded(grid, crowded, draft.Positions[node]);
        }

        /// <summary>
        /// O raio que mais melhora o chão com um auxiliar em <paramref name="cell"/> (TryEvaluate
        /// contra o mapa atual). Empate (meia célula): o raio mais perto do padrão, para não encher
        /// o mapa de override. False se nenhum raio respeita vazamento/posse/sombra.
        /// </summary>
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

        /// <summary>
        /// O melhor auxiliar que RESOLVE a célula <paramref name="seed"/>: candidatos numa malha de
        /// _candidateStep em volta dela, até o raio máximo, em cada raio. Fica o de maior pontuação
        /// positiva; empate: o mais central (maior folga), depois o raio mais perto do padrão.
        /// </summary>
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
                        // Raios em ordem decrescente: se este não alcança a célula, os
                        // menores também não.
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

        /// <summary>
        /// PROFUNDIDADE de cada célula ruim (sem nó ou no nó errado): distância andando, em metros,
        /// até a célula OK mais perto (Dijkstra que só atravessa chão ruim, 8 vizinhos). OK e
        /// não-andável ficam em 0. <paramref name="deepest"/> = a maior.
        /// </summary>
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

        /// <summary>
        /// A meta da cobertura: fração OK &gt;= _coverageTarget E nenhum chão ruim a mais de
        /// _maxHoleDepth do chão OK. É o critério de parada da gulosa, o limite da limpeza e o
        /// "OK" do relatório — um lugar só, para os três não discordarem. <paramref name="deepest"/>
        /// fica em float.MaxValue quando a fração já não bate (a profundidade nem é medida).
        /// </summary>
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

        /// <summary>
        /// Loga a cobertura do chão e preenche o gizmo: % OK / nó errado / sem nó, a profundidade
        /// do chão ruim e, como informação, o maior buraco de cada tipo (área e lado do retângulo
        /// que o envolve). Devolve se a meta (CoverageMeets) foi batida.
        /// </summary>
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

        // Maior componente (8 vizinhos) de células num estado: área em m² e maior lado do
        // retângulo que a envolve, em m.
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

        /// <summary>
        /// Invariante B do handoff: de todo ponto do chão, algum nó com RETA LIVRE a até o
        /// alcance da observação (_maxNodeDistance do agente). Sem isso a observação [4..6] ("nó
        /// mais próximo alcançável") cai no fallback e aponta através da parede. Amostra de ~1 m
        /// (é física: um cast por teste), testando os 6 nós mais próximos de cada ponto.
        /// </summary>
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

        // Invariante C: spawn point fixo (fallback do spawn aleatório) tem que cair em chão OK.
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

            // Teto de desenho: dezenas de milhares de cubos travam a Scene view, e acima disso o
            // gizmo já disse o que tinha a dizer ("tem muito chão descoberto").
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
