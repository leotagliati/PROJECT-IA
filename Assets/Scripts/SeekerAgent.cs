using System.Collections;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class SeekerAgent : Agent
{
    [SerializeField] private Transform _targetTransform;
    [SerializeField] private float _moveSpeed = 0.5f;
    [SerializeField] private float _rotationSpeed = 200f;

    [SerializeField] private Renderer _renderer;
    [SerializeField] private float _episodeEndDelay = 0.5f;

    private int _currentEpisode = 0;
    private float _cumulativeReward = 0f;
    private bool _episodeEnding = false;

    public override void Initialize()
    {
        Debug.Log("Agent initialized");

        _currentEpisode = 0;
        _cumulativeReward = 0f;
    }
    public override void OnEpisodeBegin()
    {
        Debug.Log("Episode started");

        _currentEpisode++;
        _cumulativeReward = 0f;
        _episodeEnding = false;
        _renderer.material.color = Color.blue;

        SpawnObjects();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Debug.Log("Collecting observations");

        float goalPosX_normalized = (_targetTransform.localPosition.x + 5f) / 10f;
        float goalPosZ_normalized = (_targetTransform.localPosition.z + 5f) / 10f;

        float agentPosX_normalized = (transform.localPosition.x + 5f) / 10f;
        float agentPosZ_normalized = (transform.localPosition.z + 5f) / 10f;

        float agentRotation_normalized = (transform.localRotation.eulerAngles.y / 360f) * 2f - 1f;

        sensor.AddObservation(goalPosX_normalized);
        sensor.AddObservation(goalPosZ_normalized);
        sensor.AddObservation(agentPosX_normalized);
        sensor.AddObservation(agentPosZ_normalized);
        sensor.AddObservation(agentRotation_normalized);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        Debug.Log("Action received");

        MoveAgent(actions.DiscreteActions);

        AddReward(-2f / MaxStep);

        _cumulativeReward += GetCumulativeReward();
    }

    public void MoveAgent(ActionSegment<int> actions)
    {
        var action = actions[0];

        switch (action)
        {
            case 1:
                transform.position += transform.forward * _moveSpeed * Time.deltaTime;
                break;
            case 2: // rotate left
                transform.Rotate(Vector3.up, -_rotationSpeed * Time.deltaTime);
                break;
            case 3: // rotate right
                transform.Rotate(Vector3.up, _rotationSpeed * Time.deltaTime);
                break;
        }
    }

    private void SpawnObjects()
    {
        transform.SetLocalPositionAndRotation(new Vector3(0f, 0.22f, 0f), Quaternion.identity);
        float randomAngle = Random.Range(0f, 360f);
        Vector3 randomDirection = Quaternion.Euler(0f, randomAngle, 0f) * Vector3.forward;

        float randomDistance = Random.Range(1f, 2.5f);

        Vector3 targetPosition = transform.localPosition + randomDirection * randomDistance;

        _targetTransform.localPosition = new Vector3(targetPosition.x, 0.22f, targetPosition.z);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (_episodeEnding)
        {
            return;
        }

        if (collision.gameObject.CompareTag("Wall"))
        {
            SetReward(-1f);
            _renderer.material.color = Color.red;
            StartCoroutine(EndEpisodeDelayed());
        }
        else if (collision.gameObject.CompareTag("Goal"))
        {
            GoalReached();
        }
    }

    private void GoalReached()
    {
        SetReward(1f);
        _renderer.material.color = Color.green;
        StartCoroutine(EndEpisodeDelayed());
    }

    private IEnumerator EndEpisodeDelayed()
    {
        _episodeEnding = true;
        yield return new WaitForSeconds(_episodeEndDelay);
        EndEpisode();
    }

}
