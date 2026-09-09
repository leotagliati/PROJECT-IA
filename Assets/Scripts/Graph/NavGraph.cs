using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Forma da área de chegada de um nó. Vale para o grafo inteiro: é a regra que transforma
    /// posição contínua em índice discreto, e ter duas formas convivendo no mesmo mapa tornaria
    /// impossível calibrar espaçamento olhando o gizmo.
    /// </summary>
    public enum NodeShape
    {
        /// <summary>
        /// Disco. Isotrópico: a chegada acontece à mesma distância venha o agente de onde vier.
        /// É o que faz sentido para PRIMÁRIO, onde a área certifica presença num ponto.
        /// </summary>
        Circle,

        /// <summary>
        /// Quadrado alinhado aos eixos, de lado 2 x raio. LADRILHA: uma cadeia de quadrados
        /// cobre um corredor inteiro sem as folgas em forma de lente que sobram entre discos
        /// tangentes. É o que faz sentido para uma malha de guia, onde o objetivo é não deixar
        /// buraco em que o agente fique sem âncora.
        /// </summary>
        Square,
    }

    /// <summary>
    /// Consolida os <see cref="NavNode"/> de UMA arena num grafo consultável: índices,
    /// adjacência, áreas e busca em largura. É estrutura do mapa, não estado de agente — o que
    /// o agente já visitou mora na <see cref="GraphExplorationMemory"/>, uma por agente.
    ///
    /// Existe um NavGraph por cópia da arena; os índices são locais a ele, então nove arenas na
    /// cena não compartilham nada (mesmo motivo pelo qual a grade do seeker é relativa à arena).
    /// </summary>
    public class NavGraph : MonoBehaviour
    {
        [Header("-----Nós-----")]
        // Preencha com "Coletar nós filhos" no menu de contexto do componente.
        [SerializeField] private List<NavNode> _nodes = new List<NavNode>();

        // Espelha as ligações no bake. Ligar A->B na mão e esquecer B->A produz um grafo
        // dirigido silencioso: o agente entra numa ala e a busca de fronteira não acha caminho
        // de volta. Com isto ligado, a autoria fica à prova disso.
        [SerializeField] private bool _makeLinksBidirectional = true;

        // Orçamento usado pelos nós que ficaram sem NavRegion atribuída. Eles são agrupados numa
        // única região implícita, então este valor é dividido entre TODOS eles.
        [SerializeField] private float _defaultRegionBudget = 1f;

        // Forma da área de chegada, para o mapa inteiro. No quadrado os dois raios abaixo deixam
        // de ser raio e passam a ser MEIA-ARESTA: trocar a forma sem mexer nos números aumenta a
        // área em ~27% e estica o alcance da diagonal em 41%. Reveja o espaçamento ao trocar.
        [SerializeField] private NodeShape _nodeShape = NodeShape.Circle;

        // UM RAIO PADRÃO POR PAPEL, porque os dois têm fórmulas de calibração opostas. Deixar os
        // dois no mesmo campo obrigaria a corrigir um deles nó a nó, no override — e replicar um
        // valor por nó é a forma mais rápida de dois nós discordarem sobre ele (o mesmo motivo
        // pelo qual o orçamento mora na NavRegion e não em cada NavNode).
        //
        // PRIMÁRIO — certifica presença, então quer ficar APERTADO:
        //   - grande demais: o raio do vizinho encosta no dele, a chegada acontece no meio do
        //     caminho e "visitado" deixa de significar "estive lá";
        //   - pequeno demais: ele passa reto e a visita nunca é registrada.
        // Ponto de partida: metade da MENOR aresta entre dois primários, nunca menor que o raio
        // do corpo do agente. O aviso do bake mede exatamente isso.
        [FormerlySerializedAs("_defaultNodeRadius")]
        [SerializeField] private float _defaultPrimaryRadius = 1.8f;

        // AUXILIAR — o papel dele é PEGAR o agente, então quer ficar GENEROSO. Discos se
        // sobrepondo ao longo da cadeia é o desenho pretendido: é o que garante que o agente
        // nunca fique sem âncora no meio de um corredor. Por isso o aviso de sobreposição do
        // bake ignora arestas que envolvem auxiliar — aqui não há nada a proteger.
        // Ponto de partida: um pouco acima do espaçamento da cadeia dividido por dois.
        [SerializeField] private float _defaultAuxiliaryRadius = 3f;

        [Header("-----Checagem de parede-----")]
        [SerializeField] private LayerMask _wallLayer;

        // Altura da sonda. Zero rasparia no chão e acusaria parede em qualquer degrau.
        [SerializeField] private float _linkProbeHeight = 0.5f;

        // Raio da sonda: da ordem do raio do agente. Com 0 a checagem é uma linha, e uma aresta
        // que passa raspando na quina de uma parede é aprovada — o agente então tenta segui-la
        // e entala. Com folga, a aresta só é válida se o CORPO dele passa.
        //
        // A regra é METADE DA LARGURA DO AGENTE. Com o corpo em 1.7 de largura, 0.85. O valor
        // antigo (0.3) aprovava passagens por onde ele não cabe, e o sintoma era ele tentar
        // seguir a aresta e travar na quina — indistinguível, de fora, de "a política é ruim".
        [SerializeField] private float _linkClearance = 0.85f;

        [Header("-----Auto-ligação-----")]
        // Usado só pelo menu de contexto "Auto-ligar por linha de visão".
        [SerializeField] private float _autoLinkMaxDistance = 8f;

        [Header("-----Gizmos-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Discos de chegada de todos os nós. É o gizmo para calibrar os raios padrão:
        // dois discos de PRIMÁRIO encostando significa que a chegada acontece no meio do caminho.
        [SerializeField] private bool _drawNodeRadii = true;

        // Cor do disco por PAPEL. São dois canais visuais diferentes de propósito:
        //   o PONTO do nó continua na cor da REGIÃO (quem desenha é o NavNode),
        //   o DISCO diz o papel — e o disco é o que se lê olhando o mapa de cima, porque é
        //   dezenas de vezes maior que o ponto.
        // Assim as duas informações cabem na mesma figura sem disputarem o mesmo canal.
        //
        // Evite verde, laranja, amarelo, magenta e branco: essas cinco já significam outra coisa
        // nos gizmos de Play (visitado, revisitado, âncora, fronteira, aresta percorrida). Uma
        // cor com dois significados é pior que nenhuma cor, porque você calibra olhando pra ela.
        [SerializeField] private Color _primaryRadiusColor = new Color(0.25f, 0.75f, 0.95f, 0.55f);

        // Bem apagado: a malha é cenário. Se ela competir visualmente com os primários, você
        // perde de vista justamente os poucos nós que decidem a recompensa.
        [SerializeField] private Color _auxiliaryRadiusColor = new Color(0.55f, 0.60f, 0.72f, 0.22f);

        // Colore as arestas conforme atravessam parede ou não. É um spherecast por aresta por
        // frame de editor: desligue se a cena ficar pesada.
        [SerializeField] private bool _validateLinksInGizmos = true;

        private int[][] _adjacency;
        private int[] _regionSlotOfNode;
        private int[] _regionNodeCounts;
        private NavRegion[] _regions;
        private int _regionCount;
        private bool _isBaked;

        // Rascunho da BFS, alocado uma vez. O carimbo evita limpar o array de visitados a cada
        // busca — a BFS roda uma vez por step de física, por agente.
        private int[] _bfsQueue;
        private int[] _bfsParent;
        private int[] _bfsDepth;
        private int[] _bfsStampOf;
        private int _bfsStamp;

        public LayerMask WallLayer => _wallLayer;

        public NodeShape Shape => _nodeShape;

        /// <summary>
        /// Distância PLANAR na métrica da forma escolhida — euclidiana no círculo, Chebyshev
        /// (o maior dos dois eixos) no quadrado. Uma função só, usada pela detecção de chegada,
        /// pelo desempate do nó mais próximo e pela validação de áreas sobrepostas: assim trocar
        /// a forma no Inspector muda os três de uma vez, e nenhum deles pode discordar do gizmo.
        /// </summary>
        private float AreaDistance(Vector3 a, Vector3 b)
        {
            float dx = Mathf.Abs(a.x - b.x);
            float dz = Mathf.Abs(a.z - b.z);

            return _nodeShape == NodeShape.Square
                ? Mathf.Max(dx, dz)
                : new Vector2(dx, dz).magnitude;
        }

        public float DefaultPrimaryRadius => _defaultPrimaryRadius;

        public float DefaultAuxiliaryRadius => _defaultAuxiliaryRadius;

        /// <summary>
        /// Raio efetivo de um nó: o override dele, ou o padrão DO PAPEL dele. O override fica
        /// para a exceção que a geometria exige — um saguão onde o nó deve cobrir mais chão, um
        /// doorway apertado onde o raio invadiria a sala vizinha — e não para corrigir em massa
        /// o valor de um papel inteiro; isso se faz aqui, num lugar só.
        /// </summary>
        public float NodeRadius(int index) => RadiusOf(_nodes[index]);

        private float RadiusOf(NavNode node)
        {
            float over = node.RadiusOverride;
            if (over > 0f)
                return over;

            return node.IsPrimary ? _defaultPrimaryRadius : _defaultAuxiliaryRadius;
        }

        public int NodeCount => _nodes.Count;

        public int RegionCount => _regionCount;

        public IReadOnlyList<NavNode> Nodes => _nodes;

        private void Awake() => EnsureBaked();

        /// <summary>
        /// Idempotente de propósito: o agente chama isto no Initialize e o Awake do grafo pode
        /// ou não ter rodado antes (a ordem entre componentes de objetos diferentes não é
        /// garantida). Quem chegar primeiro faz o bake.
        /// </summary>
        public void EnsureBaked()
        {
            if (_isBaked)
                return;

            _nodes.RemoveAll(node => node == null);

            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            BuildAdjacency();
            BuildRegions();

            _bfsQueue = new int[_nodes.Count];
            _bfsParent = new int[_nodes.Count];
            _bfsDepth = new int[_nodes.Count];
            _bfsStampOf = new int[_nodes.Count];

            _isBaked = true;

            ValidateBakedGraph();
        }

        private void BuildAdjacency()
        {
            var sets = new List<HashSet<int>>(_nodes.Count);
            for (int i = 0; i < _nodes.Count; i++)
                sets.Add(new HashSet<int>());

            for (int i = 0; i < _nodes.Count; i++)
            {
                foreach (NavNode neighbor in _nodes[i].Neighbors)
                {
                    if (neighbor == null || neighbor.Index == i)
                        continue;

                    // A checagem de identidade (e não só de faixa) é o que impede uma referência
                    // vazada de outra cópia da arena de virar uma aresta silenciosamente errada:
                    // aquele nó TEM um índice válido — o dele, no grafo dele — e um teste de
                    // faixa aceitaria, ligando este grafo a um nó local qualquer que por acaso
                    // ocupe aquele número. O sintoma seria uma aresta que atravessa o mapa.
                    if (neighbor.Index < 0 || neighbor.Index >= _nodes.Count || _nodes[neighbor.Index] != neighbor)
                    {
                        Debug.LogWarning(
                            $"{name}: '{_nodes[i].name}' aponta para '{neighbor.name}', que não pertence a este " +
                            "grafo. Ligação ignorada — provavelmente uma referência vazada de outra arena.",
                            _nodes[i]);
                        continue;
                    }

                    sets[i].Add(neighbor.Index);

                    if (_makeLinksBidirectional)
                        sets[neighbor.Index].Add(i);
                }
            }

            _adjacency = new int[_nodes.Count][];
            for (int i = 0; i < _nodes.Count; i++)
            {
                _adjacency[i] = new int[sets[i].Count];
                sets[i].CopyTo(_adjacency[i]);
            }
        }

        // As regiões viram slots contíguos (0..n-1) para que os arrays de cobertura por região
        // sejam pequenos e indexáveis. Nós sem região caem num slot implícito compartilhado —
        // com aviso, porque é quase sempre esquecimento de autoria, não intenção.
        private void BuildRegions()
        {
            var slotOf = new Dictionary<NavRegion, int>();
            var ordered = new List<NavRegion>();
            int unassignedSlot = -1;

            _regionSlotOfNode = new int[_nodes.Count];

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavRegion region = _nodes[i].Region;

                if (region == null)
                {
                    if (unassignedSlot < 0)
                    {
                        unassignedSlot = ordered.Count;
                        ordered.Add(null);
                    }

                    _regionSlotOfNode[i] = unassignedSlot;
                    continue;
                }

                if (!slotOf.TryGetValue(region, out int slot))
                {
                    slot = ordered.Count;
                    slotOf.Add(region, slot);
                    ordered.Add(region);
                }

                _regionSlotOfNode[i] = slot;
            }

            _regions = ordered.ToArray();
            _regionCount = _regions.Length;
            _regionNodeCounts = new int[_regionCount];

            for (int i = 0; i < _nodes.Count; i++)
                _regionNodeCounts[_regionSlotOfNode[i]]++;
        }

        public Vector3 NodePosition(int index) => _nodes[index].Position;

        public NavNode GetNode(int index) => _nodes[index];

        public bool IsNodeEnabled(int index) => _nodes[index].IsEnabled;

        /// <summary>
        /// Nó primário (ponto de vantagem) ou auxiliar (guia)? Cobertura, conclusão de região,
        /// alvo de fronteira e recompensa de aresta são todos privilégio do primário.
        /// </summary>
        public bool IsNodePrimary(int index) => _nodes[index].IsPrimary;

        public int[] GetNeighbors(int index) => _adjacency[index];

        public int RegionSlotOf(int index) => _regionSlotOfNode[index];

        public int RegionNodeCount(int regionSlot) => _regionNodeCounts[regionSlot];

        public NavRegion RegionAt(int regionSlot) => _regions[regionSlot];

        /// <summary>
        /// Orçamento da região daquele slot. Nós sem região usam <see cref="_defaultRegionBudget"/> —
        /// o treino não quebra por causa de um nó esquecido, mas o aviso no bake te avisa.
        /// </summary>
        public float RegionBudget(int regionSlot)
        {
            NavRegion region = _regions[regionSlot];
            return region != null ? region.ExplorationBudget : _defaultRegionBudget;
        }

        /// <summary>
        /// Só os primários ativos. É o denominador da cobertura geométrica: a malha auxiliar não
        /// pode diluir "quanto do mapa eu já vi" — adensar a guia faria a barra de progresso
        /// andar mais devagar sem o mapa ter ficado maior.
        /// </summary>
        public int EnabledPrimaryCount()
        {
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].IsEnabled && _nodes[i].IsPrimary)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Todos os nós ativos, primários e auxiliares. Usado pela checagem de conectividade do
        /// bake, onde o que importa é alcançabilidade — e um auxiliar isolado também é um bug.
        /// </summary>
        public int EnabledNodeCount()
        {
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].IsEnabled)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Nó ativo cuja área de chegada contém a posição; havendo mais de um, o mais central.
        /// Devolve -1 quando o agente está no meio do nada — o que é normal e não é erro: entre
        /// dois nós ele simplesmente não está em nenhum.
        ///
        /// A distância é PLANAR (X/Z) e vem de <see cref="AreaDistance"/>, então a forma
        /// escolhida no Inspector decide a área: disco no círculo, quadrado no Chebyshev. O
        /// mapa tem um andar só, e medir em 3D faria a altura do nó em relação ao agente comer
        /// parte da área — um nó desenhado no chão registraria visita numa área menor que a do
        /// gizmo, sem nada indicando o porquê.
        /// </summary>
        public int FindNodeAt(Vector3 position)
        {
            int best = -1;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavNode node = _nodes[i];
                if (!node.IsEnabled)
                    continue;

                float distance = AreaDistance(node.Position, position);

                if (distance <= NodeRadius(i) && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Nó ativo mais próximo ALCANÇÁVEL EM LINHA RETA, ignorando raio de chegada. Serve para
        /// o agente saber onde a malha está quando ele se afastou dela — sem isso a única
        /// referência que ele tem é a âncora, que é um nó que ele já deixou para trás e pode
        /// estar atrás dele.
        ///
        /// A checagem de linha livre NÃO é opcional: o nó geometricamente mais próximo pode
        /// estar do outro lado de uma parede, e apontar para ele ensinaria o agente a andar
        /// contra o concreto. Quando nenhum nó tem linha livre (o agente entalou numa quina,
        /// por exemplo), cai no mais próximo puro — uma referência ruim ainda é melhor que
        /// nenhuma, e a alternativa seria a observação piscar entre "tem alvo" e "não tem".
        /// </summary>
        public int FindNearestReachableNode(Vector3 position)
        {
            int nearestClear = -1;
            int nearestAny = -1;
            float bestClear = float.MaxValue;
            float bestAny = float.MaxValue;

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavNode node = _nodes[i];
                if (!node.IsEnabled)
                    continue;

                Vector3 delta = node.Position - position;
                float distance = new Vector2(delta.x, delta.z).magnitude;

                if (distance < bestAny)
                {
                    bestAny = distance;
                    nearestAny = i;
                }

                // Só paga o custo do SphereCast enquanto ele pode mudar a resposta.
                if (distance < bestClear && IsSegmentClear(position, node.Position))
                {
                    bestClear = distance;
                    nearestClear = i;
                }
            }

            return nearestClear >= 0 ? nearestClear : nearestAny;
        }

        /// <summary>
        /// BFS a partir de <paramref name="from"/> até o nó ativo mais próximo ainda não
        /// visitado. Devolve o alvo, o PRIMEIRO PASSO do caminho (que é o que interessa para
        /// observação e shaping) e a distância em arestas.
        ///
        /// Distância em arestas, e não euclidiana: é justamente a diferença que faz o sinal
        /// funcionar num mapa com paredes. Contornar uma sala para chegar a uma porta aumenta a
        /// distância em linha reta e diminui a de grafo — a segunda é a que descreve progresso.
        /// </summary>
        public bool TryFindNearestUnvisited(int from, bool[] visited, out int target, out int nextStep, out int graphDistance)
        {
            target = -1;
            nextStep = -1;
            graphDistance = 0;

            if (from < 0 || from >= _nodes.Count || !_nodes[from].IsEnabled)
                return false;

            _bfsStamp++;

            int head = 0;
            int tail = 0;

            _bfsQueue[tail++] = from;
            _bfsStampOf[from] = _bfsStamp;
            _bfsParent[from] = -1;
            _bfsDepth[from] = 0;

            while (head < tail)
            {
                int current = _bfsQueue[head++];

                // O ALVO tem que ser primário — um nó de malha não vale nada, e apontar a
                // fronteira para ele mandaria o agente "explorar" um pedaço de corredor que não
                // paga e não conta para cobertura. O CAMINHO continua atravessando auxiliares
                // normalmente: eles entram na expansão logo abaixo, sem filtro.
                if (current != from && !visited[current] && _nodes[current].IsPrimary)
                {
                    target = current;
                    graphDistance = _bfsDepth[current];

                    // Volta pelos pais até o nó imediatamente após a origem.
                    int step = current;
                    while (_bfsParent[step] != from && _bfsParent[step] != -1)
                        step = _bfsParent[step];

                    nextStep = step;
                    return true;
                }

                foreach (int neighbor in _adjacency[current])
                {
                    if (_bfsStampOf[neighbor] == _bfsStamp || !_nodes[neighbor].IsEnabled)
                        continue;

                    _bfsStampOf[neighbor] = _bfsStamp;
                    _bfsParent[neighbor] = current;
                    _bfsDepth[neighbor] = _bfsDepth[current] + 1;
                    _bfsQueue[tail++] = neighbor;
                }
            }

            return false;
        }

        /// <summary>
        /// O segmento entre dois pontos passa livre? Usado pelo gizmo de validação e pela
        /// auto-ligação. É a única definição de "não atravessa parede" do sistema.
        /// </summary>
        public bool IsSegmentClear(Vector3 a, Vector3 b)
        {
            Vector3 from = a + Vector3.up * _linkProbeHeight;
            Vector3 to = b + Vector3.up * _linkProbeHeight;

            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 1e-4f)
                return true;

            Vector3 direction = delta / distance;

            if (_linkClearance <= 0f)
                return !Physics.Raycast(from, direction, distance, _wallLayer, QueryTriggerInteraction.Ignore);

            return !Physics.SphereCast(from, _linkClearance, direction, out _, distance, _wallLayer, QueryTriggerInteraction.Ignore);
        }

        // Erro de autoria em grafo é silencioso do mesmo jeito que erro de wiring em ML-Agents:
        // o treino roda, só não converge. Melhor gritar no Play.
        private void ValidateBakedGraph()
        {
            if (_nodes.Count == 0)
            {
                Debug.LogError($"{name}: NavGraph sem nós. Use \"Coletar nós filhos\" no menu de contexto.", this);
                return;
            }

            int withoutRegion = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_adjacency[i].Length == 0)
                    Debug.LogWarning($"{name}: nó {i} ({_nodes[i].name}) não tem vizinhos — inalcançável.", _nodes[i]);

                if (_nodes[i].Region == null)
                    withoutRegion++;
            }

            // Sem região o nó continua funcionando, mas divide um orçamento com todos os outros
            // órfãos do mapa — o que quase nunca é o que você quis dizer.
            if (withoutRegion > 0)
            {
                Debug.LogWarning(
                    $"{name}: {withoutRegion} nó(s) sem NavRegion. Eles dividem um orçamento único de " +
                    $"{_defaultRegionBudget}. No gizmo eles aparecem em magenta.", this);
            }

            // Raios que se tocam ao longo de uma aresta: o agente entra no raio do destino antes
            // de sair do raio da origem, e a "chegada" passa a acontecer no meio do caminho.
            // Funciona, mas a visita deixa de significar "estive lá" — e num doorway isso vira
            // crédito por entrar numa sala em que ele nunca pôs o pé.
            int overlapping = 0;
            NavNode worstA = null;
            NavNode worstB = null;
            float worstRatio = 0f;

            for (int i = 0; i < _nodes.Count; i++)
            {
                foreach (int j in _adjacency[i])
                {
                    if (j <= i)
                        continue;

                    // Só entre PRIMÁRIOS. O aviso existe para proteger o significado de
                    // "cheguei", e isso só importa onde a chegada paga alguma coisa. Numa malha
                    // auxiliar os discos se tocando é o desenho pretendido — é assim que ela
                    // pega o agente sem buracos — e avisar aqui encheria o Console de ruído a
                    // cada elo da cadeia, escondendo os avisos que importam.
                    if (!_nodes[i].IsPrimary || !_nodes[j].IsPrimary)
                        continue;

                    // Na métrica da forma: dois quadrados se tocam quando a distância de
                    // Chebyshev iguala a soma das meia-arestas, não quando a euclidiana iguala.
                    float length = AreaDistance(_nodes[i].Position, _nodes[j].Position);
                    float sum = NodeRadius(i) + NodeRadius(j);

                    if (sum <= length)
                        continue;

                    overlapping++;
                    float ratio = sum / Mathf.Max(length, 1e-4f);
                    if (ratio > worstRatio)
                    {
                        worstRatio = ratio;
                        worstA = _nodes[i];
                        worstB = _nodes[j];
                    }
                }
            }

            if (overlapping > 0)
            {
                Debug.LogWarning(
                    $"{name}: {overlapping} aresta(s) mais curta(s) que a soma dos raios dos seus nós. " +
                    $"Pior caso: '{worstA.name}' <-> '{worstB.name}'. Reduza o Default Primary Radius " +
                    "(ou afaste os nós) — a chegada está sendo registrada antes da travessia.", this);
            }

            ValidateShadowedPrimaries();
            ValidateConnectivity();
        }

        /// <summary>
        /// AUXILIAR EM CIMA DE PRIMÁRIO. FindNodeAt devolve o nó mais próximo do CENTRO, não o
        /// primário: onde as duas áreas se cruzam, quem ganha é quem tem o centro mais perto.
        ///
        /// Áreas se cruzando é normal e desejado — a malha existe justamente para levar até o
        /// primário, então ela tem que chegar perto. O que quebra é o centro do auxiliar ficar
        /// EM CIMA do centro do primário: aí a região em que o primário ganha encolhe até um
        /// sliver, e com o agente andando 0.1 m por step de física ele passa por cima sem
        /// registrar. Nos centros exatamente coincidentes o desempate vira a ordem da lista —
        /// silencioso e arbitrário — e o primário pode ficar INALCANÇÁVEL: a cobertura-alvo
        /// nunca é atingida e nenhum episódio termina em sucesso.
        ///
        /// Não é a mesma checagem do laço acima: aquela só olha pares LIGADOS e ignora
        /// auxiliares de propósito (a cadeia deve mesmo ter áreas sobrepostas). Esta olha todos
        /// os pares, porque um auxiliar não precisa estar ligado ao primário para eclipsá-lo.
        /// </summary>
        private void ValidateShadowedPrimaries()
        {
            for (int p = 0; p < _nodes.Count; p++)
            {
                if (!_nodes[p].IsEnabled || !_nodes[p].IsPrimary)
                    continue;

                // Metade do raio: deixa a região de vitória do primário com pelo menos um quarto
                // do raio dele, que a 0.1 m por step são vários steps de margem.
                float minSeparation = NodeRadius(p) * 0.5f;

                for (int a = 0; a < _nodes.Count; a++)
                {
                    if (a == p || !_nodes[a].IsEnabled || _nodes[a].IsPrimary)
                        continue;

                    float separation = AreaDistance(_nodes[p].Position, _nodes[a].Position);
                    if (separation >= minSeparation)
                        continue;

                    Debug.LogError(
                        $"{name}: o auxiliar '{_nodes[a].name}' está a {separation:0.00} do primário " +
                        $"'{_nodes[p].name}' (mínimo {minSeparation:0.00}). Ele eclipsa o primário: a chegada " +
                        "vai ser registrada no auxiliar, que não paga nem conta para a cobertura. " +
                        "Afaste o auxiliar ou apague-o — o primário já serve de âncora ali.",
                        _nodes[p]);
                }
            }
        }

        /// <summary>
        /// Um grafo desconexo faz a cobertura total ser inatingível a partir de metade dos
        /// spawns, e o episódio nunca termina em sucesso. Vale detectar na autoria.
        /// </summary>
        private void ValidateConnectivity()
        {
            var reachable = new bool[_nodes.Count];
            int start = -1;
            for (int i = 0; i < _nodes.Count && start < 0; i++)
            {
                if (_nodes[i].IsEnabled)
                    start = i;
            }

            if (start < 0)
                return;

            var queue = new Queue<int>();
            queue.Enqueue(start);
            reachable[start] = true;
            int reached = 1;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in _adjacency[current])
                {
                    if (reachable[neighbor] || !_nodes[neighbor].IsEnabled)
                        continue;

                    reachable[neighbor] = true;
                    reached++;
                    queue.Enqueue(neighbor);
                }
            }

            int enabled = EnabledNodeCount();
            if (reached < enabled)
            {
                Debug.LogError(
                    $"{name}: grafo desconexo — {reached}/{enabled} nós ativos alcançáveis a partir de " +
                    $"'{_nodes[start].name}'. A cobertura total fica impossível.", this);
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Coletar nós filhos")]
        private void CollectChildNodes()
        {
            UnityEditor.Undo.RecordObject(this, "Coletar nós");
            _nodes.Clear();
            _nodes.AddRange(GetComponentsInChildren<NavNode>(includeInactive: true));
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"{name}: {_nodes.Count} nós coletados.", this);
        }

        /// <summary>
        /// Liga automaticamente todo par de nós dentro de <see cref="_autoLinkMaxDistance"/> com
        /// linha de visão livre. Ponto de PARTIDA da autoria, não substituto dela: gera ligações
        /// redundantes em sala aberta (onde três nós em linha viram um triângulo) e você limpa na
        /// mão. Só adiciona — nunca remove uma ligação que você fez.
        /// </summary>
        [ContextMenu("Auto-ligar por linha de visão")]
        private void AutoLinkByLineOfSight()
        {
            _nodes.RemoveAll(node => node == null);

            int created = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    NavNode a = _nodes[i];
                    NavNode b = _nodes[j];

                    if (Vector3.Distance(a.Position, b.Position) > _autoLinkMaxDistance)
                        continue;

                    if (!IsSegmentClear(a.Position, b.Position))
                        continue;

                    if (a.IsNeighbor(b) || b.IsNeighbor(a))
                        continue;

                    UnityEditor.Undo.RecordObject(a, "Auto-ligar nós");
                    a.EditableNeighbors.Add(b);
                    UnityEditor.EditorUtility.SetDirty(a);
                    created++;
                }
            }

            Debug.Log($"{name}: {created} ligações criadas.", this);
        }

        [ContextMenu("Validar ligações")]
        private void ValidateLinks()
        {
            _nodes.RemoveAll(node => node == null);
            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            int blocked = 0;
            foreach (NavNode node in _nodes)
            {
                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null)
                    {
                        Debug.LogWarning($"{node.name}: ligação vazia na lista de vizinhos.", node);
                        continue;
                    }

                    if (!IsSegmentClear(node.Position, neighbor.Position))
                    {
                        Debug.LogWarning($"{node.name} -> {neighbor.name}: a ligação atravessa parede.", node);
                        blocked++;
                    }
                }
            }

            Debug.Log($"{name}: validação concluída — {blocked} ligação(ões) bloqueada(s) em {_nodes.Count} nós.", this);
        }
#endif

        private void OnDrawGizmos()
        {
            if (!_drawGizmos)
                return;

            if (_drawNodeRadii)
                DrawNodeRadii();

            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null)
                        continue;

                    // Desenha cada aresta uma vez só quando ela é recíproca.
                    if (neighbor.IsNeighbor(node) && neighbor.GetInstanceID() < node.GetInstanceID())
                        continue;

                    bool clear = !_validateLinksInGizmos || IsSegmentClear(node.Position, neighbor.Position);
                    bool active = node.IsEnabled && neighbor.IsEnabled;

                    Gizmos.color = !clear ? Color.red
                        : active ? new Color(0.2f, 1f, 0.4f, 0.9f)
                        : new Color(0.4f, 0.4f, 0.4f, 0.6f);

                    Vector3 offset = Vector3.up * _linkProbeHeight;
                    Gizmos.DrawLine(node.Position + offset, neighbor.Position + offset);
                }
            }
        }

        // Roda fora do Play também, então resolve o raio pelo NÓ (RadiusOf) em vez de por índice
        // (NodeRadius): antes do bake os índices ainda não existem.
        private void DrawNodeRadii()
        {
            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                bool hasOverride = node.RadiusOverride > 0f;
                float radius = RadiusOf(node);

                // Três estados, cada um dizendo uma coisa diferente:
                if (!node.IsEnabled)
                    Gizmos.color = new Color(0.4f, 0.4f, 0.4f, 0.4f);
                else if (hasOverride)
                    // Um raio fora do padrão do papel é uma decisão de autoria, e decisão tem
                    // que ser visível sem abrir o Inspector.
                    Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.9f);
                else
                    Gizmos.color = node.IsPrimary ? _primaryRadiusColor : _auxiliaryRadiusColor;

                GraphGizmos.DrawGroundArea(_nodeShape, node.Position, radius);
            }
        }
    }
}
