using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Behavior
{
    public class BehaviorController : MonoBehaviour
    {
        [Header("Systems")]

        [SerializeField]
        private SearchBehavior searchBehavior;

        [SerializeField]
        private InvestigateBehavior investigateBehavior;

        [SerializeField]
        private ChaseBehavior chaseBehavior;

        [SerializeField]
        private SmellBehavior smellBehavior;

        private SeekerState state;

        public BehaviorState CurrentState { get; private set; }

        //==================================================
        // Initialization
        //==================================================

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;

            searchBehavior.Initialize(state);

            investigateBehavior.Initialize(state);

            chaseBehavior.Initialize(state);

            smellBehavior.Initialize(state);

            CurrentState = BehaviorState.Searching;
        }

        //==================================================
        // Update
        //==================================================

        public void UpdateBehavior()
        {
            CurrentState = DetermineState();

            switch (CurrentState)
            {
                case BehaviorState.ChasingTarget:
                    chaseBehavior.UpdateBehavior();
                    break;

                case BehaviorState.FollowingSmell:
                    smellBehavior.UpdateBehavior();
                    break;

                case BehaviorState.Investigating:
                    investigateBehavior.UpdateBehavior();
                    break;

                default:
                    searchBehavior.UpdateBehavior();
                    break;
            }
        }

        private BehaviorState DetermineState()
        {
            if (state.HasVisualContact)
                return BehaviorState.ChasingTarget;

            if (state.HasTargetMemory)
                return BehaviorState.Investigating;

            if (state.HasSmell)
                return BehaviorState.FollowingSmell;

            return BehaviorState.Searching;
        }

        //==================================================
        // Reset
        //==================================================

        public void ResetBehavior()
        {
            CurrentState = BehaviorState.Searching;

            searchBehavior.ResetBehavior();

            investigateBehavior.ResetBehavior();

            chaseBehavior.ResetBehavior();

            smellBehavior.ResetBehavior();
        }
    }
}