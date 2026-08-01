using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Agent
{
    [System.Serializable]
    public class SeekerState
    {
        //==================================================
        // Episode
        //==================================================

        public int EpisodeSteps;

        public Vector3 SpawnPosition;

        public Vector3 ExplorationOrigin;

        //==================================================
        // Transform
        //==================================================

        public Vector3 Position;

        public Vector3 Forward;

        public Quaternion Rotation;

        //==================================================
        // Movement
        //==================================================

        public Vector3 DesiredDirection;

        public Vector3 LastMoveDirection;

        public Vector3 CurrentVelocity;

        public bool IsBlocked;

        public float TotalDistanceTravelled;

        public float ExplorationDistance;

        public float MaxExplorationDistance;

        //==================================================
        // Vision
        //==================================================

        public bool HasVisualContact;

        public Vector3 TargetPosition;

        public Vector3 TargetDirection;

        public float TargetDistance;

        //==================================================
        // Target Memory
        //==================================================

        public bool HasTargetMemory;

        public Vector3 LastKnownTargetPosition;

        public float MemoryDistance;

        public float TargetMemoryStrength;

        //==================================================
        // Smell
        //==================================================

        public bool HasSmell;

        public Vector3 SmellDirection;

        public float SmellIntensity;

        //==================================================
        // Exploration
        //==================================================

        public HashSet<Vector2Int> VisitedCells =
            new HashSet<Vector2Int>();

        //==================================================
        // Reward Flags
        //==================================================

        public bool TargetCaptured;

        public bool WallCollision;

        public bool InLoop;

        public int IdleSteps;

        //==================================================
        // Perception
        //==================================================
        
        public float TimeWithoutStimulus;

        //==================================================
        // Reset
        //==================================================

        public void Reset(Vector3 spawnPosition)
        {
            EpisodeSteps = 0;

            SpawnPosition = spawnPosition;

            ExplorationOrigin = spawnPosition;

            Position = spawnPosition;

            Forward = Vector3.forward;

            Rotation = Quaternion.identity;

            DesiredDirection = Vector3.zero;

            LastMoveDirection = Vector3.zero;

            CurrentVelocity = Vector3.zero;

            IsBlocked = false;

            TotalDistanceTravelled = 0f;

            ExplorationDistance = 0f;

            MaxExplorationDistance = 0f;

            HasVisualContact = false;

            TargetPosition = Vector3.zero;

            TargetDirection = Vector3.zero;

            TargetDistance = 0f;

            HasTargetMemory = false;

            LastKnownTargetPosition = Vector3.zero;

            MemoryDistance = 0f;

            TargetMemoryStrength = 0f;

            HasSmell = false;

            SmellDirection = Vector3.zero;

            SmellIntensity = 0f;

            TimeWithoutStimulus = 0f;

            VisitedCells.Clear();

            TargetCaptured = false;

            WallCollision = false;

            InLoop = false;

            IdleSteps = 0;
        }

        //==================================================
        // Exploration Helpers
        //==================================================

        public Vector2Int CurrentCell(float cellSize = 1f)
        {
            return new Vector2Int(
                Mathf.FloorToInt(Position.x / cellSize),
                Mathf.FloorToInt(Position.z / cellSize)
            );
        }

        public bool RegisterVisitedCell(float cellSize = 1f)
        {
            return VisitedCells.Add(CurrentCell(cellSize));
        }
    }
}