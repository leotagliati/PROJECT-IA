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
    // 8 proximidades de parede + 2 flags de frescor + 3 do vetor até a última posição
    // conhecida + a janela 5x5 de células já visitadas.
    public const int ObservationCount = 13 + 25;

    [Header("-----Systems-----")]
    [SerializeField] private SeekerPerceptionSystem _perceptionSystem;
    [SerializeField] private SeekerMovementSystem _movementSystem;
    [SerializeField] private SeekerRewardSystem _rewardSystem;
    [SerializeField] private SeekerExplorationMemory _explorationMemory;
    [SerializeField] private SeekerArenaController _arenaController;
    [SerializeField] private SeekerAnimationSystem _animationSystem;

    [Header("-----Settings-----")]
    [SerializeField] private int _maxEpisodeSteps = 5000;
    [SerializeField] private float _maxHiderDistance = 20f;

    private Vector3 _initialLocalPosition;
    private Quaternion _initialLocalRotation;
    private Vector3 _previousStepPosition;
    private int _elapsedSteps;
    private bool _episodeEnding;
    private bool _touchingWall;

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

        if (_explorationMemory == null)
            _explorationMemory = GetComponentInChildren<SeekerExplorationMemory>();

        if (_arenaController == null)
            _arenaController = GetComponentInParent<SeekerArenaController>();

        if (_animationSystem == null)
            _animationSystem = GetComponentInChildren<SeekerAnimationSystem>();

        // A grade é indexada em coordenadas da arena: com 9 cópias do ambiente na cena,
        // usar coordenadas de mundo faria as arenas compartilharem células.
        if (_explorationMemory != null && _arenaController != null)
            _explorationMemory.Configure(_arenaController.transform);

        _initialLocalPosition = transform.localPosition;
        _initialLocalRotation = transform.localRotation;

        ValidateSetup();
    }

    public override void OnEpisodeBegin()
    {
        _elapsedSteps = 0;
        _episodeEnding = false;
        _touchingWall = false;

        _arenaController.ResetEpisode();

        if (_arenaController.TryGetSeekerSpawn(out Vector3 position, out Quaternion rotation))
            transform.SetPositionAndRotation(position, rotation);
        else
            transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

        // Âncora do termo de aproximação. Sem reancorar no respawn, o primeiro step do
        // episódio mediria o salto do teleporte como progresso rumo ao hider.
        _previousStepPosition = transform.position;

        _movementSystem.ResetMovement();
        _perceptionSystem.ResetHiderMemory();
        _explorationMemory.ResetEpisode();
        _rewardSystem.ResetEpisode();

        if (_animationSystem != null)
            _animationSystem.ResetEpisode();

        // O reset acontece no mesmo step de física que encerrou o episódio anterior, então o
        // dedup precisa ser invalidado: sem isso a primeira observação da nova run enxergaria
        // o snapshot tirado antes do respawn.
        _lastPerceptionStep = -1;
        TickPerception();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        TickPerception();

        // Proximidade de paredes nas 8 direções. (8)
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

        // Janela 5x5 de células já visitadas ao redor do agente. (25)
        sensor.AddObservation(_explorationMemory.Window);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_episodeEnding)
            return;

        TickPerception();

        // Avalia primeiro, age depois: esta posição já é o resultado do move pedido no step
        // anterior — a simulação de física roda entre um OnActionReceived e o próximo.
        Vector3 currentPosition = transform.position;

        AddReward(_rewardSystem.EvaluateStep(BuildStepContext(currentPosition)));
        _previousStepPosition = currentPosition;

        Vector3 direction = new(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
        _movementSystem.Move(direction);

        if (_animationSystem != null)
            _animationSystem.Tick(direction, _perceptionSystem.IsSeeingHider);

        // Consumida depois de cobrada. Se o contato continuar, o OnCollisionStay do próximo
        // step de física marca de novo; se acabou, ela fica false sozinha.
        _touchingWall = false;

        _perceptionSystem.ForgetIfArrived(currentPosition);

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
        _explorationMemory.Tick(transform.position);
    }

    private SeekerStepContext BuildStepContext(Vector3 currentPosition) => new(
        _previousStepPosition,
        currentPosition,
        _perceptionSystem.IsSeeingHider,
        _perceptionSystem.HasSeenHider,
        _perceptionSystem.LastKnownHiderPosition,
        _perceptionSystem.ClosestWallProximity,
        _maxEpisodeSteps,
        _touchingWall,
        _explorationMemory.EnteredNewCell,
        _arenaController.ApproachRewardScale,
        _arenaController.WallProximityScale);

    // Pegar o hider é um evento discreto, então continua por evento.
    private void OnCollisionEnter(Collision collision) => HandleContact(collision.gameObject);

    private void OnTriggerEnter(Collider other) => HandleContact(other.gameObject);

    // Encostar em parede é uma condição contínua. OnCollisionStay dispara uma vez por step
    // POR collider, então aqui só marca a flag — quem cobra é o step, uma vez só, mesmo que
    // o agente esteja tocando várias paredes ao mesmo tempo numa quina.
    private void OnCollisionStay(Collision collision)
    {
        if (IsWall(collision.gameObject))
            _touchingWall = true;
    }

    // Parede é identificada por LAYER, e não por tag. A percepção já usa _wallLayer nos
    // raycasts, então a tag era uma segunda fonte de verdade para a mesma pergunta — e foi
    // exatamente o que quebrou no Map_8: os objetos do mapa estão na layer Wall, mas nenhum
    // deles leva a tag, então a penalidade de contato simplesmente nunca era cobrada. Sem
    // erro, sem log: só um termo da recompensa morto.
    private bool IsWall(GameObject other) =>
        (_perceptionSystem.WallLayer.value & (1 << other.layer)) != 0;

    private void HandleContact(GameObject other)
    {
        if (_episodeEnding)
            return;

        if (other.CompareTag("Goal"))
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
        if (_perceptionSystem == null || _movementSystem == null || _rewardSystem == null || _explorationMemory == null)
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
