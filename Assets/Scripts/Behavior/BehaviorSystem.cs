using Assets.Scripts.Perception;
using UnityEngine;

namespace Assets.Scripts.Behavior
{
    public class BehaviorSystem : MonoBehaviour
    {
        [SerializeField]
        private PerceptionSystem perceptionSystem;

        private BehaviorState currentState;

        public BehaviorState CurrentState => currentState;

        public void Initialize()
        {
            currentState = BehaviorState.Searching;
        }

        public void UpdateBehavior()
        {
            if (perceptionSystem.HasVisualContact)
            {
                currentState = BehaviorState.ChasingTarget;
                return;
            }

            if (perceptionSystem.HasTargetMemory)
            {
                currentState = BehaviorState.Investigating;
                return;
            }

            if (perceptionSystem.HasSmell)
            {
                currentState = BehaviorState.FollowingSmell;
                return;
            }

            currentState = BehaviorState.Searching;
        }

        public void ResetBehavior()
        {
            currentState = BehaviorState.Searching;
        }

        public bool IsSearching =>
            currentState == BehaviorState.Searching;

        public bool IsFollowingSmell =>
            currentState == BehaviorState.FollowingSmell;

        public bool IsInvestigating =>
            currentState == BehaviorState.Investigating;

        public bool IsChasing =>
            currentState == BehaviorState.ChasingTarget;
    }
}