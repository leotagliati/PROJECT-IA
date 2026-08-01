using UnityEngine;

namespace Assets.Scripts.Behavior
{
    public class NavigationIntent : MonoBehaviour
    {
        private Vector3 desiredDirection;

        private Vector3 desiredDestination;

        public Vector3 DesiredDirection => desiredDirection;

        public Vector3 DesiredDestination => desiredDestination;

        public void SetDirection(Vector3 direction)
        {
            direction.y = 0f;

            desiredDirection =
                direction.sqrMagnitude > 0.0001f
                ? direction.normalized
                : Vector3.zero;
        }

        public void SetDestination(Vector3 destination)
        {
            desiredDestination = destination;

            SetDirection(destination - transform.position);
        }

        public void Clear()
        {
            desiredDirection = Vector3.zero;
        }
    }
}