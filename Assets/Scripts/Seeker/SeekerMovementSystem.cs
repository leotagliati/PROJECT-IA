using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;

        public void Awake()
        {
            if (_rigidbody == null)
                _rigidbody = this.transform.parent.GetComponent<Rigidbody>();
        }

        public void Move(Vector3 direction)
        {
            Vector3 movement = _moveSpeed * Time.fixedDeltaTime * direction.normalized;
            _rigidbody.MovePosition(_rigidbody.position + movement);
        }

        public void ResetMovement()
        {
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}