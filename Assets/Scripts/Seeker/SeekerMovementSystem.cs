using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _turnSpeed = 720f;

        public void Awake()
        {
            if (_rigidbody == null)
                _rigidbody = this.transform.parent.GetComponent<Rigidbody>();
        }

        public void Move(Vector3 direction)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-6f)
                return;

            Vector3 clamped = Vector3.ClampMagnitude(flat, 1f);

            Vector3 movement = _moveSpeed * Time.fixedDeltaTime * clamped;
            _rigidbody.MovePosition(_rigidbody.position + movement);

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);

            _rigidbody.MoveRotation(next);
        }

        public void ResetMovement()
        {
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}