using Assets.Scripts.Agent;
using UnityEngine;

namespace Assets.Scripts.Reward
{
    public class ExplorationReward : MonoBehaviour
    {
        [Header("Movement Reward")]

        [SerializeField]
        private float movementReward = 0.0005f;

        [Header("Exploration Reward")]

        [SerializeField]
        private float newCellReward = 0.003f;

        [SerializeField]
        private float cellSize = 1f;

        [Header("Expansion Reward")]

        [SerializeField]
        private float expansionReward = 0.001f;

        private SeekerState state;

        private Vector3 lastPosition;

        public void Initialize(SeekerState seekerState)
        {
            state = seekerState;

            lastPosition = state.Position;
        }

        public void ResetReward()
        {
            lastPosition = state.Position;

            state.MaxExplorationDistance = 0f;

            state.VisitedCells.Clear();

            state.RegisterVisitedCell(cellSize);
        }

        public float Evaluate()
        {
            float reward = 0f;

            reward += EvaluateMovement();

            reward += EvaluateNewCell();

            reward += EvaluateExpansion();

            lastPosition = state.Position;

            return reward;
        }

        private float EvaluateMovement()
        {
            float distance =
                Vector3.Distance(lastPosition, state.Position);

            return distance * movementReward;
        }

        private float EvaluateNewCell()
        {
            if (state.RegisterVisitedCell(cellSize))
                return newCellReward;

            return 0f;
        }

        private float EvaluateExpansion()
        {
            if (state.ExplorationDistance <= state.MaxExplorationDistance)
                return 0f;

            float delta =
                state.ExplorationDistance -
                state.MaxExplorationDistance;

            state.MaxExplorationDistance =
                state.ExplorationDistance;

            return delta * expansionReward;
        }
    }
}