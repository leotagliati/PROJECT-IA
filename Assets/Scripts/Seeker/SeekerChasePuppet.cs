using System;
using Assets.Scripts.Graph;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Seeker do final: o modelo do monstro sem Agent nem IA, posto na porta de saída e desativado na cena. A
    /// <see cref="EscapeCutscene"/> o comanda: <see cref="Appear"/> parado olhando o player, <see cref="Chase"/> em
    /// linha reta, <see cref="Caught"/> quando encosta.
    ///
    /// Visual e áudio são os sistemas do monstro de verdade. A perseguição é o <see cref="SeekerChaseState"/>
    /// recebendo "vendo o jogador", e é isso que liga ChaseMusic, SeenPostFX e ThemeMusic — que por isso procuram
    /// Seekers inativos também.
    ///
    /// Move por transform: o corredor é reto, não precisa de NavMesh nem de física. Pega pelo TRIGGER do corpo (o
    /// collider dele, Is Trigger, com um Rigidbody kinematic para o evento chegar): um collider sólido empurra o
    /// CharacterController do player e os dois nunca se encostam.
    /// </summary>
    public class SeekerChasePuppet : MonoBehaviour
    {
        [Header("Sistemas (vazio = acha nos filhos)")]
        [SerializeField] private GraphAnimationSystem _animationSystem;
        [SerializeField] private SeekerChaseState _chaseState;
        [SerializeField] private SeekerAudioSystem _audioSystem;
        [SerializeField] private SeekerStaticVisual _staticVisual;

        // Um pouco abaixo da corrida do player: correndo reto dá para fugir, mas parar ou bater na parede custa caro.
        [Header("Perseguição")]
        [SerializeField, Min(0.1f)] private float _speed = 8f;
        [SerializeField, Min(1f)] private float _turnSpeed = 360f;

        private Transform _target;
        private bool _chasing;
        private bool _frozen;
        private bool _initialized;

        /// <summary>O alvo entrou no trigger do corpo. Ele para; o que vem depois é de quem ouve.</summary>
        public event Action Caught;

        private void Awake()
        {
            _animationSystem = Find(_animationSystem);
            _chaseState = Find(_chaseState);
            _audioSystem = Find(_audioSystem);
            _staticVisual = Find(_staticVisual);
        }

        // No Start, como o Initialize do Agent: o visual lê o SeekerChaseState, que já existe desde o Awake.
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

        public void Appear(Transform target)
        {
            _target = target;
            _chasing = false;
            _frozen = false;
            gameObject.SetActive(true);
            FaceTarget();
        }

        public void Chase() => _chasing = true;

        /// <summary>Para e cala onde está (a tela da vitória já cobriu tudo).</summary>
        public void Freeze()
        {
            _chasing = false;
            _frozen = true;
            if (_audioSystem != null)
                _audioSystem.Silence();
        }

        private void Update()
        {
            if (_frozen || _target == null)
            {
                Tick(Vector3.zero);
                return;
            }

            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;

            if (!_chasing)
            {
                Tick(Vector3.zero);
                return;
            }

            float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, YawOf(toTarget), _turnSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward.Normalize();
            transform.position += forward * Mathf.Min(_speed * Time.deltaTime, toTarget.magnitude);

            Tick(forward);
        }

        // Stay além do Enter: o player pode já estar dentro do trigger quando a perseguição começa.
        private void OnTriggerEnter(Collider other) => TryCatch(other);

        private void OnTriggerStay(Collider other) => TryCatch(other);

        private void TryCatch(Collider other)
        {
            if (!_chasing || _target == null || !other.transform.IsChildOf(_target))
                return;

            _chasing = false;
            Tick(Vector3.zero);

            // Depois da vitória ninguém mais é pego: ele só para colado no player que corre para o clarão.
            if (GameManager.Current == null || !GameManager.Current.IsOver)
                Caught?.Invoke();
        }

        // A direção pedida, não o deslocamento do frame: o GraphAnimationSystem.Tick trata módulo abaixo do limiar
        // como parado, e um frame anda só alguns centímetros.
        private void Tick(Vector3 move)
        {
            if (_chaseState != null && _chasing)
                _chaseState.Tick(true);

            if (!_initialized)
                return;

            var state = _chasing ? GraphLocomotion.Awareness.Chase : GraphLocomotion.Awareness.Patrol;
            if (_animationSystem != null)
                _animationSystem.Tick(move, state);
            if (_staticVisual != null)
                _staticVisual.SetState(_chasing, false);
        }

        private void FaceTarget()
        {
            if (_target == null)
                return;

            Vector3 toTarget = _target.position - transform.position;
            transform.rotation = Quaternion.Euler(0f, YawOf(toTarget), 0f);
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private T Find<T>(T assigned) where T : Component =>
            assigned != null ? assigned : GetComponentInChildren<T>();
    }
}
