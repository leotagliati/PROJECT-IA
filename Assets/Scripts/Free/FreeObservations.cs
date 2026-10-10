using Assets.Scripts.Graph;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// TODA a observação da v8 num lugar só: o vetor (126 floats) e a planta de salas (BufferSensor "Rooms"). Só lê.
    ///
    /// A rede NÃO vê nó, vizinho nem ligação: vê o que falta ver, onde dá para andar e as portas. Nada aqui é seta
    /// (nenhum "vá para lá" escolhido por algoritmo): o radar diz em que direção ainda há ponto não visto DENTRO DA
    /// SALA ATUAL, e quem decide para onde ir é a política.
    ///
    /// FOCO NA PRESA (como o FocusLevel da v5): CAÇANDO = vendo o alvo, procurando (perdeu de vista há < 20 s) ou com
    /// pegada fresca na memória. Caçando, tudo que é EXPLORAÇÃO vai a zero (fração vista, sala vista, radar do que
    /// falta, porta nova, outro lado visto, planta vista): nada compete com a presa. A geometria (onde dá para andar,
    /// onde ficam as portas e as salas) continua.
    ///
    /// LAYOUT (o contrato com os .onnx; mudar invalida todos):
    ///   Globais (38)
    ///     [0..1]   para onde o CORPO está virado (X/Z no mundo)
    ///     [2..3]   para onde a CABEÇA olha (o cone de visão; o olhar vira até 60° do corpo)
    ///     [4..5]   velocidade real X/Z / velocidade de perseguição
    ///     [6]      velocidade do MEU ESTADO / a de perseguição (patrulha, alerta, perseguição: o GraphLocomotion da v5)
    ///     [7]      em PERSEGUIÇÃO (vendo o alvo, ou acabou de perder)
    ///     [8]      ALERTA: quanto ainda resta (1 = perdeu o alvo de vista agora)
    ///     [9]      encostado em parede
    ///     [10]     batidas nos últimos 5 s / 5
    ///     [11]     quão PRESO está (0 = andando, 1 = empacado há 2 s)
    ///     [12]     fração do episódio já passada
    ///     [13]     pontos vistos / coverage_target (1 = a meta)
    ///     [14]     SALA ATUAL: fração dos pontos vistos
    ///     [15]     sala atual vista (>= _roomSeenThreshold da memória)
    ///     [16]     está na zona de uma porta
    ///     [17]     portas atravessadas / portas do mapa
    ///     [18]     nº de portas da sala atual / 8
    ///     [19]     salas vistas / salas do mapa
    ///     [20]     tempo sem ponto nem porta novos / limiar da estagnação
    ///     [21]     há presa neste episódio
    ///     [22]     VENDO o alvo (inclui os 3 s de rastro depois de ver)
    ///     [23]     já viu o alvo neste episódio
    ///     [24..25] direção até o alvo (vendo: a próxima quina do caminho NavMesh; sem ver: a previsão da última vista)
    ///     [26]     distância pelo caminho / DoorReach
    ///     [27..28] velocidade do alvo enquanto vê / 5 m/s
    ///     [29]     procurando (perdeu de vista há < 20 s)
    ///     [30]     PEGADA vista: intensidade agora (0 = nenhuma lembrada)
    ///     [31..32] direção até a pegada (X/Z)
    ///     [33]     distância até a pegada / DoorReach
    ///     [34..35] para onde o alvo ia ao deixar a pegada (X/Z): o "seguir o rastro"
    ///     [36]     CAÇANDO (o foco acima está ligado)
    ///     [37]     reservado (0)
    ///   Radar (8 setores x 3), setores de 45° presos ao mundo, o setor k centrado no ângulo k x 45° a partir de +X:
    ///     [0]      quanto dá para andar nessa direção (NavMesh.Raycast até RadarReach) / RadarReach
    ///     [1]      fração dos pontos NÃO VISTOS da sala atual que estão nesse setor
    ///     [2]      quão perto está o não visto mais perto da sala nesse setor (1 = aqui, 0 = nenhum ou longe)
    ///   Portas da sala atual (8 x 8), em ordem por ângulo em volta do centro da sala (o slot não pula):
    ///     [0..1]   direção X/Z até o meio do vão
    ///     [2]      distância reta / DoorReach
    ///     [3]      distância pelo NavMesh / DoorReach (1 = sem caminho)
    ///     [4]      NOVA (nunca atravessada: é a que paga)
    ///     [5]      entrei por aqui
    ///     [6]      fração vista da sala do outro lado
    ///     [7]      válida
    /// Total 38 + 24 + 64 = 126.
    ///
    /// PLANTA (BufferSensor "Rooms", fora do VectorObservationSize), uma entrada por sala, até MaxRooms (32), 8 floats cada:
    ///   [0..1] direção X/Z ao centro, [2] distância reta / diâmetro do mapa, [3] portas até ela / 16 (1 = sem
    ///   caminho), [4] fração vista, [5] vista (0/1), [6] é a atual, [7] fração das portas dela já atravessadas.
    /// </summary>
    public class FreeObservations
    {
        public const int GlobalObservations = 38;
        public const int Sectors = 8;
        public const int FloatsPerSector = 3;
        public const int DoorSlots = 8;
        public const int FloatsPerDoor = 8;
        public const int Size = GlobalObservations + Sectors * FloatsPerSector + DoorSlots * FloatsPerDoor;

        public const int RoomFeatures = 8;
        public const int MaxRooms = 32;
        private const string RoomSensorName = "Rooms";
        private const float RoomHopsScale = 16f;

        // Alcance do "quanto dá para andar" (m): da ordem de uma sala; acima disso tudo é "livre".
        private const float RadarReach = 15f;

        // Normalizador da distância de porta e do alvo (m): a sala mais comprida do V6 tem ~30 m.
        private const float DoorReach = 30f;

        // Normalizador da velocidade do alvo (m/s): acima satura em ±1 (como o _hiderVelocityScale da v5).
        private const float TargetVelocityScale = 5f;

        private readonly FreeMap _map;
        private readonly FreeExplorationMemory _memory;
        private readonly GraphLocomotion _locomotion;
        private readonly GraphBodyTracker _body;
        private readonly FreeStuckTracker _stuck;
        private readonly GraphHiderPerception _perception;
        private readonly FreeTrackSense _track;
        private readonly BufferSensorComponent _roomSensor;
        private readonly int _stagnationSteps;

        private readonly float[] _roomBuffer = new float[RoomFeatures];
        private readonly int[] _sectorUnseen = new int[Sectors];
        private readonly float[] _sectorNearest = new float[Sectors];
        private readonly NavMeshPath _path = new NavMeshPath();

        public FreeObservations(
            FreeMap map, FreeExplorationMemory memory, GraphLocomotion locomotion, GraphBodyTracker body, FreeStuckTracker stuck,
            GraphHiderPerception perception, FreeTrackSense track, BufferSensorComponent roomSensor, int stagnationSteps)
        {
            _map = map;
            _memory = memory;
            _locomotion = locomotion;
            _body = body;
            _stuck = stuck;
            _perception = perception;
            _track = track;
            _roomSensor = roomSensor;
            _stagnationSteps = Mathf.Max(1, stagnationSteps);
        }

        /// <summary>
        /// Cria (ou acerta) o BufferSensor da planta com o tamanho certo. Chamar no Awake do Agent, ANTES do base.Awake
        /// (que coleta os sensores).
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

        /// <param name="agent">O corpo do agente.</param>
        /// <param name="elapsedFraction">Fração do episódio já passada (0..1).</param>
        /// <param name="coverageTarget">Fração dos pontos que encerra o episódio.</param>
        /// <param name="huntActive">Há presa neste episódio.</param>
        /// <param name="hunting">Caçando (vendo, procurando ou com pegada fresca): a exploração some (FOCO).</param>
        public void Write(VectorSensor sensor, Transform agent, float elapsedFraction, float coverageTarget, bool huntActive, bool hunting)
        {
            Vector3 ground = _memory.Ground;
            int room = _memory.CurrentRoom;
            _hunting = hunting;

            // ---- Corpo e estado (12) ----
            Vector3 forward = agent.forward;
            var facing = new Vector2(forward.x, forward.z);
            facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector2.up;
            sensor.AddObservation(facing.x);
            sensor.AddObservation(facing.y);

            Vector3 view = _locomotion.ViewDirection;
            sensor.AddObservation(view.x);
            sensor.AddObservation(view.z);

            Vector3 velocity = _locomotion.Velocity / _locomotion.MaxSpeed;
            sensor.AddObservation(Mathf.Clamp(velocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(velocity.z, -1f, 1f));
            sensor.AddObservation(_locomotion.StateSpeed / _locomotion.MaxSpeed);
            sensor.AddObservation(_locomotion.State == GraphLocomotion.Awareness.Chase ? 1f : 0f);
            sensor.AddObservation(_locomotion.State == GraphLocomotion.Awareness.Alert ? _locomotion.AlertRemaining : 0f);

            sensor.AddObservation(_body.IsTouchingAnyWall ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(_body.RecentHits / 5f));
            sensor.AddObservation(_stuck.Level);

            // ---- Exploração (9) ----
            sensor.AddObservation(Mathf.Clamp01(elapsedFraction));
            sensor.AddObservation(Explore(Mathf.Clamp01(_memory.SeenFraction / Mathf.Max(0.05f, Mathf.Min(1f, coverageTarget)))));
            sensor.AddObservation(Explore(_memory.RoomSeenFraction(room)));
            sensor.AddObservation(Explore(_memory.IsRoomSeen(room) ? 1f : 0f));
            sensor.AddObservation(_memory.InDoorZone ? 1f : 0f);
            sensor.AddObservation(Explore(_memory.DoorsCrossedFraction));
            sensor.AddObservation(room >= 0 ? Mathf.Clamp01(_map.DoorsOfRoom(room).Count / (float)DoorSlots) : 0f);
            sensor.AddObservation(Explore(_map.RoomCount > 0 ? (float)_memory.RoomsSeenCount / _map.RoomCount : 0f));
            sensor.AddObservation(Explore(Mathf.Clamp01((float)_memory.StepsSinceProgress / _stagnationSteps)));

            // ---- Caça (11) ----
            bool seeing = huntActive && _perception.IsSeeing;
            bool hasSeen = huntActive && _perception.HasSeen;
            sensor.AddObservation(huntActive ? 1f : 0f);
            sensor.AddObservation(seeing ? 1f : 0f);
            sensor.AddObservation(hasSeen ? 1f : 0f);
            if (hasSeen)
            {
                Vector3 aim = _perception.ChaseAim(agent.position, out float routeDistance);
                var planar = new Vector2(aim.x - agent.position.x, aim.z - agent.position.z);
                float length = planar.magnitude;
                Vector2 unit = length > 1e-4f ? planar / length : Vector2.zero;
                sensor.AddObservation(unit.x);
                sensor.AddObservation(unit.y);
                sensor.AddObservation(Mathf.Clamp01(routeDistance / DoorReach));
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }

            Vector3 targetVelocity = seeing ? _perception.HiderVelocity / TargetVelocityScale : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp(targetVelocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(targetVelocity.z, -1f, 1f));
            sensor.AddObservation(huntActive && _perception.IsSearching ? 1f : 0f);

            // ---- Pegada (6) + caçando (1) + reservado (1) ----
            bool track = huntActive && !seeing && _track != null && _track.HasTrack;
            if (track)
            {
                var toPrint = new Vector2(_track.Position.x - agent.position.x, _track.Position.z - agent.position.z);
                float printDistance = toPrint.magnitude;
                Vector2 printUnit = printDistance > 1e-4f ? toPrint / printDistance : Vector2.zero;
                sensor.AddObservation(_track.Intensity);
                sensor.AddObservation(printUnit.x);
                sensor.AddObservation(printUnit.y);
                sensor.AddObservation(Mathf.Clamp01(printDistance / DoorReach));
                sensor.AddObservation(_track.Heading.x);
                sensor.AddObservation(_track.Heading.z);
            }
            else
            {
                for (int k = 0; k < 6; k++)
                    sensor.AddObservation(0f);
            }

            sensor.AddObservation(hunting ? 1f : 0f);
            sensor.AddObservation(0f);

            // ---- Radar (8 x 3) ----
            WriteRadar(sensor, ground, room);

            // ---- Portas da sala atual (8 x 8) ----
            WriteDoors(sensor, ground, room);

            // ---- Planta ----
            WriteRoomPlan(ground, room);
        }

        private void WriteRadar(VectorSensor sensor, Vector3 ground, int room)
        {
            System.Array.Clear(_sectorUnseen, 0, Sectors);
            for (int s = 0; s < Sectors; s++)
                _sectorNearest[s] = float.MaxValue;

            int unseenTotal = 0;
            if (room >= 0)
            {
                foreach (int point in _map.RoomPoints(room))
                {
                    if (_memory.IsSeen(point))
                        continue;

                    Vector3 delta = _map.PointGround(point) - ground;
                    int sector = SectorOf(delta.x, delta.z);
                    float distance = new Vector2(delta.x, delta.z).magnitude;
                    _sectorUnseen[sector]++;
                    unseenTotal++;
                    if (distance < _sectorNearest[sector])
                        _sectorNearest[sector] = distance;
                }
            }

            float reach = _memory.ViewDistance;
            for (int s = 0; s < Sectors; s++)
            {
                float angle = s * Mathf.PI * 2f / Sectors;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                float free = NavMesh.Raycast(ground, ground + direction * RadarReach, out NavMeshHit hit, NavMesh.AllAreas)
                    ? hit.distance : RadarReach;

                sensor.AddObservation(Mathf.Clamp01(free / RadarReach));
                sensor.AddObservation(Explore(unseenTotal > 0 ? (float)_sectorUnseen[s] / unseenTotal : 0f));
                sensor.AddObservation(Explore(_sectorUnseen[s] > 0 ? Mathf.Clamp01(1f - _sectorNearest[s] / reach) : 0f));
            }
        }

        // FOCO: caçando, a exploração vale zero na observação (cabeçalho).
        private bool _hunting;

        private float Explore(float value) => _hunting ? 0f : value;

        // Setor k = ângulo (a partir de +X, anti-horário visto de cima) mais perto de k x 45°.
        private static int SectorOf(float x, float z)
        {
            float angle = Mathf.Atan2(z, x);
            if (angle < 0f)
                angle += Mathf.PI * 2f;
            return Mathf.RoundToInt(angle / (Mathf.PI * 2f / Sectors)) % Sectors;
        }

        private void WriteDoors(VectorSensor sensor, Vector3 ground, int room)
        {
            int count = room >= 0 ? _map.DoorsOfRoom(room).Count : 0;
            for (int slot = 0; slot < DoorSlots; slot++)
            {
                if (slot >= count)
                {
                    for (int k = 0; k < FloatsPerDoor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int doorIndex = _map.DoorsOfRoom(room)[slot];
                FreeMap.Door door = _map.GetDoor(doorIndex);
                var planar = new Vector2(door.Ground.x - ground.x, door.Ground.z - ground.z);
                float distance = planar.magnitude;
                Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;
                sensor.AddObservation(unit.x);
                sensor.AddObservation(unit.y);
                sensor.AddObservation(Mathf.Clamp01(distance / DoorReach));
                sensor.AddObservation(Mathf.Clamp01(PathLength(ground, door.Ground) / DoorReach));
                sensor.AddObservation(Explore(_memory.IsDoorCrossed(doorIndex) ? 0f : 1f));
                sensor.AddObservation(doorIndex == _memory.EntryDoor ? 1f : 0f);
                sensor.AddObservation(Explore(_memory.RoomSeenFraction(door.OtherRoom(room))));
                sensor.AddObservation(1f);
            }
        }

        // Metros pelo NavMesh; sem caminho = infinito (vira 1 na observação).
        private float PathLength(Vector3 from, Vector3 to)
        {
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
                return float.MaxValue;

            Vector3[] corners = _path.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

        private void WriteRoomPlan(Vector3 ground, int current)
        {
            if (_roomSensor == null)
                return;

            float diameter = _map.Diameter;
            for (int room = 0; room < _map.RoomCount && room < MaxRooms; room++)
            {
                Vector3 delta = _map.RoomCentroid(room) - ground;
                var planar = new Vector2(delta.x, delta.z);
                float distance = planar.magnitude;
                Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;
                int hops = _map.RoomHops(current, room);

                int doors = _map.DoorsOfRoom(room).Count;
                int crossed = 0;
                foreach (int door in _map.DoorsOfRoom(room))
                {
                    if (_memory.IsDoorCrossed(door))
                        crossed++;
                }

                _roomBuffer[0] = unit.x;
                _roomBuffer[1] = unit.y;
                _roomBuffer[2] = Mathf.Clamp01(distance / diameter);
                _roomBuffer[3] = hops >= 0 ? Mathf.Clamp01(hops / RoomHopsScale) : 1f;
                _roomBuffer[4] = Explore(_memory.RoomSeenFraction(room));
                _roomBuffer[5] = Explore(_memory.IsRoomSeen(room) ? 1f : 0f);
                _roomBuffer[6] = room == current ? 1f : 0f;
                _roomBuffer[7] = Explore(doors > 0 ? (float)crossed / doors : 1f);
                _roomSensor.AppendObservation(_roomBuffer);
            }
        }

        /// <summary>Erros que deixam a observação errada EM SILÊNCIO: vetor ou ações com tamanho diferente do declarado.</summary>
        public static void Validate(BehaviorParameters behavior, int continuousActions, Object context)
        {
            if (behavior == null)
                return;

            int declared = behavior.BrainParameters.VectorObservationSize;
            if (declared != Size)
            {
                Debug.LogError(
                    $"{context.name}: VectorObservationSize = {declared} mas o agente emite {Size} " +
                    $"({GlobalObservations} + {Sectors} setores x {FloatsPerSector} + {DoorSlots} portas x {FloatsPerDoor}).", context);
            }

            if (behavior.BrainParameters.ActionSpec.NumContinuousActions != continuousActions)
            {
                Debug.LogError(
                    $"{context.name}: esperadas {continuousActions} ações contínuas (andar X/Z, olhar X/Z), encontradas " +
                    $"{behavior.BrainParameters.ActionSpec.NumContinuousActions}.", context);
            }
        }
    }
}
