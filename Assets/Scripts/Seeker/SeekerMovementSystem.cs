using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Driver de Rigidbody sem regra de agente. Seeker antigo: Move(direção) (com ou sem aceleração).
    /// GraphExplorer: MoveFacing (o corpo segue o movimento; quem decide a velocidade é o GraphLocomotion).
    /// Rigidbody: o do campo ou o mais próximo subindo a hierarquia (pode ficar no agente ou num filho dele).
    /// </summary>
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _turnSpeed = 720f;

        public float StepDistance => _moveSpeed * Time.fixedDeltaTime;

        // Só o Move(direção) do Seeker antigo; 0 = velocidade instantânea. O GraphExplorer (MoveFacing) não acelera.
        [SerializeField, Min(0f)] private float _acceleration = 0f;
        [SerializeField, Min(0f)] private float _minTurnSpeed = 0.5f;

        [Header("-----Corpo-----")]
        // Material sem atrito na cápsula: raspar a parede não freia o corpo.
        [SerializeField] private bool _frictionlessBody = false;

        // Trava a altura (sem gravidade): o corpo não sobe em rodapé nem quica em quina.
        [SerializeField] private bool _lockHeight = false;

        private Vector3 _velocity;

        private Rigidbody Body
        {
            get
            {
                if (_rigidbody == null)
                    _rigidbody = GetComponentInParent<Rigidbody>();

                return _rigidbody;
            }
        }

        public void Awake()
        {
            Rigidbody body = Body;

            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.freezeRotation = true;
            body.angularVelocity = Vector3.zero;

            if (_lockHeight)
            {
                body.constraints |= RigidbodyConstraints.FreezePositionY;
                body.useGravity = false;
            }

            CapsuleCollider capsule = body.GetComponent<CapsuleCollider>();
            if (_frictionlessBody && capsule != null)
            {
                capsule.sharedMaterial = new PhysicsMaterial("SemAtrito")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
        }

        /// <summary>
        /// Trava (ou solta) a altura: sem gravidade, o corpo não sobe em rodapé nem quica em quina. O GraphLocomotion
        /// chama no Configure com o padrão dele (travado), para o prefab novo não depender deste Inspector.
        /// </summary>
        public void SetHeightLocked(bool locked)
        {
            _lockHeight = locked;
            Rigidbody body = Body;
            if (locked)
                body.constraints |= RigidbodyConstraints.FreezePositionY;
            else
                body.constraints &= ~RigidbodyConstraints.FreezePositionY;
            body.useGravity = !locked;
        }

        private void SetHorizontalVelocity(Vector3 horizontal)
        {
            float vertical = _lockHeight ? 0f : Mathf.Min(0f, Body.linearVelocity.y);
            Body.linearVelocity = new Vector3(horizontal.x, vertical, horizontal.z);
        }

        public void Move(Vector3 direction)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);

            if (_acceleration <= 0f)
            {
                MoveInstant(flat);
                return;
            }

            AccelerateTowards(_moveSpeed * Vector3.ClampMagnitude(flat, 1f));

            if (_velocity.sqrMagnitude < _minTurnSpeed * _minTurnSpeed)
                return;

            Quaternion target = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(Body.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            Body.MoveRotation(next);
        }

        /// <summary>
        /// O CORPO SEGUE O MOVIMENTO (GraphExplorer): gira para <paramref name="direction"/> a _turnSpeed e
        /// anda só para a frente, a <paramref name="speed"/> x o alinhamento com o pedido (de costas = gira
        /// parado). Sem aceleração: a velocidade vale no mesmo step. Nada de andar de lado ou de ré, porque
        /// a animação só tem andar e correr para a frente. Devolve para onde o corpo fica virado.
        /// </summary>
        /// <param name="turnSpeed">Graus/s do giro do corpo; &lt;= 0 usa o _turnSpeed do Inspector.</param>
        /// <param name="appliedSpeed">Velocidade de fato pedida ao Rigidbody (m/s), já com o alinhamento.</param>
        public Vector3 MoveFacing(Vector3 direction, float speed, float turnSpeed, out float appliedSpeed)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);
            Quaternion rotation = Body.rotation;
            appliedSpeed = 0f;

            if (speed <= 0f || flat.sqrMagnitude < 1e-6f)
            {
                _velocity = Vector3.zero;
                SetHorizontalVelocity(Vector3.zero);
                return rotation * Vector3.forward;
            }

            Vector3 wanted = flat.normalized;
            float degreesPerSecond = turnSpeed > 0f ? turnSpeed : _turnSpeed;
            rotation = Quaternion.RotateTowards(rotation, Quaternion.LookRotation(wanted, Vector3.up),
                degreesPerSecond * Time.fixedDeltaTime);
            Body.MoveRotation(rotation);

            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            forward.Normalize();
            appliedSpeed = speed * Mathf.Clamp01(Vector3.Dot(forward, wanted));
            _velocity = forward * appliedSpeed;
            SetHorizontalVelocity(_velocity);
            return forward;
        }

        /// <summary>Velocidade REAL do corpo no plano (m/s), depois da física: o que a colisão deixou.</summary>
        public Vector3 PlanarVelocity
        {
            get
            {
                Vector3 velocity = Body.linearVelocity;
                return new Vector3(velocity.x, 0f, velocity.z);
            }
        }

        private void AccelerateTowards(Vector3 desired)
        {
            _velocity = Vector3.MoveTowards(_velocity, desired, _acceleration * Time.fixedDeltaTime);
            SetHorizontalVelocity(_velocity);
        }

        private void MoveInstant(Vector3 flat)
        {
            if (flat.sqrMagnitude < 1e-6f)
            {
                SetHorizontalVelocity(Vector3.zero);
                return;
            }

            Vector3 clamped = Vector3.ClampMagnitude(flat, 1f);

            SetHorizontalVelocity(_moveSpeed * clamped);

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(Body.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            Body.MoveRotation(next);
        }

        public void ResetMovement()
        {
            _velocity = Vector3.zero;
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
        }
    }
}
