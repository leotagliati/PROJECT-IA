using Assets.Scripts.Graph;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>Uma pegada no chão: onde, para onde o alvo ia, quando e com que força.</summary>
    public struct FreeFootprint
    {
        public Vector3 Position;
        public Vector3 Heading;
        public float Time;
        public float Strength;
        public float Lifetime;

        /// <summary>Quão marcada ela está agora (0..1): a força caindo em linha reta até sumir no fim da vida.</summary>
        public float IntensityAt(float now) =>
            Lifetime > 0f ? Strength * Mathf.Clamp01(1f - (now - Time) / Lifetime) : 0f;
    }

    /// <summary>
    /// RASTRO do alvo na v8 (o hider no treino, o jogador no jogo): a cada passada fica uma pegada no chão que
    /// esmaece. CORRENDO é forte e dura (~12 s), ANDANDO é média (~6 s); o jogador AGACHADO deixa uma fraca que some
    /// logo (~2.5 s). Mesma ideia das pegadas da v7 (GraphFootprintTrail), escrita à parte para a v8 não depender do
    /// "agachado" que só a v7 pôs no IGraphTarget.
    ///
    /// O monstro só sabe da pegada que VÊ (<see cref="FreeTrackSense"/>): pegada não atravessa parede, então
    /// esconder perto continua funcionando. Nada paga seguir pegada; quem paga é a captura.
    /// Fica no objeto do alvo; o FreeExplorerManager adiciona se faltar. Sem visual no chão (só gizmo, marrom).
    /// </summary>
    public class FreeFootprintTrail : MonoBehaviour
    {
        [Header("-----Passada (m entre duas pegadas)-----")]
        [SerializeField, Min(0.2f)] private float _runStride = 1.3f;
        [SerializeField, Min(0.2f)] private float _walkStride = 0.9f;
        [SerializeField, Min(0.2f)] private float _crouchStride = 0.7f;

        [Header("-----Força (0..1) e duração (s)-----")]
        // Correndo marca mais e por mais tempo: é o que denuncia quem foge.
        [SerializeField, Range(0f, 1f)] private float _runStrength = 1f;
        [SerializeField, Min(0.1f)] private float _runLifetime = 12f;
        [SerializeField, Range(0f, 1f)] private float _walkStrength = 0.55f;
        [SerializeField, Min(0.1f)] private float _walkLifetime = 6f;
        [SerializeField, Range(0f, 1f)] private float _crouchStrength = 0.2f;
        [SerializeField, Min(0.1f)] private float _crouchLifetime = 2.5f;

        // Pulo maior que isto num step (m) é teleporte (respawn): o rastro recomeça, sem pegada atravessando o mapa.
        private const float TeleportDistance = 3f;
        private const int Capacity = 128;

        private readonly FreeFootprint[] _prints = new FreeFootprint[Capacity];
        private int _head;
        private int _count;
        private Vector3 _lastPrint;
        private Vector3 _lastPosition;
        private bool _hasLast;

        private IGraphTarget _target;
        private PlayerMovement _player;

        /// <summary>Pegadas guardadas (algumas já podem ter sumido: confira IntensityAt).</summary>
        public int Count => _count;

        /// <summary>A i-ésima pegada, da MAIS NOVA (0) para a mais velha.</summary>
        public FreeFootprint GetNewest(int i) => _prints[(_head - 1 - i + Capacity * 2) % Capacity];

        public float Now => Time.time;

        private void Awake()
        {
            _target = GetComponent<IGraphTarget>();
            _player = GetComponentInChildren<PlayerMovement>();
        }

        /// <summary>Apaga o rastro (novo episódio, respawn).</summary>
        public void Clear()
        {
            _count = 0;
            _head = 0;
            _hasLast = false;
        }

        private void OnDisable() => Clear();

        private void FixedUpdate()
        {
            if (_target != null && !_target.IsActive)
                return;

            Vector3 position = transform.position;
            if (!_hasLast)
            {
                _lastPrint = position;
                _lastPosition = position;
                _hasLast = true;
                return;
            }

            Vector3 jump = position - _lastPosition;
            _lastPosition = position;
            if (new Vector2(jump.x, jump.z).magnitude > TeleportDistance)
            {
                Clear();
                _lastPrint = position;
                _lastPosition = position;
                _hasLast = true;
                return;
            }

            Gait gait = CurrentGait();
            Vector3 moved = position - _lastPrint;
            moved.y = 0f;
            float stride = gait == Gait.Run ? _runStride : gait == Gait.Crouch ? _crouchStride : _walkStride;
            if (moved.magnitude < stride)
                return;

            AddPrint(position, moved.normalized, gait);
            _lastPrint = position;
        }

        private enum Gait { Walk, Run, Crouch }

        private Gait CurrentGait()
        {
            if (_player != null)
            {
                switch (_player.CurrentState)
                {
                    case PlayerState.Running:
                    case PlayerState.Jumping:
                        return Gait.Run;
                    case PlayerState.Crouching:
                    case PlayerState.CrouchWalking:
                        return Gait.Crouch;
                    default:
                        return Gait.Walk;
                }
            }

            return _target != null && _target.IsRunning ? Gait.Run : Gait.Walk;
        }

        private void AddPrint(Vector3 position, Vector3 heading, Gait gait)
        {
            float strength = gait == Gait.Run ? _runStrength : gait == Gait.Crouch ? _crouchStrength : _walkStrength;
            float lifetime = gait == Gait.Run ? _runLifetime : gait == Gait.Crouch ? _crouchLifetime : _walkLifetime;
            if (strength <= 0f)
                return;

            _prints[_head] = new FreeFootprint
            {
                Position = Ground(position),
                Heading = heading,
                Time = Now,
                Strength = strength,
                Lifetime = lifetime,
            };
            _head = (_head + 1) % Capacity;
            _count = Mathf.Min(_count + 1, Capacity);
        }

        // O chão sob o alvo: o pivô pode estar no pé ou no meio do corpo. O raio começa dentro do próprio collider
        // (que um raycast não acerta) e ignora a layer Ignore Raycast (a do hider de treino).
        private static Vector3 Ground(Vector3 position)
        {
            const int ignoreRaycast = 1 << 2;
            if (Physics.Raycast(position + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 4f, ~ignoreRaycast, QueryTriggerInteraction.Ignore))
                return hit.point;

            return position;
        }

        // Gizmo (marrom): as pegadas vivas, mais fortes = maiores. Só em Play, com o alvo selecionado.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying)
                return;

            float now = Now;
            Gizmos.color = new Color(0.55f, 0.35f, 0.2f, 0.9f);
            for (int i = 0; i < _count; i++)
            {
                FreeFootprint print = GetNewest(i);
                float intensity = print.IntensityAt(now);
                if (intensity > 0.01f)
                    Gizmos.DrawWireSphere(print.Position, 0.1f + 0.2f * intensity);
            }
        }
    }
}
