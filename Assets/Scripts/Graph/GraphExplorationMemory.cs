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
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _drawVisitedNodes = true;
        [SerializeField] private bool _drawPendingNodes = true;
        [SerializeField] private bool _drawAuxiliaryNodes = true;
        [SerializeField] private bool _drawTraversedEdges = true;
        [SerializeField] private bool _drawFrontier = true;

        // Quantas revisitas levam a cor ao topo da escala de calor.
        [SerializeField] private int _heatSaturationVisits = 5;

        [Header("-----Fronteira-----")]
        // A SETA SEGUE AS SAÍDAS: aponta para o vizinho com o maior "quanto resta"
        // (ExitRemainingScore), o MESMO número que a rede recebe por vizinho. Seta e observação
        // passam a concordar por construção — a seta vira só o realce da melhor saída.
        //
        // Por quê: com o alvo sorteado entre os não-visitados mais próximos (o modo antigo, com
        // isto desligado), a seta e o "quanto resta" podiam apontar para portas diferentes. Seguir
        // a observação pagava a descoberta mas COBRAVA o shaping da seta por se afastar do alvo
        // dela — um cabo de guerra entre dois sinais (observado pelo Arthur no search_03).
        //
        // Neste modo o alvo é o próprio próximo passo e muda a cada nó, então o termo por
        // ARESTAS (_frontierProgressPerMeter) fica em silêncio; quem guia é o por METRO até o
        // próximo passo (_frontierApproachReward). O alvo fica fixo durante a travessia (só é
        // recalculado ao trocar de nó), então a seta não pisca no meio do caminho.
        [SerializeField] private bool _frontierFollowsExits = true;

        // Só no modo antigo (_frontierFollowsExits desligado). Entre quantos não-visitados mais próximos a seta SORTEIA o alvo. 1 = sempre o mais
        // próximo (determinístico: do mesmo spawn, a mesma rota todo episódio — e a política
        // decora a rota em vez de aprender a regra). 3 dá variação sem mandar o agente para o
        // outro lado do mapa: o 3º mais próximo raramente está muito além do 1º. O alvo fica
        // fixo até ser visitado, então a seta não pisca entre candidatos a cada troca de nó.
        [SerializeField, Min(1)] private int _frontierCandidates = 3;

        [Header("-----Valor das saídas-----")]
        // Meia-vida, em METROS pelo grafo, do desconto do "quanto resta por esta saída"
        // (NavGraph.ScoreBeyond). Curta demais e só a sala ao lado conta (volta a ser míope);
        // longa demais e todas as saídas de um cruzamento pontuam quase igual. 20 m ~ uma sala
        // + o corredor até a próxima no mapa da Node_4.
        [SerializeField, Min(0.1f)] private float _exitHalfLifeMeters = 20f;

        [Header("-----Patrulha (value_recovery_seconds no currículo)-----")]
        // Com a recuperação ligada, um primário visitado cai a valor 0 e volta LINEARMENTE ao cheio
        // em value_recovery_seconds. Chegar nele de novo paga peso x ESTA fração x recuperação² —
        // no máximo 30% da primeira visita. Duas razões: (1) nó nunca visitado tem que valer sempre
        // bem mais que voltar (descobrir > patrulhar); (2) o quadrado faz voltar cedo render quase
        // nada, então a patrulha se espalha pelo mapa em vez de ir e voltar entre dois nós perto.
        //
        // TETO da renda de revisita por episódio: ~ soma_dos_pesos x 0.3 x (duração / (carência +
        // recuperação)) = 23 x 0.3 x (160 s / (30 + 90) s) ~ 9.2, x 0.75 (_nodeCoverageReward) ~ +6.9
        // no limite teórico (visitar TODO nó exatamente a cada 2 min). Refaça ao mudar qualquer um.
        [SerializeField, Range(0f, 1f)] private float _revisitValueFraction = 0.3f;

        // CARÊNCIA: por quanto tempo (steps de física; 1500 = 30 s) o nó recém-visitado fica em
        // valor ZERO antes de começar a recuperar. Com recuperação imediata (node4_patrol_02,
        // 60 s) a patrulha ficou eficiente demais: o agente fechava circuitos curtos pelo mesmo
        // miolo do mapa e a renda voltava rápido. Com 30 s parado + value_recovery_seconds (90 no
        // YAML), um nó só volta a valer inteiro ~2 min depois — voltar cedo não rende nada.
        [SerializeField, Min(0)] private int _recoveryDelaySteps = 1500;

        // REVISITA PRECOCE: chegar num primário visitado há menos disto (steps de física; 750 =
        // 15 s). Vale com ou sem recuperação — é o detector de loop. Quem decide quanto custa é o
        // GraphRewardSystem (só a partir da N-ésima seguida, para não punir voltar de um beco).
        [SerializeField, Min(1)] private int _earlyRevisitWindowSteps = 750;

        [Header("-----Diagnóstico de pisca-pisca-----")]
        // PISCA-PISCA de âncora: voltar ao nó de onde acabou de sair (A -> B -> A) em menos de
        // _flickerWindowSteps (100 = 2 s) tendo andado menos de _flickerDistance (m) desde que
        // chegou em B. Não é uma decisão do agente: é ele em cima da borda de dois ladrilhos (a
        // âncora troca a 1 cm da linha). Separado da revisita precoce (que é loop de verdade) para
        // dizer qual correção aplicar: folga na borda (isto) ou navegação (aquilo). Só métrica.
        [SerializeField, Min(1)] private int _flickerWindowSteps = 100;
        [SerializeField, Min(0f)] private float _flickerDistance = 1f;

        [Header("-----Tédio de sala (area_boredom no currículo)-----")]
        // Tédio da sala (NavNode._areaId) em que o agente está: SOBE enquanto ele está nela e
        // DESCE sozinho quando ele sai. Em steps de física: 1500 = 30 s para uma sala ficar 100%
        // chata; depois de sair, ela fica chata por _boredomDecayDelaySteps (1000 = 20 s) e só
        // então desce, em 6000 = 2 min até zero. Era 60 s sem carência: a sala esfriava rápido
        // demais e o agente voltava para ela logo. Sala 0 (corredor, vão) nunca entedia.
        // Zera no respawn (ResetEpisode).
        [SerializeField, Min(1)] private int _boredomRiseSteps = 1500;
        [SerializeField, Min(1)] private int _boredomDecaySteps = 6000;
        [SerializeField, Min(0)] private int _boredomDecayDelaySteps = 1000;

        private static readonly Color VisitedColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
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

        // Valor de cada saída do nó âncora (indexado pelo nó vizinho). Só muda quando a âncora
        // muda ou um nó novo é visitado — a mesma condição da fronteira.
        private float[] _exitValue;

        // Distância (m pelo grafo) até o nó que ainda vale algo mais próximo, por saída do nó
        // âncora (NavGraph.DistanceToNearestBeyond; -1 = nada por ali), e a menor delas. É a
        // observação "quão perto está o que falta" — o par do "quanto resta" (_exitValue).
        private float[] _exitNearest;
        private float _bestExitNearest;

        // DIAGNÓSTICO: loops e pisca-pisca por nó neste episódio (log do manager, _logLoopNodes).
        private int[] _loopCount;

        // Quando e onde o agente chegou ao nó âncora atual. Base do detector de pisca-pisca.
        private int _lastArrivalStep;
        private Vector3 _lastArrivalPosition;
        private float _bestExitValue;
        private int _scoredFromNode = -1;
        private bool _exitsDirty = true;

        // Valor que cada nó PAGARIA se o agente chegasse agora (inédito: peso; visitado: a renda
        // de revisita). É o que o "quanto resta por saída" soma. Rascunho, alocado uma vez.
        private float[] _potentialValue;

        // TickedSteps da última chegada a cada nó. Só tem sentido com _visited[node].
        private int[] _lastVisitStep;

        // Tédio por sala, indexado por NavNode.AreaId (a posição 0 nunca é usada).
        private float[] _areaBoredom;

        // TickedSteps da última vez em que o agente estava dentro de cada sala (carência do tédio).
        private int[] _areaLastInsideStep;

        // Configuração do episódio, vinda do currículo pelo ResetEpisode.
        private int _recoverySteps;
        private bool _areaBoredomEnabled;

        // A chegada deste step pagou uma revisita que vale como "trabalho útil" (recuperação >=
        // 0.5): zera o relógio da estagnação, como um nó inédito. Sem isso, na patrulha (quando
        // não há mais nada inédito) a estagnação cobraria o episódio inteiro.
        private bool _paidRevisitThisTick;

        public int CurrentNodeIndex { get; private set; } = -1;

        public int PreviousNodeIndex { get; private set; } = -1;

        /// <summary>Se o agente está DENTRO do raio de um nó agora (e não a caminho entre dois).</summary>
        public bool IsAtNode { get; private set; }

        public bool EnteredNewNode { get; private set; }

        /// <summary>
        /// Soma dos VALORES dos nós inéditos alcançados desde o último <see cref="ClearStepFlags"/>:
        /// pontuação do tipo x peso (NavGraph.DiscoveryValue), com o sorteio do episódio.
        /// </summary>
        public float EnteredNewNodeValue { get; private set; }

        /// <summary>Percorreu uma aresta do grafo que ainda não tinha sido percorrida.</summary>
        public bool TraversedNewEdge { get; private set; }

        public bool ChangedNode { get; private set; }

        /// <summary>Quantas vezes o agente já chegou ao nó atual neste episódio (>= 1).</summary>
        public int CurrentNodeVisitCount { get; private set; }

        /// <summary>
        /// Steps de FÍSICA desde o último nó inédito (ou revisita que pagou, com a patrulha
        /// ligada). Base do sinal de estagnação.
        /// </summary>
        public int StepsSinceNewNode { get; private set; }

        /// <summary>
        /// Renda de REVISITA acumulada desde o último <see cref="ClearStepFlags"/>: peso x
        /// _revisitValueFraction x recuperação² x (1 - tédio da sala). Zero com a patrulha desligada.
        /// </summary>
        public float RevisitValue { get; private set; }

        /// <summary>Chegadas em primário visitado há menos de _earlyRevisitWindowSteps, desde o último ClearStepFlags.</summary>
        public int EarlyRevisitArrivals { get; private set; }

        /// <summary>Revisitas precoces SEGUIDAS (zera ao chegar num primário inédito ou visitado há tempo).</summary>
        public int EarlyRevisitStreak { get; private set; }

        /// <summary>Revisitas precoces no episódio inteiro (métrica).</summary>
        public int EarlyRevisitCount { get; private set; }

        /// <summary>Pisca-pisca de âncora no episódio (métrica Exploration/AnchorFlicker; ver _flickerWindowSteps).</summary>
        public int AnchorFlickers { get; private set; }

        /// <summary>
        /// Nó que ainda vale algo mais próximo do nó âncora pelo grafo, e a distância em metros até
        /// ele. É o potencial do shaping de NAVEGAÇÃO (GraphRewardSystem._unexploredProgressPerMeter):
        /// ao contrário da seta, sempre ligado — voltar de um beco por nós visitados em direção ao
        /// que falta paga, ir e voltar soma zero. -1 / 0 quando não há nada que valha.
        /// </summary>
        public int NearestUnexploredTarget { get; private set; } = -1;

        public float NearestUnexploredDistance { get; private set; }

        /// <summary>Tédio (0..1) da sala do nó âncora. 0 em corredor ou com o tédio desligado.</summary>
        public float CurrentAreaBoredom => CurrentNodeIndex >= 0 ? AreaBoredomOf(CurrentNodeIndex) : 0f;

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

        public bool HasFrontier { get; private set; }

        /// <summary>Nó não-visitado mais próximo em número de arestas.</summary>
        public int FrontierTarget { get; private set; } = -1;

        /// <summary>Primeiro nó do caminho até ele — é para cá que o agente deve andar AGORA.</summary>
        public int FrontierNextStep { get; private set; } = -1;

        /// <summary>Distância em METROS pelo grafo, do nó âncora até o alvo da fronteira.</summary>
        public float FrontierDistance { get; private set; }

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
            _exitValue = new float[_graph.NodeCount];
            _exitNearest = new float[_graph.NodeCount];
            _loopCount = new int[_graph.NodeCount];
            _potentialValue = new float[_graph.NodeCount];
            _lastVisitStep = new int[_graph.NodeCount];
            _areaBoredom = new float[_graph.MaxAreaId + 1];
            _areaLastInsideStep = new int[_graph.MaxAreaId + 1];
        }

        /// <summary>
        /// Zera o episódio. O denominador da cobertura é fotografado AQUI: se uma lição do
        /// currículo desliga uma ala do mapa, ela precisa sair da conta antes do primeiro step,
        /// senão o alvo de cobertura fica inatingível e todo episódio termina em fracasso.
        ///
        /// <paramref name="previsitedFraction"/> (0..1) é a fração dos primários ativos que
        /// já começa marcada como visitada, sorteada a cada episódio. Ver <see cref="_previsited"/>.
        /// </summary>
        ///
        /// <paramref name="recoverySteps"/> &gt; 0 liga a PATRULHA (nó visitado recupera o valor
        /// nesse tempo); <paramref name="areaBoredom"/> liga o tédio de sala. Os dois zeram aqui —
        /// é o "reset no respawn".
        public void ResetEpisode(float previsitedFraction = 0f, float weightJitter = 0f, int recoverySteps = 0, bool areaBoredom = false)
        {
            _recoverySteps = Mathf.Max(0, recoverySteps);
            _areaBoredomEnabled = areaBoredom && _areaBoredom.Length > 1;
            System.Array.Clear(_areaBoredom, 0, _areaBoredom.Length);
            System.Array.Clear(_areaLastInsideStep, 0, _areaLastInsideStep.Length);
            System.Array.Clear(_lastVisitStep, 0, _lastVisitStep.Length);
            EarlyRevisitStreak = 0;
            EarlyRevisitCount = 0;
            AnchorFlickers = 0;
            System.Array.Clear(_loopCount, 0, _loopCount.Length);
            _lastArrivalStep = int.MinValue / 2;
            NearestUnexploredTarget = -1;
            NearestUnexploredDistance = 0f;

            System.Array.Clear(_visited, 0, _visited.Length);
            System.Array.Clear(_visitCount, 0, _visitCount.Length);
            System.Array.Clear(_previsited, 0, _previsited.Length);
            _traversedEdges.Clear();

            DrawEpisodeWeights(weightJitter);
            DrawPrevisited(previsitedFraction);

            _enabledNodeCount = 0;
            _visitedNodeCount = 0;
            _collectedWeight = 0f;

            RecomputeTotalWeight();

            CurrentNodeIndex = -1;
            PreviousNodeIndex = -1;
            IsAtNode = false;
            CurrentNodeVisitCount = 0;
            StepsSinceNewNode = 0;

            ClearStepFlags();

            HasFrontier = false;
            FrontierTarget = -1;
            FrontierNextStep = -1;
            FrontierDistance = 0f;
            _frontierDirty = true;
            _exitsDirty = true;
            OffNodeSteps = 0;
            TickedSteps = 0;
        }

        /// <summary>
        /// Denominador da cobertura por peso: a soma dos pesos dos primários ATIVOS. Um nó
        /// desligado por uma lição sai da conta, senão o alvo de cobertura vira inatingível.
        ///
        /// Auxiliares e pings ficam fora, mesmo quando a pontuação do tipo deles é maior que 0:
        /// adensar a malha para o agente não se perder nunca mexe no denominador da cobertura.
        /// </summary>
        private void RecomputeTotalWeight()
        {
            _totalWeight = 0f;

            for (int i = 0; i < _graph.NodeCount; i++)
            {
                // Pré-visitado sai do denominador dos DOIS medidores: ele não pode ser
                // coletado, então contá-lo tornaria o alvo de cobertura inatingível.
                //
                // Só EXPLORAÇÃO: auxiliar e ping podem pagar ao ser descobertos (pontuação do
                // tipo no NavGraph), mas a cobertura — a barra que encerra o episódio — é "quanto
                // dos pontos de vantagem eu vi". Com centenas de ladrilhos auxiliares pagando, a
                // barra passaria a medir quanto CHÃO ele pisou.
                if (!_graph.IsNodeEnabled(i) || _previsited[i] || !_graph.IsNodePrimary(i))
                    continue;

                _totalWeight += _episodeWeight[i];
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

                // "Visitado há um tempo": fora da janela de revisita precoce, senão passar por um
                // pré-visitado no começo do episódio contaria como loop.
                _lastVisitStep[_shuffleBuffer[k]] = -_earlyRevisitWindowSteps;
            }
        }

        /// <summary>
        /// Steps de física do episódio em que o agente estava FORA de qualquer área de nó. É a
        /// métrica que diz se o grafo cobre o chão (Exploration/OffNodeFraction no TensorBoard):
        /// fora de nó a observação de vizinhos fica presa na âncora antiga. Com o grafo gerado
        /// pelo NavGraphPlacer isto deveria ficar perto de zero.
        /// </summary>
        public int OffNodeSteps { get; private set; }

        public int TickedSteps { get; private set; }

        public void Tick(Vector3 worldPosition)
        {
            // Com histerese: o nó atual segura a âncora enquanto o agente ainda estiver na área dele.
            int node = _graph.FindNodeAt(worldPosition, CurrentNodeIndex);
            IsAtNode = node >= 0;

            TickedSteps++;
            if (!IsAtNode)
                OffNodeSteps++;

            _paidRevisitThisTick = false;

            // Fora de qualquer raio, CurrentNodeIndex NÃO volta para -1: ele continua sendo o
            // último nó alcançado. É essa persistência que dá uma âncora no grafo enquanto o
            // agente atravessa um corredor — sem ela a busca de fronteira ficaria cega no meio
            // de cada travessia, que é justamente quando ela é mais útil.
            if (node >= 0 && node != CurrentNodeIndex)
            {
                // Pisca-pisca: voltou para o nó de onde acabou de sair, rápido e quase sem andar.
                if (node == PreviousNodeIndex
                    && TickedSteps - _lastArrivalStep < _flickerWindowSteps
                    && PlanarDistance(worldPosition, _lastArrivalPosition) < _flickerDistance)
                {
                    AnchorFlickers++;
                    _loopCount[node]++;
                }

                _lastArrivalStep = TickedSteps;
                _lastArrivalPosition = worldPosition;

                RegisterArrival(node);
                ChangedNode = true;
                _frontierDirty = true;
                _exitsDirty = true;
            }

            StepsSinceNewNode = EnteredNewNode || _paidRevisitThisTick ? 0 : StepsSinceNewNode + 1;

            UpdateAreaBoredom();

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
            RevisitValue = 0f;
            EarlyRevisitArrivals = 0;
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

            if (_visited[node])
            {
                RegisterRevisit(node);
                return;
            }

            _lastVisitStep[node] = TickedSteps;

            // Marcado como visitado sempre, inclusive auxiliar: é o que faz o gizmo mostrar por
            // onde ele passou e o que impede a fronteira de reprocessá-lo. O que muda é o resto.
            _visited[node] = true;

            // Somado, e não atribuído: entre duas decisões o agente pode cruzar mais de um
            // nó, e cada um tem que pagar o seu. O valor já vem com a pontuação do TIPO
            // (NavGraph.DiscoveryValue): auxiliar paga 0 por padrão e ping paga 0 sempre (ele
            // paga ao ser atendido), então a malha continua grátis a menos que você a pontue.
            float weight = _episodeWeight[node];
            EnteredNewNodeValue += weight;

            // Só EXPLORAÇÃO conta para a cobertura e é "nó novo" (porteira da fronteira e
            // relógio da estagnação). Auxiliar e ping já fizeram o trabalho deles.
            if (!_graph.IsNodePrimary(node))
                return;

            _visitedNodeCount++;
            EnteredNewNode = true;
            _collectedWeight += weight;
            EarlyRevisitStreak = 0;
        }

        // Chegada num nó JÁ visitado. Só primário conta: auxiliar é guia de corredor, e passar
        // de novo por ele é o normal de qualquer caminho de volta.
        private void RegisterRevisit(int node)
        {
            if (!_graph.IsNodePrimary(node))
                return;

            // Lidos ANTES de reiniciar o relógio do nó.
            int since = TickedSteps - _lastVisitStep[node];
            float recovery = Recovery(node);
            _lastVisitStep[node] = TickedSteps;

            if (_recoverySteps > 0 && recovery > 0f)
            {
                RevisitValue += RevisitPotential(node, recovery);
                if (recovery >= 0.5f)
                    _paidRevisitThisTick = true;
            }

            if (since < _earlyRevisitWindowSteps)
            {
                EarlyRevisitStreak++;
                EarlyRevisitArrivals++;
                EarlyRevisitCount++;
                _loopCount[node]++;
            }
            else
            {
                EarlyRevisitStreak = 0;
            }
        }

        /// <summary>
        /// Quanto do valor o nó já recuperou (0..1). Inédito = 1. Visitado sem patrulha = 0 para
        /// sempre (o comportamento de antes: nó visitado não paga mais no episódio).
        /// </summary>
        public float Recovery(int node)
        {
            if (!_visited[node])
                return 1f;

            if (_recoverySteps <= 0)
                return 0f;

            // Carência primeiro (valor parado em zero), depois a rampa linear.
            int since = TickedSteps - _lastVisitStep[node] - _recoveryDelaySteps;
            return Mathf.Clamp01(since / (float)_recoverySteps);
        }

        /// <summary>Tédio (0..1) da sala do nó. 0 para sala 0 ou com o tédio desligado.</summary>
        public float AreaBoredomOf(int node)
        {
            if (!_areaBoredomEnabled)
                return 0f;

            int area = _graph.AreaOf(node);
            return area > 0 && area < _areaBoredom.Length ? _areaBoredom[area] : 0f;
        }

        /// <summary>
        /// O que a observação chama de "visitado", agora com a patrulha dentro: 0 = nunca
        /// visitado; 0.5 = visitado e já recuperado; 1 = acabou de passar. Com a patrulha
        /// desligada a recuperação é 0 e todo visitado vale 1 — exatamente o 0/1 de antes.
        /// </summary>
        public float VisitedObservation(int node) =>
            _visited[node] ? 0.5f + 0.5f * (1f - Recovery(node)) : 0f;

        // Renda de revisita de um nó com esta recuperação. O tédio só desconta REVISITA: o nó
        // inédito de uma sala chata ainda paga cheio — senão o agente abandonaria a sala antes de
        // terminar de vê-la, e a cobertura (a primeira metade do objetivo) sairia prejudicada.
        private float RevisitPotential(int node, float recovery) =>
            _episodeWeight[node] * _revisitValueFraction * recovery * recovery * (1f - AreaBoredomOf(node));

        private void UpdateAreaBoredom()
        {
            if (!_areaBoredomEnabled)
                return;

            int current = CurrentNodeIndex >= 0 ? _graph.AreaOf(CurrentNodeIndex) : 0;
            float rise = 1f / _boredomRiseSteps;
            float decay = 1f / _boredomDecaySteps;

            for (int area = 1; area < _areaBoredom.Length; area++)
            {
                if (area == current)
                {
                    _areaBoredom[area] = Mathf.Min(1f, _areaBoredom[area] + rise);
                    _areaLastInsideStep[area] = TickedSteps;
                }
                else if (TickedSteps - _areaLastInsideStep[area] > _boredomDecayDelaySteps)
                {
                    _areaBoredom[area] = Mathf.Max(0f, _areaBoredom[area] - decay);
                }
            }
        }

        /// <summary>
        /// Sorteia o valor de cada nó para o episódio: valor de descoberta (pontuação do tipo x
        /// peso, NavGraph.DiscoveryValue) x U[1 - jitter, 1 + jitter], nunca negativo. Com jitter 0 é o peso autorado. A SOMA esperada não muda (o fator
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
                _episodeWeight[i] = Mathf.Max(0f, _graph.DiscoveryValue(i) * factor);
                _maxEpisodeWeight = Mathf.Max(_maxEpisodeWeight, _episodeWeight[i]);
            }
        }

        /// <summary>
        /// Peso do nó neste episódio, NORMALIZADO pelo maior peso do mapa (0..1). É o que a
        /// observação entrega por vizinho: "quanto vale esta saída em relação ao nó que mais
        /// vale". Nó desligado dá 0; auxiliar e ping dão o valor de descoberta deles (0 por padrão).
        /// </summary>
        public float NormalizedEpisodeWeight(int node)
        {
            if (_maxEpisodeWeight <= 1e-6f || !_graph.IsNodeEnabled(node))
                return 0f;

            return Mathf.Clamp01(_episodeWeight[node] / _maxEpisodeWeight);
        }

        /// <summary>
        /// Calcula quanto resta por cada saída do nó âncora (ver <see cref="NavGraph.ScoreBeyond"/>).
        /// Chamar uma vez por decisão, antes de ler <see cref="ExitRemainingScore"/>; só refaz a
        /// conta quando a âncora mudou ou algo foi visitado. Sem âncora, tudo fica em zero.
        /// </summary>
        public void ScoreExits()
        {
            // Com patrulha ou tédio o valor dos nós muda com o TEMPO, não só com chegadas: aí
            // refaz a cada decisão (8 buscas num grafo de ~130 nós — barato).
            bool changesOverTime = _recoverySteps > 0 || _areaBoredomEnabled;
            if (!changesOverTime && !_exitsDirty && _scoredFromNode == CurrentNodeIndex)
                return;

            _exitsDirty = false;
            _scoredFromNode = CurrentNodeIndex;
            _bestExitValue = 0f;
            _bestExitNearest = -1f;

            if (CurrentNodeIndex < 0)
                return;

            FillPotentialValues();

            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                float value = _graph.ScoreBeyond(CurrentNodeIndex, neighbor, _exitHalfLifeMeters, _potentialValue);
                _exitValue[neighbor] = value;
                _bestExitValue = Mathf.Max(_bestExitValue, value);

                float nearest = _graph.DistanceToNearestBeyond(CurrentNodeIndex, neighbor, _potentialValue);
                _exitNearest[neighbor] = nearest;
                if (nearest > 0f && (_bestExitNearest < 0f || nearest < _bestExitNearest))
                    _bestExitNearest = nearest;
            }
        }

        // Valor que cada nó PAGARIA se o agente chegasse agora: inédito = peso do episódio;
        // visitado = renda de revisita (só com patrulha); o resto, 0.
        private void FillPotentialValues()
        {
            for (int i = 0; i < _potentialValue.Length; i++)
            {
                if (!_visited[i])
                    _potentialValue[i] = _episodeWeight[i];
                else if (_recoverySteps > 0 && _graph.IsNodePrimary(i))
                    _potentialValue[i] = RevisitPotential(i, Recovery(i));
                else
                    _potentialValue[i] = 0f;
            }
        }

        /// <summary>
        /// QUÃO PERTO está o que falta por esta saída, relativo à saída mais perto: 1 = a mais
        /// perto (ou empatada), d_melhor / d_esta nas outras, 0 = nada por ali. É um campo de
        /// distância (ver NavGraph.DistanceToNearestBeyond): seguir sempre o 1 só diminui a distância
        /// ao inexplorado, então não entra em loop, e num beco cercado de visitados ele continua
        /// apontando o caminho de volta. Ocupa o float que era o tédio da sala do vizinho (sempre 0
        /// nas etapas, que desligam o tédio): o vetor fica em 118 e os cérebros da E1/E2 continuam
        /// servindo — eles nunca usaram esse float.
        /// </summary>
        public float ExitProximityScore(int neighbor)
        {
            if (_scoredFromNode != CurrentNodeIndex || _bestExitNearest <= 0f)
                return 0f;

            float distance = _exitNearest[neighbor];
            if (distance <= 0f)
                return 0f;

            return Mathf.Clamp01(_bestExitNearest / distance);
        }

        // Potencial do shaping de navegação: o nó que ainda vale algo mais próximo da âncora.
        // Recalculado com a fronteira (troca de nó ou algo visitado).
        private void UpdateNearestUnexplored()
        {
            NearestUnexploredTarget = -1;
            NearestUnexploredDistance = 0f;

            if (CurrentNodeIndex < 0)
                return;

            FillPotentialValues();
            NearestUnexploredTarget = _graph.NearestWithValue(CurrentNodeIndex, _potentialValue, out float distance);
            NearestUnexploredDistance = NearestUnexploredTarget >= 0 ? distance : 0f;
        }

        /// <summary>
        /// DIAGNÓSTICO: os <paramref name="count"/> nós com mais loops/pisca-pisca no episódio, como
        /// "nome (n)". Vazio se não houve nenhum.
        /// </summary>
        public string TopLoopNodes(int count)
        {
            var builder = new System.Text.StringBuilder();
            var used = new HashSet<int>();

            for (int k = 0; k < count; k++)
            {
                int best = -1;
                for (int i = 0; i < _loopCount.Length; i++)
                {
                    if (_loopCount[i] > 0 && !used.Contains(i) && (best < 0 || _loopCount[i] > _loopCount[best]))
                        best = i;
                }

                if (best < 0)
                    break;

                used.Add(best);
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(_graph.GetNode(best).name).Append(" (").Append(_loopCount[best]).Append(')');
            }

            return builder.ToString();
        }

        private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        /// <summary>
        /// Quanto resta explorar por esta saída, RELATIVO à melhor saída do nó atual: 1 = a
        /// melhor (ou empatada), 0 = nada inexplorado por ali. Relativo porque a decisão é "qual
        /// porta": um número que vale 1 para a melhor porta em qualquer fase do episódio é mais
        /// fácil de ler que uma soma que encolhe conforme o mapa é coberto. "Quanto falta no
        /// total" já está na cobertura.
        /// </summary>
        public float ExitRemainingScore(int neighbor)
        {
            if (_scoredFromNode != CurrentNodeIndex || _bestExitValue <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(_exitValue[neighbor] / _bestExitValue);
        }

        private void UpdateFrontier()
        {
            _frontierDirty = false;

            // Mesma condição de recálculo da seta (troca de nó ou algo visitado), mas independente
            // dela: o potencial de navegação vale com a seta desligada.
            UpdateNearestUnexplored();

            if (_frontierFollowsExits)
            {
                UpdateFrontierFromExits();
                return;
            }

            // Alvo fixo enquanto ele continuar valendo: o sorteio só acontece quando o alvo
            // atual foi visitado (ou ficou inalcançável). É o que faz a seta apontar para o
            // MESMO lugar do começo ao fim de uma travessia — e o que mantém o shaping de
            // aproximação comparável entre dois steps.
            if (FrontierTarget >= 0 && !_visited[FrontierTarget]
                && _graph.TryFindPathTo(CurrentNodeIndex, FrontierTarget, out int keptStep, out float keptDistance))
            {
                HasFrontier = true;
                FrontierNextStep = keptStep;
                FrontierDistance = keptDistance;
                return;
            }

            HasFrontier = _graph.TryFindNearestUnvisited(
                CurrentNodeIndex, _visited, _frontierCandidates, out int target, out int nextStep, out float distance);

            FrontierTarget = HasFrontier ? target : -1;
            FrontierNextStep = HasFrontier ? nextStep : -1;
            FrontierDistance = HasFrontier ? distance : 0f;
        }

        // Seta = a saída com o maior "quanto resta" (ver _frontierFollowsExits). Empate: a primeira
        // na ordem de adjacência — determinístico, e o sorteio de pesos por episódio já varia quem
        // ganha. Sem nenhuma saída valendo algo (tudo coletado, sem patrulha), não há seta.
        private void UpdateFrontierFromExits()
        {
            HasFrontier = false;
            FrontierTarget = -1;
            FrontierNextStep = -1;
            FrontierDistance = 0f;

            if (CurrentNodeIndex < 0)
                return;

            ScoreExits();

            int best = -1;
            float bestValue = 1e-6f;
            foreach (int neighbor in _graph.GetNeighbors(CurrentNodeIndex))
            {
                if (_graph.IsNodeEnabled(neighbor) && _exitValue[neighbor] > bestValue)
                {
                    bestValue = _exitValue[neighbor];
                    best = neighbor;
                }
            }

            if (best < 0 || !_graph.TryFindPathTo(CurrentNodeIndex, best, out _, out float distance))
                return;

            HasFrontier = true;
            FrontierTarget = best;
            FrontierNextStep = best;
            FrontierDistance = distance;
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

            // O disco do nó atual, no mesmo formato do gizmo de autoria: dá para ver ao vivo se
            // o raio que você calibrou está registrando a chegada onde você achou que ia.
            if (CurrentNodeIndex >= 0)
            {
                Gizmos.color = Color.yellow;
                _graph.DrawNodeArea(CurrentNodeIndex, 0.12f, 1);
            }

            if (_drawFrontier && HasFrontier && FrontierHintVisible)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(_graph.NodePosition(FrontierTarget), 0.45f);
                Gizmos.DrawLine(transform.position, _graph.NodePosition(FrontierNextStep));
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
                    _graph.DrawNodeArea(i, 0.03f, 1);
                    continue;
                }

                if (!_visited[i])
                {
                    // O que falta, só de contorno: o pendente precisa ser localizável sem
                    // competir visualmente com o que já foi feito.
                    if (_drawPendingNodes)
                    {
                        Gizmos.color = PendingColor;
                        _graph.DrawNodeArea(i, 0.04f, 1);
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
                    _graph.DrawNodeArea(i, 0.06f, 3);
                    continue;
                }

                // Verde -> laranja conforme as revisitas. Ver o nó esquentar é a forma mais
                // rápida de flagrar vai-e-vem: nó e aresta pagam uma vez por episódio, então
                // cor quente aqui é tempo gasto sem retorno nenhum.
                float heat = Mathf.Clamp01((_visitCount[i] - 1f) / Mathf.Max(1, _heatSaturationVisits));

                Color visited = Color.Lerp(VisitedColor, RevisitedColor, heat);

                // Patrulha: o disco desbota para o cinza do pendente conforme o nó recupera o
                // valor — verde forte = acabou de passar, quase cinza = já vale voltar.
                if (_recoverySteps > 0)
                    visited = Color.Lerp(visited, PendingColor, Recovery(i));

                Gizmos.color = visited;
                _graph.DrawNodeArea(i, 0.06f, 3);
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
