using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class SeekerAgent : Agent
{
    [SerializeField] private float _moveSpeed = 0.5f;
    [SerializeField] private Renderer _renderer;

    [SerializeField] private float _explorationReward = 0.05f;
    [SerializeField] private float _wallCollisionPenalty = 0.5f;
    [SerializeField] private float _wallFlashDuration = 0.2f;

    private Rigidbody _rigidbody;
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

        _cumulativeReward += GetCumulativeReward();
    }

    public void MoveAgent(ActionSegment<int> actions)
    {
        var action = actions[0];

        switch (action)
        {
            case 1:
                _rigidbody.MovePosition(_rigidbody.position + Vector3.forward * _moveSpeed * Time.fixedDeltaTime);
                break;
            case 2: // move left
                this.transform.Rotate(0f, -90f, 0f);
                _rigidbody.MovePosition(_rigidbody.position + Vector3.left * _moveSpeed * Time.fixedDeltaTime);
                break;
            case 3: // rotate right
                this.transform.Rotate(0f, 90f, 0f);
                _rigidbody.MovePosition(_rigidbody.position + Vector3.right * _moveSpeed * Time.fixedDeltaTime);
                break;
            case 4: // move backward
                this.transform.Rotate(0f, 180f, 0f);
                _rigidbody.MovePosition(_rigidbody.position - transform.forward * _moveSpeed * Time.fixedDeltaTime);
                break;
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
