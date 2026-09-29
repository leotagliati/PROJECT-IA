using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _turnSpeed = 720f;

        public float StepDistance => _moveSpeed * Time.fixedDeltaTime;

        [SerializeField, Min(0f)] private float _acceleration = 0f;
        [SerializeField, Min(0f)] private float _minTurnSpeed = 0.5f;

        [Header("-----Olhar (só com Move(direção, olhar))-----")]
        [SerializeField, Range(0.1f, 1f)] private float _backwardSpeedFactor = 0.6f;
        [SerializeField, Range(0f, 1f)] private float _lookDeadzone = 0.1f;

        [Header("-----Steering assistido-----")]
        [SerializeField, Min(0f)] private float _steerMargin = 0.5f;
        [SerializeField] private bool _frictionlessBody = false;
        [SerializeField] private bool _lockHeight = false;

        private Vector3 _velocity;
        private CapsuleCollider _capsule;
        private float _steerAssist;
        private LayerMask _wallLayer;

        private Rigidbody Body
        {
            get
            {
                if (_rigidbody == null)
                    _rigidbody = transform.parent.GetComponent<Rigidbody>();

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

            _capsule = body.GetComponent<CapsuleCollider>();

            if (_frictionlessBody && _capsule != null)
            {
                _capsule.sharedMaterial = new PhysicsMaterial("SemAtrito")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
        }

        public void ConfigureSteering(float assist, LayerMask walls)
        {
            _steerAssist = Mathf.Clamp01(assist);
            _wallLayer = walls;
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

            AccelerateTowards(Steer(_moveSpeed * Vector3.ClampMagnitude(flat, 1f)));

            if (_velocity.sqrMagnitude < _minTurnSpeed * _minTurnSpeed)
                return;

            Quaternion target = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(Body.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            Body.MoveRotation(next);
        }

        public void Move(Vector3 direction, Vector3 look)
        {
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(direction.x, 0f, direction.z), 1f);

            Vector3 forward = Body.rotation * Vector3.forward;
            forward.y = 0f;
            float speedFactor = 1f;
            if (flat.sqrMagnitude > 1e-6f && forward.sqrMagnitude > 1e-6f)
            {
                float alignment = Vector3.Dot(forward.normalized, flat.normalized);
                if (alignment < 0f)
                    speedFactor = Mathf.Lerp(1f, _backwardSpeedFactor, -alignment);
            }

            AccelerateTowards(Steer(_moveSpeed * speedFactor * flat));

            Vector3 flatLook = new(look.x, 0f, look.z);
            if (flatLook.magnitude < _lookDeadzone)
                return;

            Quaternion target = Quaternion.LookRotation(flatLook.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(Body.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            Body.MoveRotation(next);
        }

        private void AccelerateTowards(Vector3 desired)
        {
            _velocity = Vector3.MoveTowards(_velocity, desired, _acceleration * Time.fixedDeltaTime);
            SetHorizontalVelocity(_velocity);
        }

        private Vector3 Steer(Vector3 desired)
        {
            if (_steerAssist <= 0f || _wallLayer.value == 0 || _capsule == null || _acceleration <= 0f)
                return desired;

            Vector3 origin = _capsule.bounds.center;
            Vector3 scale = _capsule.transform.lossyScale;
            float radius = 0.9f * _capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float speed = _velocity.magnitude;
            float range = speed * speed / (2f * _acceleration) + _steerMargin;

            for (int iteration = 0; iteration < 2; iteration++)
            {
                if (desired.sqrMagnitude < 1e-6f)
                    break;

                if (!Physics.SphereCast(origin, radius, desired.normalized, out RaycastHit hit, range,
                        _wallLayer, QueryTriggerInteraction.Ignore))
                    break;

                Vector3 normal = new(hit.normal.x, 0f, hit.normal.z);
                if (normal.sqrMagnitude < 1e-6f)
                    break;
                normal.Normalize();

                float into = -Vector3.Dot(desired, normal);
                if (into <= 0f)
                    break;

                float closeness = 1f - Mathf.Clamp01(hit.distance / range);
                desired += normal * (into * _steerAssist * closeness);
            }

            return desired;
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
