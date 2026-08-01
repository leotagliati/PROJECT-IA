using UnityEngine;

namespace Assets.Scripts.Perception
{
    public class SmellSystem : MonoBehaviour
    {
        [Header("Target")]

        [SerializeField]
        private Transform target;

        [Header("Smell")]

        [SerializeField]
        private float smellRadius = 12f;

        [SerializeField]
        private AnimationCurve smellFalloff =
            AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

        private bool hasSmell;

        private Vector3 smellDirection;

        private float smellIntensity;

        public bool HasSmell => hasSmell;

        public Vector3 SmellDirection => smellDirection;

        public float SmellIntensity => smellIntensity;

        public void Initialize()
        {
            ResetSmell();
        }

        public void UpdateSmell(Transform seeker)
        {
            if (target == null)
            {
                ResetSmell();
                return;
            }

            Vector3 direction = target.position - seeker.position;
            direction.y = 0f;

            float distance = direction.magnitude;

            if (distance > smellRadius)
            {
                ResetSmell();
                return;
            }

            hasSmell = true;

            smellDirection =
                distance > 0.001f
                ? direction.normalized
                : Vector3.zero;

            float normalizedDistance =
                Mathf.Clamp01(distance / smellRadius);

            smellIntensity =
                smellFalloff.Evaluate(1f - normalizedDistance);
        }

        public void ResetSmell()
        {
            hasSmell = false;
            smellDirection = Vector3.zero;
            smellIntensity = 0f;
        }

#if UNITY_EDITOR

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.15f);
            Gizmos.DrawWireSphere(transform.position, smellRadius);

            if (!Application.isPlaying || !hasSmell)
                return;

            Gizmos.color = Color.yellow;

            Gizmos.DrawRay(
                transform.position + Vector3.up * 0.25f,
                smellDirection * smellRadius * smellIntensity
            );
        }

#endif
    }
}