using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    // Salas e portas: tira as portas no bake e numera cada pedaço conexo como sala (docs/graph/salas-e-portas.md).
    public partial class NavGraph
    {
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
    }
}
