using System.Collections;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Orquestrador do agente: é o único que conhece os callbacks do ML-Agents e a ordem do step
/// (sentir -> observar -> agir -> avaliar -> terminar). Não calcula recompensa nem lê o mundo;
/// monta o SeekerStepContext e delega.
/// </summary>
public class SeekerManager : Agent
{
    // 4 proximidades de parede + 2 flags de frescor + 3 do vetor até a última posição conhecida.
    public const int ObservationCount = 9;

    [Header("-----Systems-----")]
    [SerializeField] private SeekerPerceptionSystem _perceptionSystem;
    [SerializeField] private SeekerMovementSystem _movementSystem;
    [SerializeField] private SeekerRewardSystem _rewardSystem;
    [SerializeField] private SeekerArenaController _arenaController;

    [Header("-----Settings-----")]
    [SerializeField] private int _maxEpisodeSteps = 5000;
    [SerializeField] private float _maxHiderDistance = 20f;

    private Vector3 _initialLocalPosition;
    private Quaternion _initialLocalRotation;
    private int _elapsedSteps;
    private bool _episodeEnding;

    // Percepção é amostrada uma vez por step de física. Como CollectObservations e
    // OnActionReceived rodam em cadências diferentes (Decision Period > 1), quem chegar primeiro
    // dispara o Tick e o outro reaproveita o mesmo snapshot.
    private int _physicsStep;
    private int _lastPerceptionStep = -1;

    public override void Initialize()
    {
        // Checagem explícita, e não ??=: o operador de null-coalescing ignora o "fake null" que
        // o Unity devolve para referências não atribuídas.
        if (_perceptionSystem == null)
            _perceptionSystem = GetComponentInChildren<SeekerPerceptionSystem>();

        if (_movementSystem == null)
            _movementSystem = GetComponentInChildren<SeekerMovementSystem>();

        if (_rewardSystem == null)
            _rewardSystem = GetComponentInChildren<SeekerRewardSystem>();

        if (_arenaController == null)
            _arenaController = GetComponentInParent<SeekerArenaController>();

        _initialLocalPosition = transform.localPosition;
        _initialLocalRotation = transform.localRotation;

        ValidateSetup();
    }

    public override void OnEpisodeBegin()
    {
        _elapsedSteps = 0;
        _episodeEnding = false;

        _arenaController.ResetEpisode();

        if (_arenaController.TryGetSeekerSpawn(out Vector3 position, out Quaternion rotation))
            transform.SetPositionAndRotation(position, rotation);
        else
            transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

        _movementSystem.ResetMovement();
        _perceptionSystem.ResetHiderMemory();
        _rewardSystem.ResetEpisode();

        // O reset acontece no mesmo step de física que encerrou o episódio anterior, então o
        // dedup precisa ser invalidado: sem isso a primeira observação da nova run enxergaria
        // o snapshot tirado antes do respawn.
        _lastPerceptionStep = -1;
        TickPerception();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        TickPerception();

        // Proximidade de paredes nas 4 direções. (4)
        sensor.AddObservation(_perceptionSystem.WallProximities);

        // Frescor da informação do hider. (2)
        sensor.AddObservation(_perceptionSystem.IsSeeingHider);
        sensor.AddObservation(_perceptionSystem.HasSeenHider);

        // Direção e distância até a última posição conhecida, no referencial do MUNDO — igual
        // ao referencial das ações (X/Z), então "alvo pra lá" mapeia direto em "mova pra lá". (3)
        if (_perceptionSystem.HasSeenHider)
        {
            Vector3 toLast = _perceptionSystem.LastKnownHiderPosition - transform.position;
            Vector2 planar = new(toLast.x, toLast.z);
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
            return;

        TickPerception();

        Vector3 preMovePosition = transform.position;

        Vector3 direction = new(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
        _movementSystem.Move(direction);

        AddReward(_rewardSystem.EvaluateStep(BuildStepContext(preMovePosition)));

        _perceptionSystem.ForgetIfArrived(transform.position);

        _elapsedSteps++;
        if (_elapsedSteps >= _maxEpisodeSteps)
            FinishEpisode(won: false);
    }

    private void FixedUpdate() => _physicsStep++;

    private void TickPerception()
    {
        if (_lastPerceptionStep == _physicsStep)
            return;

        _lastPerceptionStep = _physicsStep;
        _perceptionSystem.Tick();
    }

    private SeekerStepContext BuildStepContext(Vector3 preMovePosition) => new(
        preMovePosition,
        transform.position,
        _perceptionSystem.IsSeeingHider,
        _perceptionSystem.HasSeenHider,
        _perceptionSystem.LastKnownHiderPosition,
        _perceptionSystem.ClosestWallProximity,
        _maxEpisodeSteps);

    private void OnCollisionEnter(Collision collision) => HandleContact(collision.gameObject);

    private void OnTriggerEnter(Collider other) => HandleContact(other.gameObject);

    private void HandleContact(GameObject other)
    {
        if (_episodeEnding)
            return;

        if (other.CompareTag("Wall"))
        {
            AddReward(_rewardSystem.WallCollisionPenalty);
        }
        else if (other.CompareTag("Goal"))
        {
            AddReward(_rewardSystem.HiderFoundReward);
            FinishEpisode(won: true);
        }
    }

    private void FinishEpisode(bool won)
    {
        _episodeEnding = true;
        _arenaController.ShowOutcome(won);

        float delay = _arenaController.EpisodeEndDelay;
        if (delay <= 0f)
        {
            EndEpisode();
            return;
        }

        StartCoroutine(EndEpisodeAfterDelay(delay));
    }

    private IEnumerator EndEpisodeAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        EndEpisode();
    }

    // Erros de wiring em ML-Agents são silenciosos e só aparecem como treino que não converge.
    private void ValidateSetup()
    {
        if (_perceptionSystem == null || _movementSystem == null || _rewardSystem == null)
            Debug.LogError($"{name}: sistema do seeker faltando — confira os componentes filhos.", this);

        if (_arenaController == null)
            Debug.LogError($"{name}: SeekerArenaController não encontrado nos pais.", this);

        var behaviorParameters = GetComponent<BehaviorParameters>();
        if (behaviorParameters == null)
            return;

        int declared = behaviorParameters.BrainParameters.VectorObservationSize;
        if (declared != ObservationCount)
        {
            Debug.LogError(
                $"{name}: VectorObservationSize = {declared} mas o agente emite {ObservationCount} observações. " +
                "Ajuste no Behavior Parameters, senão o treino roda com o vetor truncado.", this);
        }
    }
}
