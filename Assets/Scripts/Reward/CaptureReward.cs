using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Reward
{
    public class CaptureReward : MonoBehaviour
    {
        [Header("Capture Reward")]

        [SerializeField]
        private float captureReward = 2.0f;

        private SeekerState state;

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;
        }

        public void ResetReward()
        {
            state.TargetCaptured = false;
        }

        public float Evaluate()
        {
            if (!state.TargetCaptured)
                return 0f;

            state.TargetCaptured = false;

            return captureReward;
        }
    }
}