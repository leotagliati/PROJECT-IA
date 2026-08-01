using Assets.Scripts.Behavior;
using Assets.Scripts.Movement;
using Assets.Scripts.Perception;
using Assets.Scripts.Reward;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Agent
{
    public class SeekerManager : Agent
    {
        [Header("Systems")]

        [SerializeField]
        private SeekerMovementSystem movementSystem;

        [SerializeField]
        private PerceptionSystem perceptionSystem;

        [SerializeField]
        private BehaviorController behaviorController;

        [SerializeField]
        private RewardSystem rewardSystem;

        [Header("Scene")]

        [SerializeField]
        private Transform spawnPoint;

        private SeekerState state;

        //==================================================
        // Initialization
        //==================================================

        public override void Initialize()
        {
            state = new SeekerState();

            movementSystem.Initialize(state);

            perceptionSystem.Initialize(state);

            behaviorController.Initialize(state);

            rewardSystem.Initialize(state, this);
        }

        //==================================================
        // Episode
        //==================================================

        public override void OnEpisodeBegin()
        {
            ResetAgent();

            movementSystem.ResetMovement();

            perceptionSystem.ResetPerception();

            behaviorController.ResetBehavior();

            rewardSystem.ResetReward();
        }

        private void ResetAgent()
        {
            transform.SetPositionAndRotation(
                spawnPoint.position,
                spawnPoint.rotation
            );

            state.Reset(spawnPoint.position);
        }

        //==================================================
        // Observations
        //==================================================

        public override void CollectObservations(VectorSensor sensor)
        {
            perceptionSystem.UpdatePerception();

            behaviorController.UpdateBehavior();

            CollectStateObservations(sensor);
        }

        private void CollectStateObservations(VectorSensor sensor)
        {
            // -------- Movement --------

            sensor.AddObservation(state.CurrentVelocity);

            sensor.AddObservation(state.IsBlocked ? 1f : 0f);

            // -------- Vision --------

            sensor.AddObservation(state.HasVisualContact ? 1f : 0f);

            sensor.AddObservation(state.TargetDirection);

            sensor.AddObservation(state.TargetDistance);

            // -------- Memory --------

            sensor.AddObservation(state.HasTargetMemory ? 1f : 0f);

            sensor.AddObservation(state.LastKnownTargetPosition);

            sensor.AddObservation(state.TargetMemoryStrength);

            // -------- Smell --------

            sensor.AddObservation(state.HasSmell ? 1f : 0f);

            sensor.AddObservation(state.SmellDirection);

            sensor.AddObservation(state.SmellIntensity);

            // -------- Internal --------

            sensor.AddObservation(state.TimeWithoutStimulus);

            sensor.AddObservation(state.ExplorationDistance);
        }

        //==================================================
        // Actions
        //==================================================

        public override void OnActionReceived(ActionBuffers actions)
        {
            state.DesiredDirection = new Vector3(
                actions.ContinuousActions[0],
                0f,
                actions.ContinuousActions[1]
            );

            movementSystem.Execute();

            rewardSystem.EvaluateRewards();

            state.EpisodeSteps++;
        }

        //==================================================
        // Heuristic
        //==================================================

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var actions = actionsOut.ContinuousActions;

            actions[0] = Input.GetAxisRaw("Horizontal");

            actions[1] = Input.GetAxisRaw("Vertical");
        }
    }
}