using System;
using System.Collections.Generic;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// TODA a observação do GraphExplorer num lugar só: o layout do vetor (188 floats) e a planta de salas
    /// (BufferSensor "Rooms"). O Manager chama <see cref="Write"/> no CollectObservations; isto só LÊ os
    /// sistemas, nunca muda estado de episódio (exceto os caches de leitura Refresh/ScoreExits).
    ///
    /// Observação EGOCÊNTRICA (nó atual, vizinhos, sala atual e suas portas) mais a planta de salas, e não
    /// "todos os nós", que amarraria a rede a este mapa. Parede vem do Ray Perception Sensor 3D do prefab.
    /// A ORDEM abaixo é o contrato com os .onnx: mudar layout ou tamanho invalida todos e exige ajustar o
    /// VectorObservationSize (o <see cref="Validate"/> avisa). Use os slots reservados antes de crescer.
    /// </summary>
    public class GraphObservations
    {
        // LAYOUT DAS OBSERVAÇÕES GLOBAIS (30):
        //   [0]      está dentro da área de algum nó
        //   [1..3]   direção X/Z + distância ao nó âncora
        //   [4..6]   direção X/Z + distância ao nó mais próximo COM LINHA LIVRE
        //   [7]      COBERTURA: fração das salas concluídas (a que encerra o episódio)
        //   [8]      encostado em parede, punida ou não (0/1)
        //   [9]      SALA ATUAL: progresso rumo à conclusão (1 = concluída)
        //   [10]     sala atual concluída (0/1)
        //   [11]     está num vão (a âncora é uma porta)
        //   [12]     nº de portas da sala atual / 8
        //   [13..15] PING: ativo, distância pelo grafo / diâmetro, quente/frio (-1/0/+1)
        //   [16..20] VISÃO: vendo, já viu, direção X/Z + distância à última posição vista do hider
        //   [21]     CALOR: a sala atual é a do último ping (0..1)
        //   [22..23] para onde o CORPO está virado (X/Z no mundo; o corpo segue o movimento)
        //   [24..25] VELOCIDADE do hider enquanto vê (X/Z, / hiderVelocityScale)
        //   [26]     VELOCIDADE DO MEU ESTADO / a da perseguição (0.59 patrulha, 0.78 alerta, 1 perseguição): vendo
        //            o alvo o monstro fica rápido, e ele sabe disso
        //   [27]     PROCURA ativa (há hider no episódio)
        //   [28]     CERTEZA: maior suspeita de um nó (1 = sei onde ele está)
        //   [29]     tempo desde a última pista (ping ou visão) / 60 s; 1 = nenhuma
        //   [30..31] MINHA VELOCIDADE real X/Z (mundo, / velocidade de perseguição): com inércia o mesmo comando
        //            dá velocidades diferentes, e frear antes da curva exige saber quanto se está andando
        //   [32]     estado PERSEGUIÇÃO (estou vendo o alvo)
        //   [33]     estado ALERTA: quanto ainda resta dele (1 = ouvi um ping / perdi de vista agora, 0 = patrulha).
        //            Alerta = sei a região geral do alvo; o ping [13..15] e o calor [21] dizem qual
        //   [34]     fração do episódio já passada (o tempo que resta muda o que vale a pena)
        //   [35]     tempo sem progresso de sala / limiar da estagnação (1 = já pagando o custo)
        // Depois: vizinhos (8 x 10) e portas (8 x 9). Total 36 + 80 + 72 = 188 (v5; até a v4.x eram 182).
        public const int GlobalObservations = 36;

        // Por vizinho (10): [0..1] direção X/Z, [2] distância, [3] visitado, [4] quanto resta por essa saída
        // (dentro da sala), [5] vim daqui, [6] quantas vezes passei (satura em revisitSaturation), [7] quão
        // perto está o que falta por essa saída (dentro da sala), [8] suspeita por essa saída, [9] válido.
        public const int FloatsPerNeighbor = 10;

        // Por PORTA da sala atual (9): [0..1] direção X/Z, [2] distância reta, [3] distância pelo grafo dentro
        // da sala, [4] novidade (1 = nunca atravessada), [5] entrei por aqui, [6] sala do outro lado concluída,
        // [7] calor (porta da sala do último ping), [8] válido. Ordem por ângulo em volta da sala
        // (GraphRoomMemory.DoorSlots); 8 slots, o máximo por sala no NodeTraining5 é 7.
        public const int FloatsPerDoor = 9;
        public const int DoorSlots = 8;

        // PLANTA DE SALAS (BufferSensor "Rooms", fora do VectorObservationSize): uma entrada por sala do mapa
        // (até MaxRooms); lista com atenção, para a rede não ficar amarrada a este mapa. Por sala (10):
        // [0..1] direção X/Z ao centro, [2] distância reta / diâmetro, [3] portas até ela / RoomHopsScale
        // (1 = inalcançável), [4] quanto já foi vista, [5] concluída, [6] quente (ping), [7] suspeita
        // (0 = média, 1 = teto), [8] é a atual, [9] nº de portas / 8.
        public const int RoomFeatures = 10;
        public const int MaxRooms = 32;
        // 16 (era 10): o NodeTraining - V5 training tem salas a 10 portas (S9 <-> S18), que saturavam em 1 e ficavam
        // iguais a "inalcançável".
        private const float RoomHopsScale = 16f;
        private const string RoomSensorName = "Rooms";

        private readonly NavGraph _graph;
        private readonly GraphExplorationMemory _memory;
        private readonly GraphRoomMemory _rooms;
        private readonly GraphHiderPerception _perception;
        private readonly GraphPingSystem _ping;
        private readonly GraphSuspicionMap _suspicion;
        private readonly GraphLocomotion _locomotion;
        private readonly GraphBodyTracker _body;
        private readonly BufferSensorComponent _roomSensor;

        private readonly int _neighborSlots;
        private readonly float _maxNodeDistance;
        private readonly int _revisitSaturation;
        private readonly float _hiderVelocityScale;
        private readonly int _stagnationSteps;

        private readonly float[] _roomBuffer = new float[RoomFeatures];
        private readonly List<int> _neighborBuffer = new List<int>();
        private readonly Comparison<int> _byDistanceFromNode;
        private readonly Comparison<int> _byAngleFromNode;
        private Vector3 _sortOrigin;

        public GraphObservations(
            NavGraph graph, GraphExplorationMemory memory, GraphHiderPerception perception, GraphPingSystem ping,
            GraphSuspicionMap suspicion, GraphLocomotion locomotion, GraphBodyTracker body, BufferSensorComponent roomSensor,
            int neighborSlots, float maxNodeDistance, int revisitSaturation, float hiderVelocityScale, int stagnationSteps)
        {
            _graph = graph;
            _memory = memory;
            _rooms = memory.Rooms;
            _perception = perception;
            _ping = ping;
            _suspicion = suspicion;
            _locomotion = locomotion;
            _body = body;
            _roomSensor = roomSensor;
            _neighborSlots = neighborSlots;
            _maxNodeDistance = maxNodeDistance;
            _revisitSaturation = revisitSaturation;
            _hiderVelocityScale = hiderVelocityScale;
            _stagnationSteps = Mathf.Max(1, stagnationSteps);
            _byDistanceFromNode = CompareByDistance;
            _byAngleFromNode = CompareByAngle;
        }

        /// <summary>Tamanho do vetor que <see cref="Write"/> emite (tem que bater com o VectorObservationSize).</summary>
        public static int SizeFor(int neighborSlots) =>
            GlobalObservations + neighborSlots * FloatsPerNeighbor + DoorSlots * FloatsPerDoor;

        /// <summary>
        /// Cria (ou acerta) o BufferSensor da planta de salas, já com o tamanho certo: tamanho errado no Inspector
        /// quebraria o treino em silêncio. Chamar no Awake do Agent, ANTES do base.Awake/OnEnable (que coletam os sensores).
        /// </summary>
        public static BufferSensorComponent EnsureRoomSensor(GameObject agent)
        {
            BufferSensorComponent roomSensor = null;
            foreach (BufferSensorComponent sensor in agent.GetComponents<BufferSensorComponent>())
            {
                if (sensor.SensorName == RoomSensorName)
                    roomSensor = sensor;
            }

            if (roomSensor == null)
                roomSensor = agent.AddComponent<BufferSensorComponent>();

            roomSensor.SensorName = RoomSensorName;
            roomSensor.ObservableSize = RoomFeatures;
            roomSensor.MaxNumObservables = MaxRooms;
            return roomSensor;
        }

        /// <summary>Uma decisão: o vetor (188) e a planta de salas.</summary>
        /// <param name="elapsedFraction">Fração do episódio já passada (0..1).</param>
        public void Write(VectorSensor sensor, Transform agent, float elapsedFraction)
        {
            Vector3 position = agent.position;
            int current = _memory.CurrentNodeIndex;

            // ---- Nó atual (4) ----
            sensor.AddObservation(_memory.IsAtNode);
            AddDirectionAndDistance(sensor, position, current >= 0 ? _graph.NodePosition(current) : position, current >= 0);

            // ---- Nó mais próximo alcançável (3) ----
            // Onde a malha está AGORA (a âncora diz de onde vim); sem isto, quem saiu do grafo só recebe a
            // direção de um nó que ficou para trás. Uma vez por DECISÃO, não por step de física: o CapsuleCast é caro.
            int nearest = _graph.FindNearestReachableNode(position);
            AddDirectionAndDistance(sensor, position, nearest >= 0 ? _graph.NodePosition(nearest) : position, nearest >= 0);

            // ---- Cobertura (1) + encostado em parede (1) ----
            // Salas concluídas no geral; e se o último step de física terminou em contato (OnCollisionStay roda
            // depois da decisão anterior, antes desta).
            sensor.AddObservation(_rooms.CompletedFraction);
            sensor.AddObservation(_body.IsTouchingAnyWall ? 1f : 0f);

            // ---- Sala atual (4) ----
            // Uma vez por decisão: portas da sala e quanto resta por saída.
            _rooms.Refresh();
            sensor.AddObservation(_rooms.CurrentRoomProgress);
            sensor.AddObservation(_rooms.CurrentRoomCompleted ? 1f : 0f);
            sensor.AddObservation(current >= 0 && _graph.IsDoor(current) ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(_rooms.CurrentRoomDoorCount / (float)DoorSlots));

            // ---- Ping (3) ----
            // Sem direção de propósito: a política descobre a saída lendo o quente/frio a cada troca de nó.
            bool pingActive = _ping.IsActive;
            sensor.AddObservation(pingActive ? 1f : 0f);
            sensor.AddObservation(pingActive ? Mathf.Clamp01(_ping.Distance / _graph.PathDiameter) : 0f);
            sensor.AddObservation(pingActive ? _ping.HotCold : 0f);

            // ---- Visão (5) ----
            // Direção + distância à ÚLTIMA POSIÇÃO VISTA (a atual enquanto vê; congelada ao perder).
            bool seeing = _perception.IsSeeing;
            bool hasSeen = _perception.HasSeen;
            sensor.AddObservation(seeing ? 1f : 0f);
            sensor.AddObservation(hasSeen ? 1f : 0f);
            AddDirectionAndDistance(sensor, position, hasSeen ? _perception.LastSeenPosition : position, hasSeen);

            // ---- Calor da sala atual (1): quão perto do último ping, 0..1 (esfria com o tempo) ----
            sensor.AddObservation(_rooms.CurrentRoomHeat);

            // ---- Corpo (5): para onde o corpo está virado, velocidade do hider, velocidade do meu estado ----
            Vector3 forward = agent.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);
            facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector2.up;
            sensor.AddObservation(facing.x);
            sensor.AddObservation(facing.y);

            Vector3 hiderVelocity = seeing ? _perception.HiderVelocity / _hiderVelocityScale : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.z, -1f, 1f));

            sensor.AddObservation(_locomotion.StateSpeed / _locomotion.MaxSpeed);

            // ---- Procura (3) ----
            bool searching = _suspicion.IsActive;
            sensor.AddObservation(searching ? 1f : 0f);
            sensor.AddObservation(searching ? _suspicion.Certainty : 0f);
            sensor.AddObservation(searching ? _suspicion.EvidenceAge : 0f);

            // ---- Meu corpo, meu estado e o tempo (6) ----
            Vector3 velocity = _locomotion.Velocity / _locomotion.MaxSpeed;
            sensor.AddObservation(Mathf.Clamp(velocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(velocity.z, -1f, 1f));
            sensor.AddObservation(_locomotion.State == GraphLocomotion.Awareness.Chase ? 1f : 0f);
            sensor.AddObservation(_locomotion.State == GraphLocomotion.Awareness.Alert ? _locomotion.AlertRemaining : 0f);
            sensor.AddObservation(Mathf.Clamp01(elapsedFraction));
            sensor.AddObservation(Mathf.Clamp01((float)_rooms.StepsSinceProgress / _stagnationSteps));

            // ---- Vizinhos (FloatsPerNeighbor x _neighborSlots) ----
            FillNeighborBuffer(current);
            _suspicion.ScoreExits(current);

            for (int slot = 0; slot < _neighborSlots; slot++)
            {
                if (slot >= _neighborBuffer.Count)
                {
                    // Slot vazio: zeros e validade 0, para ser distinguível de um vizinho real exatamente na posição do agente.
                    for (int k = 0; k < FloatsPerNeighbor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int neighbor = _neighborBuffer[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(neighbor), true);
                sensor.AddObservation(_memory.VisitedObservation(neighbor));

                // O que há ATRÁS desta saída, dentro da sala (para na porta).
                sensor.AddObservation(_rooms.ExitRemainingScore(neighbor));

                // Contra loop: de onde vim e quantas vezes passei.
                sensor.AddObservation(neighbor == _memory.PreviousNodeIndex ? 1f : 0f);
                sensor.AddObservation(Mathf.Clamp01((float)_memory.VisitCountOf(neighbor) / _revisitSaturation));

                // Quão perto está o que falta por esta saída (1 = a mais perto). Campo de distância: seguir o 1 só
                // diminui, ao contrário do "quanto resta" (soma com desconto); ajuda contra loop e beco.
                sensor.AddObservation(_rooms.ExitProximityScore(neighbor));

                // Onde o hider provavelmente está, por esta saída (0 sem procura).
                sensor.AddObservation(_suspicion.ExitScore(neighbor));
                sensor.AddObservation(1f);
            }

            // ---- Planta de salas (BufferSensor, uma entrada por sala) ----
            WriteRoomPlan(position);

            // ---- Portas da sala atual (FloatsPerDoor x DoorSlots) ----
            IReadOnlyList<int> doors = _rooms.DoorSlots;
            for (int slot = 0; slot < DoorSlots; slot++)
            {
                if (slot >= doors.Count)
                {
                    for (int k = 0; k < FloatsPerDoor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int door = doors[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(door), true);

                // Pelo grafo, dentro da sala: em linha reta a porta pode estar atrás de um móvel.
                float path = _rooms.DoorPathDistance(door);
                sensor.AddObservation(path >= 0f ? Mathf.Clamp01(path / _maxNodeDistance) : 1f);

                sensor.AddObservation(_rooms.DoorNovelty(door));
                sensor.AddObservation(door == _rooms.EntryDoor ? 1f : 0f);
                sensor.AddObservation(_rooms.OtherSideCompleted(door) ? 1f : 0f);
                sensor.AddObservation(_rooms.DoorHeat(door));
                sensor.AddObservation(1f);
            }
        }

        // Uma entrada por sala no BufferSensor "Rooms" (layout no topo).
        private void WriteRoomPlan(Vector3 position)
        {
            if (_roomSensor == null)
                return;

            float diameter = _graph.PathDiameter;
            int current = _rooms.CurrentRoom;
            for (int room = 0; room < _graph.RoomCount && room < MaxRooms; room++)
            {
                Vector3 delta = _graph.RoomCentroid(room) - position;
                var planar = new Vector2(delta.x, delta.z);
                float distance = planar.magnitude;
                Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;
                int hops = _rooms.RoomHopsFromCurrent(room);

                _roomBuffer[0] = unit.x;
                _roomBuffer[1] = unit.y;
                _roomBuffer[2] = Mathf.Clamp01(distance / diameter);
                _roomBuffer[3] = hops >= 0 ? Mathf.Clamp01(hops / RoomHopsScale) : 1f;
                _roomBuffer[4] = _rooms.RoomProgress(room);
                _roomBuffer[5] = _rooms.IsRoomCompleted(room) ? 1f : 0f;
                _roomBuffer[6] = _rooms.RoomHeat(room);
                _roomBuffer[7] = _rooms.RoomSuspicion(room);
                _roomBuffer[8] = room == current ? 1f : 0f;
                _roomBuffer[9] = Mathf.Clamp01(_graph.DoorsOfRoom(room).Length / (float)DoorSlots);
                _roomSensor.AppendObservation(_roomBuffer);
            }
        }

        // Direção planar X/Z no referencial do MUNDO (o das ações) + distância normalizada; zeros se !valid.
        private void AddDirectionAndDistance(VectorSensor sensor, Vector3 from, Vector3 to, bool valid)
        {
            if (!valid)
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                return;
            }

            Vector3 delta = to - from;
            Vector2 planar = new(delta.x, delta.z);
            float distance = planar.magnitude;
            Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;

            sensor.AddObservation(unit.x);
            sensor.AddObservation(unit.y);
            sensor.AddObservation(Mathf.Clamp01(distance / _maxNodeDistance));
        }

        /// <summary>
        /// Vizinhos ativos do nó atual (até _neighborSlots) em ordem ESTÁVEL: por ângulo no mundo a partir do nó,
        /// para o mesmo vizinho cair sempre no mesmo slot (senão é ruído). Com excesso, corta os mais distantes.
        /// </summary>
        private void FillNeighborBuffer(int current)
        {
            _neighborBuffer.Clear();

            if (current < 0)
                return;

            _sortOrigin = _graph.NodePosition(current);

            foreach (int neighbor in _graph.GetNeighbors(current))
            {
                if (_graph.IsNodeEnabled(neighbor))
                    _neighborBuffer.Add(neighbor);
            }

            if (_neighborBuffer.Count > _neighborSlots)
            {
                _neighborBuffer.Sort(_byDistanceFromNode);
                _neighborBuffer.RemoveRange(_neighborSlots, _neighborBuffer.Count - _neighborSlots);
            }

            _neighborBuffer.Sort(_byAngleFromNode);
        }

        private int CompareByDistance(int a, int b)
        {
            float da = (_graph.NodePosition(a) - _sortOrigin).sqrMagnitude;
            float db = (_graph.NodePosition(b) - _sortOrigin).sqrMagnitude;
            return da.CompareTo(db);
        }

        private int CompareByAngle(int a, int b)
        {
            float angleA = AngleFromOrigin(a);
            float angleB = AngleFromOrigin(b);
            int comparison = angleA.CompareTo(angleB);

            // Desempate pelo índice (nós empilhados no mesmo ângulo).
            return comparison != 0 ? comparison : a.CompareTo(b);
        }

        private float AngleFromOrigin(int node)
        {
            Vector3 delta = _graph.NodePosition(node) - _sortOrigin;
            return Mathf.Atan2(delta.z, delta.x);
        }

        /// <summary>
        /// Erros que deixam a observação errada EM SILÊNCIO: vetor com tamanho diferente do declarado, nó com mais
        /// vizinhos que slots, sala com mais portas que slots, mapa com mais salas que a planta guarda.
        /// </summary>
        public static void Validate(NavGraph graph, BehaviorParameters behavior, int neighborSlots, UnityEngine.Object context)
        {
            string name = context.name;

            // Nó com mais vizinhos que slots: os excedentes ficam invisíveis na observação.
            int worst = 0;
            NavNode worstNode = null;
            for (int i = 0; i < graph.NodeCount; i++)
            {
                int degree = graph.GetNeighbors(i).Length;
                if (degree > worst)
                {
                    worst = degree;
                    worstNode = graph.GetNode(i);
                }
            }

            if (worst > neighborSlots)
            {
                Debug.LogWarning(
                    $"{name}: '{worstNode.name}' tem {worst} vizinhos e só há {neighborSlots} slots de observação. " +
                    "Aumente _neighborSlots (e o VectorObservationSize junto) ou pode as ligações redundantes.",
                    worstNode);
            }

            if (graph.RoomCount > MaxRooms)
            {
                Debug.LogWarning(
                    $"{name}: o mapa tem {graph.RoomCount} salas e a planta (BufferSensor) só guarda {MaxRooms} — " +
                    "as excedentes ficam fora. Suba MaxRooms (e o treino recomeça do zero).", context);
            }

            for (int room = 0; room < graph.RoomCount; room++)
            {
                int doorCount = graph.DoorsOfRoom(room).Length;
                if (doorCount > DoorSlots)
                {
                    Debug.LogWarning(
                        $"{name}: a sala S{room} tem {doorCount} portas e só há {DoorSlots} slots de porta na " +
                        "observação — as que sobram ficam invisíveis.", context);
                }
            }

            if (behavior == null)
                return;

            int size = SizeFor(neighborSlots);
            int declared = behavior.BrainParameters.VectorObservationSize;
            if (declared != size)
            {
                Debug.LogError(
                    $"{name}: VectorObservationSize = {declared} mas o agente emite {size} observações " +
                    $"({GlobalObservations} + {neighborSlots} vizinhos x {FloatsPerNeighbor} + {DoorSlots} portas x {FloatsPerDoor}). " +
                    "Ajuste no Behavior Parameters, senão o treino roda com o vetor truncado.", context);
            }

            int continuousActions = behavior.BrainParameters.ActionSpec.NumContinuousActions;
            if (continuousActions != 4)
            {
                Debug.LogError(
                    $"{name}: esperadas 4 ações contínuas (andar X/Z, olhar X/Z), encontradas {continuousActions}.", context);
            }
        }
    }
}
