using System.Collections;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

public class SeekerManager : Agent
{
    [SerializeField] private SeekerPerceptionSystem _perceptionSystem;
    [SerializeField] private SeekerMovementSystem _movementSystem;
    [SerializeField] private HiderAgent _hider;
    [SerializeField] private Renderer _floorRenderer;

    [SerializeField] private int _maxEpisodeSteps = 500;
    [SerializeField] private float _episodeEndDelay = 1.5f;
    [SerializeField] private float _existentialPenalty = 2f;
    [SerializeField] private float _wallProximityPenalty = 0.01f;
    [SerializeField] private float _wallCollisionPenalty = 0.5f;
    [SerializeField] private float _hiderFoundReward = 1f;

    private float[] _wallProximities = new float[4];
    private Vector3 _initialLocalPosition;
    private Quaternion _initialLocalRotation;
    private Color _initialFloorColor;
    private int _elapsedSteps;
    private bool _episodeEnding;

    public override void Initialize()
    {
        Debug.Log("Agent initialized");

        _perceptionSystem = this.GetComponentInChildren<SeekerPerceptionSystem>();
        _movementSystem = this.GetComponentInChildren<SeekerMovementSystem>();

        _initialLocalPosition = transform.localPosition;
        _initialLocalRotation = transform.localRotation;

        _initialFloorColor = _floorRenderer.material.color;
    }
    public override void OnEpisodeBegin()
    {
        Debug.Log("Episode started");

        _floorRenderer.material.color = _initialFloorColor;

        _elapsedSteps = 0;
        _episodeEnding = false;

        transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);
        _movementSystem.ResetMovement();
        _hider.Spawn();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        _perceptionSystem.GetWallProximities(_wallProximities);

        sensor.AddObservation(_wallProximities);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_episodeEnding)
        {
            return;
        }

        Vector3 direction = new Vector3(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
        _movementSystem.Move(direction);

        AddReward(-_existentialPenalty / _maxEpisodeSteps);

        float closestWallProximity = 0f;
        for (int i = 0; i < _wallProximities.Length; i++)
        {
            closestWallProximity = Mathf.Max(closestWallProximity, _wallProximities[i]);
        }
        AddReward(-_wallProximityPenalty * closestWallProximity);

        _elapsedSteps++;
        if (_elapsedSteps >= _maxEpisodeSteps)
        {
            FinishEpisode(won: false);
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        HandleContact(collision.gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        HandleContact(other.gameObject);
    }

    private void HandleContact(GameObject other)
    {
        if (_episodeEnding)
        {
            return;
        }

        if (other.CompareTag("Wall"))
        {
            AddReward(-_wallCollisionPenalty);
        }
        else if (other.CompareTag("Goal"))
        {
            AddReward(_hiderFoundReward);
            FinishEpisode(won: true);
        }
    }

    private void FinishEpisode(bool won)
    {
        _episodeEnding = true;
        _floorRenderer.material.color = won ? Color.green : Color.red;
        StartCoroutine(EndEpisodeAfterDelay());
    }

    private IEnumerator EndEpisodeAfterDelay()
    {
        yield return new WaitForSeconds(_episodeEndDelay);
        EndEpisode();
    }
}