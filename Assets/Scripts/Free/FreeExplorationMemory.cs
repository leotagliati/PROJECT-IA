using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// O que o agente da v8 já VIU e por onde já PASSOU no episódio. Uma instância por agente; o mapa (pontos, salas,
    /// portas) é da arena (<see cref="FreeMap"/>).
    ///   VISÃO    a cada _visionInterval steps de física, testa os pontos ainda não vistos: no alcance, no cone de
    ///            _viewAngle em volta de para onde o corpo olha e com linha livre do olho até o ponto (raycast contra
    ///            a layer de parede). Perto do corpo (_touchRadius) vale sem cone: o que está debaixo do monstro é
    ///            visto. Ponto visto fica visto o episódio todo.
    ///   PORTAS   entrou na zona de uma porta por um lado e saiu pelo OUTRO = travessia. A 1ª travessia de cada
    ///            porta é "porta nova" (a recompensa); as seguintes só contam nas métricas.
    ///   SALA     a do ponto mais perto com NavMesh livre; dentro da zona de uma porta fica a anterior (sem
    ///            pisca-pisca no vão).
    /// As flags de step (pontos novos, portas novas) são lidas pela recompensa e limpas pelo Manager.
    /// </summary>
    public class FreeExplorationMemory : MonoBehaviour
    {
        [Header("-----Visão-----")]
        // Alcance (m). 18: a maior sala do V6 (o anel S24 cortado) fica a ~18 m de canto a canto.
        [SerializeField, Min(1f)] private float _viewDistance = 18f;

        // Abertura do cone (graus). 140, largo: na v8 o olhar é para onde o corpo anda (sem pescoço), então o cone
        // cobre os dois lados do corredor sem precisar virar.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;

        // Altura do olho acima do PISO (m). A cápsula do monstro na arena vai até ~3.25 m; o olho do modelo fica perto
        // de 2.5. Medida do piso do FreeMap, não do pivô do corpo (o corpo anda com a altura travada).
        [SerializeField, Min(0.1f)] private float _eyeHeight = 2.5f;

        // Pontos a até isto (m) são vistos sem cone (com linha livre): os que ficam debaixo ou atrás do corpo.
        [SerializeField, Min(0f)] private float _touchRadius = 2f;

        // Só parede bloqueia a visão (paredes, batentes e móveis estão nela; mesa baixa fica abaixo da linha).
        [SerializeField] private LayerMask _wallLayer;

        // De quantos em quantos steps de física a visão roda. 2 = 25 vezes por segundo: a 7 m/s anda 0.28 m entre
        // duas olhadas, nada passa despercebido, e o custo de raycast cai pela metade.
        [SerializeField, Min(1)] private int _visionInterval = 2;

        [Header("-----Salas-----")]
        // Fração dos pontos vistos para a sala contar como "vista" (observação e métricas; nada paga por sala).
        [SerializeField, Range(0.1f, 1f)] private float _roomSeenThreshold = 0.9f;

        private FreeMap _map;
        private bool[] _seen;
        private readonly List<int> _unseen = new List<int>();
        private int[] _roomSeenCount;
        private bool[] _doorCrossed;
        private float _cosHalfAngle;
        private int _tick;
        private Vector3 _lastView;

        // Zona de porta em que o agente está e o lado por onde entrou nela.
        private int _zoneDoor = -1;
        private float _zoneSide;

        public int SeenCount { get; private set; }
        public int CurrentRoom { get; private set; } = -1;

        /// <summary>Porta por onde o agente entrou na sala atual (-1 = nasceu nela).</summary>
        public int EntryDoor { get; private set; } = -1;

        /// <summary>Dentro da zona de alguma porta (o vão e um pouco de cada lado).</summary>
        public bool InDoorZone => _zoneDoor >= 0;

        public int NewPointsThisStep { get; private set; }
        public int NewDoorsThisStep { get; private set; }

        /// <summary>Steps de física desde o último ponto ou porta novos (a estagnação lê isto).</summary>
        public int StepsSinceProgress { get; private set; }

        // Métricas do episódio.
        public int DoorsCrossed { get; private set; }
        public int Crossings { get; private set; }
        public int RepeatCrossings { get; private set; }

        public float SeenFraction => _map != null && _map.PointCount > 0 ? (float)SeenCount / _map.PointCount : 0f;

        public float DoorsCrossedFraction => _map != null && _map.DoorCount > 0 ? (float)DoorsCrossed / _map.DoorCount : 0f;

        public float RoomSeenThreshold => _roomSeenThreshold;

        public float ViewDistance => _viewDistance;

        /// <summary>A posição do corpo projetada no NavMesh (no piso): sala, portas, radar e caminhos usam esta.</summary>
        public Vector3 Ground { get; private set; }

        /// <summary>Layers que são parede (visão, contato do corpo).</summary>
        public LayerMask WallLayer => _wallLayer;

        public bool IsSeen(int point) => _seen[point];

        public bool IsDoorCrossed(int door) => _doorCrossed[door];

        public float RoomSeenFraction(int room)
        {
            if (room < 0)
                return 0f;

            int total = _map.RoomPoints(room).Count;
            return total > 0 ? (float)_roomSeenCount[room] / total : 1f;
        }

        public bool IsRoomSeen(int room) => room >= 0 && RoomSeenFraction(room) >= _roomSeenThreshold;

        public int RoomsSeenCount
        {
            get
            {
                int count = 0;
                for (int r = 0; r < _map.RoomCount; r++)
                {
                    if (IsRoomSeen(r))
                        count++;
                }

                return count;
            }
        }

#if UNITY_EDITOR
        private void Reset() => _wallLayer = LayerMask.GetMask("Wall", "Obstacle", "Door");
#endif

        public void Configure(FreeMap map)
        {
            _map = map;
            _map.EnsureBuilt();
            _seen = new bool[_map.PointCount];
            _roomSeenCount = new int[_map.RoomCount];
            _doorCrossed = new bool[_map.DoorCount];
            _cosHalfAngle = Mathf.Cos(_viewAngle * 0.5f * Mathf.Deg2Rad);

            // Máscara vazia = visão atravessando parede, e o treino não acusaria nada: cai no padrão com aviso.
            if (_wallLayer.value == 0)
            {
                _wallLayer = LayerMask.GetMask("Wall", "Obstacle", "Door");
                Debug.LogWarning($"{name}: Wall Layer vazio na FreeExplorationMemory — usando Wall/Obstacle/Door. Salve o prefab com a máscara certa.", this);
            }
        }

#if UNITY_EDITOR
        // O menu de montar a arena copia a máscara de parede do NavGraph antigo (a mesma da visão do hider).
        internal void SetWallLayer(LayerMask layer) => _wallLayer = layer;
#endif

        public void ResetEpisode()
        {
            System.Array.Clear(_seen, 0, _seen.Length);
            System.Array.Clear(_roomSeenCount, 0, _roomSeenCount.Length);
            System.Array.Clear(_doorCrossed, 0, _doorCrossed.Length);
            _unseen.Clear();
            for (int i = 0; i < _seen.Length; i++)
                _unseen.Add(i);

            SeenCount = 0;
            CurrentRoom = -1;
            EntryDoor = -1;
            _zoneDoor = -1;
            _tick = 0;
            DoorsCrossed = 0;
            Crossings = 0;
            RepeatCrossings = 0;
            StepsSinceProgress = 0;
            ClearStepFlags();
        }

        public void ClearStepFlags()
        {
            NewPointsThisStep = 0;
            NewDoorsThisStep = 0;
        }

        /// <summary>Um step de física, com a posição que a física acabou de dar ao corpo: sala, portas e visão.</summary>
        /// <param name="position">Posição do corpo (o pivô; só X/Z importam).</param>
        /// <param name="view">Para onde a CABEÇA olha (planar, no mundo): o cone de visão.</param>
        /// <param name="forceVision">Roda a visão já (spawn), sem esperar o intervalo.</param>
        public void Tick(Vector3 position, Vector3 view, bool forceVision = false)
        {
            // Encostado na parede o corpo fica fora da borda do NavMesh (ela fica a agentRadius da parede): projeta.
            var floor = new Vector3(position.x, _map.FloorY, position.z);
            Ground = NavMesh.SamplePosition(floor, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) ? hit.position : floor;
            Vector3 ground = Ground;

            UpdateDoorZone(ground);

            if (!InDoorZone || CurrentRoom < 0)
            {
                int point = _map.NearestPoint(ground);
                if (point >= 0)
                    CurrentRoom = _map.RoomOfPoint(point);
            }

            _lastView = new Vector3(view.x, 0f, view.z).normalized;
            if (forceVision || _tick % _visionInterval == 0)
                Look(ground, view);
            _tick++;

            if (NewPointsThisStep > 0 || NewDoorsThisStep > 0)
                StepsSinceProgress = 0;
            else
                StepsSinceProgress++;
        }

        // Travessia = sair da zona pelo lado oposto ao que entrou. Voltar pelo mesmo lado não conta.
        private void UpdateDoorZone(Vector3 ground)
        {
            if (_zoneDoor >= 0)
            {
                FreeMap.Door door = _map.GetDoor(_zoneDoor);
                if (door.Contains(ground, _map.DoorMargin, _map.DoorApproach))
                    return;

                float side = door.Across(ground);
                if (Mathf.Sign(side) != Mathf.Sign(_zoneSide))
                    Cross(_zoneDoor, side > 0f ? door.RoomPositive : door.RoomNegative);

                _zoneDoor = -1;
            }

            int entered = _map.DoorZoneAt(ground);
            if (entered < 0)
                return;

            _zoneDoor = entered;
            _zoneSide = _map.GetDoor(entered).Across(ground);
            if (Mathf.Abs(_zoneSide) < 1e-3f)
                _zoneSide = 1e-3f;
        }

        private void Cross(int door, int toRoom)
        {
            Crossings++;
            CurrentRoom = toRoom;
            EntryDoor = door;

            if (_doorCrossed[door])
            {
                RepeatCrossings++;
                return;
            }

            _doorCrossed[door] = true;
            DoorsCrossed++;
            NewDoorsThisStep++;
        }

        private void Look(Vector3 ground, Vector3 view)
        {
            var eye = new Vector3(ground.x, _map.FloorY + _eyeHeight, ground.z);
            var forward = new Vector2(view.x, view.z);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector2.up;
            float rangeSq = _viewDistance * _viewDistance;
            float touchSq = _touchRadius * _touchRadius;

            // De trás para a frente: o visto sai da lista por troca com o último.
            for (int k = _unseen.Count - 1; k >= 0; k--)
            {
                int point = _unseen[k];
                Vector3 target = _map.PointTarget(point);
                var planar = new Vector2(target.x - ground.x, target.z - ground.z);
                float sq = planar.sqrMagnitude;
                if (sq > rangeSq)
                    continue;

                if (sq > touchSq && Vector2.Dot(planar / Mathf.Sqrt(sq), forward) < _cosHalfAngle)
                    continue;

                if (Physics.Linecast(eye, target, _wallLayer, QueryTriggerInteraction.Ignore))
                    continue;

                _seen[point] = true;
                SeenCount++;
                _roomSeenCount[_map.RoomOfPoint(point)]++;
                NewPointsThisStep++;
                _unseen[k] = _unseen[_unseen.Count - 1];
                _unseen.RemoveAt(_unseen.Count - 1);
            }
        }

        // Em Play, com o agente selecionado: pontos vistos em azul-violeta (os não vistos são o cinza do FreeMap, que
        // aparece selecionando a raiz da arena junto), portas já atravessadas como caixa azul-violeta (as novas ficam só
        // no vermelho-tijolo do FreeMap), o cone de visão e o alcance.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || _map == null || _seen == null)
                return;

            var violet = new Color(0.45f, 0.35f, 1f, 0.9f);
            Gizmos.color = violet;
            for (int i = 0; i < _seen.Length; i++)
            {
                if (_seen[i])
                    Gizmos.DrawSphere(_map.PointTarget(i), 0.3f);
            }

            for (int d = 0; d < _doorCrossed.Length; d++)
            {
                if (!_doorCrossed[d])
                    continue;

                FreeMap.Door door = _map.GetDoor(d);
                Gizmos.matrix = Matrix4x4.TRS(door.Center, Quaternion.LookRotation(door.Normal, Vector3.up), Vector3.one);
                Gizmos.DrawWireCube(Vector3.up * 1.5f, new Vector3(door.HalfWidth * 2f + 0.2f, 3.2f, door.HalfDepth * 2f + 0.2f));
            }

            Gizmos.matrix = Matrix4x4.identity;

            // Cone: as duas bordas e o centro, na altura do olho.
            var eye = new Vector3(Ground.x, _map.FloorY + _eyeHeight, Ground.z);
            Vector3 view = _lastView.sqrMagnitude > 1e-6f ? _lastView : Vector3.forward;
            Gizmos.color = new Color(0.45f, 0.35f, 1f, 0.6f);
            Gizmos.DrawLine(eye, eye + Quaternion.Euler(0f, -_viewAngle * 0.5f, 0f) * view * _viewDistance);
            Gizmos.DrawLine(eye, eye + Quaternion.Euler(0f, _viewAngle * 0.5f, 0f) * view * _viewDistance);
            Gizmos.DrawLine(eye, eye + view * _viewDistance);
            Gizmos.color = new Color(0.45f, 0.35f, 1f, 0.2f);
            Gizmos.DrawWireSphere(eye, _viewDistance);
        }
    }
}
