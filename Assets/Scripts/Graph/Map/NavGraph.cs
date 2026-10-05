using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>Forma da área de chegada, única para o grafo todo: converte posição contínua em índice de nó.</summary>
    public enum NodeShape
    {
        /// <summary>Disco: a chegada acontece à mesma distância de qualquer direção. Bom para porta e ping.</summary>
        Circle,

        /// <summary>Quadrado alinhado aos eixos, de lado 2 x raio. Ladrilha corredores sem as folgas em lente entre discos.</summary>
        Square,

        /// <summary>
        /// Retângulo por nó (NavNode._areaSize/_areaOffset), alinhado aos eixos do mundo; é a forma do ladrilhamento:
        /// cada ponto cai em um nó só. Nó sem retângulo vira quadrado de meia-aresta = raio.
        /// </summary>
        Rectangle,
    }

    /// <summary>
    /// Grafo de UMA arena: consolida os <see cref="NavNode"/> em índices, adjacência, SALAS, áreas de chegada e
    /// caminho mais curto. É estrutura do mapa; o que o agente visitou mora na <see cref="GraphExplorationMemory"/>
    /// (nós e salas). Índices são locais: cada cópia da arena tem o seu grafo.
    /// No bake as portas (NodeKind.Door) saem e cada pedaço conexo vira uma sala (docs/graph/salas-e-portas.md).
    /// Quebra em silêncio: esquecer "Coletar nós filhos" após mexer nos nós; porta que não liga exatamente duas
    /// salas; parede é a layer _wallLayer; mudar a regra de chegada invalida os .onnx.
    /// Gizmos: aresta verde = livre, vermelha = atravessa parede, cinza = nó desativado; disco por papel.
    /// Arquivos: este (campos, bake, nós, chegada, física, spawn), .Rooms (salas e portas), .Paths (Dijkstra),
    /// .Validation (avisos do bake), .Gizmos e .Editor (menus de contexto e auto-coletar).
    /// </summary>
    public partial class NavGraph : MonoBehaviour
    {
        [Header("-----Nós-----")]
        // Preencha com "Coletar nós filhos" (obrigatório depois de adicionar ou remover nós).
        [SerializeField] private List<NavNode> _nodes = new List<NavNode>();

        // Espelha as ligações no bake (A->B vira B->A); sem isso, ligar um lado só gera grafo dirigido silencioso.
        [SerializeField] private bool _makeLinksBidirectional = true;

        // Só no editor: a cada mudança na hierarquia, adota os NavNode soltos da arena e recoleta a lista
        // (sem Undo, de propósito). Desligue para manter um nó fora do grafo.
#pragma warning disable CS0414
        [SerializeField] private bool _autoCollectNodes = true;
#pragma warning restore CS0414

        // Forma da área de chegada do mapa todo. No quadrado os raios padrão viram meia-aresta (área ~27% maior):
        // reveja o espaçamento ao trocar. No Retângulo cada nó traz o seu; os raios só valem para nó sem retângulo.
        [SerializeField] private NodeShape _nodeShape = NodeShape.Circle;

        [Header("-----Ping-----")]
        // Multiplica o prêmio de atender o ping (GraphRewardSystem._pingReachedReward x este valor).
        [SerializeField, Min(0f)] private float _pingNodeScore = 1f;

        [Header("-----Raio padrão (Círculo/Quadrado)-----")]
        // Um raio padrão por PAPEL (m). Porta e ping certificam presença, então o raio é APERTADO: ~metade da
        // menor aresta entre dois deles, nunca menor que o corpo. Grande demais, "visitado" deixa de significar
        // "estive lá"; pequeno demais, o agente passa reto (o aviso do bake mede isso).
        [FormerlySerializedAs("_defaultNodeRadius")]
        [SerializeField] private float _defaultPrimaryRadius = 1.8f;

        // Raio do auxiliar (m): GENEROSO, para a cadeia de discos sempre pegar o agente. O aviso de
        // sobreposição do bake ignora auxiliares.
        [SerializeField] private float _defaultAuxiliaryRadius = 3f;

        [Header("-----Checagem de parede-----")]
        // Tudo que o corpo não atravessa: paredes E mobília (Map_Objects também fica na layer Wall).
        [SerializeField] private LayerMask _wallLayer;

        // Altura em que a aresta é desenhada no gizmo (a checagem usa a coluna _bodyBottom.._bodyTop).
        [SerializeField] private float _linkProbeHeight = 0.5f;

        // Raio da sonda de passagem (m) = metade da largura do agente. Com 0 a checagem é uma linha e a aresta
        // que raspa a quina passa; o agente entala. Ignorado com _useAgentBodySize.
        [SerializeField] private float _linkClearance = 0.85f;

        // Coluna do corpo (m, relativa à ALTURA DO NÓ, não do chão): a sonda é uma cápsula de _bodyBottom a
        // _bodyTop, para mesa, sofá e bancada (0.8-1.5 m) também bloquearem a aresta. A base ignora rodapé e soleira.
        [SerializeField] private float _bodyBottom = -1.6f;
        [SerializeField] private float _bodyTop = 1.8f;

        // Folga (m) para o nó servir de SPAWN: maior que a de passagem porque o spawn sorteia a rotação e o corpo
        // girado ocupa a meia-diagonal. Nó mais apertado segue válido como âncora e caminho, só não nasce ali.
        [SerializeField] private float _spawnClearance = 1.25f;

        // Mede o corpo em vez de digitar: as folgas saem do CapsuleCollider do agente da arena (raio x maior
        // escala X/Z) + as margens abaixo, e os campos manuais acima são ignorados. A coluna vertical segue manual.
        [SerializeField] private bool _useAgentBodySize = true;

        // Somada ao raio medido (m): a sonda não pode ser exatamente o corpo, senão a aresta que raspa a quina passa.
        [SerializeField, Min(0f)] private float _linkClearanceMargin = 0.05f;

        // Somada ao raio medido para o spawn (m): nascer encostado na parede já começa o episódio pagando contato.
        [SerializeField, Min(0f)] private float _spawnClearanceMargin = 0.4f;

        // A área de chegada para na parede: só vale com linha livre (raycast na altura do nó) do centro do nó até
        // o ponto, senão o agente "chega" no nó da sala vizinha. 1 a 3 raycasts por step por agente. Não vale no
        // Retângulo. Muda a regra de chegada: invalida .onnx treinados sem isto.
        [SerializeField] private bool _areasStopAtWalls = true;

        // Folga da âncora (m, só no Retângulo): o nó atual só perde a âncora depois de o agente sair mais que isto
        // da área, e o vizinho só assume depois de ele entrar isso (ou metade do vizinho, pelas portas estreitas).
        // Evita a âncora piscar A-B-A na borda comum. 0 = sem folga.
        [SerializeField, Min(0f)] private float _anchorHysteresis = 0.5f;

        [Header("-----Auto-ligação-----")]
        // Usado só pelo menu de contexto "Auto-ligar por linha de visão".
        [SerializeField] private float _autoLinkMaxDistance = 8f;

        [Header("-----Gizmos-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Discos de chegada de todos os nós (para calibrar os raios padrão).
        [SerializeField] private bool _drawNodeRadii = true;

        // Rótulo "S#" no centro de cada sala calculada (só editor, daí o pragma).
#pragma warning disable CS0414
        [SerializeField] private bool _drawRoomLabels = true;
#pragma warning restore CS0414

        // Cor do disco por papel (o ponto do nó é do NavNode). Evite verde, laranja, amarelo, magenta e branco:
        // já têm outro significado nos gizmos de Play. Azul-claro = PORTA (nome antigo do campo, para não perder
        // o valor salvo).
        [SerializeField] private Color _primaryRadiusColor = new Color(0.25f, 0.75f, 0.95f, 0.55f);

        // Apagado de propósito: a malha é cenário.
        [SerializeField] private Color _auxiliaryRadiusColor = new Color(0.55f, 0.60f, 0.72f, 0.22f);

        // Rosa, como o farol do GraphPingSystem.
        [SerializeField] private Color _pingRadiusColor = new Color(1f, 0.45f, 0.8f, 0.5f);

        // Colore as arestas por atravessarem parede ou não (um cast por aresta por repaint do editor).
        [SerializeField] private bool _validateLinksInGizmos = true;

        private int[][] _adjacency;

        // Comprimento planar (m) de cada aresta, no arranjo de _adjacency: o peso do caminho mais curto.
        private float[][] _adjacencyLength;
        private bool _isBaked;

        // Rascunho do Dijkstra, alocado uma vez (o carimbo evita limpar os arrays a cada busca). O custo é em
        // METROS, não em arestas: assim regenerar o grafo mais denso não muda o que o agente sente.
        private int[] _pathOrder;      // nós fechados, em ordem crescente de distância
        private int[] _pathParent;
        private float[] _pathCost;
        private int[] _pathStampOf;    // carimbo: o nó já foi alcançado nesta busca
        private bool[] _pathClosed;
        private int[] _heapNode;
        private float[] _heapCost;
        private int _heapCount;
        private int _pathStamp;
        private int _pathCount;

        private float _pathDiameter = -1f;
        private bool[] _spawnable;

        // Rascunho do OverlapCapsule; por instância porque cada arena tem o seu grafo.
        private readonly Collider[] _overlapBuffer = new Collider[1];

        public LayerMask WallLayer => _wallLayer;

        public NodeShape Shape => _nodeShape;

        /// <summary>Metade da largura do agente: o raio com que o corpo passa por uma aresta.</summary>
        public float LinkClearance => TryMeasureAgentRadius(out float radius) ? radius + _linkClearanceMargin : _linkClearance;

        /// <summary>Base da coluna do corpo, relativa à altura do nó.</summary>
        public float BodyBottom => _bodyBottom;

        /// <summary>Topo da coluna do corpo, relativo à altura do nó.</summary>
        public float BodyTop => _bodyTop;

        /// <summary>Folga que o corpo precisa parado num nó para nascer ali (ver _spawnClearance).</summary>
        public float SpawnClearance => TryMeasureAgentRadius(out float radius) ? radius + _spawnClearanceMargin : _spawnClearance;

        private CapsuleCollider _agentBody;
        private bool _loggedMeasuredBody;

        /// <summary>
        /// Raio real do corpo do agente DESTA arena (CapsuleCollider x maior escala planar); false sem agente
        /// (aí valem as folgas manuais).
        /// </summary>
        public bool TryMeasureAgentRadius(out float radius)
        {
            radius = 0f;
            if (!_useAgentBodySize)
                return false;

            if (_agentBody == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                Transform root = arena != null ? arena.transform : transform.parent;
                GraphExplorerManager agent = root != null ? root.GetComponentInChildren<GraphExplorerManager>(true) : null;
                if (agent != null)
                    _agentBody = agent.GetComponent<CapsuleCollider>();

                if (_agentBody == null)
                    return false;
            }

            Vector3 scale = _agentBody.transform.lossyScale;
            float planar = _agentBody.direction == 1
                ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            radius = _agentBody.radius * planar;

            if (Application.isPlaying && !_loggedMeasuredBody)
            {
                _loggedMeasuredBody = true;
                Debug.Log(
                    $"{name}: corpo do agente medido — raio {radius:0.00} m, passagem {radius + _linkClearanceMargin:0.00}, " +
                    $"spawn {radius + _spawnClearanceMargin:0.00}.", this);
            }

            return radius > 0f;
        }

        /// <summary>A área de chegada é cortada pelas paredes (ver _areasStopAtWalls).</summary>
        public bool AreasStopAtWalls => _areasStopAtWalls;

        /// <summary>
        /// Linha livre, na altura do nó, do centro do nó até o ponto? Corta a área de chegada na parede (mesa e
        /// sofá não cortam; parede, divisória alta e armário sim).
        /// </summary>
        public bool CanSeeFromNode(Vector3 nodePosition, Vector3 point)
        {
            var target = new Vector3(point.x, nodePosition.y, point.z);
            Vector3 delta = target - nodePosition;
            float distance = delta.magnitude;
            if (distance < 1e-4f)
                return true;

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            return !physics.Raycast(nodePosition, delta / distance, distance, _wallLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Distância planar na métrica da forma (euclidiana no círculo, Chebyshev no quadrado): fonte única da
        /// chegada, do desempate e da validação de sobreposição.
        /// </summary>
        internal float AreaDistance(Vector3 a, Vector3 b)
        {
            float dx = Mathf.Abs(a.x - b.x);
            float dz = Mathf.Abs(a.z - b.z);

            return _nodeShape == NodeShape.Square
                ? Mathf.Max(dx, dz)
                : new Vector2(dx, dz).magnitude;
        }

        public float DefaultPrimaryRadius => _defaultPrimaryRadius;

        public float DefaultAuxiliaryRadius => _defaultAuxiliaryRadius;

        /// <summary>Raio efetivo do nó: o override dele ou o padrão do papel.</summary>
        public float NodeRadius(int index) => RadiusOf(_nodes[index]);

        // Porta e ping usam o raio apertado; só o auxiliar usa o generoso.
        internal float RadiusOf(NavNode node)
        {
            float over = node.RadiusOverride;
            if (over > 0f)
                return over;

            return node.IsTarget ? _defaultPrimaryRadius : _defaultAuxiliaryRadius;
        }

        /// <summary>
        /// Meia-largura (X, Z) da área na forma Retângulo (quadrado de meia-aresta = raio se o nó não tem retângulo).
        /// </summary>
        internal Vector2 HalfExtentsOf(NavNode node)
        {
            if (node.HasArea)
                return node.AreaSize * 0.5f;

            float radius = RadiusOf(node);
            return new Vector2(radius, radius);
        }

        internal Vector3 AreaCenterOf(NavNode node) => node.HasArea ? node.AreaCenter : node.Position;

        /// <summary>Quão central o ponto está no retângulo: 0 no centro, 1 na borda, acima de 1 fora (normalizado por eixo).</summary>
        private float RectangleMetric(NavNode node, Vector3 point)
        {
            Vector2 half = HalfExtentsOf(node);
            Vector3 center = AreaCenterOf(node);
            float dx = Mathf.Abs(point.x - center.x) / Mathf.Max(1e-4f, half.x);
            float dz = Mathf.Abs(point.z - center.z) / Mathf.Max(1e-4f, half.y);
            return Mathf.Max(dx, dz);
        }

        public int NodeCount => _nodes.Count;

        public IReadOnlyList<NavNode> Nodes => _nodes;

        // Conjunto da lista, para o gizmo de cada NavNode perguntar "estou no grafo?" sem varrer a lista;
        // refeito quando ela muda.
        private HashSet<NavNode> _nodeSet;
        private int _nodeSetCount = -1;

        /// <summary>O nó está na lista deste grafo (e não só pendurado debaixo dele)?</summary>
        public bool ContainsNode(NavNode node)
        {
            if (_nodeSet == null || _nodeSetCount != _nodes.Count)
            {
                _nodeSet = new HashSet<NavNode>(_nodes);
                _nodeSetCount = _nodes.Count;
            }

            return _nodeSet.Contains(node);
        }

        private void OnValidate()
        {
            _nodeSetCount = -1;
            _agentBody = null;
        }

        private void Awake() => EnsureBaked();

        /// <summary>
        /// Bake idempotente (índices, adjacência, salas, validação): o Awake do grafo e o Initialize do agente
        /// podem rodar em qualquer ordem.
        /// </summary>
        public void EnsureBaked()
        {
            if (_isBaked)
                return;

            _nodes.RemoveAll(node => node == null);

            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            BuildAdjacency();

            int directedEdges = 0;
            foreach (int[] neighbors in _adjacency)
                directedEdges += neighbors.Length;

            _pathOrder = new int[_nodes.Count];
            _pathParent = new int[_nodes.Count];
            _pathCost = new float[_nodes.Count];
            _pathStampOf = new int[_nodes.Count];
            _pathClosed = new bool[_nodes.Count];

            // Heap preguiçoso (entradas velhas são puladas ao sair): teto = uma por aresta dirigida + a origem.
            _heapNode = new int[directedEdges + 1];
            _heapCost = new float[directedEdges + 1];

            _pathDiameter = -1f;
            _spawnable = null;

            _hasPingNodes = HasPingNodes;
            BuildRooms();
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

                    // Identidade, não só faixa: referência vazada de outra arena tem índice válido no grafo dela
                    // e viraria uma aresta errada.
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
            _adjacencyLength = new float[_nodes.Count][];
            for (int i = 0; i < _nodes.Count; i++)
            {
                _adjacency[i] = new int[sets[i].Count];
                sets[i].CopyTo(_adjacency[i]);

                _adjacencyLength[i] = new float[_adjacency[i].Length];
                for (int k = 0; k < _adjacency[i].Length; k++)
                {
                    Vector3 delta = _nodes[_adjacency[i][k]].Position - _nodes[i].Position;
                    _adjacencyLength[i][k] = new Vector2(delta.x, delta.z).magnitude;
                }
            }
        }

        /// <summary>
        /// Nó ativo em que o corpo cabe parado (_spawnClearance). Medido na 1ª consulta, não no bake: no Awake
        /// nem todo collider está registrado na física.
        /// </summary>
        public bool CanSpawnAt(int index)
        {
            if (!_nodes[index].IsEnabled)
                return false;

            if (_spawnable == null)
                MeasureSpawnable();

            return _spawnable[index];
        }

        /// <summary>
        /// Sorteia uma sala por igual (menos <paramref name="avoidRoom"/>, se houver outra) e um nó de spawn válido
        /// dela; nunca porta. -1 se nenhuma sala tem nó válido. Sala primeiro: sortear entre nós fazia a sala grande
        /// nascer 20x mais que um armário. Usado no spawn do agente (GraphArenaController) e do hider.
        /// </summary>
        public int RandomSpawnNode(int avoidRoom = -1) => RandomSpawnNode(null, avoidRoom);

        /// <summary>
        /// Igual, mas a sala é sorteada com peso (<paramref name="roomWeights"/>, um por sala; null = por igual).
        /// O GraphArenaController pesa as salas que o agente raramente conclui, para ele treinar onde não vai.
        /// </summary>
        public int RandomSpawnNode(float[] roomWeights, int avoidRoom = -1)
        {
            EnsureBaked();
            int rooms = RoomCount;
            if (rooms == 0)
                return -1;

            float total = 0f;
            if (roomWeights != null)
            {
                for (int r = 0; r < rooms && r < roomWeights.Length; r++)
                    total += Mathf.Max(0f, roomWeights[r]);
            }

            // Sorteio com rejeição, até 4 voltas no número de salas.
            for (int attempt = 0; attempt < rooms * 4; attempt++)
            {
                int room = total > 0f ? PickWeightedRoom(roomWeights, total) : Random.Range(0, rooms);
                if (room == avoidRoom && rooms > 1)
                    continue;

                int[] members = _roomNodes[room];
                int start = Random.Range(0, members.Length);
                for (int k = 0; k < members.Length; k++)
                {
                    int node = members[(start + k) % members.Length];
                    if (CanSpawnAt(node))
                        return node;
                }
            }

            return -1;
        }

        private int PickWeightedRoom(float[] weights, float total)
        {
            float pick = Random.value * total;
            int last = Mathf.Min(RoomCount, weights.Length) - 1;
            for (int r = 0; r < last; r++)
            {
                pick -= Mathf.Max(0f, weights[r]);
                if (pick <= 0f)
                    return r;
            }

            return last;
        }

        private void MeasureSpawnable()
        {
            _spawnable = new bool[_nodes.Count];
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                _spawnable[i] = IsBodyClear(_nodes[i].Position, SpawnClearance);
                if (_spawnable[i])
                    count++;
            }

            // Sem nenhum nó com folga: melhor nascer encostado do que não nascer (e avisa: quase certo erro
            // de montagem).
            if (count == 0 && _nodes.Count > 0)
            {
                Debug.LogWarning(
                    $"{name}: nenhum nó tem folga de spawn ({SpawnClearance:0.00} m). Liberando todos — rode " +
                    "\"1. Diagnosticar\" no NavGraphPlacer.", this);
                for (int i = 0; i < _nodes.Count; i++)
                    _spawnable[i] = true;
            }
        }

        public Vector3 NodePosition(int index) => _nodes[index].Position;

        public NavNode GetNode(int index) => _nodes[index];

        public bool IsNodeEnabled(int index) => _nodes[index].IsEnabled;

        /// <summary>O nó é PORTA (corta o grafo em salas; paga ao ser atravessado)?</summary>
        public bool IsDoor(int index) => _nodes[index].IsDoor;

        public int[] GetNeighbors(int index) => _adjacency[index];

        /// <summary>Quanto vale atender o ping neste nó (igual para todos; ver _pingNodeScore).</summary>
        public float PingValue(int index) => _pingNodeScore;

        /// <summary>O grafo tem algum nó de PING (senão o ping usa os de exploração).</summary>
        public bool HasPingNodes
        {
            get
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_nodes[i] != null && _nodes[i].IsPing)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// O nó pode tocar (GraphPingSystem) e a chegada do hider nele vira rastro? Usa os nós do episódio
        /// (<see cref="DrawEpisodePingNodes"/>); antes do 1º sorteio, os Ping ou, sem nenhum, qualquer nó de sala.
        /// </summary>
        public bool IsPingSource(int index) =>
            _episodePing != null ? _episodePing[index]
            : _hasPingNodes ? _nodes[index].IsPing
            : !_nodes[index].IsDoor;

        // Nós de ping do episódio. Mora no grafo porque é o único objeto que o hider e o ping do agente
        // compartilham.
        private bool[] _episodePing;

        /// <summary>
        /// Sorteia um nó de ping (ativo, não-porta) por sala a cada episódio, para a política não decorar onde
        /// toca. Sala de um nó só entra com <paramref name="singleNodeRoomChance"/>. Chamado pela arena.
        /// </summary>
        public void DrawEpisodePingNodes(float singleNodeRoomChance)
        {
            EnsureBaked();
            if (_episodePing == null || _episodePing.Length != _nodes.Count)
                _episodePing = new bool[_nodes.Count];
            else
                System.Array.Clear(_episodePing, 0, _episodePing.Length);

            for (int r = 0; r < RoomCount; r++)
            {
                int[] members = _roomNodes[r];
                int enabled = 0;
                foreach (int node in members)
                {
                    if (_nodes[node].IsEnabled)
                        enabled++;
                }

                if (enabled == 0 || (enabled == 1 && Random.value >= singleNodeRoomChance))
                    continue;

                int pick = Random.Range(0, enabled);
                foreach (int node in members)
                {
                    if (!_nodes[node].IsEnabled)
                        continue;

                    if (pick-- == 0)
                    {
                        _episodePing[node] = true;
                        break;
                    }
                }
            }
        }

        // Foto do bake: o tipo do nó não muda por lição.
        private bool _hasPingNodes;

        /// <summary>Quantidade de nós ativos (portas e salas); base da checagem de conectividade.</summary>
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
        /// Nó ativo cuja área contém a posição (o mais central, se vários); -1 se nenhum (normal entre dois nós).
        /// Distância planar (X/Z). <paramref name="current"/> é histerese: enquanto a posição ainda está na área
        /// dele, ele vence o desempate (evita a âncora piscar A-B-A na borda comum de dois ladrilhos).
        /// </summary>
        public int FindNodeAt(Vector3 position, int current = -1)
        {
            _areaCandidates.Clear();

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavNode node = _nodes[i];
                if (!node.IsEnabled)
                    continue;

                // Retângulo: métrica normalizada (0 no centro, 1 na borda); o desempate só decide a borda comum.
                if (_nodeShape == NodeShape.Rectangle)
                {
                    float metric = RectangleMetric(node, position);
                    if (metric <= 1f)
                        _areaCandidates.Add((metric, i));
                    continue;
                }

                float distance = AreaDistance(node.Position, position);
                if (distance <= NodeRadius(i))
                    _areaCandidates.Add((distance, i));
            }

            if (_areaCandidates.Count == 0)
                return -1;

            if (_nodeShape == NodeShape.Rectangle && KeepAnchor(current, position))
                return current;

            // Do mais central ao mais longe, o primeiro que enxerga o ponto vence (o nó atrás da parede perde).
            // No Retângulo não há corte: o retângulo já é a área declarada, e o raycast perderia o chão atrás de pilar.
            _areaCandidates.Sort(_byAreaDistance);
            bool clip = _areasStopAtWalls && _nodeShape != NodeShape.Rectangle;
            foreach ((float _, int index) in _areaCandidates)
            {
                if (index == current && (!clip || CanSeeFromNode(_nodes[index].Position, position)))
                    return index;
            }

            foreach ((float _, int index) in _areaCandidates)
            {
                if (!clip || CanSeeFromNode(_nodes[index].Position, position))
                    return index;
            }

            return -1;
        }

        // Folga da âncora (_anchorHysteresis): fica no nó atual se saiu dele há menos da folga e ainda não
        // entrou fundo no vizinho mais central.
        private bool KeepAnchor(int current, Vector3 position)
        {
            if (_anchorHysteresis <= 0f || current < 0 || current >= _nodes.Count || !_nodes[current].IsEnabled)
                return false;

            NavNode currentNode = _nodes[current];
            float outside = RectangleOutside(currentNode, position);

            if (outside <= 0f || outside > _anchorHysteresis)
                return false;

            int best = -1;
            float bestMetric = float.MaxValue;
            foreach ((float metric, int index) in _areaCandidates)
            {
                if (index != current && metric < bestMetric)
                {
                    bestMetric = metric;
                    best = index;
                }
            }

            if (best < 0)
                return true;

            NavNode candidate = _nodes[best];
            Vector2 half = HalfExtentsOf(candidate);
            float required = Mathf.Min(_anchorHysteresis, 0.5f * Mathf.Min(half.x, half.y));
            float depth = -RectangleOutside(candidate, position);
            return depth < required;
        }

        // Metros para fora da borda do retângulo (negativo = dentro), planar.
        private float RectangleOutside(NavNode node, Vector3 point)
        {
            Vector2 half = HalfExtentsOf(node);
            Vector3 center = AreaCenterOf(node);
            float ox = Mathf.Abs(point.x - center.x) - half.x;
            float oz = Mathf.Abs(point.z - center.z) - half.y;
            return Mathf.Max(ox, oz);
        }

        // Rascunho do FindNodeAt, alocado uma vez (roda a cada step de física, por agente).
        private readonly List<(float distance, int index)> _areaCandidates = new List<(float distance, int index)>();
        private static readonly System.Comparison<(float distance, int index)> _byAreaDistance =
            (a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.index.CompareTo(b.index);

        /// <summary>
        /// Nó ativo mais próximo ALCANÇÁVEL em linha reta, ignorando o raio de chegada (referência quando o agente
        /// se afastou da malha). A linha livre importa: o mais próximo geométrico pode estar atrás de uma parede.
        /// Sem nenhum com linha livre, cai no mais próximo puro, para a observação não piscar.
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

                // Só paga o cast enquanto ele pode mudar a resposta.
                if (distance < bestClear && IsSegmentClear(position, node.Position))
                {
                    bestClear = distance;
                    nearestClear = i;
                }
            }

            return nearestClear >= 0 ? nearestClear : nearestAny;
        }

        /// <summary>
        /// O segmento passa livre para o corpo? Varre a COLUNA do corpo (cápsula de _bodyBottom a _bodyTop) e é a
        /// única definição de "não atravessa parede" (gizmo, auto-ligação, nó alcançável, NavGraphPlacer). Como todo
        /// cast, ignora collider que já envolve a partida (para "o ponto está livre?" use IsBodyClear). Usa a física
        /// da CENA do grafo: no Prefab Mode a global olharia a cena errada.
        /// </summary>
        public bool IsSegmentClear(Vector3 a, Vector3 b)
        {
            Vector3 delta = b - a;
            float distance = delta.magnitude;
            if (distance < 1e-4f)
                return true;

            Vector3 direction = delta / distance;
            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            float clearance = LinkClearance;

            if (clearance <= 0f)
            {
                Vector3 from = a + Vector3.up * _linkProbeHeight;
                return !physics.Raycast(from, direction, distance, _wallLayer, QueryTriggerInteraction.Ignore);
            }

            BodyCapsule(a, clearance, out Vector3 bottom, out Vector3 top);
            return !physics.CapsuleCast(bottom, top, clearance, direction, out _, distance, _wallLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// O corpo, com este raio, cabe parado em <paramref name="position"/> sem tocar a layer de parede? O cast
        /// não responde isso: ele ignora o collider em que começa.
        /// </summary>
        public bool IsBodyClear(Vector3 position, float radius)
        {
            BodyCapsule(position, radius, out Vector3 bottom, out Vector3 top);
            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            return physics.OverlapCapsule(bottom, top, radius, _overlapBuffer, _wallLayer, QueryTriggerInteraction.Ignore) == 0;
        }

        // Centros das semiesferas da cápsula; se o raio passa de meia coluna as duas colapsam no meio.
        private void BodyCapsule(Vector3 position, float radius, out Vector3 bottom, out Vector3 top)
        {
            float low = _bodyBottom + radius;
            float high = _bodyTop - radius;
            if (high < low)
                low = high = (_bodyBottom + _bodyTop) * 0.5f;

            bottom = position + Vector3.up * low;
            top = position + Vector3.up * high;
        }
    }
}
