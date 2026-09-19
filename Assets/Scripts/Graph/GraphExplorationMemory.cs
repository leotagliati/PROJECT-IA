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
        //   esfera magenta    alvo da fronteira, com a linha até o próximo passo — só enquanto a
        //                     dica está sendo entregue ao agente (força > 0 e dentro da duração)
        //   esfera rosa       valor EXPLORAR de cada saída do nó atual (o que a rede recebe por
        //                     vizinho): tamanho = quanto do inexplorado está por ali, relativo
        //   esfera vermelha   valor CALOR de cada saída: onde o hider pode estar, relativo
        //   coluna vermelha   calor de cada nó (altura = quanto); some ao pisar nele
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _drawVisitedNodes = true;
        [SerializeField] private bool _drawPendingNodes = true;
        [SerializeField] private bool _drawAuxiliaryNodes = true;
        [SerializeField] private bool _drawTraversedEdges = true;
        [SerializeField] private bool _drawFrontier = true;
        [SerializeField] private bool _drawExitScores = true;
        [SerializeField] private bool _drawHeat = true;

        // Quantas revisitas levam a cor ao topo da escala de calor.
        [SerializeField] private int _heatSaturationVisits = 5;

        [Header("-----Fronteira-----")]
        // Entre quantos não-visitados mais próximos a seta SORTEIA o alvo. 1 = sempre o mais
        // próximo (determinístico: do mesmo spawn, a mesma rota todo episódio — e a política
        // decora a rota em vez de aprender a regra). 3 dá variação sem mandar o agente para o
        // outro lado do mapa: o 3º mais próximo raramente está muito além do 1º. O alvo fica
        // fixo até ser visitado, então a seta não pisca entre candidatos a cada troca de nó.
        [SerializeField, Min(1)] private int _frontierCandidates = 3;

        [Header("-----Valor das saídas-----")]
        // Desconto por ARESTA no valor do que está atrás de cada saída: um nó a d arestas do
        // vizinho conta peso x decay^d. Calibre pelo DIÂMETRO do grafo em arestas. O mapa atual
        // tem 42 (aresta mediana 7.4 m, primário mais próximo a 3 arestas): com 0.85, a 3
        // arestas um nó vale 61%, a 10 vale 20%, a 20 vale 4% e a 42 vale 0.1%. O perto domina,
        // mas o longe NUNCA some — num beco com tudo visitado por perto, a saída que leva ao
        // inexplorado ainda pontua mais. Como a observação é RELATIVA à melhor saída, o valor
        // absoluto pequeno não importa; o decay decide quanto "3 nós perto" vale contra "10 nós
        // longe". Mapa com diâmetro ~20: 0.75 dá a mesma curva.
        [SerializeField, Range(0.3f, 0.95f)] private float _lookaheadDecay = 0.85f;

        [Header("-----Mapa de calor (hider)-----")]
        // Calor por nó = "quanto eu acho que o hider pode estar por aqui". Fontes: passo do
        // hider num primário (ping) e avistamento. Decai com o tempo, DIFUNDE pelas arestas (o
        // hider se move) e zera no nó em que o seeker pisa (conferiu, não está). Não é a
        // posição do hider: é a memória do seeker sobre o que ouviu e viu.
        //
        // Calor colocado por um ping. O avistamento coloca mais (ver GraphHiderPerception via
        // AddHeat), porque "vi" vale mais que "ouvi".
        [SerializeField, Min(0f)] private float _pingHeat = 1f;

        // Meia-vida do calor em SEGUNDOS: em 20 s um ping vale metade. Da ordem do tempo que o
        // hider leva para trocar de sala — depois disso a pista está velha mesmo.
        [SerializeField, Min(1f)] private float _heatHalfLifeSeconds = 20f;

        // Fração do calor de cada nó que escorre para os vizinhos POR SEGUNDO (dividida entre
        // eles). É o modelo de "ele se mexe": com 0.15, em ~7 s metade do calor de um nó já
        // está nos vizinhos. Zero = calor fica onde nasceu (hider parado).
        [SerializeField, Range(0f, 1f)] private float _heatDiffusionPerSecond = 0.15f;

        private static readonly Color VisitedColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color ExitExploreColor = new Color(1f, 0.45f, 0.8f, 0.9f);
        private static readonly Color ExitHeatColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        private static readonly Color HeatColor = new Color(1f, 0.25f, 0.15f, 0.7f);
        private static readonly Color RevisitedColor = new Color(1f, 0.5f, 0.05f, 0.9f);
        private static readonly Color PendingColor = new Color(0.45f, 0.45f, 0.5f, 0.35f);
        private static readonly Color PrevisitedColor = new Color(0.25f, 0.3f, 0.65f, 0.5f);
        private static readonly Color TraversedEdgeColor = new Color(1f, 1f, 1f, 0.85f);

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

        private int _enabledNodeCount;
        private int _visitedNodeCount;

        // Soma dos pesos dos primários ATIVOS. Fotografada a cada episódio (e não no bake)
        // porque uma lição do currículo pode desligar nós — e se o denominador continuasse
        // usando o total, a cobertura por peso ficaria inatingível.
        private float _totalWeight;
        private float _collectedWeight;

        // Peso de cada nó NESTE episódio: o autorado (NavNode.ExplorationWeight) vezes um fator
        // sorteado em [1 - jitter, 1 + jitter]. Variar o valor dos nós entre episódios é o que
        // obriga a política a LER o peso do vizinho (ele está na observação) em vez de decorar
        // "aquela sala vale mais". Auxiliar continua 0. Recomputado a cada ResetEpisode.
        private float[] _episodeWeight;
        private float _maxEpisodeWeight;
        private bool _frontierDirty = true;

        // Calor por nó e o rascunho da difusão (para não difundir calor recém-chegado no mesmo
        // passo). A difusão roda uma vez por SEGUNDO simulado, não por step: é barata (O(E)),
        // mas por step seria conta jogada fora.
        private float[] _heat;
        private float[] _heatScratch;
        private float _heatDecayPerStep;
        private int _stepsSinceDiffusion;
        private int _diffusionPeriodSteps;

        // Valor bruto de cada saída do nó atual, indexado pelo ÍNDICE DO NÓ vizinho, nas duas
        // massas. Preenchido por ScoreExits uma vez por decisão; lido pela observação, pelo
        // baseline greedy e pelo gizmo.
        private float[] _exitExplore;
        private float[] _exitHeat;
        private float _bestExitExplore;
        private float _bestExitHeat;
        private int _scoredFromNode = -1;

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

        /// <summary>Percorreu uma aresta do grafo que ainda não tinha sido percorrida.</summary>
        public bool TraversedNewEdge { get; private set; }

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

        /// <summary>
        /// Chegadas a nós PRIMÁRIOS neste episódio, inéditos ou não. Com
        /// <see cref="VisitedNodeCount"/> dá a razão de revisitas — a métrica de vai-e-vem.
        /// </summary>
        public int PrimaryArrivalCount { get; private set; }

        public int EnabledNodeCount => _enabledNodeCount;

        public bool HasFrontier { get; private set; }

        /// <summary>Nó não-visitado mais próximo em número de arestas.</summary>
        public int FrontierTarget { get; private set; } = -1;

        /// <summary>Primeiro nó do caminho até ele — é para cá que o agente deve andar AGORA.</summary>
        public int FrontierNextStep { get; private set; } = -1;

        public int FrontierDistance { get; private set; }

        /// <summary>
        /// Se a dica de fronteira está sendo ENTREGUE ao agente agora (força > 0 e dentro da
        /// duração da lição). Só o gizmo lê isto: a fronteira continua sendo calculada — é
        /// estado do episódio e o BFS é barato — mas desenhar a seta quando a rede não a recebe
        /// faria você calibrar olhando uma dica que o agente não tem. Quem escreve é o manager,
        /// que é quem conhece força e duração.
        /// </summary>
        public bool FrontierHintVisible { get; set; } = true;

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();

            _visited = new bool[_graph.NodeCount];
            _visitCount = new int[_graph.NodeCount];
            _previsited = new bool[_graph.NodeCount];
            _shuffleBuffer = new int[_graph.NodeCount];
            _episodeWeight = new float[_graph.NodeCount];
            _heat = new float[_graph.NodeCount];
            _heatScratch = new float[_graph.NodeCount];
            _exitExplore = new float[_graph.NodeCount];
            _exitHeat = new float[_graph.NodeCount];

            // decay^steps_por_meia_vida = 0.5  ->  decay = 0.5^(1/steps)
            float stepsPerHalfLife = _heatHalfLifeSeconds / Time.fixedDeltaTime;
            _heatDecayPerStep = Mathf.Pow(0.5f, 1f / Mathf.Max(1f, stepsPerHalfLife));
            _diffusionPeriodSteps = Mathf.Max(1, Mathf.RoundToInt(1f / Time.fixedDeltaTime));
        }

        /// <summary>
        /// Zera o episódio. O denominador da cobertura é fotografado AQUI: se uma lição do
        /// currículo desliga uma ala do mapa, ela precisa sair da conta antes do primeiro step,
        /// senão o alvo de cobertura fica inatingível e todo episódio termina em fracasso.
        ///
        /// <paramref name="previsitedFraction"/> (0..1) é a fração dos primários ativos que
        /// já começa marcada como visitada, sorteada a cada episódio. Ver <see cref="_previsited"/>.
        /// </summary>
        public void ResetEpisode(float previsitedFraction = 0f, float weightJitter = 0f)
        {
            System.Array.Clear(_visited, 0, _visited.Length);
            System.Array.Clear(_visitCount, 0, _visitCount.Length);
            System.Array.Clear(_previsited, 0, _previsited.Length);
            _traversedEdges.Clear();

            DrawEpisodeWeights(weightJitter);
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

            System.Array.Clear(_heat, 0, _heat.Length);
            _stepsSinceDiffusion = 0;
            _scoredFromNode = -1;
            _bestExitExplore = 0f;
            _bestExitHeat = 0f;

            HasFrontier = false;
            FrontierTarget = -1;
            FrontierNextStep = -1;
            FrontierDistance = 0;
            _frontierDirty = true;
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

                _totalWeight += _episodeWeight[i];

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
                _frontierDirty = true;
            }

            StepsSinceNewNode = EnteredNewNode ? 0 : StepsSinceNewNode + 1;

            // A fronteira só muda quando o nó âncora muda ou quando algo é visitado — as duas
            // coisas acontecem juntas, aqui. Recalcular a BFS nos outros steps seria trabalho
            // jogado fora, multiplicado por arena e por step de física.
            if (_frontierDirty)
                UpdateFrontier();

            TickHeat();
        }

        // ---------------- Mapa de calor ----------------

        /// <summary>Adiciona calor num nó (ping = _pingHeat; avistamento passa o próprio valor).</summary>
        public void AddHeat(int node, float amount)
        {
            if (node >= 0 && node < _heat.Length && amount > 0f)
                _heat[node] += amount;
        }

        public void AddPingHeat(int node) => AddHeat(node, _pingHeat);

        public float HeatAt(int node) => _heat[node];

        private void TickHeat()
        {
            // Pisar num nó zera o calor dele: olhei, não está aqui. É o que faz a busca avançar
            // em vez de o seeker ficar orbitando a sala do último ping.
            if (IsAtNode && CurrentNodeIndex >= 0)
                _heat[CurrentNodeIndex] = 0f;

            if (_heatDecayPerStep < 1f)
            {
                for (int i = 0; i < _heat.Length; i++)
                    _heat[i] *= _heatDecayPerStep;
            }

            _stepsSinceDiffusion++;
            if (_stepsSinceDiffusion < _diffusionPeriodSteps || _heatDiffusionPerSecond <= 0f)
                return;

            _stepsSinceDiffusion = 0;
            System.Array.Clear(_heatScratch, 0, _heatScratch.Length);

            for (int i = 0; i < _heat.Length; i++)
            {
                float h = _heat[i];
                if (h <= 1e-6f || !_graph.IsNodeEnabled(i))
                    continue;

                int[] neighbors = _graph.GetNeighbors(i);
                int enabled = 0;
                foreach (int n in neighbors)
                {
                    if (_graph.IsNodeEnabled(n))
                        enabled++;
                }

                if (enabled == 0)
                {
                    _heatScratch[i] += h;
                    continue;
                }

                float leaving = h * _heatDiffusionPerSecond;
                _heatScratch[i] += h - leaving;
                float share = leaving / enabled;
                foreach (int n in neighbors)
                {
                    if (_graph.IsNodeEnabled(n))
                        _heatScratch[n] += share;
                }
            }

            (_heat, _heatScratch) = (_heatScratch, _heat);
        }

        // ---------------- Valor das saídas ----------------

        /// <summary>
        /// Calcula o valor de cada saída do nó atual nas duas massas (ver
        /// <see cref="NavGraph.ScoreBeyond"/>). Uma vez por decisão, antes de ler
        /// <see cref="ExitExploreScore"/>/<see cref="ExitHeatScore"/>. Sem nó âncora não há
        /// saídas: tudo fica em zero.
        /// </summary>
        public void ScoreExits()
        {
            _bestExitExplore = 0f;
            _bestExitHeat = 0f;
            _scoredFromNode = CurrentNodeIndex;

            if (CurrentNodeIndex < 0)
                return;

            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                _graph.ScoreBeyond(CurrentNodeIndex, neighbor, _lookaheadDecay, _visited, _heat, out float explore, out float heat);
                _exitExplore[neighbor] = explore;
                _exitHeat[neighbor] = heat;
                _bestExitExplore = Mathf.Max(_bestExitExplore, explore);
                _bestExitHeat = Mathf.Max(_bestExitHeat, heat);
            }
        }

        /// <summary>
        /// Valor EXPLORAR da saída, RELATIVO à melhor saída do nó atual: 1 = a melhor (ou
        /// empatada), 0 = nada inexplorado por ali. Relativo porque a decisão é "qual porta":
        /// um número que vale 1.0 para a melhor porta em qualquer mapa e fase é mais fácil de
        /// ler que uma fração que encolhe conforme o mapa é coberto. "Quanto falta no total"
        /// já está na cobertura, uma observação global.
        /// </summary>
        public float ExitExploreScore(int neighbor)
        {
            if (_scoredFromNode != CurrentNodeIndex || _bestExitExplore <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(_exitExplore[neighbor] / _bestExitExplore);
        }

        /// <summary>Valor CALOR da saída, relativo à melhor. 0 quando não há calor nenhum no mapa.</summary>
        public float ExitHeatScore(int neighbor)
        {
            if (_scoredFromNode != CurrentNodeIndex || _bestExitHeat <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(_exitHeat[neighbor] / _bestExitHeat);
        }

        /// <summary>Há calor em algum lugar do mapa (soma > 0)? Para o greedy decidir o que seguir.</summary>
        public bool HasHeat => _bestExitHeat > 1e-6f;

        /// <summary>
        /// Zera as flags acumuladas. Chamar DEPOIS de cobrá-las na recompensa, uma vez por
        /// decisão. Os contadores (visitas, cobertura) não são afetados.
        /// </summary>
        public void ClearStepFlags()
        {
            EnteredNewNode = false;
            EnteredNewNodeValue = 0f;
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
            //
            // Só entre PRIMÁRIOS: o _newEdgeReward é um valor plano, não escalado por peso,
            // então uma malha auxiliar densa multiplicaria a contagem de arestas e com ela a
            // renda do episódio — percorrer a guia passaria a pagar mais que cobrir o mapa.
            // Quem dá gradiente durante a travessia é o _frontierApproachReward, que é por metro
            // e não depende de quantos nós existem no caminho.
            if (PreviousNodeIndex >= 0
                && _graph.IsNodePrimary(PreviousNodeIndex)
                && _graph.IsNodePrimary(node)
                && IsAdjacent(PreviousNodeIndex, node))
            {
                long key = EdgeKey(PreviousNodeIndex, node);
                TraversedNewEdge |= _traversedEdges.Add(key);
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
            float weight = _episodeWeight[node];
            EnteredNewNodeValue += weight;
            _collectedWeight += weight;
        }

        /// <summary>
        /// Sorteia o peso de cada nó para o episódio: autorado x U[1 - jitter, 1 + jitter],
        /// nunca negativo. Com jitter 0 é o peso autorado. A SOMA esperada não muda (o fator
        /// tem média 1), então o teto de recompensa do mapa fica o mesmo — o que muda é QUEM
        /// vale mais a cada episódio.
        /// </summary>
        private void DrawEpisodeWeights(float jitter)
        {
            jitter = Mathf.Clamp01(jitter);
            _maxEpisodeWeight = 0f;

            for (int i = 0; i < _graph.NodeCount; i++)
            {
                float factor = jitter > 0f ? Random.Range(1f - jitter, 1f + jitter) : 1f;
                _episodeWeight[i] = Mathf.Max(0f, _graph.NodeWeight(i) * factor);
                _maxEpisodeWeight = Mathf.Max(_maxEpisodeWeight, _episodeWeight[i]);
            }
        }

        /// <summary>
        /// Peso do nó neste episódio, NORMALIZADO pelo maior peso do mapa (0..1). É o que a
        /// observação entrega por vizinho: "quanto vale esta saída em relação ao nó que mais
        /// vale". Auxiliar e nó desligado dão 0.
        /// </summary>
        public float NormalizedEpisodeWeight(int node)
        {
            if (_maxEpisodeWeight <= 1e-6f || !_graph.IsNodeEnabled(node))
                return 0f;

            return Mathf.Clamp01(_episodeWeight[node] / _maxEpisodeWeight);
        }

        private void UpdateFrontier()
        {
            _frontierDirty = false;

            // Alvo fixo enquanto ele continuar valendo: o sorteio só acontece quando o alvo
            // atual foi visitado (ou ficou inalcançável). É o que faz a seta apontar para o
            // MESMO lugar do começo ao fim de uma travessia — e o que mantém o shaping de
            // aproximação comparável entre dois steps.
            if (FrontierTarget >= 0 && !_visited[FrontierTarget]
                && _graph.TryFindPathTo(CurrentNodeIndex, FrontierTarget, out int keptStep, out int keptDistance))
            {
                HasFrontier = true;
                FrontierNextStep = keptStep;
                FrontierDistance = keptDistance;
                return;
            }

            HasFrontier = _graph.TryFindNearestUnvisited(
                CurrentNodeIndex, _visited, _frontierCandidates, out int target, out int nextStep, out int distance);

            FrontierTarget = HasFrontier ? target : -1;
            FrontierNextStep = HasFrontier ? nextStep : -1;
            FrontierDistance = HasFrontier ? distance : 0;
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

            if (_drawHeat)
                DrawHeat();

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

            if (_drawFrontier && HasFrontier && FrontierHintVisible)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(_graph.NodePosition(FrontierTarget), 0.45f);
                Gizmos.DrawLine(transform.position, _graph.NodePosition(FrontierNextStep));
            }
        }

        // Uma coluna vermelha em cada nó com calor, altura = calor (0..3 m). É o "mapa de
        // calor" literal: onde o seeker acha que o hider pode estar.
        private void DrawHeat()
        {
            Gizmos.color = HeatColor;
            for (int i = 0; i < _heat.Length; i++)
            {
                float h = _heat[i];
                if (h <= 0.02f)
                    continue;

                Vector3 p = _graph.NodePosition(i);
                float height = Mathf.Min(3f, h * 1.5f);
                Gizmos.DrawLine(p, p + Vector3.up * height);
                Gizmos.DrawSphere(p + Vector3.up * height, 0.12f);
            }
        }

        /// <summary>
        /// Uma esfera em cada vizinho do nó atual, com o tamanho do valor RELATIVO daquela saída
        /// — exatamente os números que a rede recebe no slot. Rosa = explorar, vermelha = calor
        /// (deslocada para cima para não se sobreporem). É o gizmo para responder "por que ele
        /// foi por ali?": a maior esfera é a saída com mais inexplorado (ou mais calor) atrás.
        /// </summary>
        private void DrawExitScores()
        {
            if (_scoredFromNode < 0 || _scoredFromNode != CurrentNodeIndex)
                return;

            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                Vector3 p = _graph.NodePosition(neighbor);

                float explore = ExitExploreScore(neighbor);
                if (explore > 0f)
                {
                    Gizmos.color = ExitExploreColor;
                    Gizmos.DrawSphere(p + Vector3.up * 0.9f, 0.15f + 0.35f * explore);
                }

                float heat = ExitHeatScore(neighbor);
                if (heat > 0f)
                {
                    Gizmos.color = ExitHeatColor;
                    Gizmos.DrawSphere(p + Vector3.up * 1.7f, 0.15f + 0.35f * heat);
                }
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
