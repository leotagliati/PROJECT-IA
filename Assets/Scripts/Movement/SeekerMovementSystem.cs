using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Movement
{
    [RequireComponent(typeof(Rigidbody))]
    public class SeekerMovementSystem : MonoBehaviour
    {
        [Header("Movement")]

        [SerializeField]
        private float moveSpeed = 3f;

        [SerializeField]
        private float rotationSpeed = 720f;

        [SerializeField]
        private float blockedDistanceThreshold = 0.01f;

        private Rigidbody rb;

        private SeekerState state;

        private Vector3 lastPosition;

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;

            rb = GetComponent<Rigidbody>();

            rb.freezeRotation = true;

            lastPosition = rb.position;
        }

        public void Execute()
        {
            Vector3 direction = state.DesiredDirection;
            direction.y = 0f;

            if (direction.sqrMagnitude > 1f)
                direction.Normalize();

            state.LastMoveDirection = direction;

            Move(direction);

            Rotate(direction);

            UpdateMovementState();
        }

        private void Move(Vector3 direction)
        {
            Vector3 nextPosition =
                rb.position +
                direction * moveSpeed * Time.fixedDeltaTime;

            rb.MovePosition(nextPosition);
        }

        private void Rotate(Vector3 direction)
        {
            if (direction.sqrMagnitude < 0.0001f)
                return;

            Quaternion targetRotation =
                Quaternion.LookRotation(direction, Vector3.up);

            rb.MoveRotation(
                Quaternion.RotateTowards(
                    rb.rotation,
                    targetRotation,
                    rotationSpeed * Time.fixedDeltaTime
                )
            );
        }

        private void UpdateMovementState()
        {
            Vector3 displacement = rb.position - lastPosition;
            displacement.y = 0f;

            state.Position = rb.position;

            state.Rotation = rb.rotation;

            state.Forward = transform.forward;

            state.CurrentVelocity =
                displacement / Time.fixedDeltaTime;

            state.IsBlocked =
                state.LastMoveDirection.sqrMagnitude > 0.001f &&
                displacement.magnitude < blockedDistanceThreshold;

            state.TotalDistanceTravelled += displacement.magnitude;

            state.ExplorationDistance =
                Vector3.Distance(
                    state.ExplorationOrigin,
                    state.Position
                );

            lastPosition = rb.position;
        }

        public void RegisterWallCollision()
        {
            state.WallCollision = true;
        }

        public void ResetMovement()
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            lastPosition = rb.position;

            state.Position = rb.position;
            state.Rotation = rb.rotation;
            state.Forward = transform.forward;

            state.DesiredDirection = Vector3.zero;
            state.LastMoveDirection = Vector3.zero;
            state.CurrentVelocity = Vector3.zero;

            state.IsBlocked = false;

            state.TotalDistanceTravelled = 0f;
            state.ExplorationDistance = 0f;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.gameObject.CompareTag("Wall"))
            {
                RegisterWallCollision();
            }
        }
    }
}