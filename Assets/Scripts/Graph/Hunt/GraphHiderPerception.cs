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
    /// Também é dona da APROXIMAÇÃO (distância na decisão anterior), que só vale vendo nas duas.
    /// Um por agente; Tick a cada step de física. CanSeePoint também é usado pela GraphRoomMemory
    /// e pela GraphSuspicionMap. O alvo (hider ou jogador) vem da arena no Configure.
    /// </summary>
    public class GraphHiderPerception : MonoBehaviour
    {
        [Header("-----Cone-----")]
        // Abertura TOTAL do cone, em graus, em torno de para onde a CABEÇA olha (GraphLocomotion: o
        // olhar [2..3] vira o pescoço em relação ao corpo, que segue o movimento).
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 100f;

        // Alcance da visão, em metros. 22 (06/10; era 15): a 15 m o jogador sumia de vista no meio de uma sala
        // grande (S24 ~32 x 36 m). Também é o alcance com que a visão conclui salas e limpa suspeita.
        [SerializeField, Min(1f)] private float _viewDistance = 22f;

        // Altura (m) dos olhos acima do pivô (o pé) do seeker; o ponto olhado fica nessa mesma altura (AtEyeLevel).
        // Mobília bloqueia a visão: baixo demais, qualquer mesa tapa o cone.
        [SerializeField] private float _eyeHeight = 1.67f;

        [Header("-----Visão do ALVO (não vale para nós)-----")]
        // Já VENDO o alvo, o cone para ele abre e alcança mais (06/10): o monstro "trava" no jogador. Com o cone de
        // 100° seguindo a cabeça, um passo de lado a 3 m já tirava o jogador da vista. 30 m vendo (o normal é 22). Só vale para manter, não para
        // avistar: a primeira vista continua sendo o cone normal.
        [SerializeField, Range(10f, 360f)] private float _lockedViewAngle = 160f;
        [SerializeField, Min(1f)] private float _lockedViewDistance = 30f;

        // Perto assim (m), vê o alvo em qualquer direção (com linha livre): ouve e sente quem está colado nele.
        [SerializeField, Min(0f)] private float _closeSenseDistance = 4f;

        // Meia largura (m) do alvo para os raios: testa o centro e os dois ombros e vê se QUALQUER um passa. Com um
        // raio só, o batente de uma porta ou a quina de um móvel na frente do centro escondia o jogador inteiro.
        [SerializeField, Min(0f)] private float _targetHalfWidth = 0.35f;

        // Quanto abaixo do topo do collider do alvo fica a CABEÇA mirada (m). Os raios saem do olho e descem até o
        // CORPO do alvo (cabeça e peito, 06/10), não mais até a altura do olho sobre o X/Z dele: daquele jeito a
        // linha passava por cima de qualquer móvel mais baixo que o olho (2.78 m na arena) e o monstro via o jogador
        // agachado atrás de um armário. Agora a mesa não esconde (a linha até a cabeça passa por cima), um armário
        // alto entre os dois esconde. Sem collider no alvo, volta ao teste antigo na altura do olho.
        [SerializeField, Min(0f)] private float _targetHeadOffset = 0.15f;

        // Steps de física que a visão do alvo aguenta sem linha livre antes de soltar (25 = 0.5 s): uma coluna ou um
        // batente cruzando a linha não apaga a perseguição. Durante isso a posição segue a real (o monstro "acompanha
        // a curva"); passou disso, perdeu de vista de verdade.
        [SerializeField, Min(0)] private int _trackGraceSteps = 25;

        [Header("-----Procura depois de perder de vista-----")]
        // Segundos depois de perder o alvo de vista em que o seeker fica em PROCURA: a observação de exploração
        // segue zerada e só suspeita e ping contam (GraphObservations). Passou disso sem ver, volta a ver o mapa
        // para explorar. 20 s = a perseguição que segura (3 s) + boa parte do alerta (10 s) + folga.
        [SerializeField, Min(0f)] private float _searchFocusSeconds = 20f;

        [Header("-----Captura-----")]
        // Distância planar (m) entre centros em que o hider conta como pego (com linha livre).
        // Não exige o cone nem trombar de frente.
        [SerializeField, Min(0.5f)] private float _captureDistance = 2.5f;

        [Header("-----Métricas-----")]
        // Steps de física sem ver para contar como "perdeu de vista" (Hunt/LostSight). 50 = 1 s: o raio até o centro
        // do hider pisca numa quina ou atrás de uma coluna, e cada piscada não é uma perda de verdade.
        [SerializeField, Min(1)] private int _lostSightSteps = 50;

        // O hider no treino, o jogador no modo de jogo (GraphArenaController.Target).
        private IGraphTarget _target;

        private NavGraph _graph;
        private int _lastSeenStep = int.MinValue;
        private int _lastVisibleStep = int.MinValue;
        private int _step;

        /// <summary>Vendo o hider agora (dentro do cone, com linha de visão livre).</summary>
        public bool IsSeeing { get; private set; }

        /// <summary>Alcance do cone (m). A GraphRoomMemory filtra por ele antes de gastar raycast.</summary>
        public float ViewDistance => _viewDistance;

        /// <summary>Já viu o hider neste episódio.</summary>
        public bool HasSeen { get; private set; }

        /// <summary>
        /// Perdeu o alvo de vista há menos de _searchFocusSeconds: procurando ELE, não o mapa. Falso vendo (aí é
        /// perseguição) e antes da primeira vista.
        /// </summary>
        public bool IsSearching => HasSeen && !IsSeeing && (_step - _lastSeenStep) * Time.fixedDeltaTime < _searchFocusSeconds;

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

        // Só para o TensorBoard (Hunt/Sightings, Hunt/LostSight, Hunt/SightToCatchSeconds); nada disso entra na
        // observação nem na recompensa.
        private int _unseenRun;
        private int _firstSeenStep;

        /// <summary>Vezes que passou a ver o hider no episódio (aquisições separadas por >= _lostSightSteps sem ver).</summary>
        public int EpisodeSightings { get; private set; }

        /// <summary>Vezes que perdeu o hider de vista por pelo menos _lostSightSteps.</summary>
        public int EpisodeLostSight { get; private set; }

        /// <summary>Segundos entre a primeira vez que viu e a captura; negativo se não pegou ou nunca viu.</summary>
        public float SightToCatchSeconds => Caught && HasSeen ? (_step - _firstSeenStep) * Time.fixedDeltaTime : -1f;

        // Direção da cabeça (SetViewDirection). Sem ela, o cone segue o corpo (seeker.forward).
        private Vector3 _viewForward;
        private bool _hasViewDirection;

        /// <summary>
        /// Avistou (não-vendo -> vendo, fora do cooldown) desde o último <see cref="ClearStepFlags"/>.
        /// </summary>
        public bool Spotted { get; private set; }

        /// <summary>
        /// Pegou o hider. Fica de pé até o ResetEpisode: é evento TERMINAL (o GraphRewardSystem paga, o Manager encerra).
        /// </summary>
        public bool Caught { get; private set; }

        // Visão e distância no ClearStepFlags anterior (a decisão anterior, com TakeActionsBetweenDecisions).
        // APROXIMAÇÃO por RECORDE (06/10): paga só os metros que batem a menor distância já vista no episódio.
        // Antes pagava todo metro encurtado vendo, sem descontar o que o hider abria fora da vista; com o hider que
        // foge de verdade (~12 perdas de vista por episódio) cada nova perseguição pagava de novo e perseguir rendia
        // mais que pegar (reward ~100 com a captura caindo de 0.95 para 0.86). Teto: distância da 1ª vista menos a
        // captura (~20-30 m).
        private float _closestSeenDistance = float.PositiveInfinity;
        private float _approachThisStep;

        /// <summary>Bateu o recorde de proximidade neste step (vendo).</summary>
        public bool HasApproach => _approachThisStep > 0f;

        /// <summary>Metros abaixo do recorde de proximidade do episódio desde o último ClearStepFlags (>= 0).</summary>
        public float ApproachDelta => _approachThisStep;

        public void Configure(NavGraph graph, IGraphTarget target)
        {
            _graph = graph;
            _target = target;

            // O corpo do alvo (CapsuleCollider do hider, CharacterController do jogador) para mirar cabeça e peito.
            _targetCollider = null;
            if (target is Component component && component != null)
                _targetCollider = component.GetComponentInChildren<Collider>();
        }

        private Collider _targetCollider;
        private bool _spottedThisEpisode;

        /// <summary>Para onde a cabeça olha (planar, no mundo); o cone passa a seguir isto, não o corpo.</summary>
        public void SetViewDirection(Vector3 forward)
        {
            forward.y = 0f;
            _hasViewDirection = forward.sqrMagnitude > 1e-6f;
            if (_hasViewDirection)
                _viewForward = forward.normalized;
        }

        public void ResetEpisode()
        {
            IsSeeing = false;
            HasSeen = false;
            LastSeenPosition = Vector3.zero;
            CurrentDistance = 0f;
            HiderVelocity = Vector3.zero;
            _hasPreviousSeenPosition = false;
            _hasViewDirection = false;
            Caught = false;
            _lastSeenStep = int.MinValue;
            _lastVisibleStep = int.MinValue;
            _closestSeenDistance = float.PositiveInfinity;
            _approachThisStep = 0f;
            _spottedThisEpisode = false;
            _step = 0;
            _unseenRun = 0;
            _firstSeenStep = 0;
            EpisodeSightings = 0;
            EpisodeLostSight = 0;
            ClearStepFlags();
        }

        /// <summary>Consome o "avistou" e guarda a distância desta decisão para a próxima aproximação.</summary>
        public void ClearStepFlags()
        {
            Spotted = false;
            _approachThisStep = 0f;
        }

        /// <summary>Desfaz a captura (modo de jogo: encostar no jogador antes de a partida começar).</summary>
        public void ForgetCaught() => Caught = false;

        /// <summary>Chamar a cada step de física, com o transform do seeker.</summary>
        public void Tick(Transform seeker)
        {
            _step++;

            bool visible = GraphTarget.IsLive(_target) && CanSeeTarget(seeker, _target.Position);
            if (visible)
                _lastVisibleStep = _step;

            bool seeing = visible || (IsSeeing && GraphTarget.IsLive(_target) && _step - _lastVisibleStep <= _trackGraceSteps);

            if (seeing)
            {
                if (!HasSeen)
                {
                    _firstSeenStep = _step;
                    EpisodeSightings++;
                }
                else if (_unseenRun >= _lostSightSteps)
                {
                    EpisodeSightings++;
                }

                _unseenRun = 0;

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

                // Avistar paga só a PRIMEIRA vista do episódio (06/10): reencontrar depois de deixar escapar virava
                // renda com o hider fugindo de verdade. Achar de novo vale pelo que leva à captura.
                if (!_spottedThisEpisode)
                {
                    Spotted = true;
                    _spottedThisEpisode = true;
                }

                if (CurrentDistance < _closestSeenDistance)
                {
                    if (!float.IsPositiveInfinity(_closestSeenDistance))
                        _approachThisStep += _closestSeenDistance - CurrentDistance;
                    _closestSeenDistance = CurrentDistance;
                }

                _lastSeenStep = _step;
            }
            else
            {
                // Sem posição anterior, a próxima aquisição não vira um salto de velocidade absurdo.
                HiderVelocity = Vector3.zero;
                _hasPreviousSeenPosition = false;

                _unseenRun++;
                if (HasSeen && _unseenRun == _lostSightSteps)
                    EpisodeLostSight++;
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
            Vector3 target = AtEyeLevel(hiderPosition, eye);
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
            Vector3 target = AtEyeLevel(point, eye);
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            if (distance > _viewDistance || distance < 1e-3f)
                return false;

            Vector3 forward = ViewForward(seeker);
            if (Vector3.Angle(forward, planar) > _viewAngle * 0.5f)
                return false;

            // Mesma máscara que valida as ligações do grafo; inclui a mobília, por isso _eyeHeight importa.
            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            return !Physics.Raycast(eye, delta.normalized, delta.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // Visão do ALVO: como CanSeePoint, mas já vendo o cone abre (_lockedViewAngle/_lockedViewDistance), colado
        // (_closeSenseDistance) não precisa de cone, e a linha livre vale para o centro OU um dos ombros.
        private bool CanSeeTarget(Transform seeker, Vector3 point)
        {
            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = AtEyeLevel(point, eye);
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            float range = IsSeeing ? Mathf.Max(_viewDistance, _lockedViewDistance) : _viewDistance;
            if (distance > range || distance < 1e-3f)
                return false;

            if (distance > _closeSenseDistance)
            {
                float angle = IsSeeing ? Mathf.Max(_viewAngle, _lockedViewAngle) : _viewAngle;
                if (Vector3.Angle(ViewForward(seeker), planar) > angle * 0.5f)
                    return false;
            }

            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            Vector3 side = Vector3.Cross(Vector3.up, planar / distance) * _targetHalfWidth;

            if (_targetCollider == null || !_targetCollider.enabled)
            {
                // Sem corpo para mirar: linha horizontal na altura do olho, como os nós.
                return IsClear(eye, target, walls)
                       || (_targetHalfWidth > 0f && (IsClear(eye, target + side, walls) || IsClear(eye, target - side, walls)));
            }

            // Cabeça e peito do alvo (mais os ombros na altura do peito); basta um raio livre.
            Bounds body = _targetCollider.bounds;
            Vector3 chest = new Vector3(point.x, body.center.y, point.z);
            Vector3 head = new Vector3(point.x, Mathf.Max(body.center.y, body.max.y - _targetHeadOffset), point.z);
            return IsClear(eye, head, walls)
                   || IsClear(eye, chest, walls)
                   || (_targetHalfWidth > 0f && (IsClear(eye, chest + side, walls) || IsClear(eye, chest - side, walls)));
        }

        private static bool IsClear(Vector3 eye, Vector3 target, LayerMask walls)
        {
            Vector3 ray = target - eye;
            return !Physics.Raycast(eye, ray.normalized, ray.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // O ponto olhado fica na ALTURA DO OLHO, sobre o X/Z do ponto: visão horizontal. Assim não importa em que
        // altura o ponto mora (nó do grafo, que no mapa v4 fica ~1.8 m acima do piso, ou pé do alvo, no piso): o
        // monstro olha reto, e mobília mais alta que o olho tapa. Com o seeker no nível do nó (prefabs antigos) dá o
        // mesmo de antes; mapa de um andar só.
        private static Vector3 AtEyeLevel(Vector3 point, Vector3 eye) => new Vector3(point.x, eye.y, point.z);

        private Vector3 ViewForward(Transform seeker)
        {
            if (_hasViewDirection)
                return _viewForward;

            Vector3 forward = new Vector3(seeker.forward.x, 0f, seeker.forward.z);
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        // Gizmo (azul-claro): cone; em Play, os raios até cabeça e peito do alvo (azul-claro = livre, cinza =
        // bloqueado), um círculo no alvo enquanto vê e X na última posição vista quando não vê.
        private void OnDrawGizmosSelected()
        {
            Vector3 eye = transform.position + Vector3.up * _eyeHeight;
            Vector3 forward = ViewForward(transform);

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            Quaternion left = Quaternion.Euler(0f, -_viewAngle * 0.5f, 0f);
            Quaternion right = Quaternion.Euler(0f, _viewAngle * 0.5f, 0f);
            Gizmos.DrawLine(eye, eye + left * forward * _viewDistance);
            Gizmos.DrawLine(eye, eye + right * forward * _viewDistance);

            if (!Application.isPlaying)
                return;

            // Os raios de verdade até o CORPO do alvo (cabeça e peito): azul-claro cheio = livre, cinza = bloqueado.
            // Não diz se está no cone/alcance, só se a linha passa; para depurar "por que não me vê".
            if (GraphTarget.IsLive(_target) && _targetCollider != null && _targetCollider.enabled)
            {
                LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
                Bounds body = _targetCollider.bounds;
                Vector3 point = _target.Position;
                Vector3 chest = new Vector3(point.x, body.center.y, point.z);
                Vector3 head = new Vector3(point.x, Mathf.Max(body.center.y, body.max.y - _targetHeadOffset), point.z);
                foreach (Vector3 aim in new[] { head, chest })
                {
                    bool clear = walls.value == 0 || IsClear(eye, aim, walls);
                    Gizmos.color = clear ? new Color(0.4f, 0.7f, 1f, 1f) : new Color(0.5f, 0.5f, 0.5f, 0.9f);
                    Gizmos.DrawLine(eye, aim);
                }

                Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            }

            if (IsSeeing && GraphTarget.IsLive(_target))
            {
                Gizmos.DrawWireSphere(_target.Position, 0.5f);
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
