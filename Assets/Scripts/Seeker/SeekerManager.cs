using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

public class SeekerManager : Agent
{
    [SerializeField] private SeekerPerceptionSystem _perceptionSystem;
    [SerializeField] private SeekerMovementSystem _movementSystem;

    [SerializeField] private float _existentialPenalty = 2f;
    [SerializeField] private float _wallProximityPenalty = 0.01f;
    [SerializeField] private float _wallCollisionPenalty = 0.5f;

    private float[] _wallProximities = new float[4];
    private Vector3 _initialLocalPosition;
    private Quaternion _initialLocalRotation;

    public override void Initialize()
    {
        Debug.Log("Agent initialized");

        _perceptionSystem = this.GetComponentInChildren<SeekerPerceptionSystem>();
        _movementSystem = this.GetComponentInChildren<SeekerMovementSystem>();

        _initialLocalPosition = transform.localPosition;
        _initialLocalRotation = transform.localRotation;
    }
    public override void OnEpisodeBegin()
    {
        Debug.Log("Episode started");

        transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);
        _movementSystem.ResetMovement();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        _perceptionSystem.GetWallProximities(_wallProximities);

        sensor.AddObservation(_wallProximities);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        Vector3 direction = new Vector3(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
        _movementSystem.Move(direction);

        if (MaxStep > 0)
        {
            AddReward(-_existentialPenalty / MaxStep);
        }

        float closestWallProximity = 0f;
        for (int i = 0; i < _wallProximities.Length; i++)
        {
            closestWallProximity = Mathf.Max(closestWallProximity, _wallProximities[i]);
        }
        AddReward(-_wallProximityPenalty * closestWallProximity);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall"))
        {
            AddReward(-_wallCollisionPenalty);
        }
    }
}