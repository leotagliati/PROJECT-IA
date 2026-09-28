using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _turnSpeed = 720f;

        /// <summary>Deslocamento por step de física com ação de magnitude 1. Telemetria.</summary>
        public float StepDistance => _moveSpeed * Time.fixedDeltaTime;

        // Resolvido no uso, e não só no Awake: com o Academy já inicializado (cena recarregada),
        // o Agent chama OnEpisodeBegin → ResetMovement de dentro do OnEnable do pai, antes do
        // Awake deste filho. A exceção abortava o OnEnable do SeekerManager no meio.
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
            _ = Body;
        }

        public void Move(Vector3 direction)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-6f)
                return;

            Vector3 clamped = Vector3.ClampMagnitude(flat, 1f);

            Rigidbody body = Body;
            Vector3 movement = _moveSpeed * Time.fixedDeltaTime * clamped;
            body.MovePosition(body.position + movement);

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(body.rotation, target, _turnSpeed * Time.fixedDeltaTime);

            body.MoveRotation(next);
        }

        public void ResetMovement()
        {
            Rigidbody body = Body;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}