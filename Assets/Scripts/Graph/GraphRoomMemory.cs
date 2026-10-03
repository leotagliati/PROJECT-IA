using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A exploração por SALAS e PORTAS (docs/graph/salas-e-portas.md): o que este agente já cobriu
    /// de cada sala, quantas vezes atravessou cada porta, e o que ainda vale. Lê a
    /// <see cref="GraphExplorationMemory"/> (camada de nó) a cada step de física; as salas e
    /// portas vêm do <see cref="NavGraph"/>.
    ///
    /// O QUE VALE (em UNIDADES; o GraphRewardSystem converte em recompensa — todo o tuning mora lá):
    ///   nó novo da sala      1/⌈limiar x N⌉ da sala por nó: a sala inteira vale 1 até concluir,
    ///                        qualquer que seja o tamanho (o peso por área saiu). Depois de concluída,
    ///                        os nós que sobram pagam como "cauda" (o reward desconta).
    ///   sala concluída       1 ao chegar a ⌈limiar x N⌉ nós pisados (limiar = room_complete_threshold).
    ///   travessia de porta   NOVIDADE da porta: 1 na primeira, decai x _doorNoveltyDecay a cada
    ///                        repetição. Sair pela porta por onde entrou é a 2ª travessia dela (vale
    ///                        metade); sair por outra é a 1ª (cheia) — é isso que faz "sair por outra
    ///                        porta" compensar, sem guardar pilha de entradas. Numa sala de UMA porta a
    ///                        ida e a volta contam como uma travessia só (não há outra saída).
    ///   saída de concluída   a mesma novidade, paga ao atravessar uma porta saindo de sala concluída.
    ///
    /// SALA QUENTE (ping, S4): quando um ping começa, a sala dele fica quente — se estava concluída,
    /// volta a ser explorável — e cada fatia dela vale _hotRoomValue vezes mais até ela ser concluída
    /// de novo. Uma sala quente por vez (a do último barulho). O agente vê o calor na sala atual [21]
    /// e nas portas que dão para a sala quente: é o "mapa de calor" da sala até as portas dela.
    ///
    /// VER CONTA COMO EXPLORAR (vision_explores): nós de QUALQUER sala que entram no cone de visão
    /// (GraphHiderPerception.CanSeePoint) contam como pisados. Explorar = ver; olhar a sala da porta,
    /// ou a sala vizinha pelo vão, já vale. É o "querer ver tudo".
    ///
    /// SUSPEITA MULTIPLICA (com hider, GraphSuspicionMap): a suspeita não paga sozinha. Ela MULTIPLICA o
    /// valor de ver a sala (sala com o dobro da suspeita média vale o dobro, até _maxSuspicionScale) e
    /// REABRE sala concluída onde ela voltou a crescer (o hider pode ter ido para lá). Um sinal só:
    /// ver tudo, começando por onde ele provavelmente está.
    ///
    /// LIBERAÇÃO (release_fraction, 0 = desligada): com essa fração das portas já usadas, a porta
    /// usada há mais tempo volta a valer; com essa fração das salas concluídas, a sala concluída há
    /// mais tempo volta a ser explorável. As duas voltam valendo _releasedValue (menos que uma nova:
    /// explorar o inédito sempre compensa mais).
    ///
    /// Uma instância por agente (é estado de episódio). Mesmo ritmo da memória de nós: Tick por
    /// step de física, flags acumuladas até o <see cref="ClearStepFlags"/>.
    /// </summary>
    public class GraphRoomMemory : MonoBehaviour
    {
        [Header("-----Portas-----")]
        // Quanto a porta perde a cada travessia: 0.5 -> 1, 0.5, 0.25... A soma de um vai-e-vem
        // infinito no mesmo vão é 1/(1-0.5) = 2x a primeira travessia: não vira renda.
        [SerializeField, Range(0.05f, 0.95f)] private float _doorNoveltyDecay = 0.5f;

        [Header("-----Liberação (release_fraction no currículo)-----")]
        // Valor com que porta e sala liberadas voltam (1 = igual a uma nova). Abaixo de 1 de
        // propósito: o inédito tem que pagar mais que o revisitado, senão patrulhar e explorar empatam.
        [SerializeField, Range(0f, 1f)] private float _releasedValue = 0.5f;

        [Header("-----Sala quente (ping)-----")]
        // Quanto vale cada fatia da sala do ping (e a conclusão dela) em relação a uma sala normal.
        // 2 = "explorar a sala do barulho vale o dobro". Não é renda: a sala só esquenta quando um
        // ping começa nela, e re-esquentar a MESMA sala quente não faz nada.
        [SerializeField, Min(1f)] private float _hotRoomValue = 2f;

        [Header("-----Visão (vision_explores no currículo)-----")]
        // A cada quantos steps de física o cone é testado contra os nós da sala atual. 5 = uma vez
        // por decisão; são até ~20 raycasts (a maior sala), só com a visão ligada.
        [SerializeField, Min(1)] private int _visionIntervalSteps = 5;

        [Header("-----Suspeita (com hider)-----")]
        // Teto do multiplicador da suspeita no valor de ver uma sala (sala 3x mais suspeita que a média
        // vale 3x; acima disso, 3x). Sem teto, o nó do ping (90% da crença) valeria dezenas de salas.
        [SerializeField, Min(1f)] private float _maxSuspicionScale = 3f;

        // Teto do multiplicador na RECOMPENSA (a observação segue normalizada por _maxSuspicionScale).
        // 8 na fuga (era 3): com discovery_reward_scale 0.5 a sala comum paga metade do v4b, e a mais
        // suspeita 4x o valor base (era 3x) — explorar à toa fica barato, procurar onde ele pode estar
        // fica caro. Ainda limitado: sem teto o nó do ping (90% da crença) valeria dezenas de salas.
        [SerializeField, Min(1f)] private float _rewardSuspicionScale = 8f;

        // Sala concluída REABRE quando a suspeita dela passa disto (x a média). 2 = "o dobro da chance
        // de um lugar qualquer". Abaixo, ela continua vista.
        [SerializeField, Min(1f)] private float _suspicionReopenRatio = 2f;

        [Header("-----Observação das saídas-----")]
        // Meia-vida, em METROS pelo grafo, do desconto do "quanto resta por esta saída" (dentro da
        // sala). Uma sala grande do v4 tem ~25 m de ponta a ponta.
        [SerializeField, Min(0.1f)] private float _exitHalfLifeMeters = 20f;

        // Quanto uma porta "pesa" no quanto-resta, vezes a novidade dela, em unidades de SALA (a
        // sala inteira vale 1). 0.5 = uma porta nova vale meia sala: dentro de uma sala que falta,
        // os nós dela ganham; com a sala concluída, as portas novas passam a ganhar. Só observação.
        [SerializeField, Min(0f)] private float _doorPotential = 0.5f;

        // Fração do valor de um nó de sala JÁ CONCLUÍDA no quanto-resta (a "cauda" que ainda paga um
        // pouco). Só observação — o quanto a cauda PAGA é do reward system.
        [SerializeField, Range(0f, 1f)] private float _completedNodePotential = 0.2f;

        [Header("-----Gizmos (só em Play)-----")]
        // Legenda:
        //   disco ciano -> azul-escuro   porta, da novidade cheia à gasta
        //   rótulo "S# 3/4 ok"           sala, nós pisados / necessários, concluída
        [SerializeField] private bool _drawGizmos = true;

        private static readonly Color FreshDoorColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        private static readonly Color UsedDoorColor = new Color(0.12f, 0.18f, 0.45f, 0.9f);

        private NavGraph _graph;
        private GraphExplorationMemory _nodes;
        private GraphHiderPerception _perception;
        private GraphSuspicionMap _suspicion;
        private int _hotRoom = -1;
        private bool _visionExplores;

        // Por sala.
        private int[] _roomVisited;
        private int[] _roomNeeded;
        private bool[] _roomCompleted;
        private bool[] _roomPrevisited;
        private int[] _roomCompletedStep;
        private float[] _roomValueScale;   // base: 1, liberada (_releasedValue) ou quente (_hotRoomValue)
        private float[] _roomSuspicion;    // multiplicador da suspeita (>= 1), refeito a cada _visionIntervalSteps
        private int[] _roomHops;           // portas da sala atual até cada sala (planta do prédio)
        private int _hopsRoom = -2;

        // Por nó (só faz sentido em porta).
        private int[] _doorCrossings;
        private int[] _doorLastCrossStep;
        private float[] _doorBase;
        private bool[] _doorPairOpen;

        // Rascunhos alocados uma vez.
        private float[] _potential;
        private float[] _exitValue;
        private float[] _exitNearest;
        private float[] _doorPathDistance;
        private int[] _shuffle;
        private readonly List<int> _doorSlots = new List<int>();

        private float _releaseFraction;
        private int _step;
        private int _pendingDoor = -1;
        private int _completedCount;
        private int _previsitedCount;
        private int _usedDoorCount;
        private int _doorCount;
        private bool _dirty = true;
        private int _refreshedAnchor = -1;
        private int _slotsRoom = -1;
        private float _bestExitValue;
        private float _bestExitNearest;

        /// <summary>Sala em que o agente está (a última em que pisou num nó de sala); -1 antes da primeira.</summary>
        public int CurrentRoom { get; private set; } = -1;

        /// <summary>Porta pela qual o agente entrou na sala atual; -1 se nasceu nela.</summary>
        public int EntryDoor { get; private set; } = -1;

        // ---- Eventos acumulados até o ClearStepFlags (em unidades; o reward converte) ----

        /// <summary>Fatia de sala descoberta ANTES de ela ser concluída.</summary>
        public float RoomNodeValue { get; private set; }

        /// <summary>Fatia de sala descoberta DEPOIS de ela ser concluída (a "cauda").</summary>
        public float RoomTailValue { get; private set; }

        /// <summary>Salas concluídas no intervalo (liberada conta _releasedValue).</summary>
        public float RoomCompletedValue { get; private set; }

        /// <summary>Soma das novidades das portas atravessadas.</summary>
        public float DoorCrossValue { get; private set; }

        /// <summary>Soma das novidades das portas atravessadas SAINDO de sala concluída.</summary>
        public float RoomExitValue { get; private set; }

        /// <summary>Steps de física sem progresso de sala (nó novo, conclusão ou porta com novidade).</summary>
        public int StepsSinceProgress { get; private set; }

        // ---- Métricas do episódio ----
        public int Crossings { get; private set; }
        public int RepeatCrossings { get; private set; }
        public int RoomsCompletedTotal { get; private set; }
        public int DoorsUsed => _usedDoorCount;
        public int DoorCount => _doorCount;

        /// <summary>
        /// Fração das salas concluídas, sem as que já nasceram concluídas (elas saem do numerador
        /// e do denominador). É a cobertura que encerra o episódio e que o agente observa.
        /// </summary>
        public float CompletedFraction
        {
            get
            {
                int total = _graph.RoomCount - _previsitedCount;
                return total > 0 ? Mathf.Clamp01((float)(_completedCount - _previsitedCount) / total) : 1f;
            }
        }

        public void Configure(NavGraph graph, GraphExplorationMemory nodes, GraphHiderPerception perception,
            GraphSuspicionMap suspicion)
        {
            _graph = graph;
            _nodes = nodes;
            _perception = perception;
            _suspicion = suspicion;
            _graph.EnsureBaked();

            int rooms = _graph.RoomCount;
            _roomVisited = new int[rooms];
            _roomNeeded = new int[rooms];
            _roomCompleted = new bool[rooms];
            _roomPrevisited = new bool[rooms];
            _roomCompletedStep = new int[rooms];
            _roomValueScale = new float[rooms];
            _roomSuspicion = new float[rooms];
            _roomHops = new int[rooms];
            _shuffle = new int[rooms];

            int count = _graph.NodeCount;
            _doorCrossings = new int[count];
            _doorLastCrossStep = new int[count];
            _doorBase = new float[count];
            _doorPairOpen = new bool[count];
            _potential = new float[count];
            _exitValue = new float[count];
            _exitNearest = new float[count];
            _doorPathDistance = new float[count];

            _doorCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (_graph.IsDoor(i))
                    _doorCount++;
            }
        }

        /// <summary>
        /// Zera o episódio. <paramref name="completeThreshold"/> é a fração de nós da sala que a
        /// conclui (0.8); <paramref name="previsitedFraction"/> a fração das salas que já nasce
        /// concluída (anti-decoreba, sempre sobra uma); <paramref name="releaseFraction"/> liga a
        /// liberação (0 = desligada); <paramref name="visionExplores"/> faz o que o agente VÊ na sala
        /// atual contar como pisado. Chamar DEPOIS do ResetEpisode da memória de nós.
        /// </summary>
        public void ResetEpisode(float completeThreshold, float previsitedFraction, float releaseFraction, bool visionExplores)
        {
            completeThreshold = Mathf.Clamp(completeThreshold, 0.05f, 1f);
            _releaseFraction = Mathf.Clamp01(releaseFraction);
            _visionExplores = visionExplores && _perception != null;
            _hotRoom = -1;
            _hopsRoom = -2;
            _step = 0;
            _pendingDoor = -1;
            _completedCount = 0;
            _previsitedCount = 0;
            _usedDoorCount = 0;
            CurrentRoom = -1;
            EntryDoor = -1;
            StepsSinceProgress = 0;
            Crossings = 0;
            RepeatCrossings = 0;
            RoomsCompletedTotal = 0;
            _slotsRoom = -1;
            _dirty = true;

            for (int r = 0; r < _graph.RoomCount; r++)
            {
                _roomVisited[r] = 0;
                _roomCompleted[r] = false;
                _roomPrevisited[r] = false;
                _roomCompletedStep[r] = 0;
                _roomValueScale[r] = 1f;
                _roomSuspicion[r] = 1f;
                _roomNeeded[r] = Mathf.Max(1, Mathf.CeilToInt(completeThreshold * _graph.NodesOfRoom(r).Length - 1e-4f));
            }

            System.Array.Clear(_doorCrossings, 0, _doorCrossings.Length);
            System.Array.Clear(_doorLastCrossStep, 0, _doorLastCrossStep.Length);
            System.Array.Clear(_doorPairOpen, 0, _doorPairOpen.Length);
            for (int i = 0; i < _doorBase.Length; i++)
                _doorBase[i] = 1f;

            DrawPrevisited(previsitedFraction);
            ClearStepFlags();
        }

        // Sorteia salas que já nascem concluídas (nós marcados como pisados). Sempre sobra pelo
        // menos uma, senão a cobertura nasce em 100%.
        private void DrawPrevisited(float fraction)
        {
            int rooms = _graph.RoomCount;
            int count = Mathf.Min(Mathf.RoundToInt(rooms * Mathf.Clamp01(fraction)), rooms - 1);
            if (count <= 0)
                return;

            for (int r = 0; r < rooms; r++)
                _shuffle[r] = r;

            for (int k = 0; k < count; k++)
            {
                int j = Random.Range(k, rooms);
                (_shuffle[k], _shuffle[j]) = (_shuffle[j], _shuffle[k]);

                int room = _shuffle[k];
                foreach (int node in _graph.NodesOfRoom(room))
                    _nodes.MarkVisited(node);

                _roomVisited[room] = _graph.NodesOfRoom(room).Length;
                _roomCompleted[room] = true;
                _roomPrevisited[room] = true;
                _roomCompletedStep[room] = -1;
                _completedCount++;
                _previsitedCount++;
            }
        }

        /// <summary>Zera os eventos acumulados. Chamar depois de cobrá-los, uma vez por decisão.</summary>
        public void ClearStepFlags()
        {
            RoomNodeValue = 0f;
            RoomTailValue = 0f;
            RoomCompletedValue = 0f;
            DoorCrossValue = 0f;
            RoomExitValue = 0f;
        }

        /// <summary>
        /// Um step de física. Chamar DEPOIS do Tick da memória de nós, no mesmo step: é a chegada
        /// que ela acabou de registrar que vira travessia ou nó novo de sala.
        /// </summary>
        public void Tick(Transform agent)
        {
            _step++;
            bool progress = false;
            Vector3 agentPosition = agent.position;

            if (_nodes.ArrivedThisTick)
            {
                int node = _nodes.CurrentNodeIndex;
                _dirty = true;

                if (_graph.IsDoor(node))
                {
                    _pendingDoor = node;

                    // Nasceu num vão: adota uma das salas dele como a atual (a travessia para a
                    // outra conta normalmente).
                    if (CurrentRoom < 0 && _graph.RoomsOfDoor(node).Length > 0)
                        CurrentRoom = _graph.RoomsOfDoor(node)[0];
                }
                else
                {
                    int room = _graph.RoomOf(node);
                    if (CurrentRoom >= 0 && room != CurrentRoom)
                        progress |= RegisterCrossing(CurrentRoom, room, ResolveDoor(CurrentRoom, room, agentPosition));
                    else if (CurrentRoom < 0)
                        CurrentRoom = room;

                    _pendingDoor = -1;

                    if (_nodes.ArrivalWasNew)
                        progress |= RegisterRoomNode(room);
                }
            }

            if (_step % _visionIntervalSteps == 0)
            {
                UpdateSuspicion();

                if (_visionExplores)
                    progress |= SeeVisibleNodes(agent);
            }

            if (_releaseFraction > 0f)
                ReleaseOldest();

            StepsSinceProgress = progress ? 0 : StepsSinceProgress + 1;
        }

        // Nós de sala (de QUALQUER sala) ainda não vistos que estão no cone: contam como pisados e pagam
        // a fatia deles. O filtro por distância vem antes do raycast — só os nós ao alcance do cone
        // (15 m) chegam a testar parede.
        private bool SeeVisibleNodes(Transform agent)
        {
            bool progress = false;
            float reach = _perception.ViewDistance;
            float reachSq = reach * reach;
            Vector3 eye = agent.position;

            for (int node = 0; node < _graph.NodeCount; node++)
            {
                if (_graph.IsDoor(node) || _nodes.IsVisited(node) || !_graph.IsNodeEnabled(node))
                    continue;

                Vector3 delta = _graph.NodePosition(node) - eye;
                if (delta.x * delta.x + delta.z * delta.z > reachSq)
                    continue;

                if (!_perception.CanSeePoint(agent, _graph.NodePosition(node)))
                    continue;

                _nodes.MarkVisited(node);
                progress |= RegisterRoomNode(_graph.RoomOf(node));
                _dirty = true;
            }

            return progress;
        }

        // Refaz o multiplicador da suspeita de cada sala e REABRE sala concluída onde ela voltou a
        // crescer. Sem procura (sem hider), tudo fica em 1 e nada reabre.
        private void UpdateSuspicion()
        {
            bool active = _suspicion != null && _suspicion.IsActive;
            for (int r = 0; r < _graph.RoomCount; r++)
            {
                float ratio = active ? _suspicion.RoomRatio(r) : 1f;
                float scale = Mathf.Clamp(ratio, 1f, _maxSuspicionScale);
                if (Mathf.Abs(scale - _roomSuspicion[r]) > 0.05f)
                {
                    _roomSuspicion[r] = scale;
                    _dirty = true;
                }

                // A sala em que ele está não reabre: zerar o chão debaixo dele seria renda de graça.
                if (active && _roomCompleted[r] && r != CurrentRoom && ratio >= _suspicionReopenRatio)
                    Reopen(r, 1f);
            }
        }

        // Sala concluída volta a ser explorável (nós esquecidos), valendo baseScale.
        private void Reopen(int room, float baseScale)
        {
            foreach (int node in _graph.NodesOfRoom(room))
                _nodes.Forget(node);

            if (_roomPrevisited[room])
            {
                _roomPrevisited[room] = false;
                _previsitedCount--;
            }

            _roomCompleted[room] = false;
            _roomVisited[room] = 0;
            _roomValueScale[room] = baseScale;
            _completedCount--;
            _dirty = true;
        }

        // Valor efetivo de uma fatia da sala: base (normal, liberada, quente) x suspeita.
        // O multiplicador de recompensa estica a faixa [1, _maxSuspicionScale] da observação para
        // [1, _rewardSuspicionScale]: a OBSERVAÇÃO (RoomSuspicion) continua normalizada pelo teto
        // antigo, então a rede herdada do v4b não vê o vetor mudar; só a renda da sala suspeita sobe.
        private float Scale(int room)
        {
            float stretch = _maxSuspicionScale > 1f
                ? (_rewardSuspicionScale - 1f) / (_maxSuspicionScale - 1f)
                : 1f;
            return _roomValueScale[room] * (1f + (_roomSuspicion[room] - 1f) * stretch);
        }

        /// <summary>
        /// Esquenta a sala de um ping que começou (S4): concluída volta a ser explorável, e cada fatia
        /// dela vale _hotRoomValue vezes até ela ser concluída de novo. A sala quente anterior esfria.
        /// Re-esquentar a sala que já está quente não faz nada (senão um hider passeando nela viraria
        /// renda: cada barulho apagaria a sala de novo).
        /// </summary>
        public void HeatRoom(int room)
        {
            if (room < 0 || room == _hotRoom)
                return;

            CoolHotRoom();
            _hotRoom = room;

            if (_roomCompleted[room])
                Reopen(room, _hotRoomValue);

            _roomValueScale[room] = _hotRoomValue;
            _dirty = true;
        }

        private void CoolHotRoom()
        {
            if (_hotRoom >= 0 && !_roomCompleted[_hotRoom])
                _roomValueScale[_hotRoom] = 1f;

            _hotRoom = -1;
        }

        // A porta da travessia A -> B: a que o agente pisou entre as duas (o normal), ou — se a
        // âncora pulou o vão (ladrilho de porta estreito, agente rápido) — a porta entre A e B mais
        // perto dele. -1 se A e B não têm porta entre si (mudou de sala por uma ligação sem vão).
        private int ResolveDoor(int from, int to, Vector3 position)
        {
            if (_pendingDoor >= 0 && _graph.OtherRoom(_pendingDoor, from) == to)
                return _pendingDoor;

            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (int door in _graph.DoorsOfRoom(from))
            {
                if (_graph.OtherRoom(door, from) != to)
                    continue;

                Vector3 delta = _graph.NodePosition(door) - position;
                float distance = delta.x * delta.x + delta.z * delta.z;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = door;
                }
            }

            return best;
        }

        private float Novelty(int door, int crossings) =>
            _doorBase[door] * Mathf.Pow(_doorNoveltyDecay, Mathf.Max(0, crossings));

        /// <summary>Novidade ATUAL da porta (0..1): quanto a próxima travessia dela paga.</summary>
        public float DoorNovelty(int door) => Novelty(door, _doorCrossings[door]);

        private bool RegisterCrossing(int from, int to, int door)
        {
            CurrentRoom = to;
            EntryDoor = door;
            _slotsRoom = -1;

            if (door < 0)
                return false;

            // Sala de UMA porta: a volta pela mesma porta fecha o par aberto na ida e não conta
            // como nova travessia (paga a mesma novidade da ida). Sem isso, entrar num beco e sair
            // custaria metade só por ser beco — e não existe "outra porta" para preferir.
            float novelty;
            if (_doorPairOpen[door] && _graph.DoorsOfRoom(from).Length == 1)
            {
                novelty = Novelty(door, _doorCrossings[door] - 1);
                _doorPairOpen[door] = false;
            }
            else
            {
                novelty = Novelty(door, _doorCrossings[door]);
                if (_doorCrossings[door] == 0)
                    _usedDoorCount++;
                _doorCrossings[door]++;
                _doorPairOpen[door] = _graph.DoorsOfRoom(to).Length == 1;
            }

            _doorLastCrossStep[door] = _step;
            DoorCrossValue += novelty;
            Crossings++;
            if (novelty < 0.999f)
                RepeatCrossings++;

            // Sair de sala concluída paga a mesma novidade. Pré-concluída não: o mérito não foi dele.
            if (_roomCompleted[from] && !_roomPrevisited[from])
                RoomExitValue += novelty;

            return novelty >= 0.25f;
        }

        private bool RegisterRoomNode(int room)
        {
            _roomVisited[room]++;
            float share = Scale(room) / _roomNeeded[room];

            if (_roomCompleted[room])
            {
                RoomTailValue += share;
                return false;
            }

            RoomNodeValue += share;
            if (_roomVisited[room] >= _roomNeeded[room])
            {
                _roomCompleted[room] = true;
                _roomCompletedStep[room] = _step;
                _completedCount++;
                RoomsCompletedTotal++;
                RoomCompletedValue += Scale(room);

                // Sala quente concluída: o barulho foi conferido, ela esfria.
                if (room == _hotRoom)
                {
                    _hotRoom = -1;
                    _roomValueScale[room] = 1f;
                }
            }

            return true;
        }

        // Uma liberação de cada tipo por step, no máximo — a fração cai logo abaixo do limiar.
        private void ReleaseOldest()
        {
            if (_doorCount > 0 && _usedDoorCount >= _releaseFraction * _doorCount)
            {
                int oldest = -1;
                for (int i = 0; i < _doorCrossings.Length; i++)
                {
                    if (_doorCrossings[i] > 0 && (oldest < 0 || _doorLastCrossStep[i] < _doorLastCrossStep[oldest]))
                        oldest = i;
                }

                if (oldest >= 0)
                {
                    _doorCrossings[oldest] = 0;
                    _doorPairOpen[oldest] = false;
                    _doorBase[oldest] = _releasedValue;
                    _usedDoorCount--;
                    _dirty = true;
                }
            }

            if (_completedCount >= _releaseFraction * _graph.RoomCount)
            {
                // A sala em que ele está não é liberada: zerar o chão debaixo dele não é "voltar a
                // valer", é recompensa de graça no próximo passo.
                int oldest = -1;
                for (int r = 0; r < _graph.RoomCount; r++)
                {
                    if (_roomCompleted[r] && r != CurrentRoom
                        && (oldest < 0 || _roomCompletedStep[r] < _roomCompletedStep[oldest]))
                        oldest = r;
                }

                if (oldest >= 0)
                    Reopen(oldest, _releasedValue);
            }
        }

        // ================================================================================
        // Leitura para a observação e o shaping (refeita só quando algo mudou)
        // ================================================================================

        /// <summary>
        /// Recalcula as distâncias até as portas da sala e o quanto-resta das saídas da âncora. Chamar uma
        /// vez por decisão, antes de ler qualquer coisa abaixo; só refaz a conta quando a âncora
        /// mudou ou algo foi pisado/liberado.
        /// </summary>
        public void Refresh()
        {
            int anchor = _nodes.CurrentNodeIndex;
            if (!_dirty && anchor == _refreshedAnchor)
                return;

            _dirty = false;
            _refreshedAnchor = anchor;
            _bestExitValue = 0f;
            _bestExitNearest = -1f;

            if (_slotsRoom != CurrentRoom)
                FillDoorSlots();

            for (int i = 0; i < _doorPathDistance.Length; i++)
                _doorPathDistance[i] = -1f;

            if (anchor < 0 || CurrentRoom < 0)
                return;

            FillPotential();
            MeasureDoorPaths(anchor);
            ScoreExits(anchor);
        }

        private void FillPotential()
        {
            for (int i = 0; i < _potential.Length; i++)
            {
                if (_graph.IsDoor(i))
                {
                    _potential[i] = DoorNovelty(i) * _doorPotential;
                    continue;
                }

                int room = _graph.RoomOf(i);
                if (_nodes.IsVisited(i) || room < 0)
                {
                    _potential[i] = 0f;
                    continue;
                }

                float share = Scale(room) / _roomNeeded[room];
                _potential[i] = _roomCompleted[room] ? share * _completedNodePotential : share;
            }
        }

        // Distância pelo grafo, DENTRO da sala atual, da âncora até cada porta dela. É informação
        // (a observação da porta), não um caminho: nenhum algoritmo escolhe para onde o agente deve ir
        // — quem escolhe a porta é a política, olhando a novidade de cada uma.
        private void MeasureDoorPaths(int anchor)
        {
            int count = _graph.SearchRoom(anchor, CurrentRoom);
            for (int i = 0; i < count; i++)
            {
                int node = _graph.SearchedNode(i);
                if (_graph.IsDoor(node))
                    _doorPathDistance[node] = _graph.SearchedCost(node);
            }
        }

        private void ScoreExits(int anchor)
        {
            foreach (int neighbor in _graph.GetNeighbors(anchor))
            {
                // Saída para uma porta: conta dentro da sala atual. Saída para chão: dentro da sala
                // daquele chão (parado num vão, o agente vê as duas salas que ele liga).
                int room = _graph.IsDoor(neighbor) ? CurrentRoom : _graph.RoomOf(neighbor);

                float value = _graph.ScoreBeyond(anchor, neighbor, _exitHalfLifeMeters, _potential, room);
                _exitValue[neighbor] = value;
                _bestExitValue = Mathf.Max(_bestExitValue, value);

                float nearest = _graph.DistanceToNearestBeyond(anchor, neighbor, _potential, room);
                _exitNearest[neighbor] = nearest;
                if (nearest > 0f && (_bestExitNearest < 0f || nearest < _bestExitNearest))
                    _bestExitNearest = nearest;
            }
        }

        /// <summary>Quanto resta por esta saída da âncora, relativo à melhor (1 = a melhor; 0 = nada).</summary>
        public float ExitRemainingScore(int neighbor) =>
            _bestExitValue > 1e-6f ? Mathf.Clamp01(_exitValue[neighbor] / _bestExitValue) : 0f;

        /// <summary>Quão perto está o que falta por esta saída, relativo à mais perto (1 = a mais perto; 0 = nada).</summary>
        public float ExitProximityScore(int neighbor)
        {
            if (_bestExitNearest <= 0f)
                return 0f;

            float distance = _exitNearest[neighbor];
            return distance > 0f ? Mathf.Clamp01(_bestExitNearest / distance) : 0f;
        }

        // ---- Sala atual e as portas dela ----

        /// <summary>Progresso da sala atual rumo à conclusão (1 = concluída).</summary>
        public float CurrentRoomProgress =>
            CurrentRoom >= 0 ? Mathf.Clamp01((float)_roomVisited[CurrentRoom] / _roomNeeded[CurrentRoom]) : 0f;

        public bool CurrentRoomCompleted => CurrentRoom >= 0 && _roomCompleted[CurrentRoom];

        public int CurrentRoomDoorCount => CurrentRoom >= 0 ? _graph.DoorsOfRoom(CurrentRoom).Length : 0;

        /// <summary>A sala atual é a do último ping (quente)?</summary>
        public bool CurrentRoomHot => CurrentRoom >= 0 && CurrentRoom == _hotRoom;

        /// <summary>A porta é da sala quente (dá para ela, ou a sala atual é a quente)?</summary>
        public bool DoorIsHot(int door) =>
            _hotRoom >= 0 && (CurrentRoom == _hotRoom || _graph.OtherRoom(door, CurrentRoom) == _hotRoom);

        /// <summary>Portas da sala atual, em ordem ESTÁVEL (ângulo em volta do centro da sala).</summary>
        public IReadOnlyList<int> DoorSlots => _doorSlots;

        /// <summary>A sala do outro lado da porta (vista da sala atual) já está concluída?</summary>
        public bool OtherSideCompleted(int door)
        {
            int other = CurrentRoom >= 0 ? _graph.OtherRoom(door, CurrentRoom) : -1;
            return other >= 0 && _roomCompleted[other];
        }

        /// <summary>Distância (m pelo grafo, dentro da sala) da âncora até a porta; -1 se não alcança.</summary>
        public float DoorPathDistance(int door) => _doorPathDistance[door];

        public bool IsRoomCompleted(int room) => _roomCompleted[room];

        /// <summary>Quanto da sala já foi visto, rumo à conclusão (1 = concluída).</summary>
        public float RoomProgress(int room) => Mathf.Clamp01((float)_roomVisited[room] / _roomNeeded[room]);

        public bool IsRoomHot(int room) => room == _hotRoom;

        /// <summary>Multiplicador da suspeita da sala normalizado (0 = média ou menos, 1 = o teto).</summary>
        public float RoomSuspicion(int room) =>
            _maxSuspicionScale > 1f ? Mathf.Clamp01((_roomSuspicion[room] - 1f) / (_maxSuspicionScale - 1f)) : 0f;

        /// <summary>Portas entre a sala atual e esta (-1 = inalcançável ou sem sala atual). Recalculado ao trocar de sala.</summary>
        public int RoomHopsFromCurrent(int room)
        {
            if (_hopsRoom != CurrentRoom)
            {
                _hopsRoom = CurrentRoom;
                _graph.RoomHops(CurrentRoom, _roomHops);
            }

            return _roomHops[room];
        }

        // A ordem do slot só muda quando a SALA muda (mesma lógica dos vizinhos: slot com
        // significado geométrico, "a porta mais ao norte desta sala").
        private void FillDoorSlots()
        {
            _slotsRoom = CurrentRoom;
            _doorSlots.Clear();
            if (CurrentRoom < 0)
                return;

            _doorSlots.AddRange(_graph.DoorsOfRoom(CurrentRoom));
            Vector3 center = _graph.RoomCentroid(CurrentRoom);
            _doorSlots.Sort((a, b) =>
            {
                Vector3 da = _graph.NodePosition(a) - center;
                Vector3 db = _graph.NodePosition(b) - center;
                int comparison = Mathf.Atan2(da.z, da.x).CompareTo(Mathf.Atan2(db.z, db.x));
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
        }

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || _graph == null || _roomVisited == null || !Application.isPlaying)
                return;

            for (int i = 0; i < _doorCrossings.Length; i++)
            {
                if (!_graph.IsDoor(i) || !_graph.IsNodeEnabled(i))
                    continue;

                Gizmos.color = Color.Lerp(UsedDoorColor, FreshDoorColor, DoorNovelty(i));
                _graph.DrawNodeArea(i, 0.08f, 3);
            }

#if UNITY_EDITOR
            for (int r = 0; r < _graph.RoomCount; r++)
            {
                string done = _roomCompleted[r] ? (_roomPrevisited[r] ? " pré" : " ok") : r == _hotRoom ? " QUENTE" : "";
                UnityEditor.Handles.Label(
                    _graph.RoomCentroid(r) + Vector3.up * 2.6f,
                    $"S{r} {Mathf.Min(_roomVisited[r], _roomNeeded[r])}/{_roomNeeded[r]}{done}");
            }
#endif
        }
    }
}
