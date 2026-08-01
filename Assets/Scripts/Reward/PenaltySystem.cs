using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Reward
{
    public class PenaltySystem : MonoBehaviour
    {
        [Header("Step Penalty")]

        [SerializeField]
        private float stepPenalty = -0.0002f;

        [Header("Blocked Penalty")]

        [SerializeField]
        private float blockedPenalty = -0.003f;

        [Header("Loop Penalty")]

        [SerializeField]
        private float loopPenalty = -0.01f;

        [Header("Wall Collision Penalty")]

        [SerializeField]
        private float wallCollisionPenalty = -0.02f;

        [Header("Idle Penalty")]

        [SerializeField]
        private int idleStepsThreshold = 50;

        [SerializeField]
        private float idlePenalty = -0.01f;

        private SeekerState state;

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;
        }

        public void ResetPenalty()
        {
            state.IdleSteps = 0;
            state.WallCollision = false;
            state.InLoop = false;
        }

        public float Evaluate()
        {
            float penalty = 0f;

            penalty += stepPenalty;

            if (state.IsBlocked)
                penalty += blockedPenalty;

            if (state.InLoop)
                penalty += loopPenalty;

            if (state.WallCollision)
            {
                penalty += wallCollisionPenalty;
                state.WallCollision = false;
            }

            UpdateIdleCounter();

            if (state.IdleSteps >= idleStepsThreshold)
                penalty += idlePenalty;

            return penalty;
        }

        private void UpdateIdleCounter()
        {
            if (state.CurrentVelocity.magnitude < 0.05f)
            {
                state.IdleSteps++;
            }
            else
            {
                state.IdleSteps = 0;
            }
        }
    }
}