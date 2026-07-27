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
    [SerializeField] private float _maxHiderDistance = 20f;
    [SerializeField] private float _hiderSightReward = 0.1f;      // bônus único ao avistar
    [SerializeField] private float _hiderApproachReward = 0.3f;   // por aproximar da última posição
    [SerializeField] private float _hiderArrivalThreshold = 0.5f; // distância p/ considerar "chegou"

    private float[] _wallProximities = new float[4];
    private Vector3 _initialLocalPosition;
    private Quaternion _initialLocalRotation;
    private Color _initialFloorColor;
    private int _elapsedSteps;
    private bool _episodeEnding;

    private bool _wasSeeingHider;

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

        _wasSeeingHider = false;

        transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);
        _movementSystem.ResetMovement();
        _perceptionSystem.ResetHiderMemory();
        _hider.Spawn();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        _perceptionSystem.ScanForHider();

        // Proximidade de paredes nas 4 direções. (4)
        _perceptionSystem.GetWallProximities(_wallProximities);
        sensor.AddObservation(_wallProximities);

        // Frescor da informação do hider. (2)
        sensor.AddObservation(_perceptionSystem.IsSeeingHider);
        sensor.AddObservation(_perceptionSystem.HasSeenHider);

        // Direção e distância até a última posição conhecida, no referencial do MUNDO — igual
        // ao referencial das ações (X/Z), então "alvo pra lá" mapeia direto em "mova pra lá". (3)
        if (_perceptionSystem.HasSeenHider)
        {
            Vector3 toLast = _perceptionSystem.LastKnownHiderPosition - transform.position;
            Vector2 planar = new Vector2(toLast.x, toLast.z);
            float distance = planar.magnitude;
            Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;

            sensor.AddObservation(unit.x); // direção mundo X
            sensor.AddObservation(unit.y); // direção mundo Z
            sensor.AddObservation(Mathf.Clamp01(distance / _maxHiderDistance));
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_episodeEnding)
        {
            return;
        }

        Vector3 preMovePosition = transform.position;

        Vector3 direction = new Vector3(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
        _movementSystem.Move(direction);

        AddReward(-_existentialPenalty / _maxEpisodeSteps);

        float closestWallProximity = 0f;
        for (int i = 0; i < _wallProximities.Length; i++)
        {
            closestWallProximity = Mathf.Max(closestWallProximity, _wallProximities[i]);
        }
        AddReward(-_wallProximityPenalty * closestWallProximity);

        RewardHiderProgress(preMovePosition);

        _elapsedSteps++;
        if (_elapsedSteps >= _maxEpisodeSteps)
        {
            FinishEpisode(won: false);
        }
    }

    // Recompensa por avistar o hider e por reduzir a distância até sua última posição conhecida.
    private void RewardHiderProgress(Vector3 preMovePosition)
    {
        // Bônus único no passo em que passa a enxergar o hider (borda de subida).
        bool seeing = _perceptionSystem.IsSeeingHider;
        if (seeing && !_wasSeeingHider)
        {
            AddReward(_hiderSightReward);
        }
        _wasSeeingHider = seeing;

        if (!_perceptionSystem.HasSeenHider)
        {
            return;
        }

        // Progresso do PRÓPRIO seeker rumo ao alvo atual: ambas as distâncias usam a mesma
        // posição conhecida, então um salto do alvo (novo avistamento) não gera falso ganho.
        Vector3 known = _perceptionSystem.LastKnownHiderPosition;
        float previousDistance = Vector3.Distance(preMovePosition, known);
        float currentDistance = Vector3.Distance(transform.position, known);
        AddReward(_hiderApproachReward * (previousDistance - currentDistance));

        // Chegou à última posição conhecida sem ver o hider: esquece e volta a procurar.
        if (!seeing && currentDistance <= _hiderArrivalThreshold)
        {
            _perceptionSystem.ForgetHider();
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