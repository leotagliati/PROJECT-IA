using Assets.Scripts.Seeker;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Como o seeker do grafo se move (v5): o ESTADO DE ALERTA decide a velocidade; a ação só diz para onde ir
    /// (e se fica parado). O <see cref="SeekerMovementSystem"/> só executa no Rigidbody (MoveFacing).
    ///
    ///   ESTADOS (a política vê qual está ativo e quanto falta, observação [26] e [32..33]):
    ///     Patrulha     sem pista                                        -> 7 m/s (um pouco acima do andar do jogador, 6)
    ///     Alerta       ouviu um ping ou perdeu o alvo de vista há pouco  -> 8.5 m/s, por _alertSeconds
    ///     Perseguição  está VENDO o alvo                                 -> 10 m/s (logo abaixo da corrida do jogador, 10.2)
    ///   Não há corrida por ação nem fôlego: ver o jogador É o que deixa o monstro rápido. Ao perder de vista,
    ///   cai para Alerta (sabe a região, não o ponto), e depois de _alertSeconds sem pista volta à Patrulha.
    ///
    ///   - |andar| abaixo de _moveDeadzone = parado; acima, anda na velocidade do estado (para casar com os clipes).
    ///   - INÉRCIA leve (acelera a _acceleration, freia a _braking) e GIRO por estado (mais lento na perseguição:
    ///     curva larga = sensação de peso, e o jogador escapa cortando quina).
    ///   - PAREDE CUSTA VELOCIDADE: o que a colisão tirou do corpo não volta de graça; reacelera.
    ///   - PESCOÇO: o olhar [2..3] vira a cabeça até _neckAngle de cada lado; o cone de visão vai junto.
    /// Expõe State, Speed, IsMoving e HeadYaw para a animação (GraphAnimationSystem; só leitura).
    /// </summary>
    public class GraphLocomotion : MonoBehaviour
    {
        public enum Awareness
        {
            Patrol = 0,
            Alert = 1,
            Chase = 2,
        }

        [Header("-----Velocidade por estado (m/s)-----")]
        // 7 (05/10, ~21M; era 6 = o andar do jogador, PlayerDummy moveSpeed 6): rondando parecia lento demais.
        // Um pouco acima do andar: o jogador andando é alcançado devagar; agachado (2.7) nem se fala.
        [SerializeField, Min(0f)] private float _patrolSpeed = 7f;

        // Entre patrulhar e correr: sabe a região do alvo (ping, ou acabou de perdê-lo de vista). 8.5 (era 8).
        [SerializeField, Min(0f)] private float _alertSpeed = 8.5f;

        // 10 (era 10.2 = moveSpeed x sprintMultiplier do PlayerDummy, 6 x 1.7): vendo o jogador, corre quase como
        // ele, mas sem fôlego. O jogador correndo abre 0.2 m/s enquanto tem os 10 s de fôlego; depois, é alcançado.
        // Mudou a velocidade do jogador: mude aqui e GraphHider._speed/_walkFraction (hider_speed no currículo).
        [SerializeField, Min(0f)] private float _chaseSpeed = 10f;

        [Header("-----Estado de alerta-----")]
        // Segundos em Alerta depois da última pista (perdeu de vista ou um ping começou).
        [SerializeField, Min(0f)] private float _alertSeconds = 10f;

        [Header("-----Movimento-----")]
        // |andar| (0..1) abaixo disto = parado.
        [SerializeField, Range(0f, 0.5f)] private float _moveDeadzone = 0.1f;

        // m/s² ganhando velocidade: 0 -> perseguição em ~0.5 s.
        [SerializeField, Min(0.1f)] private float _acceleration = 20f;

        // m/s² perdendo velocidade: da perseguição, para em ~1.3 m.
        [SerializeField, Min(0.1f)] private float _braking = 40f;

        [Header("-----Giro do corpo (graus/s)-----")]
        // Patrulha e Alerta: uma curva de 90 graus o leva ~0.3 m para fora (v / 2w).
        [SerializeField, Min(1f)] private float _turnSpeed = 540f;

        // Perseguição: ~0.8 m para fora numa curva de 90 graus.
        [SerializeField, Min(1f)] private float _chaseTurnSpeed = 360f;

        [Header("-----Corpo-----")]
        // Altura travada, sem gravidade (como o NodeTraining5): o corpo não sobe em rodapé nem quica em quina.
        // Vale sobre o Lock Height do SeekerMovementSystem.
        [SerializeField] private bool _lockHeight = true;

        [Header("-----Pescoço-----")]
        // Quanto a cabeça vira para cada lado do corpo (graus). O cone de visão (100°) vai junto.
        [SerializeField, Range(0f, 180f)] private float _neckAngle = 60f;

        // Velocidade da cabeça (graus/s).
        [SerializeField, Min(1f)] private float _neckTurnSpeed = 360f;

        // |olhar| abaixo disto = cabeça volta para a frente.
        [SerializeField, Range(0f, 1f)] private float _lookDeadzone = 0.1f;

        private SeekerMovementSystem _body;
        private float _headYaw;
        private Vector3 _bodyForward = Vector3.forward;
        private float _lastApplied;
        private float _alertLeft;
        private bool _heardThisStep;

        // Métricas do episódio (Movement/ChaseFraction, AlertFraction, MeanSpeed).
        private int _steps;
        private int _chaseSteps;
        private int _alertSteps;
        private float _speedSum;

        public Awareness State { get; private set; }

        /// <summary>Andando (|andar| acima da zona morta) neste step.</summary>
        public bool IsMoving { get; private set; }

        /// <summary>Velocidade de deslocamento atual (m/s), com inércia e perdas na parede.</summary>
        public float Speed { get; private set; }

        /// <summary>Velocidade do estado atual (m/s): o teto que o corpo busca andando.</summary>
        public float StateSpeed => State == Awareness.Chase ? _chaseSpeed : State == Awareness.Alert ? _alertSpeed : _patrolSpeed;

        /// <summary>A maior velocidade (perseguição): normaliza as velocidades na observação.</summary>
        public float MaxSpeed => Mathf.Max(_chaseSpeed, 0.1f);

        public float PatrolSpeed => _patrolSpeed;

        /// <summary>Fração do Alerta que ainda resta (1 = pista agora, 0 = sem alerta).</summary>
        public float AlertRemaining => _alertSeconds > 0f ? Mathf.Clamp01(_alertLeft / _alertSeconds) : 0f;

        /// <summary>Velocidade REAL do corpo no plano (m/s), depois da física.</summary>
        public Vector3 Velocity => _body != null ? _body.PlanarVelocity : Vector3.zero;

        /// <summary>Para onde a CABEÇA olha (planar, no mundo): o cone de visão.</summary>
        public Vector3 ViewDirection => Quaternion.Euler(0f, _headYaw, 0f) * _bodyForward;

        /// <summary>Giro da cabeça em relação ao corpo (graus, + = direita). Para a animação.</summary>
        public float HeadYaw => _headYaw;

        public float ChaseFraction => _steps > 0 ? (float)_chaseSteps / _steps : 0f;

        public float AlertFraction => _steps > 0 ? (float)_alertSteps / _steps : 0f;

        public float MeanSpeed => _steps > 0 ? _speedSum / _steps : 0f;

        public void Configure(SeekerMovementSystem body)
        {
            _body = body;
            if (_body != null)
                _body.SetHeightLocked(_lockHeight);
        }

        public void ResetEpisode(Vector3 bodyForward)
        {
            _headYaw = 0f;
            _bodyForward = Flat(bodyForward, Vector3.forward);
            State = Awareness.Patrol;
            IsMoving = false;
            Speed = 0f;
            _lastApplied = 0f;
            _alertLeft = 0f;
            _heardThisStep = false;
            _steps = 0;
            _chaseSteps = 0;
            _alertSteps = 0;
            _speedSum = 0f;
            if (_body != null)
                _body.ResetMovement();
        }

        /// <summary>Um ping começou (o alvo fez barulho): entra em Alerta no próximo Drive, se não estiver vendo.</summary>
        public void NotifyHeard() => _heardThisStep = true;

        /// <summary>
        /// Atualiza o estado sem mover (chamado no step de física, depois da visão e do ping), para a observação e a
        /// recompensa do mesmo step já lerem o estado certo.
        /// </summary>
        public void UpdateAwareness(bool seeingTarget)
        {
            float dt = Time.fixedDeltaTime;
            if (seeingTarget)
            {
                State = Awareness.Chase;
                _alertLeft = _alertSeconds;
            }
            else
            {
                if (_heardThisStep)
                    _alertLeft = _alertSeconds;
                else
                    _alertLeft = Mathf.Max(0f, _alertLeft - dt);

                State = _alertLeft > 0f ? Awareness.Alert : Awareness.Patrol;
            }

            _heardThisStep = false;
        }

        /// <summary>Um step de física: velocidade do estado com inércia, corpo e cabeça.</summary>
        /// <param name="move">Ação de andar (mundo, X/Z); só a direção e se passa da zona morta importam.</param>
        /// <param name="look">Ação de olhar (mundo, X/Z); vira a cabeça até _neckAngle.</param>
        public void Drive(Vector3 move, Vector3 look)
        {
            float dt = Time.fixedDeltaTime;
            Vector3 flat = new(move.x, 0f, move.z);
            IsMoving = flat.magnitude >= _moveDeadzone;
            float target = IsMoving ? StateSpeed : 0f;

            // O que a física tirou no step anterior (parede, quina) sai da velocidade: bater custa reacelerar.
            if (_body != null && _lastApplied > 0f)
            {
                float actual = Vector3.Dot(_body.PlanarVelocity, _bodyForward);
                float lost = _lastApplied - Mathf.Max(0f, actual);
                if (lost > 0.05f)
                    Speed = Mathf.Max(0f, Speed - lost);
            }

            Speed = Mathf.MoveTowards(Speed, target, (target > Speed ? _acceleration : _braking) * dt);

            // Parado, mas ainda freando: segue para onde o corpo aponta até a velocidade zerar.
            Vector3 heading = IsMoving ? flat : _bodyForward;
            float turnSpeed = State == Awareness.Chase ? _chaseTurnSpeed : _turnSpeed;
            _lastApplied = 0f;
            if (_body != null)
                _bodyForward = Flat(_body.MoveFacing(heading, Speed, turnSpeed, out _lastApplied), _bodyForward);

            TurnHead(look, dt);

            _steps++;
            _speedSum += Speed;
            if (State == Awareness.Chase)
                _chaseSteps++;
            else if (State == Awareness.Alert)
                _alertSteps++;
        }

        /// <summary>Para tudo (modo de jogo fora da partida, ou pegou o jogador).</summary>
        public void Stop()
        {
            Speed = 0f;
            _lastApplied = 0f;
            IsMoving = false;
            if (_body != null)
                _body.ResetMovement();
        }

        private void TurnHead(Vector3 look, float dt)
        {
            Vector3 flatLook = new(look.x, 0f, look.z);
            float target = 0f;
            if (flatLook.magnitude >= _lookDeadzone)
            {
                float angle = Vector3.SignedAngle(_bodyForward, flatLook, Vector3.up);

                // Olhar quase para trás: fica do lado em que a cabeça já está, senão o sinal do ângulo troca
                // em 180° e a cabeça varre de um limite ao outro sem a política ter pedido.
                if (Mathf.Abs(angle) > 170f && Mathf.Abs(_headYaw) > 1f)
                    angle = Mathf.Sign(_headYaw) * Mathf.Abs(angle);

                target = Mathf.Clamp(angle, -_neckAngle, _neckAngle);
            }

            _headYaw = Mathf.MoveTowards(_headYaw, target, _neckTurnSpeed * dt);
        }

        private static Vector3 Flat(Vector3 direction, Vector3 fallback)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : fallback;
        }

        // Gizmo azul-claro mais forte que o do cone: a direção da cabeça.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying)
                return;

            Vector3 eye = transform.position + Vector3.up * 1.4f;
            Gizmos.color = new Color(0.2f, 0.5f, 1f, 1f);
            Gizmos.DrawLine(eye, eye + ViewDirection * 3f);
        }
    }
}
