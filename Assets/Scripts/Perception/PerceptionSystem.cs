using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Perception
{
    public class PerceptionSystem : MonoBehaviour
    {
        [Header("Systems")]

        [SerializeField]
        private VisionSystem visionSystem;

        [SerializeField]
        private SmellSystem smellSystem;

        [SerializeField]
        private TargetTracker targetTracker;

        private SeekerState state;

        //==================================================
        // Initialization
        //==================================================

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;

            visionSystem.Initialize(state);

            smellSystem.Initialize(state);

            targetTracker.Initialize(state);
        }

        //==================================================
        // Update
        //==================================================

        public void UpdatePerception()
        {
            visionSystem.UpdateVision();

            smellSystem.UpdateSmell();

            targetTracker.UpdateTracker();

            UpdateStimulusTimer();
        }

        private void UpdateStimulusTimer()
        {
            if (state.HasVisualContact || state.HasSmell)
            {
                state.TimeWithoutStimulus = 0f;
            }
            else
            {
                state.TimeWithoutStimulus += Time.fixedDeltaTime;
            }
        }

        //==================================================
        // Reset
        //==================================================

        public void ResetPerception()
        {
            visionSystem.ResetVision();

            smellSystem.ResetSmell();

            targetTracker.ResetTracker();

            state.HasVisualContact = false;
            state.HasTargetMemory = false;
            state.HasSmell = false;

            state.TargetMemoryStrength = 0f;

            state.TimeWithoutStimulus = 0f;
        }
    }
}