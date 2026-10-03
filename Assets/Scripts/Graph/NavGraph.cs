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
    /// e na <see cref="GraphRoomMemory"/>. Índices são locais: cada cópia da arena tem o seu grafo.
    /// No bake as portas (NodeKind.Door) saem e cada pedaço conexo vira uma sala (docs/graph/salas-e-portas.md).
    /// Quebra em silêncio: esquecer "Coletar nós filhos" após mexer nos nós; porta que não liga exatamente duas
    /// salas; parede é a layer _wallLayer; mudar a regra de chegada invalida os .onnx.
    /// Gizmos: aresta verde = livre, vermelha = atravessa parede, cinza = nó desativado; disco por papel.
    /// </summary>
    public class NavGraph : MonoBehaviour
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

        // ================================================================================
        // Salas e portas
        // ================================================================================

        // Sala de cada nó (-1 = porta), sobre TODOS os nós, ativos ou não: lição que desliga nó não pode
        // renumerar as salas.
        private int[] _roomOf;
        private int[][] _roomNodes;     // nós (não-porta) de cada sala
        private int[][] _roomDoors;     // portas que encostam em cada sala
        private int[][] _doorRooms;     // por nó: as salas que a porta liga (vazio para não-porta)
        private Vector3[] _roomCentroid;
        private int[][] _roomNeighbors; // salas ligadas por uma porta
        private static readonly int[] NoRooms = new int[0];

        /// <summary>Quantas salas (corredores incluídos) o grafo tem.</summary>
        public int RoomCount => _roomNodes != null ? _roomNodes.Length : 0;

        /// <summary>Sala do nó; -1 para porta.</summary>
        public int RoomOf(int index) => _roomOf[index];

        /// <summary>Nós (não-porta) da sala.</summary>
        public int[] NodesOfRoom(int room) => _roomNodes[room];

        /// <summary>Portas que encostam na sala (as entradas/saídas dela).</summary>
        public int[] DoorsOfRoom(int room) => _roomDoors[room];

        /// <summary>Salas que a porta liga (normalmente duas). Vazio para nó que não é porta.</summary>
        public int[] RoomsOfDoor(int door) => _doorRooms[door];

        /// <summary>Centro (média das posições dos nós) da sala — origem estável para ordenar as portas dela.</summary>
        public Vector3 RoomCentroid(int room) => _roomCentroid[room];

        /// <summary>A outra sala que a porta liga, vista de <paramref name="room"/>; -1 se ela não liga duas salas.</summary>
        public int OtherRoom(int door, int room)
        {
            int[] rooms = _doorRooms[door];
            if (rooms.Length != 2)
                return -1;

            if (rooms[0] == room)
                return rooms[1];

            return rooms[1] == room ? rooms[0] : -1;
        }

        // O nó pertence à sala ou é uma porta dela? É o filtro da busca dentro de uma sala.
        private bool TouchesRoom(int node, int room)
        {
            if (_roomOf[node] == room)
                return true;

            int[] rooms = _doorRooms[node];
            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i] == room)
                    return true;
            }

            return false;
        }

        /// <summary>Tira as portas e numera os pedaços conexos que sobram (BFS); monta salas, portas por sala e vizinhança.</summary>
        private void BuildRooms()
        {
            int count = _nodes.Count;
            _roomOf = new int[count];
            for (int i = 0; i < count; i++)
                _roomOf[i] = -1;

            var rooms = new List<List<int>>();
            var queue = new Queue<int>();
            for (int start = 0; start < count; start++)
            {
                if (_nodes[start].IsDoor || _roomOf[start] >= 0)
                    continue;

                var members = new List<int>();
                int room = rooms.Count;
                _roomOf[start] = room;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    members.Add(current);
                    foreach (int next in _adjacency[current])
                    {
                        if (_nodes[next].IsDoor || _roomOf[next] >= 0)
                            continue;

                        _roomOf[next] = room;
                        queue.Enqueue(next);
                    }
                }

                rooms.Add(members);
            }

            _roomNodes = new int[rooms.Count][];
            _roomCentroid = new Vector3[rooms.Count];
            var doorsOfRoom = new List<int>[rooms.Count];
            for (int r = 0; r < rooms.Count; r++)
            {
                _roomNodes[r] = rooms[r].ToArray();
                doorsOfRoom[r] = new List<int>();

                Vector3 sum = Vector3.zero;
                foreach (int node in _roomNodes[r])
                    sum += _nodes[node].Position;
                _roomCentroid[r] = sum / Mathf.Max(1, _roomNodes[r].Length);
            }

            _doorRooms = new int[count][];
            for (int i = 0; i < count; i++)
            {
                if (!_nodes[i].IsDoor)
                {
                    _doorRooms[i] = NoRooms;
                    continue;
                }

                var touching = new List<int>();
                foreach (int next in _adjacency[i])
                {
                    int room = _roomOf[next];
                    if (room >= 0 && !touching.Contains(room))
                        touching.Add(room);
                }

                _doorRooms[i] = touching.ToArray();
                foreach (int room in touching)
                    doorsOfRoom[room].Add(i);
            }

            _roomDoors = new int[rooms.Count][];
            var neighbors = new List<int>[rooms.Count];
            for (int r = 0; r < rooms.Count; r++)
            {
                _roomDoors[r] = doorsOfRoom[r].ToArray();
                neighbors[r] = new List<int>();
            }

            for (int r = 0; r < rooms.Count; r++)
            {
                foreach (int door in _roomDoors[r])
                {
                    int other = OtherRoom(door, r);
                    if (other >= 0 && !neighbors[r].Contains(other))
                        neighbors[r].Add(other);
                }
            }

            _roomNeighbors = new int[rooms.Count][];
            for (int r = 0; r < rooms.Count; r++)
                _roomNeighbors[r] = neighbors[r].ToArray();
        }

        /// <summary>
        /// Quantas portas separam cada sala de <paramref name="fromRoom"/> (BFS no grafo de salas; -1 =
        /// inalcançável). Preenche <paramref name="hops"/> (tamanho RoomCount).
        /// </summary>
        public void RoomHops(int fromRoom, int[] hops)
        {
            for (int r = 0; r < hops.Length; r++)
                hops[r] = -1;

            if (fromRoom < 0 || fromRoom >= RoomCount)
                return;

            var queue = new Queue<int>();
            hops[fromRoom] = 0;
            queue.Enqueue(fromRoom);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in _roomNeighbors[current])
                {
                    if (hops[next] >= 0)
                        continue;

                    hops[next] = hops[current] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        // Avisa no Play erro de autoria de salas: porta que não separa duas salas não paga travessia, e sala
        // sem porta fica isolada.
        private void ValidateRooms()
        {
            if (RoomCount == 0)
            {
                Debug.LogError($"{name}: nenhuma sala — todo nó é porta? Marque o chão como Auxiliar.", this);
                return;
            }

            int doors = 0;
            int maxDoors = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].IsDoor)
                    continue;

                doors++;
                int[] rooms = _doorRooms[i];
                if (rooms.Length != 2)
                {
                    string why = rooms.Length == 0 ? "não encosta em sala nenhuma"
                        : rooms.Length == 1 ? "tem os DOIS lados na mesma sala (uma ligação contorna o vão?) ou só um lado ligado"
                        : $"liga {rooms.Length} salas";
                    Debug.LogWarning($"{name}: a porta '{_nodes[i].name}' {why}. Ela não paga travessia como deveria.", _nodes[i]);
                }

                foreach (int next in _adjacency[i])
                {
                    if (_nodes[next].IsDoor && next > i)
                    {
                        Debug.LogWarning(
                            $"{name}: as portas '{_nodes[i].name}' e '{_nodes[next].name}' estão ligadas direto — " +
                            "entre duas portas tem que haver chão de sala.", _nodes[i]);
                    }
                }
            }

            for (int r = 0; r < RoomCount; r++)
            {
                maxDoors = Mathf.Max(maxDoors, _roomDoors[r].Length);
                if (_roomDoors[r].Length == 0 && RoomCount > 1)
                {
                    Debug.LogWarning(
                        $"{name}: a sala de '{_nodes[_roomNodes[r][0]].name}' não tem porta — está isolada do resto.",
                        _nodes[_roomNodes[r][0]]);
                }
            }

            if (doors == 0)
            {
                Debug.LogError(
                    $"{name}: nenhuma PORTA no grafo — o mapa inteiro vira uma sala só. Marque os nós dos vãos " +
                    "como \"Porta\".", this);
            }

            Debug.Log($"{name}: {RoomCount} sala(s), {doors} porta(s), no máximo {maxDoors} porta(s) por sala.", this);
        }

        /// <summary>
        /// Maior distância em metros, pelo grafo, entre dois nós ativos (o "diâmetro" do mapa): normaliza as
        /// distâncias de caminho na observação (1.0 = outro lado do mapa). Automático, calculado na 1ª consulta.
        /// </summary>
        public float PathDiameter
        {
            get
            {
                if (_pathDiameter > 0f)
                    return _pathDiameter;

                EnsureBaked();
                float diameter = 0f;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (!_nodes[i].IsEnabled)
                        continue;

                    RunDijkstra(i, -1);
                    for (int k = 0; k < _pathCount; k++)
                        diameter = Mathf.Max(diameter, _pathCost[_pathOrder[k]]);
                }

                // Piso de 1 m: grafo de um nó só não pode virar divisão por zero.
                _pathDiameter = Mathf.Max(1f, diameter);
                return _pathDiameter;
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
        /// Caminho mais curto até um alvo já escolhido: devolve o próximo passo e a distância em metros;
        /// false se o alvo ficou inalcançável.
        /// </summary>
        public bool TryFindPathTo(int from, int target, out int nextStep, out float pathDistance)
        {
            nextStep = -1;
            pathDistance = 0f;

            if (from < 0 || from >= _nodes.Count || target < 0 || target >= _nodes.Count || from == target)
                return false;

            if (!_nodes[from].IsEnabled || !_nodes[target].IsEnabled)
                return false;

            RunDijkstra(from, target);

            if (_pathStampOf[target] != _pathStamp || !_pathClosed[target])
                return false;

            pathDistance = _pathCost[target];
            nextStep = FirstStepTowards(from, target);
            return true;
        }

        /// <summary>
        /// O que resta por esta saída: entrando por <paramref name="via"/> a partir de <paramref name="from"/>,
        /// soma o <paramref name="value"/> dos nós alcançáveis, cada um descontado por 0.5^(metros / meia-vida).
        /// A busca não passa por from. Com <paramref name="room"/> &gt;= 0 fica dentro da sala (entra nas portas
        /// dela, sem atravessá-las). Ciclos podem contar o mesmo nó em duas saídas; a observação é comparativa.
        /// </summary>
        public float ScoreBeyond(int from, int via, float halfLifeMeters, float[] value, int room = -1)
        {
            if (from < 0 || from >= _nodes.Count || via < 0 || via >= _nodes.Count || from == via)
                return 0f;

            if (!_nodes[via].IsEnabled)
                return 0f;

            float entryCost = 0f;
            int[] fromNeighbors = _adjacency[from];
            for (int k = 0; k < fromNeighbors.Length; k++)
            {
                if (fromNeighbors[k] == via)
                    entryCost = _adjacencyLength[from][k];
            }

            RunDijkstra(via, -1, blocked: from, startCost: entryCost, room: room, expandDoorOrigin: false);

            float decayPerMeter = Mathf.Log(0.5f) / Mathf.Max(0.1f, halfLifeMeters);
            float total = 0f;
            for (int i = 0; i < _pathCount; i++)
            {
                int node = _pathOrder[i];
                if (value[node] <= 0f)
                    continue;

                total += value[node] * Mathf.Exp(decayPerMeter * _pathCost[node]);
            }

            return total;
        }

        /// <summary>
        /// Distância por esta saída: metros pelo grafo, entrando por <paramref name="via"/> (sem voltar por
        /// <paramref name="from"/>), até o nó mais próximo com <paramref name="value"/> &gt; 0; -1 se não há.
        /// Par do <see cref="ScoreBeyond"/>: a soma com desconto pode empatar e gerar loop, a distância ao mais
        /// próximo só diminui seguindo a menor.
        /// </summary>
        public float DistanceToNearestBeyond(int from, int via, float[] value, int room = -1)
        {
            if (from < 0 || from >= _nodes.Count || via < 0 || via >= _nodes.Count || from == via)
                return -1f;

            if (!_nodes[via].IsEnabled)
                return -1f;

            float entryCost = 0f;
            int[] fromNeighbors = _adjacency[from];
            for (int k = 0; k < fromNeighbors.Length; k++)
            {
                if (fromNeighbors[k] == via)
                    entryCost = _adjacencyLength[from][k];
            }

            RunDijkstra(via, -1, blocked: from, startCost: entryCost, room: room, expandDoorOrigin: false);

            // _pathOrder sai em ordem de distância: o primeiro com valor é o mais próximo.
            for (int i = 0; i < _pathCount; i++)
            {
                int node = _pathOrder[i];
                if (value[node] > 0f)
                    return _pathCost[node];
            }

            return -1f;
        }

        /// <summary>
        /// Dijkstra a partir de <paramref name="from"/> DENTRO da sala <paramref name="room"/> (nós e portas dela;
        /// portas entram mas não são atravessadas). Devolve quantos nós foram alcançados: leia-os, em ordem de
        /// distância, com <see cref="SearchedNode"/>/<see cref="SearchedCost"/>
        /// ANTES de outra busca (os buffers são compartilhados).
        /// </summary>
        public int SearchRoom(int from, int room)
        {
            if (from < 0 || from >= _nodes.Count || room < 0 || room >= RoomCount)
                return 0;

            RunDijkstra(from, -1, room: room, expandDoorOrigin: true);
            return _pathCount;
        }

        /// <summary>O i-ésimo nó da última <see cref="SearchRoom"/>, do mais perto para o mais longe.</summary>
        public int SearchedNode(int i) => _pathOrder[i];

        /// <summary>Distância (m pelo grafo) até o nó na última <see cref="SearchRoom"/>.</summary>
        public float SearchedCost(int node) => _pathCost[node];


        /// <summary>
        /// Dijkstra em metros por nós ativos; deixa em _pathOrder os nós fechados em ordem de distância e em
        /// _pathParent/_pathCost o caminho de cada um. <paramref name="stopAt"/> &gt;= 0 para ao fechar esse nó.
        /// </summary>
        // blocked: nó que a busca não atravessa (ScoreBeyond/DistanceToNearestBeyond isolam uma saída, com startCost).
        // room >= 0: só entra nos nós da sala e nas portas dela, e porta não é expandida (exceto a origem, se
        // expandDoorOrigin: o agente parado num vão precisa achar o caminho para dentro da sala).
        private void RunDijkstra(int from, int stopAt, int blocked = -1, float startCost = 0f,
            int room = -1, bool expandDoorOrigin = true)
        {
            _pathStamp++;
            _pathCount = 0;
            _heapCount = 0;

            // Fechado sem entrar na lista: separa esta saída das outras.
            if (blocked >= 0)
            {
                _pathStampOf[blocked] = _pathStamp;
                _pathClosed[blocked] = true;
            }

            _pathStampOf[from] = _pathStamp;
            _pathClosed[from] = false;
            _pathParent[from] = -1;
            _pathCost[from] = startCost;
            HeapPush(from, startCost);

            while (_heapCount > 0)
            {
                HeapPop(out int current, out float cost);

                // Entrada velha: o nó já fechou, ou foi relaxado de novo com custo menor.
                if (_pathClosed[current] || cost > _pathCost[current])
                    continue;

                _pathClosed[current] = true;
                _pathOrder[_pathCount++] = current;

                if (current == stopAt)
                    return;

                if (room >= 0 && _nodes[current].IsDoor && (current != from || !expandDoorOrigin))
                    continue;

                int[] neighbors = _adjacency[current];
                float[] lengths = _adjacencyLength[current];
                for (int k = 0; k < neighbors.Length; k++)
                {
                    int next = neighbors[k];
                    if (!_nodes[next].IsEnabled)
                        continue;

                    if (room >= 0 && !TouchesRoom(next, room))
                        continue;

                    float nextCost = cost + lengths[k];
                    bool seen = _pathStampOf[next] == _pathStamp;
                    if (seen && (_pathClosed[next] || nextCost >= _pathCost[next]))
                        continue;

                    if (!seen)
                    {
                        _pathStampOf[next] = _pathStamp;
                        _pathClosed[next] = false;
                    }

                    _pathCost[next] = nextCost;
                    _pathParent[next] = current;
                    HeapPush(next, nextCost);
                }
            }
        }

        private void HeapPush(int node, float cost)
        {
            int i = _heapCount++;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapCost[parent] <= cost)
                    break;

                _heapNode[i] = _heapNode[parent];
                _heapCost[i] = _heapCost[parent];
                i = parent;
            }

            _heapNode[i] = node;
            _heapCost[i] = cost;
        }

        private void HeapPop(out int node, out float cost)
        {
            node = _heapNode[0];
            cost = _heapCost[0];

            int lastNode = _heapNode[--_heapCount];
            float lastCost = _heapCost[_heapCount];
            int i = 0;
            while (true)
            {
                int child = i * 2 + 1;
                if (child >= _heapCount)
                    break;

                if (child + 1 < _heapCount && _heapCost[child + 1] < _heapCost[child])
                    child++;

                if (_heapCost[child] >= lastCost)
                    break;

                _heapNode[i] = _heapNode[child];
                _heapCost[i] = _heapCost[child];
                i = child;
            }

            if (_heapCount > 0)
            {
                _heapNode[i] = lastNode;
                _heapCost[i] = lastCost;
            }
        }

        // Volta pelos pais até o nó imediatamente após a origem. Só vale logo após RunDijkstra(from).
        private int FirstStepTowards(int from, int target)
        {
            int step = target;
            while (_pathParent[step] != from && _pathParent[step] != -1)
                step = _pathParent[step];

            return step;
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

        // Avisa no Play erro de autoria do grafo (silencioso: o treino roda, só não converge).
        private void ValidateBakedGraph()
        {
            if (_nodes.Count == 0)
            {
                Debug.LogError($"{name}: NavGraph sem nós. Use \"Coletar nós filhos\" no menu de contexto.", this);
                return;
            }

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_adjacency[i].Length == 0)
                    Debug.LogWarning($"{name}: nó {i} ({_nodes[i].name}) não tem vizinhos — inalcançável.", _nodes[i]);
            }

            ValidateRooms();

            if (_nodeShape == NodeShape.Rectangle)
            {
                ValidateRectangles();
                ValidateConnectivity();
                return;
            }

            // Áreas de PORTAS sobrepostas: a chegada acontece no meio do caminho e "visitado" deixa de significar
            // "estive lá". Confere todos os pares, não só os ligados: portas de salas vizinhas não têm aresta entre si.
            int overlapping = 0;
            NavNode worstA = null;
            NavNode worstB = null;
            float worstRatio = 0f;

            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    // Só entre portas, onde a chegada paga. Na malha auxiliar discos se tocando é o desenho pretendido.
                    if (!_nodes[i].IsDoor || !_nodes[j].IsDoor || !_nodes[i].IsEnabled || !_nodes[j].IsEnabled)
                        continue;

                    // Na métrica da forma (Chebyshev no quadrado, euclidiana no círculo).
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
                    $"{name}: {overlapping} par(es) de portas com áreas sobrepostas. " +
                    $"Pior caso: '{worstA.name}' <-> '{worstB.name}'. Reduza o Default Primary Radius, afaste " +
                    "os nós ou rode NavGraphPlacer > \"Ajustar raios\" — a chegada está sendo registrada antes " +
                    "da travessia.", this);
            }

            ValidateShadowedPrimaries();
            ValidateConnectivity();
        }

        /// <summary>
        /// Auxiliar em cima de porta: o FindNodeAt devolve o nó de centro mais perto, então um auxiliar quase
        /// coincidente encolhe a região da porta a um sliver (ou a deixa inalcançável, por desempate arbitrário) e
        /// a travessia nunca é registrada. Confere todos os pares, ligados ou não.
        /// </summary>
        private void ValidateShadowedPrimaries()
        {
            for (int p = 0; p < _nodes.Count; p++)
            {
                if (!_nodes[p].IsEnabled || !_nodes[p].IsDoor)
                    continue;

                // Metade do raio: deixa a região da porta com ao menos 1/4 do raio (vários steps de margem).
                float minSeparation = NodeRadius(p) * 0.5f;

                for (int a = 0; a < _nodes.Count; a++)
                {
                    if (a == p || !_nodes[a].IsEnabled || _nodes[a].IsDoor)
                        continue;

                    float separation = AreaDistance(_nodes[p].Position, _nodes[a].Position);
                    if (separation >= minSeparation)
                        continue;

                    Debug.LogError(
                        $"{name}: o auxiliar '{_nodes[a].name}' está a {separation:0.00} da porta " +
                        $"'{_nodes[p].name}' (mínimo {minSeparation:0.00}). Ele eclipsa a porta: a chegada " +
                        "vai ser registrada no auxiliar, e a travessia da porta some. " +
                        "Afaste o auxiliar ou apague-o — a porta já serve de âncora ali.",
                        _nodes[p]);
                }
            }
        }

        /// <summary>
        /// Forma Retângulo: nenhum par de áreas pode se sobrepor (promessa do ladrilhamento: cada ponto em um nó
        /// só); até 2 cm é arredondamento da grade. Avisa também nós sem retângulo.
        /// </summary>
        private void ValidateRectangles()
        {
            const float tolerance = 0.02f;
            int overlapping = 0;
            int withoutArea = 0;
            NavNode worstA = null;
            NavNode worstB = null;

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].IsEnabled)
                    continue;

                if (!_nodes[i].HasArea)
                    withoutArea++;

                Vector3 ci = AreaCenterOf(_nodes[i]);
                Vector2 hi = HalfExtentsOf(_nodes[i]);

                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    if (!_nodes[j].IsEnabled)
                        continue;

                    Vector3 cj = AreaCenterOf(_nodes[j]);
                    Vector2 hj = HalfExtentsOf(_nodes[j]);
                    float overlapX = hi.x + hj.x - Mathf.Abs(ci.x - cj.x);
                    float overlapZ = hi.y + hj.y - Mathf.Abs(ci.z - cj.z);
                    if (overlapX <= tolerance || overlapZ <= tolerance)
                        continue;

                    overlapping++;
                    if (worstA == null)
                    {
                        worstA = _nodes[i];
                        worstB = _nodes[j];
                    }
                }
            }

            if (overlapping > 0)
            {
                Debug.LogWarning(
                    $"{name}: {overlapping} par(es) de retângulos sobrepostos (ex.: '{worstA.name}' <-> '{worstB.name}'). " +
                    "Onde dois se cruzam vence o de centro mais perto — rode o ladrilhamento de novo (NavGraphPlacer, " +
                    "menu 9) ou acerte o Area Size à mão.", worstA);
            }

            if (withoutArea > 0)
            {
                Debug.LogWarning(
                    $"{name}: {withoutArea} nó(s) sem retângulo na forma Retângulo — viram um quadrado de meia-aresta " +
                    "= raio padrão do papel, que provavelmente se sobrepõe aos ladrilhos em volta.", this);
            }
        }

        /// <summary>Grafo desconexo torna a cobertura total inatingível a partir de metade dos spawns.</summary>
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
        /// <summary>
        /// Refaz o bake e loga cada sala (nós, portas) e cada porta problemática; depois o gizmo mostra "S#"
        /// em cada sala (até recarregar scripts).
        /// </summary>
        [ContextMenu("Relatório de salas e portas")]
        internal void LogRoomReport()
        {
            _isBaked = false;
            EnsureBaked();

            var builder = new System.Text.StringBuilder();
            builder.Append($"{name}: {RoomCount} sala(s)\n");
            for (int r = 0; r < RoomCount; r++)
            {
                builder.Append($"  S{r}: {_roomNodes[r].Length} nó(s), {_roomDoors[r].Length} porta(s) [");
                for (int k = 0; k < _roomDoors[r].Length; k++)
                {
                    if (k > 0)
                        builder.Append(", ");
                    builder.Append(_nodes[_roomDoors[r][k]].name);
                }

                builder.Append("]\n");
            }

            Debug.Log(builder.ToString(), this);
        }

        [ContextMenu("Coletar nós filhos")]
        internal void CollectChildNodes()
        {
            UnityEditor.Undo.RecordObject(this, "Coletar nós");
            _nodes.Clear();
            _nodes.AddRange(GetComponentsInChildren<NavNode>(includeInactive: true));
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"{name}: {_nodes.Count} nós coletados.", this);
        }

        // Só para o ladrilhamento do NavGraphPlacer, que grava o Undo antes.
        internal void SetShape(NodeShape shape) => _nodeShape = shape;

        /// <summary>
        /// Traz para este grafo todo NavNode da MESMA arena sem NavGraph acima e recoleta a lista (com Undo; a
        /// versão automática não grava).
        /// </summary>
        [ContextMenu("Adotar nós soltos da arena e coletar")]
        private void AdoptAndCollect()
        {
            int adopted = AdoptStrayNodes(recordUndo: true);
            CollectChildNodes();
            if (adopted > 0)
                Debug.Log($"{name}: {adopted} nó(s) solto(s) da arena agora são filhos deste grafo.", this);
        }

        /// <summary>
        /// Chamado pela hierarquia do editor (NavGraphAutoCollect) com _autoCollectNodes ligado: adota nós soltos e
        /// recoleta SE a lista mudou (evita marcar as arenas como modificadas a cada clique).
        /// </summary>
        internal void AutoCollect()
        {
            if (!_autoCollectNodes || Application.isPlaying)
                return;

            AdoptStrayNodes(recordUndo: false);

            NavNode[] children = GetComponentsInChildren<NavNode>(includeInactive: true);
            _nodes.RemoveAll(node => node == null);
            if (children.Length == _nodes.Count && new HashSet<NavNode>(children).SetEquals(_nodes))
                return;

            // A ordem dos filhos manda na ordem da lista, a mesma do "Coletar nós filhos".
            _nodes.Clear();
            _nodes.AddRange(children);
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }

        // Nós soltos = na mesma arena e sem NavGraph acima; nó de outro grafo nunca é roubado. Nó de instância de
        // prefab não pode trocar de pai pela cena: fica onde está, desenhando a própria área, até abrir o prefab.
        private int AdoptStrayNodes(bool recordUndo)
        {
            GraphArenaController arena = GetComponentInParent<GraphArenaController>(true);
            Transform root = arena != null ? arena.transform : transform.root;
            int adopted = 0;

            foreach (NavNode node in root.GetComponentsInChildren<NavNode>(includeInactive: true))
            {
                if (node.GetComponentInParent<NavGraph>(true) != null)
                    continue;

                GameObject go = node.gameObject;
                if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go) && !UnityEditor.PrefabUtility.IsAddedGameObjectOverride(go))
                    continue;

                if (recordUndo)
                    UnityEditor.Undo.SetTransformParent(node.transform, transform, "Adotar nós soltos");
                else
                    node.transform.SetParent(transform, worldPositionStays: true);

                adopted++;
            }

            return adopted;
        }

        /// <summary>
        /// Liga todo par de nós a até <see cref="_autoLinkMaxDistance"/> com linha livre. Ponto de partida, não
        /// substituto da autoria: gera ligações redundantes em sala aberta. Só adiciona, nunca remove.
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

#if UNITY_EDITOR
            // As salas só existem depois do bake (Play, ou o menu "Relatório de salas e portas").
            if (_drawRoomLabels && _isBaked)
            {
                for (int r = 0; r < RoomCount; r++)
                    UnityEditor.Handles.Label(_roomCentroid[r] + Vector3.up * 2f, $"S{r}");
            }
#endif

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

        // Roda fora do Play também: resolve o raio pelo NÓ (RadiusOf), pois antes do bake não há índices.
        private void DrawNodeRadii()
        {
            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                bool hasOverride = node.RadiusOverride > 0f;
                float radius = RadiusOf(node);

                if (!node.IsEnabled)
                {
                    Gizmos.color = new Color(0.4f, 0.4f, 0.4f, 0.4f);
                }
                else
                {
                    // O disco fica sempre na cor do papel; o override aparece só pela opacidade (cheio = override).
                    Color color = node.IsDoor ? _primaryRadiusColor : node.IsPing ? _pingRadiusColor : _auxiliaryRadiusColor;
                    if (hasOverride)
                        color.a = Mathf.Min(1f, color.a * 1.6f);

                    Gizmos.color = color;
                }

                DrawArea(node, radius, 0.05f, 1);
            }
        }

        // ================================================================================
        // Desenho da área (cortada pelas paredes)
        // ================================================================================

        // Direções amostradas no contorno (48 = a cada 7.5 graus; os cantos do quadrado caem numa amostra).
        private const int OutlineSamples = 48;

        private sealed class AreaOutline
        {
            public Vector3 Position;
            public float Radius;
            public NodeShape Shape;
            public float Time;
            public readonly float[] Reach = new float[OutlineSamples];
            public readonly float[] Boundary = new float[OutlineSamples];
        }

        // Cache do contorno por nó (um raycast por direção é caro por repaint); refeito quando nó ou raio mudam
        // e, fora do Play, a cada 2 s.
        private readonly Dictionary<NavNode, AreaOutline> _outlines = new Dictionary<NavNode, AreaOutline>();

        /// <summary>Desenha a área do nó <paramref name="index"/> (contorno, ou cheia com anéis).</summary>
        public void DrawNodeArea(int index, float height, int rings) =>
            DrawArea(_nodes[index], NodeRadius(index), height, rings);

        private void DrawArea(NavNode node, float radius, float height, int rings)
        {
            // Retângulo: sem corte pela parede (como o FindNodeAt); anéis encolhem para o centro (o "cheio" da memória).
            if (_nodeShape == NodeShape.Rectangle)
            {
                Vector3 center = AreaCenterOf(node);
                Vector2 half = HalfExtentsOf(node);
                int rectRings = Mathf.Max(1, rings);
                for (int ring = rectRings; ring > 0; ring--)
                    GraphGizmos.DrawGroundRect(center, half * ((float)ring / rectRings), height);
                return;
            }

            if (!_areasStopAtWalls)
            {
                if (rings <= 1)
                    GraphGizmos.DrawGroundArea(_nodeShape, node.Position, radius, height);
                else
                    GraphGizmos.DrawGroundAreaFilled(_nodeShape, node.Position, radius, rings, height);
                return;
            }

            AreaOutline outline = OutlineOf(node, radius);
            Vector3 origin = node.Position + Vector3.up * height;
            int count = Mathf.Max(1, rings);

            for (int ring = count; ring > 0; ring--)
            {
                float scale = (float)ring / count;
                Vector3 previous = Vector3.zero;
                for (int i = 0; i <= OutlineSamples; i++)
                {
                    int k = i % OutlineSamples;
                    float angle = k * Mathf.PI * 2f / OutlineSamples;
                    float reach = Mathf.Min(outline.Reach[k], outline.Boundary[k] * scale);
                    Vector3 point = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * reach;
                    if (i > 0)
                        Gizmos.DrawLine(previous, point);
                    previous = point;
                }
            }
        }

        private AreaOutline OutlineOf(NavNode node, float radius)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_outlines.TryGetValue(node, out AreaOutline cached)
                && cached.Position == node.Position && Mathf.Approximately(cached.Radius, radius) && cached.Shape == _nodeShape
                && (Application.isPlaying || now - cached.Time < 2f))
                return cached;

            AreaOutline outline = cached ?? new AreaOutline();
            outline.Position = node.Position;
            outline.Radius = radius;
            outline.Shape = _nodeShape;
            outline.Time = now;

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            for (int k = 0; k < OutlineSamples; k++)
            {
                float angle = k * Mathf.PI * 2f / OutlineSamples;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                // Até onde a forma vai nesta direção: o raio no círculo; raio / maior componente no quadrado.
                float boundary = _nodeShape == NodeShape.Square
                    ? radius / Mathf.Max(Mathf.Abs(direction.x), Mathf.Abs(direction.z))
                    : radius;

                outline.Boundary[k] = boundary;
                outline.Reach[k] = physics.Raycast(node.Position, direction, out RaycastHit hit, boundary, _wallLayer, QueryTriggerInteraction.Ignore)
                    ? hit.distance
                    : boundary;
            }

            _outlines[node] = outline;
            return outline;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Liga o auto-coletar do <see cref="NavGraph"/> à hierarquia do editor. Gancho estático porque fora do Play o
    /// Unity não chama OnEnable de MonoBehaviour comum; adiado (delayCall) para não reentrar no evento de hierarquia.
    /// Olha só o estágio aberto (o prefab no Prefab Mode, senão as cenas).
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    internal static class NavGraphAutoCollect
    {
        private static bool _queued;

        static NavGraphAutoCollect()
        {
            UnityEditor.EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            if (_queued || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            _queued = true;
            UnityEditor.EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            _queued = false;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            UnityEditor.SceneManagement.StageHandle stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle();
            foreach (NavGraph graph in stage.FindComponentsOfType<NavGraph>())
            {
                if (graph != null)
                    graph.AutoCollect();
            }
        }
    }
#endif
}
