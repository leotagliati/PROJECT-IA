using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O que ESTE agente já viu do grafo, neste episódio: nós visitados, arestas percorridas,
    /// áreas alcançadas e onde fica a fronteira (o não-visitado mais próximo).
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
        private NavGraph _graph;

        private bool[] _visited;
        private int[] _visitCount;
        private bool[] _regionVisited;
        private int[] _regionVisitedCount;
        private int[] _regionEnabledCount;

        // Quanto vale UM nó de cada região: orçamento da região dividido pelos nós ATIVOS dela.
        // Recalculado a cada episódio (e não no bake) porque uma lição do currículo pode
        // desligar nós — e se a divisão continuasse usando o total, cobrir a região inteira
        // pagaria menos que o orçamento prometido.
        private float[] _regionNodeValue;

        private readonly HashSet<long> _traversedEdges = new HashSet<long>();

        private int _enabledNodeCount;
        private int _visitedNodeCount;
        private float _totalBudget;
        private float _collectedBudget;
        private bool _frontierDirty = true;

        public int CurrentNodeIndex { get; private set; } = -1;

        public int PreviousNodeIndex { get; private set; } = -1;

        /// <summary>Se o agente está DENTRO do raio de um nó agora (e não a caminho entre dois).</summary>
        public bool IsAtNode { get; private set; }

        public bool EnteredNewNode { get; private set; }

        /// <summary>
        /// Soma do valor dos nós inéditos alcançados desde o último <see cref="ClearStepFlags"/>.
        /// Cada nó vale orçamento_da_região / nós_ativos_da_região — é aqui que a normalização
        /// acontece, e é por isso que a recompensa não olha mais para a CONTAGEM de nós.
        /// </summary>
        public float EnteredNewNodeValue { get; private set; }

        public bool EnteredNewRegion { get; private set; }

        /// <summary>Soma dos orçamentos das regiões inéditas alcançadas neste intervalo.</summary>
        public float EnteredNewRegionBudget { get; private set; }

        /// <summary>Percorreu uma aresta do grafo que ainda não tinha sido percorrida.</summary>
        public bool TraversedNewEdge { get; private set; }

        public bool ChangedNode { get; private set; }

        /// <summary>Quantas vezes o agente já chegou ao nó atual neste episódio (>= 1).</summary>
        public int CurrentNodeVisitCount { get; private set; }

        /// <summary>Steps de FÍSICA desde o último nó inédito. Base do sinal de estagnação.</summary>
        public int StepsSinceNewNode { get; private set; }

        /// <summary>
        /// Fração dos NÓS ativos já visitados. Medida geométrica pura: quanto do mapa foi
        /// fisicamente coberto, sem opinião sobre o valor de cada parte. Regiões descritas com
        /// muitos nós pesam mais aqui, simplesmente por serem maiores no chão.
        /// </summary>
        public float VisitedFraction => _enabledNodeCount > 0 ? (float)_visitedNodeCount / _enabledNodeCount : 1f;

        /// <summary>
        /// Fração do ORÇAMENTO total já coletada. Mesma normalização da recompensa: cobrir um
        /// corredor de orçamento 0.2 avança pouco, cobrir a sala de orçamento 2.0 avança muito,
        /// independente de quantos nós cada um tem.
        /// </summary>
        public float VisitedBudgetFraction => _totalBudget > 1e-6f ? _collectedBudget / _totalBudget : 1f;

        public int VisitedNodeCount => _visitedNodeCount;

        public int EnabledNodeCount => _enabledNodeCount;

        public float CurrentRegionVisitedFraction
        {
            get
            {
                if (CurrentNodeIndex < 0)
                    return 0f;

                int slot = _graph.RegionSlotOf(CurrentNodeIndex);
                int total = _regionEnabledCount[slot];
                return total > 0 ? (float)_regionVisitedCount[slot] / total : 1f;
            }
        }

        public bool HasFrontier { get; private set; }

        /// <summary>Nó não-visitado mais próximo em número de arestas.</summary>
        public int FrontierTarget { get; private set; } = -1;

        /// <summary>Primeiro nó do caminho até ele — é para cá que o agente deve andar AGORA.</summary>
        public int FrontierNextStep { get; private set; } = -1;

        public int FrontierDistance { get; private set; }

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();

            _visited = new bool[_graph.NodeCount];
            _visitCount = new int[_graph.NodeCount];
            _regionVisited = new bool[_graph.RegionCount];
            _regionVisitedCount = new int[_graph.RegionCount];
            _regionEnabledCount = new int[_graph.RegionCount];
            _regionNodeValue = new float[_graph.RegionCount];
        }

        /// <summary>
        /// Zera o episódio. O denominador da cobertura é fotografado AQUI: se uma lição do
        /// currículo desliga uma ala do mapa, ela precisa sair da conta antes do primeiro step,
        /// senão o alvo de cobertura fica inatingível e todo episódio termina em fracasso.
        /// </summary>
        public void ResetEpisode()
        {
            System.Array.Clear(_visited, 0, _visited.Length);
            System.Array.Clear(_visitCount, 0, _visitCount.Length);
            System.Array.Clear(_regionVisited, 0, _regionVisited.Length);
            System.Array.Clear(_regionVisitedCount, 0, _regionVisitedCount.Length);
            _traversedEdges.Clear();

            _enabledNodeCount = _graph.EnabledNodeCount();
            _visitedNodeCount = 0;
            _collectedBudget = 0f;

            RecomputeRegionValues();

            CurrentNodeIndex = -1;
            PreviousNodeIndex = -1;
            IsAtNode = false;
            CurrentNodeVisitCount = 0;
            StepsSinceNewNode = 0;

            ClearStepFlags();

            HasFrontier = false;
            FrontierTarget = -1;
            FrontierNextStep = -1;
            FrontierDistance = 0;
            _frontierDirty = true;
        }

        /// <summary>
        /// A divisão do orçamento: cada nó ativo de uma região passa a valer
        /// orçamento / nós_ativos_da_região. Cobrir a região inteira paga exatamente o
        /// orçamento, independente de ela ter sido descrita com 3 ou com 30 nós — que é o
        /// ponto: a densidade da sua autoria deixa de ser função de recompensa.
        /// </summary>
        private void RecomputeRegionValues()
        {
            System.Array.Clear(_regionEnabledCount, 0, _regionEnabledCount.Length);

            for (int i = 0; i < _graph.NodeCount; i++)
            {
                if (_graph.IsNodeEnabled(i))
                    _regionEnabledCount[_graph.RegionSlotOf(i)]++;
            }

            _totalBudget = 0f;

            for (int slot = 0; slot < _regionNodeValue.Length; slot++)
            {
                int count = _regionEnabledCount[slot];
                if (count == 0)
                {
                    // Região inteiramente desligada por uma lição: sai do valor por nó E do
                    // denominador da cobertura, senão o alvo vira inatingível.
                    _regionNodeValue[slot] = 0f;
                    continue;
                }

                _regionNodeValue[slot] = _graph.RegionBudget(slot) / count;
                _totalBudget += _graph.RegionBudget(slot);
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
                _frontierDirty = true;
            }

            StepsSinceNewNode = EnteredNewNode ? 0 : StepsSinceNewNode + 1;

            // A fronteira só muda quando o nó âncora muda ou quando algo é visitado — as duas
            // coisas acontecem juntas, aqui. Recalcular a BFS nos outros steps seria trabalho
            // jogado fora, multiplicado por arena e por step de física.
            if (_frontierDirty)
                UpdateFrontier();
        }

        /// <summary>
        /// Zera as flags acumuladas. Chamar DEPOIS de cobrá-las na recompensa, uma vez por
        /// decisão. Os contadores (visitas, cobertura) não são afetados.
        /// </summary>
        public void ClearStepFlags()
        {
            EnteredNewNode = false;
            EnteredNewNodeValue = 0f;
            EnteredNewRegion = false;
            EnteredNewRegionBudget = 0f;
            TraversedNewEdge = false;
            ChangedNode = false;
        }

        private void RegisterArrival(int node)
        {
            PreviousNodeIndex = CurrentNodeIndex;

            // A aresta paga uma vez por episódio. É o que impede o ping-pong: ir e voltar entre
            // dois nós rende na PRIMEIRA travessia e nada depois, então oscilar passa a custar
            // só a pressão existencial. Uma recompensa por travessia sem essa memória é a forma
            // mais fácil de o agente descobrir uma máquina de fazer pontos parado no lugar.
            if (PreviousNodeIndex >= 0 && IsAdjacent(PreviousNodeIndex, node))
            {
                long key = EdgeKey(PreviousNodeIndex, node);
                TraversedNewEdge |= _traversedEdges.Add(key);
            }

            CurrentNodeIndex = node;
            _visitCount[node]++;
            CurrentNodeVisitCount = _visitCount[node];

            if (!_visited[node])
            {
                _visited[node] = true;
                _visitedNodeCount++;
                EnteredNewNode = true;

                int slot = _graph.RegionSlotOf(node);

                // Somado, e não atribuído: entre duas decisões o agente pode cruzar mais de um
                // nó, e cada um tem que pagar o seu.
                EnteredNewNodeValue += _regionNodeValue[slot];
                _collectedBudget += _regionNodeValue[slot];
                _regionVisitedCount[slot]++;

                if (!_regionVisited[slot])
                {
                    _regionVisited[slot] = true;
                    EnteredNewRegion = true;
                    EnteredNewRegionBudget += _graph.RegionBudget(slot);
                }
            }
        }

        private void UpdateFrontier()
        {
            _frontierDirty = false;

            HasFrontier = _graph.TryFindNearestUnvisited(
                CurrentNodeIndex, _visited, out int target, out int nextStep, out int distance);

            FrontierTarget = HasFrontier ? target : -1;
            FrontierNextStep = HasFrontier ? nextStep : -1;
            FrontierDistance = HasFrontier ? distance : 0;
        }

        public bool IsVisited(int node) => _visited[node];

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
            if (_graph == null || _visited == null || !Application.isPlaying)
                return;

            for (int i = 0; i < _visited.Length; i++)
            {
                if (!_visited[i])
                    continue;

                Gizmos.color = new Color(0.1f, 0.9f, 0.3f, 0.8f);
                Gizmos.DrawSphere(_graph.NodePosition(i), 0.22f);
            }

            // O disco do nó atual, no mesmo formato do gizmo de autoria: dá para ver ao vivo se
            // o raio que você calibrou está registrando a chegada onde você achou que ia.
            if (CurrentNodeIndex >= 0)
            {
                Gizmos.color = Color.yellow;
                GraphGizmos.DrawGroundCircle(
                    _graph.NodePosition(CurrentNodeIndex),
                    _graph.NodeRadius(CurrentNodeIndex),
                    height: 0.08f);
            }

            if (HasFrontier)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(_graph.NodePosition(FrontierTarget), 0.45f);
                Gizmos.DrawLine(transform.position, _graph.NodePosition(FrontierNextStep));
            }
        }
    }
}
