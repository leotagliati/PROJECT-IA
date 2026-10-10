using System.Collections.Generic;
using Assets.Scripts.Graph;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// O MAPA da v8 (explorador livre), um por arena, GERADO em runtime a partir do NavMesh assado e dos batentes,
    /// sem nó posto à mão e sem ligação que o agente siga:
    ///   PORTAS  as peças Door_Hole (o vão entre os dois pilares; as paredes duplas costas com costas viram uma
    ///           porta só, lógica do gerador da v7), mais os <see cref="NavDoorMarker"/> e, se ligado, os nós Porta
    ///           do NavGraph antigo que não têm batente perto (o corte do anel S24/S26, que existe só como nó).
    ///   PONTOS  uma grade de _cellSize encaixada no NavMesh, na altura do piso. São só ALVOS DE VISÃO: o agente
    ///           ganha por VER um ponto novo, nunca por ir até ele, e não existe "vizinho" do ponto para seguir
    ///           (o defeito da v5: o monstro andava de centro em centro de nó).
    ///   SALAS   por dentro, os pontos ligados pela grade sem cruzar porta (flood fill); cada pedaço vira uma sala,
    ///           e cada porta liga as salas dos dois lados. Serve à observação (o que falta na sala atual, as portas
    ///           dela, a planta) e às métricas. Nada paga por sala.
    /// Mapa novo = assar o NavMesh; nada mais a autorar. Menu ⋮ "Gerar e relatar" mostra o resultado no editor.
    /// Todas as posições estão no mundo (cada arena tem o NavMesh dela, posto pelo NavMeshSurface).
    /// </summary>
    public class FreeMap : MonoBehaviour
    {
        [Header("-----Pontos de visão-----")]
        // Lado da célula (m). 3 m = ~600 pontos no escritório do V6. Menor = mais fiel aos cantos e mais raycast.
        [SerializeField, Min(1f)] private float _cellSize = 3f;

        // Altura do alvo acima do piso (m). Baixo o bastante para "ver o chão ali" e alto o bastante para mesa
        // (0.75 m) não esconder o ponto de um olho a 2.5 m: armário alto esconde, mesa não.
        [SerializeField, Min(0f)] private float _pointHeight = 1.2f;

        // Ponto com piso mais longe que isto da altura do chão da arena é tampo de móvel (o NavMesh do V6 foi
        // assado sem os móveis marcados como não-andáveis) e sai.
        [SerializeField, Min(0.05f)] private float _floorTolerance = 0.3f;

        [Header("-----Portas-----")]
        // Peça de batente, pelo nome (o kit horror-game-floor: Wall_01_Door_Hole).
        [SerializeField] private string _doorNameContains = "Door_Hole";

        // Duas peças paralelas com centros a até isto (m), deslocadas na TRAVESSIA, são o mesmo vão (paredes duplas).
        [SerializeField, Min(0f)] private float _doorPairDistance = 2.6f;

        // Margem (m) da caixa do vão: ponto de grade dentro dela sai, e ligação de grade que a cruza não conta.
        // 0.4 (era 0.25): com 0.25, 3 das 5 arenas do V8 tinham 1 VAZAMENTO (uma ligação diagonal raspava a quina
        // do vão e juntava duas salas: 25 salas em vez de 27), e o Rooms/S## deixava de ser a mesma sala em toda arena.
        [SerializeField, Min(0f)] private float _doorMargin = 0.4f;

        // Folga (m) somada à profundidade do vão em cada lado: a "zona da porta" onde a travessia é medida.
        [SerializeField, Min(0f)] private float _doorApproach = 0.8f;

        // Nós Porta do NavGraph antigo (filhos desta arena) sem batente a menos de _unframedDoorRadius viram porta
        // virtual, com o tamanho do ladrilho deles. É o corte do anel S24/S26: sem ele o anel vira uma sala gigante
        // que a visão não fecha (ficou em 0% na v5.1 até ser cortado).
        [SerializeField] private bool _useAuthoredDoorNodes = true;
        [SerializeField, Min(0f)] private float _unframedDoorRadius = 3f;

        [Header("-----Gizmos-----")]
        [SerializeField] private bool _drawPoints = true;
        [SerializeField] private bool _drawDoors = true;

        /// <summary>Um vão: caixa no plano (u = ao longo, v = travessia) e as salas dos dois lados.</summary>
        public struct Door
        {
            public Vector3 Center;
            // Meio do vão no NavMesh (a observação mede o caminho até aqui).
            public Vector3 Ground;
            public Vector3 Along;
            public float HalfWidth;
            public float HalfDepth;
            public string Source;
            public int RoomNegative;
            public int RoomPositive;

            public Vector3 Normal => new Vector3(-Along.z, 0f, Along.x);

            /// <summary>Coordenada na travessia (+ = lado RoomPositive).</summary>
            public float Across(Vector3 point) => (point.x - Center.x) * -Along.z + (point.z - Center.z) * Along.x;

            public bool Contains(Vector3 point, float widthMargin, float depthMargin)
            {
                float u = (point.x - Center.x) * Along.x + (point.z - Center.z) * Along.z;
                return Mathf.Abs(u) <= HalfWidth + widthMargin && Mathf.Abs(Across(point)) <= HalfDepth + depthMargin;
            }

            public int OtherRoom(int room) => room == RoomNegative ? RoomPositive : room == RoomPositive ? RoomNegative : -1;
        }

        private bool _built;
        private float _floorY;
        private Vector3[] _points;          // no chão (NavMesh)
        private int[] _pointRoom;
        private Door[] _doors;
        private int[][] _roomPoints;
        private int[][] _roomDoors;         // por ângulo em volta do centro da sala (slot estável)
        private Vector3[] _roomCentroid;
        private int[,] _roomHops;           // portas entre duas salas (-1 = sem caminho)
        private float _diameter = 1f;

        // Grade para achar o ponto mais perto em O(1).
        private Vector3 _gridOrigin;
        private int _gridMinX, _gridMinZ, _gridWidth, _gridDepth;
        private int[] _gridPoint;

        public int PointCount { get { EnsureBuilt(); return _points.Length; } }
        public int RoomCount { get { EnsureBuilt(); return _roomPoints.Length; } }
        public int DoorCount { get { EnsureBuilt(); return _doors.Length; } }
        public float FloorY { get { EnsureBuilt(); return _floorY; } }
        public float PointHeight => _pointHeight;
        public float DoorApproach => _doorApproach;
        public float DoorMargin => _doorMargin;

        /// <summary>Diagonal da área do mapa (m): normaliza distâncias da planta.</summary>
        public float Diameter { get { EnsureBuilt(); return _diameter; } }

        public Vector3 PointGround(int point) => _points[point];
        public Vector3 PointTarget(int point) => _points[point] + Vector3.up * _pointHeight;
        public int RoomOfPoint(int point) => _pointRoom[point];
        public IReadOnlyList<int> RoomPoints(int room) => _roomPoints[room];
        public IReadOnlyList<int> DoorsOfRoom(int room) => _roomDoors[room];
        public Vector3 RoomCentroid(int room) => _roomCentroid[room];
        public int RoomHops(int from, int to) => from < 0 || to < 0 ? -1 : _roomHops[from, to];
        public Door GetDoor(int door) => _doors[door];

        /// <summary>Monta o mapa uma vez (idempotente). Chamar antes de qualquer consulta; as propriedades já chamam.</summary>
        public void EnsureBuilt()
        {
            if (!_built)
                Build(log: false);
        }

        /// <summary>
        /// Ponto mais perto de <paramref name="ground"/> com NavMesh livre até ele (não pega ponto atrás da parede).
        /// -1 se não há ponto a até 2 células.
        /// </summary>
        public int NearestPoint(Vector3 ground)
        {
            EnsureBuilt();
            int cx = Mathf.FloorToInt((ground.x - _gridOrigin.x) / _cellSize) - _gridMinX;
            int cz = Mathf.FloorToInt((ground.z - _gridOrigin.z) / _cellSize) - _gridMinZ;

            for (int ring = 1; ring <= 2; ring++)
            {
                int best = -1;
                float bestSq = float.MaxValue;
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    for (int z = cz - ring; z <= cz + ring; z++)
                    {
                        if (x < 0 || z < 0 || x >= _gridWidth || z >= _gridDepth)
                            continue;

                        int point = _gridPoint[x * _gridDepth + z];
                        if (point < 0)
                            continue;

                        Vector3 delta = _points[point] - ground;
                        float sq = delta.x * delta.x + delta.z * delta.z;
                        if (sq >= bestSq || NavMesh.Raycast(ground, _points[point], out _, NavMesh.AllAreas))
                            continue;

                        best = point;
                        bestSq = sq;
                    }
                }

                if (best >= 0)
                    return best;
            }

            return -1;
        }

        /// <summary>Porta cuja zona (vão + _doorApproach de cada lado) contém o ponto; -1 se nenhuma.</summary>
        public int DoorZoneAt(Vector3 ground)
        {
            EnsureBuilt();
            for (int d = 0; d < _doors.Length; d++)
            {
                if (_doors[d].Contains(ground, _doorMargin, _doorApproach))
                    return d;
            }

            return -1;
        }

        // ================================================================================
        // Geração
        // ================================================================================

        private Transform ArenaRoot
        {
            get
            {
                FreeArenaController arena = GetComponentInParent<FreeArenaController>(true);
                return arena != null ? arena.transform : transform;
            }
        }

        private void Build(bool log)
        {
            _points = new Vector3[0];
            _pointRoom = new int[0];
            _doors = new Door[0];
            _roomPoints = new int[0][];
            _roomDoors = new int[0][];
            _roomCentroid = new Vector3[0];
            _roomHops = new int[0, 0];
            _gridPoint = new int[0];

            Transform root = ArenaRoot;
            if (!TryArenaBounds(root, out Bounds bounds))
            {
                Debug.LogError($"{name}: nenhum collider na arena para medir a área do mapa.", this);
                return;
            }

            if (!TryFloorHeight(bounds, out _floorY))
            {
                Debug.LogError($"{name}: sem NavMesh na área da arena. Asse o NavMeshSurface do prefab.", this);
                return;
            }

            _diameter = Mathf.Max(1f, new Vector2(bounds.size.x, bounds.size.z).magnitude);

            List<Door> doors = FindDoorHoles(root);
            int virtualDoors = AddVirtualDoors(root, doors);

            // ---- Pontos: grade alinhada à origem da arena (gerar de novo dá os mesmos) ----
            _gridOrigin = root.position;
            _gridMinX = Mathf.FloorToInt((bounds.min.x - _gridOrigin.x) / _cellSize);
            _gridMinZ = Mathf.FloorToInt((bounds.min.z - _gridOrigin.z) / _cellSize);
            _gridWidth = Mathf.CeilToInt((bounds.max.x - _gridOrigin.x) / _cellSize) - _gridMinX + 1;
            _gridDepth = Mathf.CeilToInt((bounds.max.z - _gridOrigin.z) / _cellSize) - _gridMinZ + 1;

            var points = new List<Vector3>();
            var cells = new List<Vector2Int>();
            var cellIndex = new Dictionary<Vector2Int, int>();
            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridDepth; z++)
                {
                    var center = new Vector3(
                        _gridOrigin.x + (x + _gridMinX + 0.5f) * _cellSize, _floorY,
                        _gridOrigin.z + (z + _gridMinZ + 0.5f) * _cellSize);

                    if (!NavMesh.SamplePosition(center, out NavMeshHit hit, _cellSize * 0.6f, NavMesh.AllAreas))
                        continue;

                    // O ponto tem que cair na célula dele (senão duas células encaixam no mesmo pedaço de chão) e no
                    // piso (tampo de móvel fora).
                    if (Mathf.Abs(hit.position.x - center.x) > _cellSize * 0.5f
                        || Mathf.Abs(hit.position.z - center.z) > _cellSize * 0.5f
                        || Mathf.Abs(hit.position.y - _floorY) > _floorTolerance
                        || InsideAnyDoor(doors, hit.position))
                        continue;

                    cellIndex[new Vector2Int(x, z)] = points.Count;
                    cells.Add(new Vector2Int(x, z));
                    points.Add(hit.position);
                }
            }

            // ---- Salas: pedaços da grade ligados sem cruzar porta ----
            var links = new List<int>[points.Count];
            for (int i = 0; i < points.Count; i++)
                links[i] = new List<int>();

            foreach (KeyValuePair<Vector2Int, int> entry in cellIndex)
            {
                foreach (Vector2Int step in GridSteps)
                {
                    if (!cellIndex.TryGetValue(entry.Key + step, out int other))
                        continue;

                    Vector3 a = points[entry.Value];
                    Vector3 b = points[other];
                    if (NavMesh.Raycast(a, b, out _, NavMesh.AllAreas) || CrossesDoor(doors, a, b))
                        continue;

                    links[entry.Value].Add(other);
                    links[other].Add(entry.Value);
                }
            }

            int[] component = Components(links, out int componentCount);

            // Cada porta liga o ponto mais perto de CADA lado (com NavMesh livre desde o meio do vão).
            var doorSides = new List<(int negative, int positive)>();
            foreach (Door door in doors)
                doorSides.Add((NearestOnSide(points, door, -1), NearestOnSide(points, door, +1)));

            // Fica o maior grupo de pedaços ligados por portas: ilha dentro de móvel, o vão entre paredes duplas e
            // o chão fora do mapa somem.
            var componentLinks = new List<int>[componentCount];
            for (int c = 0; c < componentCount; c++)
                componentLinks[c] = new List<int>();
            for (int d = 0; d < doors.Count; d++)
            {
                (int negative, int positive) = doorSides[d];
                if (negative < 0 || positive < 0)
                    continue;

                int ca = component[negative];
                int cb = component[positive];
                if (ca != cb)
                {
                    componentLinks[ca].Add(cb);
                    componentLinks[cb].Add(ca);
                }
            }

            var componentSize = new int[componentCount];
            foreach (int c in component)
                componentSize[c]++;

            int[] cluster = Components(componentLinks, out int clusterCount);
            var clusterSize = new int[clusterCount];
            for (int c = 0; c < componentCount; c++)
                clusterSize[cluster[c]] += componentSize[c];

            int mainCluster = 0;
            for (int k = 1; k < clusterCount; k++)
            {
                if (clusterSize[k] > clusterSize[mainCluster])
                    mainCluster = k;
            }

            // Reindexa: só os pontos e salas do grupo principal.
            var roomOfComponent = new int[componentCount];
            int roomCount = 0;
            for (int c = 0; c < componentCount; c++)
                roomOfComponent[c] = cluster[c] == mainCluster ? roomCount++ : -1;

            var keptPoints = new List<Vector3>();
            var keptRooms = new List<int>();
            _gridPoint = new int[_gridWidth * _gridDepth];
            for (int i = 0; i < _gridPoint.Length; i++)
                _gridPoint[i] = -1;

            var newIndex = new int[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                int room = roomOfComponent[component[i]];
                newIndex[i] = -1;
                if (room < 0)
                    continue;

                newIndex[i] = keptPoints.Count;
                _gridPoint[cells[i].x * _gridDepth + cells[i].y] = keptPoints.Count;
                keptPoints.Add(points[i]);
                keptRooms.Add(room);
            }

            _points = keptPoints.ToArray();
            _pointRoom = keptRooms.ToArray();

            // Portas que separam duas salas do mapa; o resto sai (com aviso: vão sem chão, ou VAZAMENTO).
            var keptDoors = new List<Door>();
            int leaks = 0, loose = 0;
            for (int d = 0; d < doors.Count; d++)
            {
                (int negative, int positive) = doorSides[d];
                int ra = negative >= 0 ? roomOfComponent[component[negative]] : -1;
                int rb = positive >= 0 ? roomOfComponent[component[positive]] : -1;
                if (ra < 0 || rb < 0)
                {
                    loose++;
                    continue;
                }

                if (ra == rb)
                {
                    // Os dois lados caem na mesma sala sem passar por porta: há um vão sem batente em volta, e a
                    // porta não separa nada (não é travessia de verdade). Ponha um NavDoorMarker no vão.
                    leaks++;
                    Vector3 local = root.InverseTransformPoint(doors[d].Center);
                    Debug.LogWarning(
                        $"{name}: VAZAMENTO na porta '{doors[d].Source}' (local {local.x:0.0}, {local.z:0.0}; largura " +
                        $"{doors[d].HalfWidth * 2f:0.00} m): os dois lados são a mesma sala. Há um vão sem batente por perto, " +
                        "ou a caixa do vão é estreita demais; ponha um NavDoorMarker nele.", this);
                    continue;
                }

                Door door = doors[d];
                door.Ground = NavMesh.SamplePosition(new Vector3(door.Center.x, _floorY, door.Center.z), out NavMeshHit ground, 2f, NavMesh.AllAreas)
                    ? ground.position : new Vector3(door.Center.x, _floorY, door.Center.z);
                door.RoomNegative = ra;
                door.RoomPositive = rb;
                keptDoors.Add(door);
            }

            _doors = keptDoors.ToArray();
            BuildRooms(roomCount);

            // Uma linha por arena sempre (o nº de pontos e portas é o que o orçamento do FreeRewardSystem usa); o
            // relatório por sala só no menu do editor.
            Debug.Log(
                $"{name}: mapa v8 gerado — {_points.Length} ponto(s) de visão, {RoomCountOrZero()} sala(s), " +
                $"{_doors.Length} porta(s) ({virtualDoors} virtual(is)); {points.Count - _points.Length} ponto(s) fora do mapa " +
                $"descartado(s), {loose} porta(s) sem chão nos dois lados, {leaks} vazamento(s). Piso em y={_floorY:0.00}.", this);
            if (log)
                LogRoomReport();

            // Mapa vazio (NavMesh ainda não carregado pelo NavMeshSurface, que pode habilitar depois do agente): a
            // próxima consulta tenta de novo em vez de ficar vazio o treino inteiro.
            _built = _points.Length > 0;
        }

        private int RoomCountOrZero() => _roomPoints != null ? _roomPoints.Length : 0;

        private void BuildRooms(int roomCount)
        {
            var roomPoints = new List<int>[roomCount];
            var roomDoors = new List<int>[roomCount];
            for (int r = 0; r < roomCount; r++)
            {
                roomPoints[r] = new List<int>();
                roomDoors[r] = new List<int>();
            }

            for (int i = 0; i < _points.Length; i++)
                roomPoints[_pointRoom[i]].Add(i);

            for (int d = 0; d < _doors.Length; d++)
            {
                roomDoors[_doors[d].RoomNegative].Add(d);
                roomDoors[_doors[d].RoomPositive].Add(d);
            }

            _roomPoints = new int[roomCount][];
            _roomDoors = new int[roomCount][];
            _roomCentroid = new Vector3[roomCount];
            for (int r = 0; r < roomCount; r++)
            {
                _roomPoints[r] = roomPoints[r].ToArray();
                Vector3 sum = Vector3.zero;
                foreach (int p in _roomPoints[r])
                    sum += _points[p];
                _roomCentroid[r] = _roomPoints[r].Length > 0 ? sum / _roomPoints[r].Length : Vector3.zero;

                // Ordem por ângulo em volta do centro: a mesma porta cai sempre no mesmo slot da observação.
                Vector3 centroid = _roomCentroid[r];
                roomDoors[r].Sort((a, b) =>
                {
                    float angleA = Mathf.Atan2(_doors[a].Center.z - centroid.z, _doors[a].Center.x - centroid.x);
                    float angleB = Mathf.Atan2(_doors[b].Center.z - centroid.z, _doors[b].Center.x - centroid.x);
                    int comparison = angleA.CompareTo(angleB);
                    return comparison != 0 ? comparison : a.CompareTo(b);
                });
                _roomDoors[r] = roomDoors[r].ToArray();
            }

            // Portas entre salas: BFS de cada sala (o mapa tem ~30; barato e feito uma vez).
            _roomHops = new int[roomCount, roomCount];
            var queue = new Queue<int>();
            for (int start = 0; start < roomCount; start++)
            {
                for (int r = 0; r < roomCount; r++)
                    _roomHops[start, r] = -1;

                _roomHops[start, start] = 0;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int room = queue.Dequeue();
                    foreach (int d in _roomDoors[room])
                    {
                        int next = _doors[d].OtherRoom(room);
                        if (next < 0 || _roomHops[start, next] >= 0)
                            continue;

                        _roomHops[start, next] = _roomHops[start, room] + 1;
                        queue.Enqueue(next);
                    }
                }
            }
        }

        private static readonly Vector2Int[] GridSteps =
        {
            new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, -1),
        };

        // Componentes conexos de uma lista de adjacência.
        private static int[] Components(List<int>[] links, out int count)
        {
            var component = new int[links.Length];
            for (int i = 0; i < component.Length; i++)
                component[i] = -1;

            count = 0;
            var queue = new Queue<int>();
            for (int start = 0; start < links.Length; start++)
            {
                if (component[start] >= 0)
                    continue;

                component[start] = count;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int node = queue.Dequeue();
                    foreach (int next in links[node])
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

        // Ponto mais perto do vão do lado pedido (pela travessia), com NavMesh livre desde o meio do vão.
        private int NearestOnSide(List<Vector3> points, Door door, int side)
        {
            if (!NavMesh.SamplePosition(new Vector3(door.Center.x, _floorY, door.Center.z), out NavMeshHit center, 2f, NavMesh.AllAreas))
                return -1;

            float reach = door.HalfDepth + _doorApproach + _cellSize * 2f;
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 offset = points[i] - center.position;
                float sq = offset.x * offset.x + offset.z * offset.z;
                if (sq > reach * reach || sq >= bestSq || door.Across(points[i]) * side < door.HalfDepth)
                    continue;

                // Só ao longo do vão (um ponto de outra sala ao lado da parede não serve).
                float u = Mathf.Abs(offset.x * door.Along.x + offset.z * door.Along.z);
                if (u > door.HalfWidth + _cellSize)
                    continue;

                if (NavMesh.Raycast(center.position, points[i], out _, NavMesh.AllAreas))
                    continue;

                best = i;
                bestSq = sq;
            }

            return best;
        }

        private bool InsideAnyDoor(List<Door> doors, Vector3 point)
        {
            foreach (Door door in doors)
            {
                if (door.Contains(point, _doorMargin, _doorMargin))
                    return true;
            }

            return false;
        }

        // A reta a-b passa por dentro de algum vão? Amostrada a cada 0.25 m (as ligações têm até ~4.3 m). A caixa aqui
        // é MAIS LARGA que a do vão (meia célula a mais de cada lado, ao longo da parede): uma ligação diagonal que
        // contornava a ponta do pilar juntava as duas salas em 3 das 5 arenas do V8 (a porta Door_Hole 57+58), por
        // diferença de arredondamento entre as cópias. Mais larga só aqui: os pontos ao lado do vão continuam.
        private bool CrossesDoor(List<Door> doors, Vector3 a, Vector3 b)
        {
            Vector3 delta = b - a;
            int samples = Mathf.Max(2, Mathf.CeilToInt(new Vector2(delta.x, delta.z).magnitude / 0.25f));
            float widthMargin = _doorMargin + _cellSize * 0.5f;
            for (int s = 0; s <= samples; s++)
            {
                Vector3 point = a + delta * (s / (float)samples);
                foreach (Door door in doors)
                {
                    if (door.Contains(point, widthMargin, _doorMargin))
                        return true;
                }
            }

            return false;
        }

        // Caixa de todos os colliders da arena (paredes e móveis), menos agentes e hider.
        private static bool TryArenaBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger || collider.GetComponentInParent<Agent>(true) != null
                    || collider.GetComponentInParent<GraphHider>(true) != null)
                    continue;

                if (any)
                    bounds.Encapsulate(collider.bounds);
                else
                    bounds = collider.bounds;
                any = true;
            }

            return any;
        }

        // Altura do piso: a mediana dos vértices do NavMesh dentro da área da arena (mapa de um andar só). A mediana
        // ignora os tampos de móvel, que são poucos vértices perto do chão inteiro.
        private static bool TryFloorHeight(Bounds bounds, out float floorY)
        {
            floorY = 0f;
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            var heights = new List<float>();
            foreach (Vector3 vertex in triangulation.vertices)
            {
                if (vertex.x >= bounds.min.x && vertex.x <= bounds.max.x && vertex.z >= bounds.min.z && vertex.z <= bounds.max.z
                    && vertex.y >= bounds.min.y - 1f && vertex.y <= bounds.max.y)
                    heights.Add(vertex.y);
            }

            if (heights.Count == 0)
                return false;

            heights.Sort();
            floorY = heights[heights.Count / 2];
            return true;
        }

        // Uma porta por peça Door_Hole (o vão entre os dois pilares altos mais distantes), depois as peças paralelas
        // costas com costas fundidas num vão só. Mesma lógica do gerador da v7 (NavGraph.Generation).
        private List<Door> FindDoorHoles(Transform root)
        {
            var pieces = new List<Door>();
            if (string.IsNullOrEmpty(_doorNameContains))
                return pieces;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.Contains(_doorNameContains) || !t.gameObject.activeInHierarchy)
                    continue;

                var tall = new List<Bounds>();
                foreach (Collider collider in t.GetComponents<Collider>())
                {
                    if (collider.enabled && !collider.isTrigger && collider.bounds.size.y >= 1f)
                        tall.Add(collider.bounds);
                }

                if (tall.Count < 2)
                    continue;

                // Os dois pilares mais distantes: o vão é entre as faces internas deles.
                float widest = -1f;
                Door door = default;
                for (int i = 0; i < tall.Count; i++)
                {
                    for (int j = i + 1; j < tall.Count; j++)
                    {
                        Vector3 between = tall[j].center - tall[i].center;
                        between.y = 0f;
                        float distance = between.magnitude;
                        if (distance <= widest || distance < 1e-3f)
                            continue;

                        widest = distance;
                        Vector3 along = between / distance;
                        var normal = new Vector3(-along.z, 0f, along.x);
                        float pillar = Mathf.Min(PlanarExtent(tall[i], along), PlanarExtent(tall[j], along));
                        door = new Door
                        {
                            Center = (tall[i].center + tall[j].center) * 0.5f,
                            Along = along,
                            HalfWidth = Mathf.Max(0.3f, distance * 0.5f - pillar),
                            HalfDepth = Mathf.Max(0.05f, Mathf.Min(PlanarExtent(tall[i], normal), PlanarExtent(tall[j], normal))),
                            Source = t.name,
                        };
                    }
                }

                if (widest > 0f)
                    pieces.Add(door);
            }

            // Funde as paredes duplas: paralelas, perto e deslocadas na TRAVESSIA (não ao longo).
            var merged = new List<Door>();
            var used = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                if (used[i])
                    continue;

                Door door = pieces[i];
                used[i] = true;
                for (int j = i + 1; j < pieces.Count; j++)
                {
                    if (used[j] || Mathf.Abs(Vector3.Dot(door.Along, pieces[j].Along)) < 0.9f)
                        continue;

                    Vector3 offset = pieces[j].Center - door.Center;
                    offset.y = 0f;
                    if (offset.magnitude > _doorPairDistance)
                        continue;

                    float acrossOffset = Mathf.Abs(Vector3.Dot(offset, door.Normal));
                    float alongOffset = Mathf.Abs(Vector3.Dot(offset, door.Along));
                    if (alongOffset > door.HalfWidth)
                        continue;

                    used[j] = true;
                    door.Center = (door.Center + pieces[j].Center) * 0.5f;
                    door.HalfWidth = Mathf.Min(door.HalfWidth, pieces[j].HalfWidth);
                    door.HalfDepth = acrossOffset * 0.5f + Mathf.Max(door.HalfDepth, pieces[j].HalfDepth);
                    door.Source += " + " + pieces[j].Source;
                    break;
                }

                merged.Add(door);
            }

            return merged;
        }

        private static float PlanarExtent(Bounds b, Vector3 direction) =>
            Mathf.Abs(direction.x) * b.extents.x + Mathf.Abs(direction.z) * b.extents.z;

        // NavDoorMarker (posto à mão) e nós Porta antigos sem batente perto: cortes de sala sem peça de porta.
        private int AddVirtualDoors(Transform root, List<Door> doors)
        {
            int added = 0;
            foreach (NavDoorMarker marker in root.GetComponentsInChildren<NavDoorMarker>(true))
            {
                doors.Add(new Door
                {
                    Center = marker.transform.position, Along = marker.Along, HalfWidth = marker.HalfWidth,
                    HalfDepth = marker.HalfDepth, Source = marker.name,
                });
                added++;
            }

            if (!_useAuthoredDoorNodes)
                return added;

            int framedCount = doors.Count;
            foreach (NavNode node in root.GetComponentsInChildren<NavNode>(true))
            {
                if (!node.IsDoor)
                    continue;

                Vector3 center = node.HasArea ? node.AreaCenter : node.Position;
                bool framed = false;
                for (int d = 0; d < framedCount; d++)
                {
                    Vector3 offset = center - doors[d].Center;
                    if (new Vector2(offset.x, offset.z).magnitude < _unframedDoorRadius)
                    {
                        framed = true;
                        break;
                    }
                }

                if (framed)
                    continue;

                // O lado maior do ladrilho é a largura do vão (o corredor cortado); o menor, a travessia.
                Vector2 size = node.HasArea ? node.AreaSize : new Vector2(3f, 1f);
                bool alongX = size.x >= size.y;
                doors.Add(new Door
                {
                    Center = new Vector3(center.x, _floorY, center.z),
                    Along = alongX ? Vector3.right : Vector3.forward,
                    HalfWidth = Mathf.Max(size.x, size.y) * 0.5f,
                    HalfDepth = Mathf.Max(0.05f, Mathf.Min(size.x, size.y) * 0.5f),
                    Source = $"nó {node.name}",
                });
                added++;
            }

            return added;
        }

        private void LogRoomReport()
        {
            if (_roomPoints.Length > FreeObservations.MaxRooms)
                Debug.LogWarning($"{name}: {_roomPoints.Length} salas e a planta só guarda {FreeObservations.MaxRooms}; as excedentes ficam fora da observação.", this);

            var lines = new System.Text.StringBuilder();
            for (int r = 0; r < _roomPoints.Length; r++)
            {
                lines.Append($"S{r:00}: {_roomPoints[r].Length} pontos, {_roomDoors[r].Length} porta(s)");
                if (_roomDoors[r].Length > FreeObservations.DoorSlots)
                    lines.Append($"  <-- mais que os {FreeObservations.DoorSlots} slots de porta");
                if (_roomDoors[r].Length == 0 && _roomPoints.Length > 1)
                    lines.Append("  <-- sem porta (inalcançável pelo grafo de salas)");
                lines.AppendLine();
            }

            Debug.Log($"{name}: salas do mapa v8\n{lines}", this);
        }

#if UNITY_EDITOR
        // Pré-visualização no editor (o NavMesh do prefab tem que estar carregado: abra a cena de treino ou o Prefab
        // Mode com o NavMeshSurface assado). Não grava nada: em Play o mapa é gerado de novo.
        [ContextMenu("Gerar e relatar (v8)")]
        private void GenerateAndReport() => Build(log: true);

        private void OnDrawGizmosSelected()
        {
            if (!_built || _points == null)
                return;

            // Pontos: cinza-escuro, um disco por ponto (a FreeExplorationMemory pinta os vistos em Play).
            if (_drawPoints)
            {
                Gizmos.color = new Color(0.35f, 0.35f, 0.4f, 0.9f);
                foreach (Vector3 point in _points)
                    Gizmos.DrawWireSphere(point + Vector3.up * _pointHeight, 0.25f);

                for (int r = 0; r < _roomCentroid.Length; r++)
                    UnityEditor.Handles.Label(_roomCentroid[r] + Vector3.up * 3f, $"S{r:00} ({_roomPoints[r].Length})");
            }

            // Portas: caixa vermelho-tijolo do vão e a zona da travessia em volta.
            if (_drawDoors)
            {
                foreach (Door door in _doors)
                {
                    Gizmos.matrix = Matrix4x4.TRS(door.Center, Quaternion.LookRotation(door.Normal, Vector3.up), Vector3.one);
                    Gizmos.color = new Color(0.85f, 0.35f, 0.25f, 0.9f);
                    Gizmos.DrawWireCube(Vector3.up * 1.5f, new Vector3(door.HalfWidth * 2f, 3f, door.HalfDepth * 2f));
                    Gizmos.color = new Color(0.85f, 0.35f, 0.25f, 0.3f);
                    Gizmos.DrawWireCube(Vector3.up * 0.1f, new Vector3(door.HalfWidth * 2f, 0.2f, (door.HalfDepth + _doorApproach) * 2f));
                }

                Gizmos.matrix = Matrix4x4.identity;
            }
        }
#endif
    }
}
