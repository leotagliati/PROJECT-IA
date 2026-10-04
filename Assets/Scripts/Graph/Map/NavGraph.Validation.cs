using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    // Avisos de autoria no bake (silenciosos no treino: o grafo errado só não converge).
    public partial class NavGraph
    {
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
    }
}
