using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Reward
{
    public class TrackingReward : MonoBehaviour
    {
        [Header("Tracking Reward")]

        [SerializeField]
        private float targetApproachReward = 0.01f;

        [SerializeField]
        private float smellFollowReward = 0.003f;

        [SerializeField]
        private float investigationReward = 0.005f;

        private SeekerState state;

        private float previousTargetDistance;

        private float previousMemoryDistance;

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;

            previousTargetDistance = Mathf.Infinity;
            previousMemoryDistance = Mathf.Infinity;
        }

        public void ResetReward()
        {
            previousTargetDistance = Mathf.Infinity;
            previousMemoryDistance = Mathf.Infinity;
        }

        public float Evaluate()
        {
            float reward = 0f;

            if (state.HasVisualContact)
                reward += EvaluateTargetTracking();

            else if (state.HasTargetMemory)
                reward += EvaluateInvestigation();

            else if (state.HasSmell)
                reward += EvaluateSmellTracking();

            return reward;
        }

        private float EvaluateTargetTracking()
        {
            float reward = 0f;

            if (state.TargetDistance < previousTargetDistance)
            {
                reward +=
                    (previousTargetDistance - state.TargetDistance)
                    * targetApproachReward;
            }

            previousTargetDistance = state.TargetDistance;

            return reward;
        }

        private float EvaluateSmellTracking()
        {
            return state.SmellIntensity * smellFollowReward;
        }

        private float EvaluateInvestigation()
        {
            float reward = 0f;

            if (state.MemoryDistance < previousMemoryDistance)
            {
                reward +=
                    (previousMemoryDistance - state.MemoryDistance)
                    * investigationReward;
            }

            previousMemoryDistance = state.MemoryDistance;

            return reward;
        }
    }
}