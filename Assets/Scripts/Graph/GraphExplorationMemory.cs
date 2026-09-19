using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O que ESTE agente já viu do grafo, neste episódio: nós visitados, arestas percorridas e
    /// quanto ainda há por ver atrás de cada saída do nó atual.
    ///
    /// É o análogo da <see cref="Assets.Scripts.Seeker.SeekerExplorationMemory"/>, trocando a
    /// grade regular por um grafo. A troca importa: numa grade, "célula vizinha" pode estar do
    /// outro lado de uma parede, então o mapa que o agente carrega mente sobre o que é
    /// alcançável. No grafo, vizinhança JÁ É alcançabilidade — foi você quem garantiu isso ao
    /// ligar os nós. É por isso que aqui dá para medir progresso em número de arestas em vez de
    /// distância em linha reta, que é o que faz o sinal continuar correto atrás de uma parede.
    ///
    /// Uma instância por agente (é estado de episódio); o <see cref="NavGraph"/>, que é
    /// estrutura, é compartilhado pela arena.
    ///
    /// O Tick é chamado a cada step de FÍSICA, e não a cada decisão: com Decision Period > 1 o
    /// agente anda vários steps entre duas decisões e pode atravessar o raio de um nó inteiro no
    /// meio — a visita simplesmente não seria registrada. Por isso as flags do step são
    /// ACUMULATIVAS e o consumidor as zera com <see cref="ClearStepFlags"/> depois de cobrá-las.
    /// </summary>
    public class GraphExplorationMemory : MonoBehaviour
    {
        [Header("-----Gizmos (só em Play)-----")]
        // A memória é estado de EPISÓDIO: fora do Play não existe nada para desenhar. Quem
        // desenha a estrutura do mapa (nós, raios, ligações) é o NavGraph, e ele desenha sempre.
        //
        // Legenda:
        //   disco verde       PRIMÁRIO já visitado neste episódio
        //   disco alaranjado  primário revisitado — quanto mais quente, mais vezes ele voltou
        //   disco azul-escuro primário PRÉ-VISITADO: já nasceu marcado (sorteio do currículo),
        //                     não paga nem conta — o agente o vê como visitado
        //   contorno cinza    primário que ainda falta
        //   traço fino        auxiliar (guia), esverdeado se já passou por ele
        //   linha branca      aresta já percorrida (não paga de novo neste episódio)
        //   disco amarelo     nó âncora atual
        //   esfera rosa       valor de cada SAÍDA do nó atual (o que a rede recebe por vizinho):
        //                     tamanho = quanto do inexplorado está por ali, relativo à melhor
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _drawVisitedNodes = true;
        [SerializeField] private bool _drawPendingNodes = true;
        [SerializeField] private bool _drawAuxiliaryNodes = true;
        [SerializeField] private bool _drawTraversedEdges = true;
        [SerializeField] private bool _drawExitScores = true;

        // Quantas revisitas levam a cor ao topo da escala de calor.
        [SerializeField] private int _heatSaturationVisits = 5;

        [Header("-----Valor das saídas-----")]
        // Desconto por ARESTA no valor do que está atrás de cada saída: um nó a d arestas do
        // vizinho conta peso x decay^d. Quanto menor, mais "míope" a observação.
        //
        // Calibre pelo DIÂMETRO do grafo em arestas. O mapa atual do NodeTraining tem 42 (aresta
        // mediana 7.4 m, primário mais próximo a 3 arestas): com 0.85, a 3 arestas um nó vale
        // 61%, a 10 vale 20%, a 20 vale 4% e a 42 vale 0.1%. O perto domina, mas o longe NUNCA
        // some — num beco com tudo visitado por perto, a saída que leva ao inexplorado ainda
        // pontua mais que as outras. Sem isso o agente ficaria cego em beco, que era o que a
        // seta resolvia. Como a observação é RELATIVA à melhor saída, o valor absoluto pequeno
        // não importa; o que o decay decide é quanto "3 nós perto" vale contra "10 nós longe".
        // Mapa com diâmetro ~20: 0.75 dá a mesma curva.
        [SerializeField, Range(0.3f, 0.95f)] private float _lookaheadDecay = 0.85f;

        private static readonly Color VisitedColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color RevisitedColor = new Color(1f, 0.5f, 0.05f, 0.9f);
        private static readonly Color PendingColor = new Color(0.45f, 0.45f, 0.5f, 0.35f);
        private static readonly Color PrevisitedColor = new Color(0.25f, 0.3f, 0.65f, 0.5f);
        private static readonly Color TraversedEdgeColor = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color ExitScoreColor = new Color(1f, 0.45f, 0.8f, 0.9f);

        private NavGraph _graph;

        private bool[] _visited;
        private int[] _visitCount;

        // Nós que já nasceram visitados neste episódio (sorteio do currículo). Ficam fora do
        // denominador da cobertura e do valor coletado, mas aparecem como visitados para o
        // agente e para a BFS — é isso que faz cada episódio começar num "meio de exploração"
        // diferente, e o que impede a política de decorar uma rota a partir do spawn.
        private bool[] _previsited;

        // Rascunho do sorteio de pré-visitados, alocado uma vez.
        private int[] _shuffleBuffer;

        private readonly HashSet<long> _traversedEdges = new HashSet<long>();

        // Valor bruto de cada saída do nó atual, indexado pelo ÍNDICE DO NÓ vizinho. Preenchido
        // por ScoreExits uma vez por decisão; lido pela observação e pelo gizmo.
        private float[] _exitScore;
        private float _bestExitScore;
        private int _scoredFromNode = -1;

        private int _enabledNodeCount;
        private int _visitedNodeCount;

        // Soma dos pesos dos primários ATIVOS. Fotografada a cada episódio (e não no bake)
        // porque uma lição do currículo pode desligar nós — e se o denominador continuasse
        // usando o total, a cobertura por peso ficaria inatingível.
        private float _totalWeight;
        private float _collectedWeight;

        public int CurrentNodeIndex { get; private set; } = -1;

        public int PreviousNodeIndex { get; private set; } = -1;

        /// <summary>Se o agente está DENTRO do raio de um nó agora (e não a caminho entre dois).</summary>
        public bool IsAtNode { get; private set; }

        public bool EnteredNewNode { get; private set; }

        /// <summary>
        /// Soma dos PESOS dos nós inéditos alcançados desde o último <see cref="ClearStepFlags"/>.
        /// O peso é o do próprio nó (NavNode.ExplorationWeight); auxiliar entra como zero.
        /// </summary>
        public float EnteredNewNodeValue { get; private set; }

        /// <summary>Arestas inéditas percorridas desde o último <see cref="ClearStepFlags"/>.</summary>
        public int NewEdgeCount { get; private set; }

        public bool ChangedNode { get; private set; }

        /// <summary>Quantas vezes o agente já chegou ao nó atual neste episódio (>= 1).</summary>
        public int CurrentNodeVisitCount { get; private set; }

        /// <summary>Steps de FÍSICA desde o último nó inédito. Base do sinal de estagnação.</summary>
        public int StepsSinceNewNode { get; private set; }

        /// <summary>
        /// Fração dos nós PRIMÁRIOS ativos já visitados. Medida geométrica pura: quanto do mapa
        /// foi fisicamente coberto, sem opinião sobre o valor de cada parte. Salas descritas
        /// com muitos pontos de vantagem pesam mais aqui, por serem maiores no chão.
        ///
        /// A malha auxiliar fica fora: adensar a guia faria esta barra andar mais devagar sem o
        /// mapa ter ficado maior.
        /// </summary>
        public float VisitedFraction => _enabledNodeCount > 0 ? (float)_visitedNodeCount / _enabledNodeCount : 1f;

        /// <summary>
        /// Fração do PESO total já coletada. Mesma normalização da recompensa: descobrir um nó
        /// de peso 0.2 avança pouco, um de peso 2.0 avança muito.
        /// </summary>
        public float VisitedWeightFraction => _totalWeight > 1e-6f ? _collectedWeight / _totalWeight : 1f;

        public int VisitedNodeCount => _visitedNodeCount;

        public int EnabledNodeCount => _enabledNodeCount;

        /// <summary>Peso que ainda falta coletar. Normalizador do lookahead por saída.</summary>
        public float RemainingWeight => Mathf.Max(0f, _totalWeight - _collectedWeight);

        /// <summary>
        /// Chegadas a nós PRIMÁRIOS neste episódio, inéditos ou não. Com
        /// <see cref="VisitedNodeCount"/> dá a razão de revisitas — a métrica de vai-e-vem.
        /// </summary>
        public int PrimaryArrivalCount { get; private set; }

        /// <summary>
        /// Calcula o valor de cada saída do nó atual (ver
        /// <see cref="NavGraph.DiscountedUnvisitedBeyond"/>). Uma vez por decisão, antes de
        /// ler <see cref="ExitScore"/>. Sem nó âncora não há saídas: tudo fica em zero.
        /// </summary>
        public void ScoreExits()
        {
            _bestExitScore = 0f;
            _scoredFromNode = CurrentNodeIndex;

            if (CurrentNodeIndex < 0)
                return;

            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                float score = _graph.IsNodeEnabled(neighbor)
                    ? _graph.DiscountedUnvisitedBeyond(CurrentNodeIndex, neighbor, _lookaheadDecay, _visited)
                    : 0f;

                _exitScore[neighbor] = score;
                if (score > _bestExitScore)
                    _bestExitScore = score;
            }
        }

        /// <summary>
        /// Valor da saída <paramref name="neighbor"/> RELATIVO à melhor saída do nó atual:
        /// 1 = a melhor (ou empatada com ela), 0 = nada inexplorado por ali. Relativo, e não
        /// absoluto, porque o que a política precisa decidir é "qual porta", e um número que
        /// vale 1.0 para a melhor porta em qualquer mapa e em qualquer fase do episódio é muito
        /// mais fácil de ler que uma fração pequena que encolhe conforme o mapa é coberto.
        /// "Quanto falta no total" já está na cobertura, uma observação global.
        /// </summary>
        public float ExitScore(int neighbor)
        {
            if (_scoredFromNode != CurrentNodeIndex || _bestExitScore <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(_exitScore[neighbor] / _bestExitScore);
        }

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();

            _visited = new bool[_graph.NodeCount];
            _visitCount = new int[_graph.NodeCount];
            _previsited = new bool[_graph.NodeCount];
            _shuffleBuffer = new int[_graph.NodeCount];
            _exitScore = new float[_graph.NodeCount];
        }

        /// <summary>
        /// Zera o episódio. O denominador da cobertura é fotografado AQUI: se uma lição do
        /// currículo desliga uma ala do mapa, ela precisa sair da conta antes do primeiro step,
        /// senão o alvo de cobertura fica inatingível e todo episódio termina em fracasso.
        ///
        /// <paramref name="previsitedFraction"/> (0..1) é a fração dos primários ativos que
        /// já começa marcada como visitada, sorteada a cada episódio. Ver <see cref="_previsited"/>.
        /// </summary>
        public void ResetEpisode(float previsitedFraction = 0f)
        {
            System.Array.Clear(_visited, 0, _visited.Length);
            System.Array.Clear(_visitCount, 0, _visitCount.Length);
            System.Array.Clear(_previsited, 0, _previsited.Length);
            _traversedEdges.Clear();

            DrawPrevisited(previsitedFraction);

            _enabledNodeCount = 0;
            _visitedNodeCount = 0;
            _collectedWeight = 0f;
            PrimaryArrivalCount = 0;

            RecomputeTotalWeight();

            CurrentNodeIndex = -1;
            PreviousNodeIndex = -1;
            IsAtNode = false;
            CurrentNodeVisitCount = 0;
            StepsSinceNewNode = 0;

            ClearStepFlags();

            _scoredFromNode = -1;
            _bestExitScore = 0f;
        }

        /// <summary>
        /// Denominador da cobertura por peso: a soma dos pesos dos primários ATIVOS. Um nó
        /// desligado por uma lição sai da conta, senão o alvo de cobertura vira inatingível.
        ///
        /// Auxiliares ficam fora (NodeWeight devolve 0 para eles). É isso que torna a malha de
        /// navegação GRÁTIS: você adensa o quanto quiser para o agente não se perder, e nem o
        /// valor de um nó nem o denominador da cobertura se mexem.
        /// </summary>
        private void RecomputeTotalWeight()
        {
            _totalWeight = 0f;

            for (int i = 0; i < _graph.NodeCount; i++)
            {
                // Pré-visitado sai do denominador dos DOIS medidores: ele não pode ser
                // coletado, então contá-lo tornaria o alvo de cobertura inatingível.
                if (!_graph.IsNodeEnabled(i) || _previsited[i])
                    continue;

                _totalWeight += _graph.NodeWeight(i);

                if (_graph.IsNodePrimary(i))
                    _enabledNodeCount++;
            }
        }

        /// <summary>
        /// Sorteia a fração pedida dos primários ativos e marca-os como visitados de nascença.
        /// Sempre deixa pelo menos um primário por descobrir, senão a cobertura nasce em 100% e
        /// o episódio termina em "sucesso" antes do primeiro step.
        ///
        /// Auxiliares nunca entram no sorteio: marcá-los não mudaria nada que o agente veja
        /// (eles já não pagam) e só tiraria âncoras do caminho.
        /// </summary>
        private void DrawPrevisited(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0f)
                return;

            int candidates = 0;
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                if (_graph.IsNodeEnabled(i) && _graph.IsNodePrimary(i))
                    _shuffleBuffer[candidates++] = i;
            }

            int count = Mathf.Min(Mathf.RoundToInt(candidates * fraction), candidates - 1);

            // Fisher-Yates parcial: só embaralha as `count` primeiras posições.
            for (int k = 0; k < count; k++)
            {
                int j = Random.Range(k, candidates);
                (_shuffleBuffer[k], _shuffleBuffer[j]) = (_shuffleBuffer[j], _shuffleBuffer[k]);

                _previsited[_shuffleBuffer[k]] = true;
                _visited[_shuffleBuffer[k]] = true;
            }
        }

        public void Tick(Vector3 worldPosition)
        {
            int node = _graph.FindNodeAt(worldPosition);
            IsAtNode = node >= 0;

            // Fora de qualquer raio, CurrentNodeIndex NÃO volta para -1: ele continua sendo o
            // último nó alcançado. É essa persistência que dá uma âncora no grafo enquanto o
            // agente atravessa um corredor — sem ela a busca de fronteira ficaria cega no meio
            // de cada travessia, que é justamente quando ela é mais útil.
            if (node >= 0 && node != CurrentNodeIndex)
            {
                RegisterArrival(node);
                ChangedNode = true;
            }

            StepsSinceNewNode = EnteredNewNode ? 0 : StepsSinceNewNode + 1;
        }

        /// <summary>
        /// Zera as flags acumuladas. Chamar DEPOIS de cobrá-las na recompensa, uma vez por
        /// decisão. Os contadores (visitas, cobertura) não são afetados.
        /// </summary>
        public void ClearStepFlags()
        {
            EnteredNewNode = false;
            EnteredNewNodeValue = 0f;
            NewEdgeCount = 0;
            ChangedNode = false;
        }

        private void RegisterArrival(int node)
        {
            PreviousNodeIndex = CurrentNodeIndex;

            // A aresta paga uma vez por episódio. É o que impede o ping-pong: ir e voltar entre
            // dois nós rende na PRIMEIRA travessia e nada depois, então oscilar passa a custar
            // só a pressão existencial. Uma recompensa por travessia sem essa memória é a forma
            // mais fácil de o agente descobrir uma máquina de fazer pontos parado no lugar.
            //
            // Qualquer aresta, inclusive as da malha auxiliar: desde que a seta saiu, esta é a
            // única recompensa densa que existe, e é ela que dá gradiente no meio de um
            // corredor. O risco de a malha inflar a renda é controlado pelo VALOR (pequeno) e
            // pelo teto arestas x valor, que o GraphRewardSystem documenta — não por filtro.
            if (PreviousNodeIndex >= 0 && IsAdjacent(PreviousNodeIndex, node))
            {
                long key = EdgeKey(PreviousNodeIndex, node);
                if (_traversedEdges.Add(key))
                    NewEdgeCount++;
            }

            CurrentNodeIndex = node;
            _visitCount[node]++;
            CurrentNodeVisitCount = _visitCount[node];

            if (_graph.IsNodePrimary(node))
                PrimaryArrivalCount++;

            if (_visited[node])
                return;

            // Marcado como visitado sempre, inclusive auxiliar: é o que faz o gizmo mostrar por
            // onde ele passou e o que impede a fronteira de reprocessá-lo. O que muda é o resto.
            _visited[node] = true;

            // Auxiliar não paga nem conta. Ele já fez o trabalho dele — servir de âncora e de
            // caminho. Sair daqui é o que mantém a malha grátis.
            if (!_graph.IsNodePrimary(node))
                return;

            _visitedNodeCount++;
            EnteredNewNode = true;

            // Somado, e não atribuído: entre duas decisões o agente pode cruzar mais de um
            // nó, e cada um tem que pagar o seu.
            float weight = _graph.NodeWeight(node);
            EnteredNewNodeValue += weight;
            _collectedWeight += weight;
        }

        public bool IsVisited(int node) => _visited[node];

        /// <summary>Nasceu marcado como visitado neste episódio (sorteio do currículo).</summary>
        public bool IsPrevisited(int node) => _previsited[node];

        public int VisitCountOf(int node) => _visitCount[node];

        private bool IsAdjacent(int a, int b)
        {
            foreach (int neighbor in _graph.GetNeighbors(a))
            {
                if (neighbor == b)
                    return true;
            }

            return false;
        }

        // Aresta é não-dirigida: (3,7) e (7,3) são a mesma. A chave normaliza a ordem.
        private static long EdgeKey(int a, int b)
        {
            int low = Mathf.Min(a, b);
            int high = Mathf.Max(a, b);
            return ((long)low << 32) | (uint)high;
        }

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || _graph == null || _visited == null || !Application.isPlaying)
                return;

            if (_drawVisitedNodes || _drawPendingNodes || _drawAuxiliaryNodes)
                DrawNodeMemory();

            if (_drawTraversedEdges)
                DrawTraversedEdges();

            if (_drawExitScores)
                DrawExitScores();

            // O disco do nó atual, no mesmo formato do gizmo de autoria: dá para ver ao vivo se
            // o raio que você calibrou está registrando a chegada onde você achou que ia.
            if (CurrentNodeIndex >= 0)
            {
                Gizmos.color = Color.yellow;
                GraphGizmos.DrawGroundArea(
                    _graph.Shape,
                    _graph.NodePosition(CurrentNodeIndex),
                    _graph.NodeRadius(CurrentNodeIndex),
                    height: 0.12f);
            }

        }

        /// <summary>
        /// Uma esfera rosa em cada vizinho do nó atual, com o tamanho do valor RELATIVO daquela
        /// saída — exatamente o número que a rede recebe no slot. É o gizmo para responder "por
        /// que ele foi por ali?": a maior esfera é a saída com mais inexplorado atrás.
        /// </summary>
        private void DrawExitScores()
        {
            if (_scoredFromNode < 0 || _scoredFromNode != CurrentNodeIndex)
                return;

            Gizmos.color = ExitScoreColor;
            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                float score = ExitScore(neighbor);
                if (score <= 0f)
                    continue;

                Gizmos.DrawSphere(_graph.NodePosition(neighbor) + Vector3.up * 0.9f, 0.15f + 0.35f * score);
            }
        }

        /// <summary>
        /// Pinta o raio de chegada de cada nó com o estado dele neste episódio. O disco (e não
        /// um pontinho) porque é ele que diz onde a visita É registrada: assim a cor responde
        /// "já passei aqui?" e a área responde "onde eu preciso passar?" na mesma figura.
        /// </summary>
        private void DrawNodeMemory()
        {
            for (int i = 0; i < _visited.Length; i++)
            {
                if (!_graph.IsNodeEnabled(i))
                    continue;

                Vector3 position = _graph.NodePosition(i);
                float radius = _graph.NodeRadius(i);

                // Auxiliar: só um traço fino do disco, visitado ou não. Ele é cenário para a
                // leitura que interessa — quais PONTOS DE VANTAGEM já foram cobertos. Pintar a
                // malha inteira de verde afogaria essa informação numa tela de verde.
                if (!_graph.IsNodePrimary(i))
                {
                    if (!_drawAuxiliaryNodes)
                        continue;

                    Color aux = _visited[i] ? VisitedColor : PendingColor;
                    aux.a *= 0.4f;
                    Gizmos.color = aux;
                    GraphGizmos.DrawGroundArea(_graph.Shape, position, radius, height: 0.03f, segments: 14);
                    continue;
                }

                if (!_visited[i])
                {
                    // O que falta, só de contorno: o pendente precisa ser localizável sem
                    // competir visualmente com o que já foi feito.
                    if (_drawPendingNodes)
                    {
                        Gizmos.color = PendingColor;
                        GraphGizmos.DrawGroundArea(_graph.Shape, position, radius, height: 0.04f);
                    }

                    continue;
                }

                if (!_drawVisitedNodes)
                    continue;

                // Pré-visitado: cheio e frio, sem escala de calor. Distinguir do verde importa
                // porque este aqui NÃO foi mérito do agente — se ele orbitar em volta de um
                // azul, é vai-e-vem tanto quanto em volta de um laranja.
                if (_previsited[i])
                {
                    Gizmos.color = PrevisitedColor;
                    GraphGizmos.DrawGroundAreaFilled(_graph.Shape, position, radius, height: 0.06f);
                    continue;
                }

                // Verde -> laranja conforme as revisitas. Ver o nó esquentar é a forma mais
                // rápida de flagrar vai-e-vem: nó e aresta pagam uma vez por episódio, então
                // cor quente aqui é tempo gasto sem retorno nenhum.
                float heat = Mathf.Clamp01((_visitCount[i] - 1f) / Mathf.Max(1, _heatSaturationVisits));

                Gizmos.color = Color.Lerp(VisitedColor, RevisitedColor, heat);
                GraphGizmos.DrawGroundAreaFilled(_graph.Shape, position, radius, height: 0.06f);
                Gizmos.DrawSphere(position, 0.3f);
            }
        }

        /// <summary>
        /// As arestas já percorridas, desenhadas ACIMA das ligações do NavGraph (que são verdes
        /// e significam outra coisa: "esta ligação é válida"). Sem isto, a regra de que a aresta
        /// paga uma vez só por episódio é invisível — e ela é justamente a defesa contra o
        /// ping-pong entre dois nós vizinhos.
        /// </summary>
        private void DrawTraversedEdges()
        {
            Gizmos.color = TraversedEdgeColor;

            foreach (long key in _traversedEdges)
            {
                // Desempacota a chave montada por EdgeKey.
                int a = (int)(key >> 32);
                int b = (int)(key & 0xFFFFFFFFL);

                Vector3 offset = Vector3.up * 0.7f;
                Gizmos.DrawLine(_graph.NodePosition(a) + offset, _graph.NodePosition(b) + offset);
            }
        }
    }
}
