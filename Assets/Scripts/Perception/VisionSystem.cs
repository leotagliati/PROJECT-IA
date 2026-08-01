using UnityEngine;

namespace Assets.Scripts.Perception
{
    public class VisionSystem : MonoBehaviour
    {
        [Header("Target")]

        [SerializeField]
        private Transform target;

        [Header("Vision")]

        [SerializeField]
        private float visionRange = 8f;

        [SerializeField]
        [Range(1f, 180f)]
        private float visionAngle = 90f;

        [SerializeField]
        private LayerMask obstacleMask;

        [SerializeField]
        private float eyeHeight = 0.4f;

        private TargetTracker tracker;

        public void Initialize(TargetTracker targetTracker)
        {
            tracker = targetTracker;
        }

        public void UpdateVision()
        {
            if (tracker == null)
                return;

            if (target == null)
            {
                tracker.LoseVisualContact();
                return;
            }

            Vector3 eyePosition = transform.position + Vector3.up * eyeHeight;
            Vector3 targetPosition = target.position + Vector3.up * eyeHeight;

            Vector3 toTarget = targetPosition - eyePosition;
            float distance = toTarget.magnitude;

            if (distance > visionRange)
            {
                tracker.LoseVisualContact();
                return;
            }

            Vector3 direction = toTarget.normalized;

            if (Vector3.Angle(transform.forward, direction) > visionAngle * 0.5f)
            {
                tracker.LoseVisualContact();
                return;
            }

            if (Physics.Raycast(
                eyePosition,
                direction,
                out RaycastHit hit,
                distance,
                obstacleMask))
            {
                tracker.LoseVisualContact();
                return;
            }

            tracker.UpdateVisualContact(transform, target);
        }

        public void ResetVision()
        {
            tracker.ResetTracker();
        }

#if UNITY_EDITOR

        private void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position + Vector3.up * eyeHeight;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(origin, visionRange);

            Vector3 left =
                Quaternion.Euler(0f, -visionAngle * 0.5f, 0f) * transform.forward;

            Vector3 right =
                Quaternion.Euler(0f, visionAngle * 0.5f, 0f) * transform.forward;

            Gizmos.color = Color.cyan;

            Gizmos.DrawRay(origin, left * visionRange);
            Gizmos.DrawRay(origin, right * visionRange);

            if (target != null)
            {
                Gizmos.color =
                    tracker != null && tracker.HasVisualContact
                    ? Color.green
                    : Color.red;

                Gizmos.DrawLine(origin, target.position + Vector3.up * eyeHeight);
            }
        }

#endif
    }
}