using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class SeekerAgent : Agent
{
    [SerializeField] private float _moveSpeed = 0.5f;
    [SerializeField] private float _rotationSpeed = 200f;
    [SerializeField] private Renderer _renderer;

    [SerializeField] private float _cellSize = 1f;
    [SerializeField] private float _explorationReward = 0.02f;
    [SerializeField] private float _wallCollisionPenalty = 0.5f;
    [SerializeField] private float _wallFlashDuration = 0.2f;

    private Rigidbody _rigidbody;
    private readonly HashSet<Vector2Int> _visitedCells = new HashSet<Vector2Int>();
    private int _currentEpisode = 0;
    private float _cumulativeReward = 0f;

    public override void Initialize()
    {
        Debug.Log("Agent initialized");

        _rigidbody = GetComponent<Rigidbody>();
        _currentEpisode = 0;
        _cumulativeReward = 0f;
    }

    public override void OnEpisodeBegin()
    {
        Debug.Log("Episode started");

        _currentEpisode++;
        _cumulativeReward = 0f;
        _renderer.material.color = Color.blue;

        _visitedCells.Clear();
        ResetAgentPose();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        float agentPosX_normalized = (transform.localPosition.x + 5f) / 10f;
        float agentPosZ_normalized = (transform.localPosition.z + 5f) / 10f;

        float agentRotation_normalized = (transform.localRotation.eulerAngles.y / 360f) * 2f - 1f;

        sensor.AddObservation(agentPosX_normalized);
        sensor.AddObservation(agentPosZ_normalized);
        sensor.AddObservation(agentRotation_normalized);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        MoveAgent(actions.DiscreteActions);

        if (MaxStep > 0)
        {
            AddReward(-2f / MaxStep);
        }

        RewardExplorationIfNewCell();

        _cumulativeReward += GetCumulativeReward();
    }

    public void MoveAgent(ActionSegment<int> actions)
    {
        var action = actions[0];

        switch (action)
        {
            case 1:
                _rigidbody.MovePosition(_rigidbody.position + transform.forward * _moveSpeed * Time.fixedDeltaTime);
                break;
            case 2: // rotate left
                _rigidbody.MoveRotation(_rigidbody.rotation * Quaternion.Euler(0f, -_rotationSpeed * Time.fixedDeltaTime, 0f));
                break;
            case 3: // rotate right
                _rigidbody.MoveRotation(_rigidbody.rotation * Quaternion.Euler(0f, _rotationSpeed * Time.fixedDeltaTime, 0f));
                break;
        }
    }

    private void RewardExplorationIfNewCell()
    {
        Vector3 pos = transform.localPosition;
        var cell = new Vector2Int(Mathf.FloorToInt(pos.x / _cellSize), Mathf.FloorToInt(pos.z / _cellSize));

        if (_visitedCells.Add(cell))
        {
            AddReward(_explorationReward);
        }
    }

    private void ResetAgentPose()
    {
        transform.SetLocalPositionAndRotation(new Vector3(0f, 0.22f, 0f), Quaternion.identity);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall"))
        {
            AddReward(-_wallCollisionPenalty);
            StopAllCoroutines();
            StartCoroutine(FlashWallHit());
        }
    }

    private IEnumerator FlashWallHit()
    {
        _renderer.material.color = Color.red;
        yield return new WaitForSeconds(_wallFlashDuration);
        _renderer.material.color = Color.blue;
    }
}
