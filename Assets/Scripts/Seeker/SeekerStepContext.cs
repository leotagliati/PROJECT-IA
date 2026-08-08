using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Snapshot imutável de um único step, montado pelo SeekerManager. É o único input do
    /// SeekerRewardSystem — assim ele nunca precisa consultar percepção, movimento ou arena,
    /// e o cálculo de recompensa vira uma função pura do estado do step.
    /// </summary>
    public readonly struct SeekerStepContext
    {
        public readonly Vector3 PreMovePosition;
        public readonly Vector3 CurrentPosition;
        public readonly bool IsSeeingHider;
        public readonly bool HasSeenHider;
        public readonly Vector3 LastKnownHiderPosition;
        public readonly float ClosestWallProximity;
        public readonly int MaxEpisodeSteps;

        public SeekerStepContext(
            Vector3 preMovePosition,
            Vector3 currentPosition,
            bool isSeeingHider,
            bool hasSeenHider,
            Vector3 lastKnownHiderPosition,
            float closestWallProximity,
            int maxEpisodeSteps)
        {
            PreMovePosition = preMovePosition;
            CurrentPosition = currentPosition;
            IsSeeingHider = isSeeingHider;
            HasSeenHider = hasSeenHider;
            LastKnownHiderPosition = lastKnownHiderPosition;
            ClosestWallProximity = closestWallProximity;
            MaxEpisodeSteps = maxEpisodeSteps;
        }
    }
}
