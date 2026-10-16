using Assets.Scripts.Graph;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// HABILIDADE DO CLONE da v9 (porte da GraphCloneAbility da v7 para o mundo só NavMesh). O cérebro pede pelo ramo
    /// discreto 0 (nada / andarilho / vigia) e a MÁSCARA só libera com a recarga pronta e a lição permitindo
    /// (clone_enabled). Pedido aceito, o monstro CANALIZA: fica _channelSeconds parado (o Manager zera o andar; o
    /// olhar continua) e só então o clone nasce onde ele está. É a troca que dá decisão ao uso: colado no alvo não vale
    /// parar; tendo perdido o alvo, vale.
    ///   ANDARILHO  sai na direção do olhar e segue o corredor (a saída mais livre e mais para a frente ao bater numa
    ///              parede), na velocidade de patrulha.
    ///   VIGIA      fica onde o monstro estava, varrendo o olhar ±_sentrySweepDegrees em volta da direção do olhar.
    /// O clone SÓ VÊ, nunca pega. Vendo o alvo, não vira "visão" do monstro (como na v7): vira PISTA forte na
    /// FreeTrackSense (posição e para onde o alvo ia), que dá Alerta, foco na caça e o caminho até lá; e um evento
    /// "clone viu". Assim a perseguição de verdade (cone travado, aproximação paga) continua sendo só do monstro.
    /// Vive _durationSeconds; a recarga (_cooldownSeconds) conta de quando some; um por vez. Usar não paga nada: só
    /// vale se ajudar a pegar. Um por agente; o Manager chama Tick a cada step de física.
    /// </summary>
    public class FreeCloneAbility : MonoBehaviour
    {
        public enum Mode
        {
            None = 0,
            Wander = 1,
            Sentry = 2,
        }

        /// <summary>Tamanho do ramo discreto do clone (nada, andarilho, vigia).</summary>
        public const int BranchSize = 3;

        [Header("-----Tempo (decisão do Arthur, 12/10)-----")]
        [SerializeField, Min(1f)] private float _durationSeconds = 20f;

        // Contada de quando o clone SOME: 20 s vivo + 120 s = no máximo um clone a cada ~2min20s.
        [SerializeField, Min(0f)] private float _cooldownSeconds = 120f;

        // Recarga no começo do episódio/partida: sem isso o primeiro reflexo seria soltar o clone no spawn.
        [SerializeField, Min(0f)] private float _initialCooldownSeconds = 20f;

        // Parado canalizando antes do clone nascer.
        [SerializeField, Min(0f)] private float _channelSeconds = 1.5f;

        [Header("-----Visão do clone-----")]
        // Os mesmos números da visão do alvo do monstro (GraphHiderPerception), sem o "travar".
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;
        [SerializeField, Min(1f)] private float _viewDistance = 35f;
        [SerializeField, Min(0.1f)] private float _eyeHeight = 2.5f;

        // Pista entregue ao monstro quando o clone vê o alvo: força, duração e no máximo uma a cada isto (s).
        [SerializeField, Range(0f, 1f)] private float _reportStrength = 1f;
        [SerializeField, Min(0.1f)] private float _reportLifetime = 8f;
        [SerializeField, Min(0.05f)] private float _reportInterval = 0.5f;

        [Header("-----Andarilho-----")]
        // Velocidade (m/s) = a de patrulha do monstro: é um batedor, não um caçador.
        [SerializeField, Min(0.5f)] private float _wanderSpeed = 7f;

        // Alcance (m) das sondas que escolhem para onde seguir quando o trecho reto acaba.
        [SerializeField, Min(2f)] private float _probeDistance = 12f;

        // Trecho livre mínimo (m) para uma direção valer; menos que isso é parede, volta.
        [SerializeField, Min(0.5f)] private float _minLeg = 1.5f;

        [Header("-----Vigia-----")]
        [SerializeField, Range(0f, 180f)] private float _sentrySweepDegrees = 45f;
        [SerializeField, Min(0.5f)] private float _sentrySweepPeriod = 6f;

        [Header("-----Visual (só no jogo)-----")]
        // Corpo do clone no jogo (cópia do modelo do monstro sem collider nem agente). Vazio = invisível; no treino
        // nunca é criado.
        [SerializeField] private GameObject _visualPrefab;

        [Header("-----NavMesh-----")]
        [SerializeField, Min(0.1f)] private float _navMeshSnap = 2f;

        private static readonly float[] CandidateAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };

        private bool _allowed;
        private int _activeStepsLeft;
        private int _cooldownStepsLeft;
        private int _channelStepsLeft;
        private Mode _pendingMode;
        private Vector3 _pendingFrom;
        private Vector3 _pendingLook;
        private Vector3 _legEnd;
        private Vector3 _baseForward;
        private float _sweepTime;
        private float _lastReport = float.NegativeInfinity;
        private GameObject _visual;

        /// <summary>O clone está no mapa agora.</summary>
        public bool IsActive { get; private set; }

        /// <summary>O monstro está parado canalizando o clone (o Manager zera o andar).</summary>
        public bool IsChanneling => _channelStepsLeft > 0;

        public Mode CurrentMode { get; private set; }

        /// <summary>Pé do clone (no NavMesh).</summary>
        public Vector3 Feet { get; private set; }

        /// <summary>Para onde o clone olha (planar, unitário).</summary>
        public Vector3 Forward { get; private set; } = Vector3.forward;

        /// <summary>O clone viu o alvo neste step (a pista já foi entregue ao monstro pelo Manager).</summary>
        public bool SeesTarget { get; private set; }

        /// <summary>Pode pedir agora (a máscara do ramo discreto lê isto).</summary>
        public bool IsReady => _allowed && !IsActive && !IsChanneling && _cooldownStepsLeft <= 0;

        /// <summary>A lição permite o clone (clone_enabled).</summary>
        public bool Allowed => _allowed;

        /// <summary>Recarga que falta (0 = pronto, 1 = acabou de sumir).</summary>
        public float CooldownRemaining => !_allowed ? 1f
            : CooldownSteps > 0 ? Mathf.Clamp01((float)_cooldownStepsLeft / CooldownSteps) : 0f;

        /// <summary>Progresso da canalização (0 = não canaliza, 1 = o clone vai nascer).</summary>
        public float ChannelProgress => IsChanneling && ChannelSteps > 0 ? 1f - (float)_channelStepsLeft / ChannelSteps : 0f;

        /// <summary>Vida que resta ao clone ativo (1 = acabou de nascer).</summary>
        public float LifeRemaining => IsActive && DurationSteps > 0 ? Mathf.Clamp01((float)_activeStepsLeft / DurationSteps) : 0f;

        // Métricas do episódio (Clone/Uses, Clone/SawTarget, Clone/Reports).
        public int EpisodeUses { get; private set; }
        public bool EpisodeSawTarget { get; private set; }
        public int EpisodeReports { get; private set; }

        private int DurationSteps => Mathf.Max(1, Mathf.RoundToInt(_durationSeconds / Time.fixedDeltaTime));
        private int CooldownSteps => Mathf.Max(0, Mathf.RoundToInt(_cooldownSeconds / Time.fixedDeltaTime));
        private int ChannelSteps => Mathf.Max(0, Mathf.RoundToInt(_channelSeconds / Time.fixedDeltaTime));

        private static bool IsTraining => Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;

        public void ResetEpisode(bool allowed)
        {
            Despawn();
            _allowed = allowed;
            _channelStepsLeft = 0;
            _cooldownStepsLeft = Mathf.RoundToInt(_initialCooldownSeconds / Time.fixedDeltaTime);
            _lastReport = float.NegativeInfinity;
            SeesTarget = false;
            EpisodeUses = 0;
            EpisodeSawTarget = false;
            EpisodeReports = 0;
        }

        /// <summary>
        /// Pede o clone (se pronto): começa a canalização. <paramref name="look"/> = para onde a cabeça do monstro olha
        /// (o andarilho parte nessa direção, o vigia olha para lá). False se não estava pronto.
        /// </summary>
        public bool TryActivate(Mode mode, Vector3 from, Vector3 look)
        {
            if (mode == Mode.None || !IsReady)
                return false;

            _pendingMode = mode;
            _pendingFrom = from;
            _pendingLook = look;
            _channelStepsLeft = Mathf.Max(1, ChannelSteps);
            return true;
        }

        /// <summary>
        /// Um step de física: canaliza, anda/varre, olha o alvo, conta vida e recarga. <paramref name="from"/> =
        /// posição atual do monstro (o clone nasce nela no fim da canalização). True quando o clone viu o alvo e já
        /// passou o intervalo entre pistas: o Manager entrega a pista ao monstro.
        /// </summary>
        public bool Tick(Vector3 from, IGraphTarget target, Collider targetBody, LayerMask walls)
        {
            SeesTarget = false;
            if (IsChanneling)
            {
                _pendingFrom = from;
                if (--_channelStepsLeft == 0)
                    Spawn();
                return false;
            }

            if (!IsActive)
            {
                if (_cooldownStepsLeft > 0)
                    _cooldownStepsLeft--;
                return false;
            }

            if (--_activeStepsLeft <= 0)
            {
                Despawn();
                return false;
            }

            if (CurrentMode == Mode.Wander)
                StepWander(Time.fixedDeltaTime);
            else
                StepSentry(Time.fixedDeltaTime);

            if (_visual != null)
                _visual.transform.SetPositionAndRotation(Feet, Quaternion.LookRotation(Forward, Vector3.up));

            SeesTarget = GraphTarget.IsLive(target) && CanSee(target.Position, targetBody, walls);
            if (!SeesTarget)
                return false;

            EpisodeSawTarget = true;
            if (Time.time - _lastReport < _reportInterval)
                return false;

            _lastReport = Time.time;
            EpisodeReports++;
            return true;
        }

        public float ReportStrength => _reportStrength;
        public float ReportLifetime => _reportLifetime;

        private void Spawn()
        {
            if (!NavMesh.SamplePosition(_pendingFrom, out NavMeshHit hit, _navMeshSnap, NavMesh.AllAreas))
            {
                // Sem chão ali (raro: o monstro encostado fora da borda): perde o uso, sem recarga cheia.
                _cooldownStepsLeft = Mathf.RoundToInt(_initialCooldownSeconds / Time.fixedDeltaTime);
                return;
            }

            Vector3 look = _pendingLook;
            look.y = 0f;
            _baseForward = look.sqrMagnitude > 1e-6f ? look.normalized : Vector3.forward;
            Forward = _baseForward;
            Feet = hit.position;
            CurrentMode = _pendingMode;
            IsActive = true;
            _activeStepsLeft = DurationSteps;
            _sweepTime = 0f;
            EpisodeUses++;

            if (CurrentMode == Mode.Wander)
                PlanLeg(_baseForward);

            if (!IsTraining && _visualPrefab != null)
                _visual = Instantiate(_visualPrefab, Feet, Quaternion.LookRotation(Forward, Vector3.up));
        }

        // Cone, alcance e linha livre do olho do clone até a cabeça ou o peito do alvo (como o monstro mira).
        private bool CanSee(Vector3 targetPosition, Collider targetBody, LayerMask walls)
        {
            Vector3 eye = Feet + Vector3.up * _eyeHeight;
            var planar = new Vector2(targetPosition.x - Feet.x, targetPosition.z - Feet.z);
            float distance = planar.magnitude;
            if (distance > _viewDistance)
                return false;

            var forward = new Vector2(Forward.x, Forward.z);
            if (distance > 1e-3f && Vector2.Dot(planar / distance, forward) < Mathf.Cos(_viewAngle * 0.5f * Mathf.Deg2Rad))
                return false;

            if (targetBody != null && targetBody.enabled)
            {
                Bounds body = targetBody.bounds;
                var chest = new Vector3(targetPosition.x, body.center.y, targetPosition.z);
                var head = new Vector3(targetPosition.x, Mathf.Max(body.center.y, body.max.y - 0.15f), targetPosition.z);
                return IsClear(eye, head, walls) || IsClear(eye, chest, walls);
            }

            return IsClear(eye, new Vector3(targetPosition.x, eye.y, targetPosition.z), walls);
        }

        private static bool IsClear(Vector3 eye, Vector3 target, LayerMask walls)
        {
            Vector3 ray = target - eye;
            return !Physics.Raycast(eye, ray.normalized, ray.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        private void StepWander(float dt)
        {
            Vector3 delta = _legEnd - Feet;
            delta.y = 0f;
            float distance = delta.magnitude;
            float step = _wanderSpeed * dt;
            if (distance <= step + 0.05f)
            {
                Feet = _legEnd;
                PlanLeg(Forward);
                return;
            }

            Vector3 next = Feet + delta / distance * step;
            next.y = Mathf.Lerp(Feet.y, _legEnd.y, step / distance);
            Feet = next;
            Forward = delta / distance;
        }

        // Próximo trecho reto (o da v7): das direções candidatas em volta da atual, a mais livre pesando a frente
        // (seguir o corredor, não ficar indo e voltando). Tudo bloqueado = meia-volta. O trecho para 0.5 m antes do
        // fim do NavMesh, então o clone nunca sai do chão andável.
        private void PlanLeg(Vector3 heading)
        {
            heading.y = 0f;
            heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.forward;

            float bestScore = -1f;
            Vector3 bestEnd = Feet;
            Vector3 bestDirection = -heading;
            foreach (float angle in CandidateAngles)
            {
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * heading;
                Vector3 target = Feet + direction * _probeDistance;
                float free = NavMesh.Raycast(Feet, target, out NavMeshHit hit, NavMesh.AllAreas) ? hit.distance : _probeDistance;

                free -= 0.5f;
                if (free < _minLeg)
                    continue;

                float score = free * (1.5f + Mathf.Cos(angle * Mathf.Deg2Rad));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEnd = Feet + direction * free;
                    bestDirection = direction;
                }
            }

            if (bestScore < 0f)
            {
                Vector3 back = Feet - heading * _probeDistance;
                bestEnd = NavMesh.Raycast(Feet, back, out NavMeshHit hit, NavMesh.AllAreas) ? hit.position : back;
                bestDirection = -heading;
            }

            if (NavMesh.SamplePosition(bestEnd, out NavMeshHit snapped, 1f, NavMesh.AllAreas))
                bestEnd = snapped.position;

            _legEnd = bestEnd;
            Forward = bestDirection;
        }

        private void StepSentry(float dt)
        {
            _sweepTime += dt;
            float angle = _sentrySweepDegrees * Mathf.Sin(_sweepTime * 2f * Mathf.PI / _sentrySweepPeriod);
            Forward = Quaternion.Euler(0f, angle, 0f) * _baseForward;
        }

        private void Despawn()
        {
            if (!IsActive)
                return;

            IsActive = false;
            CurrentMode = Mode.None;
            _cooldownStepsLeft = CooldownSteps;
            if (_visual != null)
            {
                Destroy(_visual);
                _visual = null;
            }
        }

        private void OnDisable() => Despawn();

        // Gizmo ciano: o clone, para onde olha e o alcance da visão.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || !IsActive)
                return;

            Gizmos.color = SeesTarget ? new Color(1f, 0.3f, 0.3f, 0.9f) : new Color(0.2f, 0.9f, 0.9f, 0.9f);
            Gizmos.DrawWireSphere(Feet + Vector3.up, 0.6f);
            Gizmos.DrawLine(Feet + Vector3.up * _eyeHeight, Feet + Vector3.up * _eyeHeight + Forward * 4f);
        }
    }
}
