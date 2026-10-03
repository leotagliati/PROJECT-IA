using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A VISÃO do seeker sobre o hider: cone à frente + linha livre de parede, a memória da última
    /// posição vista e a CAPTURA (hider ao alcance, sem parede no meio). É local de propósito:
    /// fora do cone ou atrás de parede o seeker não sabe onde o hider está.
    ///
    /// Observação [16..20]: vendo, já viu, direção X/Z e distância à última posição vista (congela
    /// ao perder de vista). Paga, no GraphRewardSystem: avistar (com cooldown), metros de
    /// aproximação enquanto vê e capturar (terminal).
    ///
    /// Um por agente; Tick a cada step de física. CanSeePoint também é usado pela GraphRoomMemory
    /// e pela GraphSuspicionMap.
    /// </summary>
    public class GraphHiderPerception : MonoBehaviour
    {
        [Header("-----Cone-----")]
        // Abertura TOTAL do cone, em graus, em torno de para onde o corpo OLHA (ações [2..3]), não
        // necessariamente para onde anda.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 100f;

        // Alcance da visão, em metros.
        [SerializeField, Min(1f)] private float _viewDistance = 15f;

        // Altura (m) dos olhos e do ponto olhado acima do pivô de cada transform. Mobília está na
        // layer Wall e bloqueia a visão: baixo demais, qualquer mesa tapa o cone.
        [SerializeField] private float _eyeHeight = 1.4f;

        [Header("-----Captura-----")]
        // Distância planar (m) entre centros em que o hider conta como pego (com linha livre).
        // Não exige o cone nem trombar de frente.
        [SerializeField, Min(0.5f)] private float _captureDistance = 2.5f;

        [Header("-----Avistar-----")]
        // Steps de física sem ver (250 = 5 s) para uma nova aquisição pagar de novo; evita
        // farmar bônus entrando e saindo do cone numa quina.
        [SerializeField, Min(0)] private int _respotCooldownSteps = 250;

        [Header("-----Referências-----")]
        [SerializeField] private GraphHider _hider;

        // O alvo de fato: o hider no treino, o jogador no modo de jogo (SetTarget).
        private IGraphTarget _target;

        private NavGraph _graph;
        private int _lastSeenStep = int.MinValue;
        private int _step;

        /// <summary>Vendo o hider agora (dentro do cone, com linha de visão livre).</summary>
        public bool IsSeeing { get; private set; }

        /// <summary>Alcance do cone (m). A GraphRoomMemory filtra por ele antes de gastar raycast.</summary>
        public float ViewDistance => _viewDistance;

        /// <summary>Já viu o hider neste episódio.</summary>
        public bool HasSeen { get; private set; }

        /// <summary>Última posição em que viu (a atual, enquanto vê). Válida só com HasSeen.</summary>
        public Vector3 LastSeenPosition { get; private set; }

        /// <summary>Distância planar ao hider AGORA. Válida só com IsSeeing.</summary>
        public float CurrentDistance { get; private set; }

        /// <summary>
        /// Velocidade planar do hider (m/s), medida entre dois steps seguidos de visão. Zero sem
        /// ver ou no primeiro step de visão.
        /// </summary>
        public Vector3 HiderVelocity { get; private set; }

        private Vector3 _previousSeenPosition;
        private bool _hasPreviousSeenPosition;

        /// <summary>
        /// Avistou (não-vendo -> vendo, fora do cooldown) desde o último <see cref="ClearStepFlags"/>.
        /// </summary>
        public bool Spotted { get; private set; }

        /// <summary>
        /// Pegou o hider. Fica de pé até o ResetEpisode: é evento TERMINAL, o manager paga e encerra.
        /// </summary>
        public bool Caught { get; private set; }

        public void Configure(NavGraph graph)
        {
            _graph = graph;

            if (_hider == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                if (arena != null)
                    _hider = arena.GetComponentInChildren<GraphHider>(includeInactive: true);
            }

            if (_target == null && _hider != null)
                _target = _hider;
        }

        public void SetTarget(IGraphTarget target) => _target = target;

        public void ResetEpisode()
        {
            IsSeeing = false;
            HasSeen = false;
            LastSeenPosition = Vector3.zero;
            CurrentDistance = 0f;
            HiderVelocity = Vector3.zero;
            _hasPreviousSeenPosition = false;
            Caught = false;
            _lastSeenStep = int.MinValue;
            _step = 0;
            ClearStepFlags();
        }

        public void ClearStepFlags() => Spotted = false;

        /// <summary>Desfaz a captura (modo de jogo: encostar no jogador antes de a partida começar).</summary>
        public void ForgetCaught() => Caught = false;

        /// <summary>Chamar a cada step de física, com o transform do seeker.</summary>
        public void Tick(Transform seeker)
        {
            _step++;

            bool seeing = GraphTarget.IsLive(_target) && CanSeePoint(seeker, _target.Position);

            if (seeing)
            {
                Vector3 delta = _target.Position - seeker.position;
                CurrentDistance = new Vector2(delta.x, delta.z).magnitude;
                LastSeenPosition = _target.Position;
                HasSeen = true;

                Vector3 moved = LastSeenPosition - _previousSeenPosition;
                HiderVelocity = _hasPreviousSeenPosition
                    ? new Vector3(moved.x, 0f, moved.z) / Time.fixedDeltaTime
                    : Vector3.zero;
                _previousSeenPosition = LastSeenPosition;
                _hasPreviousSeenPosition = true;

                if (!IsSeeing && _step - _lastSeenStep > _respotCooldownSteps)
                    Spotted = true;

                _lastSeenStep = _step;
            }
            else
            {
                // Sem posição anterior, a próxima aquisição não vira um salto de velocidade absurdo.
                HiderVelocity = Vector3.zero;
                _hasPreviousSeenPosition = false;
            }

            IsSeeing = seeing;

            if (!Caught && GraphTarget.IsLive(_target) && IsWithinReach(seeker, _target.Position))
                Caught = true;
        }

        private bool IsWithinReach(Transform seeker, Vector3 hiderPosition)
        {
            Vector3 delta = hiderPosition - seeker.position;
            if (new Vector2(delta.x, delta.z).magnitude > _captureDistance)
                return false;

            // Pegar através de parede fina não conta.
            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = hiderPosition + Vector3.up * _eyeHeight;
            Vector3 ray = target - eye;
            return !Physics.Raycast(eye, ray.normalized, ray.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// O ponto está no cone, ao alcance e com linha livre de parede? É o teste que decide se o
        /// hider foi visto e que a GraphSuspicionMap/GraphRoomMemory aplicam aos nós.
        /// </summary>
        public bool CanSeePoint(Transform seeker, Vector3 point)
        {
            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = point + Vector3.up * _eyeHeight;
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            if (distance > _viewDistance || distance < 1e-3f)
                return false;

            Vector3 forward = new Vector3(seeker.forward.x, 0f, seeker.forward.z);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;

            if (Vector3.Angle(forward, planar) > _viewAngle * 0.5f)
                return false;

            // Mesma máscara que valida as ligações do grafo; inclui a mobília, por isso _eyeHeight importa.
            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            return !Physics.Raycast(eye, delta.normalized, delta.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // Gizmo (azul-claro): cone, linha até o hider enquanto vê e X na última posição vista quando não vê.
        private void OnDrawGizmosSelected()
        {
            Vector3 eye = transform.position + Vector3.up * _eyeHeight;
            Vector3 forward = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            Quaternion left = Quaternion.Euler(0f, -_viewAngle * 0.5f, 0f);
            Quaternion right = Quaternion.Euler(0f, _viewAngle * 0.5f, 0f);
            Gizmos.DrawLine(eye, eye + left * forward * _viewDistance);
            Gizmos.DrawLine(eye, eye + right * forward * _viewDistance);

            if (!Application.isPlaying)
                return;

            if (IsSeeing && GraphTarget.IsLive(_target))
            {
                Gizmos.DrawLine(eye, _target.Position + Vector3.up * _eyeHeight);
            }
            else if (HasSeen)
            {
                Vector3 p = LastSeenPosition + Vector3.up * _eyeHeight;
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, -0.4f), p + new Vector3(0.4f, 0f, 0.4f));
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, 0.4f), p + new Vector3(0.4f, 0f, -0.4f));
            }
        }
    }
}
