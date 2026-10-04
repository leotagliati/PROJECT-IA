using UnityEngine;

namespace Assets.Scripts.Graph
{
    // Caminho mais curto em METROS (Dijkstra com heap preguiçoso) e as consultas que dependem dele.
    public partial class NavGraph
    {
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
            if (!SearchBeyond(from, via, room))
                return 0f;

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
            if (!SearchBeyond(from, via, room))
                return -1f;

            // _pathOrder sai em ordem de distância: o primeiro com valor é o mais próximo.
            for (int i = 0; i < _pathCount; i++)
            {
                int node = _pathOrder[i];
                if (value[node] > 0f)
                    return _pathCost[node];
            }

            return -1f;
        }

        // Busca que isola UMA saída: começa em via (já pagando a aresta from-via) e nunca volta por from.
        // false se a saída não existe ou está desligada.
        private bool SearchBeyond(int from, int via, int room)
        {
            if (from < 0 || from >= _nodes.Count || via < 0 || via >= _nodes.Count || from == via)
                return false;

            if (!_nodes[via].IsEnabled)
                return false;

            float entryCost = 0f;
            int[] fromNeighbors = _adjacency[from];
            for (int k = 0; k < fromNeighbors.Length; k++)
            {
                if (fromNeighbors[k] == via)
                    entryCost = _adjacencyLength[from][k];
            }

            RunDijkstra(via, -1, blocked: from, startCost: entryCost, room: room, expandDoorOrigin: false);
            return true;
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
    }
}
