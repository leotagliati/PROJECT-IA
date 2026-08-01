using UnityEngine;

namespace Assets.Scripts.Behavior
{
    public class ExplorationSystem : MonoBehaviour
    {
        [Header("Exploration")]

        [SerializeField]
        private float explorationRadius = 8f;

        [SerializeField]
        private float destinationTolerance = 0.75f;

        [SerializeField]
        private int maxAttempts = 20;

        private Vector3 currentDestination;

        private bool hasDestination;

        public bool HasDestination => hasDestination;

        public Vector3 CurrentDestination => currentDestination;

        public void Initialize()
        {
            hasDestination = false;
        }

        public void ResetExploration()
        {
            hasDestination = false;
        }

        public void CancelDestination()
        {
            hasDestination = false;
        }

        public void UpdateExploration(Transform seeker)
        {
            if (!hasDestination)
            {
                ChooseNewDestination(seeker);
                return;
            }

            float distance =
                Vector3.Distance(
                    seeker.position,
                    currentDestination);

            if (distance <= destinationTolerance)
            {
                ChooseNewDestination(seeker);
            }
        }

        private void ChooseNewDestination(Transform seeker)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                Vector2 random =
                    Random.insideUnitCircle * explorationRadius;

                Vector3 candidate =
                    seeker.position +
                    new Vector3(random.x, 0f, random.y);

                if (Physics.Raycast(
                    candidate + Vector3.up,
                    Vector3.down,
                    out RaycastHit hit,
                    5f))
                {
                    currentDestination = hit.point;
                    hasDestination = true;
                    return;
                }
            }

            hasDestination = false;
        }
    }
}