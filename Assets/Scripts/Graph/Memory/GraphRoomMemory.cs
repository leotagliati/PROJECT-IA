using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Memória de salas e portas de UM agente (estado de episódio): quanto cada sala foi coberta,
    /// quantas vezes cada porta foi atravessada e o que ainda vale. Lê a GraphExplorationMemory a
    /// cada step de física e entrega UNIDADES (sala inteira = 1, porta nova = 1); o GraphRewardSystem
    /// converte em recompensa. Também serve a observação (Refresh, saídas, slots de porta).
    /// Regras: na exploração TODA SALA VALE 1, qualquer que seja o tamanho (a fatia de cada nó é
    /// 1 / ⌈limiar x N⌉); só o calor do ping, a suspeita do hider e a liberação mudam esse valor (Scale).
    /// Sala concluída com ⌈limiar x N⌉ nós pisados; novidade da porta decai por travessia
    /// (sala de UMA porta: ida e volta contam como uma); sala quente = a do último ping, com calor
    /// pelo prédio; suspeita multiplica o valor de ver a sala; vision_explores conta nós vistos
    /// como pisados; liberação devolve a porta/sala mais antiga valendo _releasedValue.
    /// Não é componente: é a camada de SALA da <see cref="GraphExplorationMemory"/>, que a serializa (ajustes no
    /// Inspector dela) e chama Reset/Tick/Clear na ordem certa (nós antes de salas).
    /// </summary>
    [System.Serializable]
    public class GraphRoomMemory
    {
        [Header("-----Portas-----")]
        // Quanto a porta perde a cada travessia (1, 0.5, 0.25...). Um vai-e-vem infinito soma no máximo 2x a primeira.
        [SerializeField, Range(0.05f, 0.95f)] private float _doorNoveltyDecay = 0.5f;

        [Header("-----Liberação (release_fraction no currículo)-----")]
        // Valor com que porta e sala liberadas voltam (1 = igual a nova). Abaixo de 1 para o inédito pagar mais.
        [SerializeField, Range(0f, 1f)] private float _releasedValue = 0.5f;

        [Header("-----Sala quente (ping)-----")]
        // Valor de cada fatia da sala do ping (e da conclusão dela) em relação a uma sala normal.
        // Só esquenta quando um ping começa; re-esquentar a mesma sala não faz nada.
        [SerializeField, Min(1f)] private float _hotRoomValue = 2f;

        [Header("-----Visão (vision_explores no currículo)-----")]
        // Steps de física entre testes do cone contra os nós da sala atual (5 = uma vez por decisão).
        [SerializeField, Min(1)] private int _visionIntervalSteps = 5;

        [Header("-----Suspeita (com hider)-----")]
        // Teto do multiplicador da suspeita na OBSERVAÇÃO (normalizada por ele) e na leitura da razão.
        [SerializeField, Min(1f)] private float _maxSuspicionScale = 3f;

        // Teto do multiplicador na RECOMPENSA: a faixa [1, _maxSuspicionScale] da observação é esticada
        // até aqui. Sem teto, o nó do ping valeria dezenas de salas.
        [SerializeField, Min(1f)] private float _rewardSuspicionScale = 8f;

        // Sala concluída REABRE quando a suspeita dela passa deste múltiplo da média.
        [SerializeField, Min(1f)] private float _suspicionReopenRatio = 2f;

        [Header("-----Mapa de calor do ping-----")]
        // Fator de calor por porta de distância da sala do ping (1, 0.65, 0.42, ... 0.03 a 8 portas):
        // dá gradiente em qualquer ponto do prédio.
        [SerializeField, Range(0.1f, 0.95f)] private float _heatFalloffPerHop = 0.65f;

        // Meia-vida do calor, em segundos: o ping esfria sozinho se ninguém o atende.
        [SerializeField, Min(1f)] private float _heatHalfLifeSeconds = 25f;

        // Soma do calor ao multiplicador do valor de VER a sala: x (quieto + boost x calor). Sala do
        // ping com calor cheio: 2 (_hotRoomValue) x (0.25 + 4) = ~8.5x o valor base.
        [SerializeField, Min(0f)] private float _heatValueBoost = 4f;

        // Valor de explorar NO FRIO enquanto há calor (lerp 1 -> este, com o calor): o ping desvia
        // o agente de varrer o outro lado do mapa. Volta a 1 conforme esfria.
        [SerializeField, Range(0.05f, 1f)] private float _pingQuietScale = 0.25f;

        [Header("-----Observação das saídas-----")]
        // Meia-vida, em METROS pelo grafo, do desconto do "quanto resta por esta saída" (dentro da sala).
        [SerializeField, Min(0.1f)] private float _exitHalfLifeMeters = 20f;

        // Peso de uma porta no quanto-resta, vezes a novidade dela, em unidades de SALA (0.5 = meia
        // sala). Só observação.
        [SerializeField, Min(0f)] private float _doorPotential = 0.5f;

        // Fração do valor de um nó de sala JÁ CONCLUÍDA no quanto-resta (a cauda). Só observação.
        [SerializeField, Range(0f, 1f)] private float _completedNodePotential = 0.2f;

        [Header("-----Gizmos (só em Play)-----")]
        // Legenda: disco ciano -> azul-escuro = novidade da porta (cheia -> gasta);
        // rótulo "S# 3/4 ok" = nós pisados / necessários da sala, concluída.
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
        private float[] _roomSuspicion;    // multiplicador da suspeita (>= 1)
        private int[] _roomHops;           // portas da sala atual até cada sala
        private int[] _heatHops;           // portas da sala do último ping até cada sala
        private float[] _heatShape;        // calor por sala logo após o ping (1 na sala dele)
        private float _heatLevel;          // 1 no ping, esfria com a meia-vida
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
        /// Fração das salas concluídas, sem as que já nasceram concluídas (saem do numerador e do
        /// denominador). É a cobertura que encerra o episódio e que o agente observa.
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
            _heatHops = new int[rooms];
            _heatShape = new float[rooms];
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
        /// Zera o episódio. Chamar DEPOIS do ResetEpisode da memória de nós. Sempre sobra ao menos
        /// uma sala por concluir (previsited_fraction).
        /// </summary>
        public void ResetEpisode(in GraphEpisodeSettings settings)
        {
            float completeThreshold = Mathf.Clamp(settings.RoomCompleteThreshold, 0.05f, 1f);
            float previsitedFraction = settings.PrevisitedFraction;
            _releaseFraction = Mathf.Clamp01(settings.ReleaseFraction);
            _visionExplores = settings.VisionExplores && _perception != null;
            _hotRoom = -1;
            _heatLevel = 0f;
            System.Array.Clear(_heatShape, 0, _heatShape.Length);
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

        // Sorteia salas que já nascem concluídas; sempre sobra uma, senão a cobertura nasce em 100%.
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

            if (_heatLevel > 0f)
            {
                _heatLevel *= Mathf.Pow(0.5f, Time.fixedDeltaTime / _heatHalfLifeSeconds);
                if (_heatLevel < 0.01f)
                    _heatLevel = 0f;
            }
            bool progress = false;
            Vector3 agentPosition = agent.position;

            if (_nodes.ArrivedThisTick)
            {
                int node = _nodes.CurrentNodeIndex;
                _dirty = true;

                if (_graph.IsDoor(node))
                {
                    _pendingDoor = node;

                    // Nasceu num vão: adota uma das salas dele (a travessia para a outra conta normal).
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

        // Nós de QUALQUER sala dentro do cone contam como pisados. O filtro por distância vem antes
        // do raycast, para só testar parede nos nós ao alcance.
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

        // Refaz o multiplicador da suspeita por sala e reabre sala concluída onde ela voltou a crescer.
        // Sem hider, tudo fica em 1 e nada reabre.
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

                // A sala atual não reabre: zerar o chão debaixo do agente seria renda de graça.
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

        // Valor efetivo de uma fatia da sala: base (normal, liberada, quente) x suspeita x calor do ping.
        // A observação (RoomSuspicion) segue normalizada por _maxSuspicionScale; só a recompensa é esticada.
        private float Scale(int room)
        {
            float stretch = _maxSuspicionScale > 1f
                ? (_rewardSuspicionScale - 1f) / (_maxSuspicionScale - 1f)
                : 1f;
            float suspicion = 1f + (_roomSuspicion[room] - 1f) * stretch;

            float ping = Mathf.Lerp(1f, _pingQuietScale, _heatLevel) + _heatValueBoost * RoomHeat(room);
            return _roomValueScale[room] * suspicion * ping;
        }

        /// <summary>Calor da sala (0..1): 1 na sala do último ping, cai por porta de distância e com o tempo.</summary>
        public float RoomHeat(int room) => room >= 0 ? _heatLevel * _heatShape[room] : 0f;

        public float CurrentRoomHeat => RoomHeat(CurrentRoom);

        /// <summary>Calor do mapa (0..1): 1 quando um ping começa, cai com a meia-vida. Alerta do GraphLocomotion.</summary>
        public float HeatLevel => _heatLevel;

        /// <summary>Calor da sala do OUTRO lado da porta (vista da sala atual).</summary>
        public float DoorHeat(int door)
        {
            int other = CurrentRoom >= 0 ? _graph.OtherRoom(door, CurrentRoom) : -1;
            return RoomHeat(other);
        }

        /// <summary>
        /// Esquenta a sala de um ping que começou: concluída volta a ser explorável, e a sala quente
        /// anterior esfria. Re-esquentar a sala que já está quente só renova o calor (senão um hider
        /// passeando nela viraria renda).
        /// </summary>
        public void HeatRoom(int room)
        {
            if (room < 0)
                return;

            if (room == _hotRoom)
            {
                _heatLevel = 1f;
                return;
            }

            _graph.RoomHops(room, _heatHops);
            for (int r = 0; r < _heatShape.Length; r++)
                _heatShape[r] = _heatHops[r] >= 0 ? Mathf.Pow(_heatFalloffPerHop, _heatHops[r]) : 0f;
            _heatLevel = 1f;

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

        // Porta da travessia A -> B: a pisada entre as duas ou, se a âncora pulou o vão (ladrilho
        // estreito, agente rápido), a mais perto do agente. -1 se não há porta entre A e B.
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

            // Sala de UMA porta: a volta fecha o par aberto na ida e paga a mesma novidade; sem isso,
            // entrar num beco custaria metade só por ser beco.
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

            // Sala pré-concluída não paga saída: o mérito não foi do agente.
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

        // Uma liberação de cada tipo por step, no máximo.
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
                // A sala atual não é liberada: zerar o chão debaixo do agente seria renda de graça.
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
        // Leitura para a observação (refeita só quando algo mudou)
        // ================================================================================

        /// <summary>
        /// Recalcula distâncias até as portas da sala e o quanto-resta das saídas da âncora. Chamar
        /// uma vez por decisão, antes de ler qualquer coisa abaixo; é barato quando nada mudou.
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

        // Distância pelo grafo, dentro da sala atual, da âncora até cada porta. Só informação para a
        // observação: quem escolhe a porta é a política.
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
                // Saída para porta conta na sala atual; para chão, na sala daquele chão (num vão o agente vê as duas).
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

        // A ordem dos slots só muda quando a SALA muda: slot com significado geométrico ("a porta mais ao norte").
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

        /// <summary>Chamado pelo OnDrawGizmos da GraphExplorationMemory (esta classe não é componente).</summary>
        public void DrawGizmos()
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
