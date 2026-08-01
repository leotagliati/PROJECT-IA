using UnityEngine;

namespace Assets.Scripts.Perception
{
    public class TargetTracker : MonoBehaviour
    {
        [Header("Memory")]

        [SerializeField]
        private float memoryDuration = 5f;

        private bool hasVisualContact;

        private bool hasMemory;

        private Vector3 lastKnownPosition;

        private Vector3 targetDirection;

        private float targetDistance;

        private float memoryAge;

        // ============================================================
        // Public Properties
        // ============================================================

        public bool HasVisualContact => hasVisualContact;

        public bool HasMemory => hasMemory;

        public Vector3 LastKnownPosition => lastKnownPosition;

        public Vector3 TargetDirection => targetDirection;

        public float TargetDistance => targetDistance;

        public float MemoryAge => memoryAge;

        public float MemoryStrength
        {
            get
            {
                if (!hasMemory)
                    return 0f;

                return Mathf.Clamp01(1f - (memoryAge / memoryDuration));
            }
        }

        // ============================================================
        // Update
        // ============================================================

        public void UpdateVisualContact(Transform observer, Transform target)
        {
            hasVisualContact = true;
            hasMemory = true;

            lastKnownPosition = target.position;

            Vector3 direction = target.position - observer.position;
            direction.y = 0f;

            targetDistance = direction.magnitude;

            targetDirection =
                targetDistance > 0.001f
                ? direction.normalized
                : Vector3.zero;

            memoryAge = 0f;
        }

        public void LoseVisualContact()
        {
            hasVisualContact = false;
        }

        public void Tick()
        {
            if (hasVisualContact)
            {
                memoryAge = 0f;
                return;
            }

            if (!hasMemory)
                return;

            memoryAge += Time.deltaTime;

            if (memoryAge >= memoryDuration)
            {
                Forget();
            }
        }

        // ============================================================
        // Memory
        // ============================================================

        public void Forget()
        {
            hasVisualContact = false;
            hasMemory = false;

            lastKnownPosition = Vector3.zero;
            targetDirection = Vector3.zero;

            targetDistance = 0f;

            memoryAge = 0f;
        }

        public void ResetTracker()
        {
            Forget();
        }
    }
}