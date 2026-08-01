using Assets.Scripts.Agent;
using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Reward
{
    public class RewardSystem : MonoBehaviour
    {
        [Header("Reward Modules")]

        [SerializeField]
        private ExplorationReward explorationReward;

        [SerializeField]
        private TrackingReward trackingReward;

        [SerializeField]
        private CaptureReward captureReward;

        [SerializeField]
        private PenaltySystem penaltySystem;

        private Agent agent;

        private SeekerState state;

        public void Initialize(Agent seekerAgent, SeekerState seekerState)
        {
            agent = seekerAgent;
            state = seekerState;

            explorationReward.Initialize(state);
            trackingReward.Initialize(state);
            captureReward.Initialize(state);
            penaltySystem.Initialize(state);
        }

        public void ResetRewards()
        {
            explorationReward.ResetReward();

            trackingReward.ResetReward();

            captureReward.ResetReward();

            penaltySystem.ResetPenalty();
        }

        public void UpdateRewards()
        {
            float reward = 0f;

            reward += explorationReward.Evaluate();

            reward += trackingReward.Evaluate();

            reward += captureReward.Evaluate();

            reward += penaltySystem.Evaluate();

            if (Mathf.Abs(reward) > Mathf.Epsilon)
            {
                agent.AddReward(reward);
            }
        }
    }
}