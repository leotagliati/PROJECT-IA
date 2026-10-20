using System;
using Assets.Scripts.Graph;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Seeker da perseguição final: o mesmo modelo, sem Agent nem IA, posicionado atrás da porta e DESATIVADO na
    /// cena. O <see cref="EscapeCutscene"/> o comanda: <see cref="Begin"/> faz ele andar até uma marca, virar para o
    /// forward dela e só então perseguir o alvo; <see cref="ResetTo"/> o devolve desativado ao ponto de partida.
    ///
    /// Os sistemas cosméticos são os do monstro de verdade (o papel que o FreeExplorerManager faz com eles): a
    /// perseguição é o <see cref="SeekerChaseState"/> recebendo "vendo o jogador", e é isso que liga cor, estática,
    /// música, susto e o SeenPostFX — que procuram Seekers inativos também, porque este começa desligado.
    ///
    /// Move por transform, no plano: é um corredor reto, sem pathfinding. O toque é a distância entre os raios do
    /// CapsuleCollider deste e do CharacterController do alvo, que é barrado pelo collider e não chega mais perto.
    /// </summary>
    public class SeekerCutscenePuppet : MonoBehaviour
    {
        private enum Mode { Idle, Walking, Turning, Chasing, Stopped }

        [Header("References")]
        [SerializeField] private GraphAnimationSystem _animationSystem;
        [SerializeField] private SeekerAudioSystem _audioSystem;
        [SerializeField] private SeekerStaticVisual _staticVisual;
        [SerializeField] private SeekerChaseState _chaseState;
        [SerializeField] private CapsuleCollider _body;

        // Giro rápido: chega na marca e vira para o corredor quase num golpe — o jogador já está olhando, e um giro
        // lento lê como o monstro "só rodando".
        [Header("Entrance")]
        [SerializeField, Min(0.1f)] private float _walkSpeed = 4f;
        [SerializeField, Min(1f)] private float _turnSpeed = 540f;

        // Logo abaixo dos 8,5 m/s da corrida do player: correndo de cara, dá para fugir, mas sem folga. O de IA corre
        // 11,5 (8 na End Game).
        [Header("Chase")]
        [SerializeField, Min(0.1f)] private float _chaseSpeed = 8f;
        [SerializeField, Min(1f)] private float _chaseTurnSpeed = 360f;
        [SerializeField, Min(0f)] private float _catchMargin = 0.15f;

        // Chegou na marca: abaixo disto a direção até ela fica instável.
        private const float ArriveTolerance = 0.05f;

        private Mode _mode = Mode.Idle;
        private Transform _mark;
        private Transform _target;
        private CharacterController _targetBody;
        private bool _initialized;

        /// <summary>Terminou o giro na marca e começou a perseguir.</summary>
        public event Action ChaseStarted;

        /// <summary>Encostou no alvo durante a perseguição. Ele para; quem decide o que vem depois é quem ouve.</summary>
        public event Action Caught;

        private void Awake()
        {
            _animationSystem = Find(_animationSystem);
            _audioSystem = Find(_audioSystem);
            _staticVisual = Find(_staticVisual);
            _chaseState = Find(_chaseState);
            _body = Find(_body);
        }

        // Mesma ordem do FreeExplorerManager.Initialize: o visual lê o SeekerChaseState, que já existe desde o Awake.
        private void Start()
        {
            if (_animationSystem != null)
                _animationSystem.Initialize();
            if (_audioSystem != null)
                _audioSystem.Initialize();
            if (_staticVisual != null)
                _staticVisual.Initialize();

            _initialized = true;
        }

        private void OnEnable()
        {
            GameManager.StateChanged += HandleGameState;

            // Desativado, as AudioSources da estática param e não voltam sozinhas. Na primeira ativação quem liga é o
            // Initialize do Start.
            if (_initialized && _audioSystem != null)
                _audioSystem.ResetEpisode();
        }

        private void OnDisable() => GameManager.StateChanged -= HandleGameState;

        // Na derrota para e cala na hora, como o de IA. Na vitória NÃO: ele continua vindo atrás do jogador durante o
        // clarão (o som desce junto com a imagem, pelo AudioProvider.Fade), e quem o congela no fim é o EscapeCutscene,
        // pelo Freeze.
        private void HandleGameState(GameState state)
        {
            if (state == GameState.Lost)
                Freeze();
        }

        /// <summary>Para onde está e cala. Fim de verdade: depois disto só um <see cref="ResetTo"/> o move de novo.</summary>
        public void Freeze()
        {
            _mode = Mode.Stopped;
            if (_audioSystem != null)
                _audioSystem.Silence();
        }

        /// <summary>Ativa parado onde está (idle, cor e estática de patrulha), esperando o <see cref="Begin"/>.</summary>
        public void Appear()
        {
            _mode = Mode.Idle;
            gameObject.SetActive(true);
        }

        /// <summary>Ativa, anda até <paramref name="mark"/>, vira para o forward dela e persegue <paramref name="target"/>.</summary>
        public void Begin(Transform mark, Transform target)
        {
            _mark = mark;
            _target = target;
            _targetBody = target != null ? target.GetComponent<CharacterController>() : null;

            gameObject.SetActive(true);
            _mode = mark != null ? Mode.Walking : Mode.Chasing;
            if (_mode == Mode.Chasing)
                ChaseStarted?.Invoke();
        }

        /// <summary>Para, esquece a perseguição e volta desativado para a pose dada (a de onde foi posto na cena).</summary>
        public void ResetTo(Vector3 position, Quaternion rotation)
        {
            _mode = Mode.Idle;

            if (_chaseState != null)
                _chaseState.ResetEpisode();
            if (_animationSystem != null && _initialized)
                _animationSystem.ResetEpisode();

            transform.SetPositionAndRotation(position, rotation);
            gameObject.SetActive(false);
        }

        private void Update()
        {
            Vector3 move = Vector3.zero;
            bool chasing = false;

            switch (_mode)
            {
                case Mode.Walking:
                    move = StepTowards(_mark.position, _walkSpeed, _turnSpeed, out bool arrived);
                    if (arrived)
                        _mode = Mode.Turning;
                    break;

                case Mode.Turning:
                    if (TurnTowards(YawOf(_mark.forward), _turnSpeed))
                    {
                        _mode = Mode.Chasing;
                        ChaseStarted?.Invoke();
                    }
                    break;

                case Mode.Chasing:
                    chasing = true;
                    if (_target == null)
                        break;

                    if (Touching())
                    {
                        // Depois da vitória ninguém mais é pego: ele só para colado, em vez de atravessar o jogador
                        // que anda para o clarão.
                        if (GameManager.Current != null && GameManager.Current.IsOver)
                            break;

                        _mode = Mode.Stopped;
                        Caught?.Invoke();
                        break;
                    }

                    move = StepTowards(_target.position, _chaseSpeed, _chaseTurnSpeed, out _);
                    break;
            }

            // "Vendo o jogador" só na perseguição: andando até a marca ele ainda está em patrulha (cor e estática
            // normais), e a virada de cor junto com o susto da música é o "ele me viu".
            if (_chaseState != null && chasing)
                _chaseState.Tick(true);

            // Walk andando, Run perseguindo, Idle girando ou parado — o Tick só olha se há movimento e o estado.
            if (_animationSystem != null && _initialized)
                _animationSystem.Tick(move, chasing ? GraphLocomotion.Awareness.Chase : GraphLocomotion.Awareness.Patrol);
        }

        // Gira para o rumo e avança pelo forward, como um corpo que precisa virar antes de ir: sem isso ele andaria
        // de lado nas curvas. Devolve a DIREÇÃO pedida (módulo 1), não o deslocamento do frame: é o contrato do
        // GraphAnimationSystem.Tick, que trata abaixo de 0,1 como parado — e um frame anda só ~0,06 m.
        private Vector3 StepTowards(Vector3 point, float speed, float turnSpeed, out bool arrived)
        {
            Vector3 toPoint = point - transform.position;
            toPoint.y = 0f;
            float distance = toPoint.magnitude;

            arrived = distance <= ArriveTolerance;
            if (arrived)
                return Vector3.zero;

            TurnTowards(YawOf(toPoint), turnSpeed);

            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            transform.position += forward * Mathf.Min(speed * Time.deltaTime, distance);
            return forward;
        }

        private bool TurnTowards(float targetYaw, float turnSpeed)
        {
            float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetYaw, turnSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return Mathf.Abs(Mathf.DeltaAngle(yaw, targetYaw)) < 0.5f;
        }

        private bool Touching()
        {
            // Centros das cápsulas, não as origens: o collider e o CharacterController podem ter center deslocado.
            Vector3 ownCenter = transform.position;
            float ownRadius = 0f;
            if (_body != null)
            {
                Vector3 scale = _body.transform.lossyScale;
                ownCenter = _body.transform.TransformPoint(_body.center);
                ownRadius = _body.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            }

            Vector3 targetCenter = _targetBody != null
                ? _targetBody.transform.TransformPoint(_targetBody.center)
                : _target.position;

            Vector3 delta = targetCenter - ownCenter;
            delta.y = 0f;

            float targetRadius = _targetBody != null ? _targetBody.radius : 0.3f;
            return delta.magnitude <= ownRadius + targetRadius + _catchMargin;
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private T Find<T>(T assigned) where T : Component =>
            assigned != null ? assigned : GetComponentInChildren<T>();
    }
}
