using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
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

        // Raio de chegada de TODOS os nós do mapa (cada NavNode pode sobrescrever). Calibre uma
        // vez, aqui:
        //   - grande demais: raios de nós vizinhos se sobrepõem, o agente ganha visita sem
        //     percorrer o caminho e a troca de nó vira ruído;
        //   - pequeno demais: ele passa reto e a visita nunca é registrada.
        // Ponto de partida: metade da MENOR distância entre dois nós ligados, e nunca menor que
        // o raio do corpo do agente.
        [SerializeField] private float _defaultNodeRadius = 1.2f;

        [Header("-----Checagem de parede-----")]
        [SerializeField] private LayerMask _wallLayer;

        // Altura da sonda. Zero rasparia no chão e acusaria parede em qualquer degrau.
        [SerializeField] private float _linkProbeHeight = 0.5f;

        // Raio da sonda: da ordem do raio do agente. Com 0 a checagem é uma linha, e uma aresta
        // que passa raspando na quina de uma parede é aprovada — o agente então tenta segui-la
        // e entala. Com folga, a aresta só é válida se o CORPO dele passa.
        [SerializeField] private float _linkClearance = 0.3f;

        [Header("-----Auto-ligação-----")]
        // Usado só pelo menu de contexto "Auto-ligar por linha de visão".
        [SerializeField] private float _autoLinkMaxDistance = 8f;

        [Header("-----Gizmos-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Discos de chegada de todos os nós. É o gizmo para calibrar o Default Node Radius:
        // dois discos encostando significa que a chegada acontece no meio do caminho.
        [SerializeField] private bool _drawNodeRadii = true;

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

        public float DefaultNodeRadius => _defaultNodeRadius;

        /// <summary>Raio efetivo de um nó: o override dele, ou o padrão do grafo.</summary>
        public float NodeRadius(int index)
        {
            float over = _nodes[index].RadiusOverride;
            return over > 0f ? over : _defaultNodeRadius;
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
        /// Nó ativo mais próximo cujo raio de chegada contém a posição. Devolve -1 quando o
        /// agente está no meio do nada — o que é normal e não é erro: entre dois nós ele
        /// simplesmente não está em nenhum.
        ///
        /// A distância é PLANAR (X/Z). O mapa tem um andar só, e medir em 3D faria a altura do
        /// nó em relação ao agente comer parte do raio — um nó desenhado no chão registraria
        /// visita numa área menor que a do gizmo, sem nada indicando o porquê.
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

                Vector3 delta = node.Position - position;
                float distance = new Vector2(delta.x, delta.z).magnitude;

                if (distance <= NodeRadius(i) && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
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

                if (current != from && !visited[current])
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

                    Vector3 delta = _nodes[j].Position - _nodes[i].Position;
                    float length = new Vector2(delta.x, delta.z).magnitude;
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
                    $"Pior caso: '{worstA.name}' <-> '{worstB.name}'. Reduza o Default Node Radius " +
                    "(ou afaste os nós) — a chegada está sendo registrada antes da travessia.", this);
            }

            // Um grafo desconexo faz a cobertura total ser inatingível a partir de metade dos
            // spawns, e o episódio nunca termina em sucesso. Vale detectar na autoria.
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

        // Roda fora do Play também, então usa o override direto em vez de NodeRadius(i): antes
        // do bake os índices ainda não existem.
        private void DrawNodeRadii()
        {
            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                bool hasOverride = node.RadiusOverride > 0f;
                float radius = hasOverride ? node.RadiusOverride : _defaultNodeRadius;

                if (!node.IsEnabled)
                    Gizmos.color = new Color(0.4f, 0.4f, 0.4f, 0.4f);
                else if (hasOverride)
                    // Amarelo: um raio diferente do resto do mapa é uma decisão, e decisão tem
                    // que ser visível sem abrir o Inspector.
                    Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.9f);
                else
                    Gizmos.color = new Color(node.RegionColor.r, node.RegionColor.g, node.RegionColor.b, 0.55f);

                GraphGizmos.DrawGroundCircle(node.Position, radius);
            }
        }
    }
}
