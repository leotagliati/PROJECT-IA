using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Orquestrador do agente explorador: conhece os callbacks do ML-Agents e a ordem do step
    /// (sentir -> observar -> agir -> avaliar -> terminar). Não calcula recompensa nem varre o
    /// mundo; monta o <see cref="GraphStepContext"/> e delega.
    ///
    /// OBSERVAÇÃO — a decisão de projeto mais importante deste arquivo:
    /// o agente NÃO recebe a lista de todos os nós, nem a lista dos nós da região. Ele recebe
    /// uma visão EGOCÊNTRICA: o nó em que está, os K vizinhos dele (direção, distância, se já
    /// foram visitados) e dois resumos escalares (fração do grafo e da área atual já cobertas).
    ///
    /// Por quê:
    ///   - "Todos os nós" tem tamanho fixo amarrado a ESTE mapa. Cada slot do vetor vira "o nó
    ///     17", que não quer dizer nada em outro mapa — trocar de cena obriga a retreinar do
    ///     zero, e você quer justamente levar a política para um cenário novo depois.
    ///   - "Nós da região" tem o mesmo problema em escala menor, e ainda precisa de padding para
    ///     a maior sala; o slot continua sem significado geométrico estável.
    ///   - A visão local é a mesma em qualquer mapa: "tenho uma saída à minha direita, ainda não
    ///     visitada". É isso que transfere. É também o mínimo necessário para decidir o próximo
    ///     passo — que é a única decisão que o agente toma.
    /// O que a visão local NÃO resolve sozinho é sair de um beco: os vizinhos imediatos podem
    /// estar todos visitados e a resposta certa estar a cinco nós dali. Esse buraco é tapado
    /// pela DICA DE FRONTEIRA (BFS no grafo até o não-visitado mais próximo), que é um
    /// escalar + direção e continua independente do tamanho do mapa. O currículo desliga a dica
    /// nas lições finais, para o comportamento não virar "seguir a seta".
    ///
    /// Se um dia você quiser mesmo alimentar N nós de tamanho variável, o caminho é o
    /// BufferSensorComponent do ML-Agents (atenção sobre lista de entidades) — não um vetor
    /// achatado com um slot por nó.
    ///
    /// Percepção de PAREDE não passa por aqui: use um Ray Perception Sensor 3D no prefab do
    /// agente. Ele já entrega os raycasts como observação, fora do VectorObservationSize.
    /// </summary>
    public class GraphExplorerManager : Agent
    {
        // Por vizinho: direção X, direção Z, distância normalizada, visitado, slot válido.
        private const int FloatsPerNeighbor = 5;

        // 1 (está num nó) + 3 (vetor até o nó atual) + 2 (coberturas) + 4 (fronteira).
        private const int GlobalObservations = 10;

        [Header("-----Systems-----")]
        [SerializeField] private GraphExplorationMemory _memory;
        [SerializeField] private GraphRewardSystem _rewardSystem;
        // Reaproveitado do seeker de propósito: é um driver de Rigidbody sem nenhuma regra de
        // seeker dentro (Move / ResetMovement). Duplicá-lo criaria dois lugares para ajustar a
        // mesma física. Se um dia ele ganhar lógica específica do seeker, copie-o para cá.
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphArenaController _arenaController;

        /// <summary>Como "explorei X% do mapa" é medido.</summary>
        public enum CoverageMeasure
        {
            /// <summary>Fração dos nós ativos visitados. Cobertura geométrica do chão.</summary>
            NodeFraction,

            /// <summary>
            /// Fração do orçamento das regiões coletada. Coerente com a recompensa: um corredor
            /// de orçamento 0.2 conta pouco, a sala de 2.0 conta muito.
            /// </summary>
            RegionBudget,
        }

        [Header("-----Cobertura-----")]
        // Qual medida encerra o episódio (contra o coverage_target do currículo) E é observada
        // pelo agente. Os dois de propósito na mesma chave: observar uma barra de progresso
        // diferente da que julga o episódio é a receita de um agente que "acha" que está indo bem.
        //
        // RegionBudget é o default porque é o único que fica imune à densidade de nós — com
        // NodeFraction, uma sala descrita com 30 nós domina o alvo de cobertura mesmo valendo
        // o mesmo que um corredor de 3, e a normalização por região vira meia-normalização.
        [SerializeField] private CoverageMeasure _coverageMeasure = CoverageMeasure.RegionBudget;

        [Header("-----Observação-----")]
        // Quantos vizinhos cabem na observação. Nós com mais vizinhos que isto têm os excedentes
        // (os mais distantes) cortados — o aviso no Play te diz se está acontecendo. 6 cobre
        // com folga cruzamentos de corredor; salas muito auto-ligadas passam disso e é sinal de
        // que faltou podar ligações redundantes na autoria.
        [SerializeField] private int _neighborSlots = 6;

        // Normalizador da distância em METROS até um nó. Da ordem da maior aresta do mapa.
        [SerializeField] private float _maxNodeDistance = 15f;

        // Normalizador da distância em ARESTAS até a fronteira. Da ordem do diâmetro do grafo.
        [SerializeField] private int _maxGraphDistance = 20;

        [Header("-----Settings-----")]
        // Em steps de FÍSICA, não em decisões: com TakeActionsBetweenDecisions ligado no
        // DecisionRequester (que é como a cena está montada), OnActionReceived roda todo
        // FixedUpdate e é ele quem incrementa o contador. 4000 steps = 80 s a 0.02 de timestep,
        // ou 800 decisões com Decision Period 5 — que é o número que aparece no
        // "Environment/Episode Length" do TensorBoard.
        [SerializeField] private int _maxEpisodeSteps = 4000;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private NavGraph _graph;

        private int _elapsedSteps;
        private bool _episodeEnding;
        private bool _touchingWall;

        // Distância de fronteira na decisão anterior. O delta entre decisões é o que vira
        // shaping — medir isso dentro da memória daria o delta de um step de física, que é
        // uma fração do que o agente controla com uma ação.
        private int _frontierDistanceAtLastDecision;
        private bool _hadFrontierAtLastDecision;

        // Estado do shaping denso: qual nó era o próximo passo da fronteira no step anterior e a
        // que distância em metros ele estava. Guardar o ÍNDICE, e não só a distância, é o que
        // permite comparar por identidade — quando a BFS reaponta para outro nó a distância
        // salta, e sem essa checagem o salto viraria recompensa (ou punição) fantasma.
        private int _frontierNextStepAtLastDecision = -1;
        private float _frontierApproachDistanceAtLastDecision;

        private readonly List<int> _neighborBuffer = new List<int>();
        private Comparison<int> _byDistanceFromNode;
        private Comparison<int> _byAngleFromNode;
        private Vector3 _sortOrigin;

        public int ObservationSize => _neighborSlots * FloatsPerNeighbor + GlobalObservations;

        private float CurrentCoverage => _coverageMeasure == CoverageMeasure.RegionBudget
            ? _memory.VisitedBudgetFraction
            : _memory.VisitedFraction;

        public override void Initialize()
        {
            // Checagem explícita, e não ??=: o operador de null-coalescing ignora o "fake null"
            // que o Unity devolve para referências não atribuídas.
            if (_memory == null)
                _memory = GetComponentInChildren<GraphExplorationMemory>();

            if (_rewardSystem == null)
                _rewardSystem = GetComponentInChildren<GraphRewardSystem>();

            if (_movementSystem == null)
                _movementSystem = GetComponentInChildren<SeekerMovementSystem>();

            if (_arenaController == null)
                _arenaController = GetComponentInParent<GraphArenaController>();

            _byDistanceFromNode = CompareByDistance;
            _byAngleFromNode = CompareByAngle;

            _initialLocalPosition = transform.localPosition;
            _initialLocalRotation = transform.localRotation;

            if (_arenaController != null)
                _graph = _arenaController.Graph;

            if (_graph != null && _memory != null)
            {
                _graph.EnsureBaked();
                _memory.Configure(_graph);
            }

            ValidateSetup();
        }

        public override void OnEpisodeBegin()
        {
            _elapsedSteps = 0;
            _episodeEnding = false;
            _touchingWall = false;

            _arenaController.ResetEpisode();

            if (_arenaController.TryGetSpawn(out Vector3 position, out Quaternion rotation))
                transform.SetPositionAndRotation(position, rotation);
            else
                transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

            _movementSystem.ResetMovement();
            _memory.ResetEpisode();
            _rewardSystem.ResetEpisode();

            // Registra de imediato o nó do spawn: sem isto o primeiro nó do episódio pagaria
            // recompensa de descoberta por o agente simplesmente ter nascido em cima dele.
            _memory.Tick(transform.position);
            _memory.ClearStepFlags();

            _hadFrontierAtLastDecision = _memory.HasFrontier;
            _frontierDistanceAtLastDecision = _memory.FrontierDistance;
            RememberFrontierApproach();
        }

        // A memória é amostrada a cada step de FÍSICA. Com Decision Period > 1 o agente percorre
        // vários steps entre duas decisões e pode cruzar o raio de um nó inteiro no meio — a
        // visita seria perdida se a amostragem acompanhasse a cadência das decisões.
        private void FixedUpdate()
        {
            if (_episodeEnding || _graph == null)
                return;

            _memory.Tick(transform.position);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector3 position = transform.position;
            int current = _memory.CurrentNodeIndex;

            // ---- Nó atual (4) ----
            sensor.AddObservation(_memory.IsAtNode);
            AddDirectionAndDistance(sensor, position, current >= 0 ? _graph.NodePosition(current) : position, current >= 0);

            // ---- Cobertura (2) ----
            // O quanto falta explorar, no geral e na região atual. É o que permite à política
            // decidir entre "esta sala ainda tem coisa" e "hora de procurar a porta".
            sensor.AddObservation(CurrentCoverage);
            sensor.AddObservation(_memory.CurrentRegionVisitedFraction);

            // ---- Fronteira (4) ----
            float hint = _arenaController.FrontierHintScale;
            bool showFrontier = _memory.HasFrontier && hint > 0f && _memory.FrontierNextStep >= 0;

            AddDirectionAndDistance(
                sensor,
                position,
                showFrontier ? _graph.NodePosition(_memory.FrontierNextStep) : position,
                showFrontier);

            // Distância em ARESTAS até o alvo: diz se a fronteira é "logo ali" ou "do outro lado
            // do mapa", informação que a direção sozinha não carrega.
            sensor.AddObservation(showFrontier ? Mathf.Clamp01((float)_memory.FrontierDistance / _maxGraphDistance) : 0f);

            // ---- Vizinhos (5 x _neighborSlots) ----
            FillNeighborBuffer(current);

            for (int slot = 0; slot < _neighborSlots; slot++)
            {
                if (slot >= _neighborBuffer.Count)
                {
                    // Slot vazio: zeros e a flag de validade em 0. O padding precisa ser
                    // distinguível de um vizinho real — senão "não existe saída aqui" e "existe
                    // uma saída exatamente na minha posição" chegam à rede como o mesmo vetor.
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    continue;
                }

                int neighbor = _neighborBuffer[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(neighbor), true);
                sensor.AddObservation(_memory.IsVisited(neighbor) ? 1f : 0f);
                sensor.AddObservation(1f);
            }
        }

        // Direção planar (X, Z) no referencial do MUNDO — o mesmo das ações, então "alvo pra lá"
        // mapeia direto em "mova pra lá", sem rotação nenhuma para a rede aprender.
        private void AddDirectionAndDistance(VectorSensor sensor, Vector3 from, Vector3 to, bool valid)
        {
            if (!valid)
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                return;
            }

            Vector3 delta = to - from;
            Vector2 planar = new(delta.x, delta.z);
            float distance = planar.magnitude;
            Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;

            sensor.AddObservation(unit.x);
            sensor.AddObservation(unit.y);
            sensor.AddObservation(Mathf.Clamp01(distance / _maxNodeDistance));
        }

        /// <summary>
        /// Vizinhos ativos do nó atual, no máximo <see cref="_neighborSlots"/>, em ordem ESTÁVEL.
        ///
        /// A ordem é o detalhe que faz ou quebra esta observação: se o mesmo vizinho aparecer no
        /// slot 2 num step e no slot 4 no seguinte, a rede recebe ruído puro. Ordenar por ângulo
        /// no mundo (a partir do nó, não do agente) dá uma ordem que só muda quando a geometria
        /// muda — e ainda faz o slot ter significado geométrico: "a saída mais ao norte".
        /// Quando há mais vizinhos que slots, os mais distantes são os cortados.
        /// </summary>
        private void FillNeighborBuffer(int current)
        {
            _neighborBuffer.Clear();

            if (current < 0)
                return;

            _sortOrigin = _graph.NodePosition(current);

            foreach (int neighbor in _graph.GetNeighbors(current))
            {
                if (_graph.IsNodeEnabled(neighbor))
                    _neighborBuffer.Add(neighbor);
            }

            if (_neighborBuffer.Count > _neighborSlots)
            {
                _neighborBuffer.Sort(_byDistanceFromNode);
                _neighborBuffer.RemoveRange(_neighborSlots, _neighborBuffer.Count - _neighborSlots);
            }

            _neighborBuffer.Sort(_byAngleFromNode);
        }

        private int CompareByDistance(int a, int b)
        {
            float da = (_graph.NodePosition(a) - _sortOrigin).sqrMagnitude;
            float db = (_graph.NodePosition(b) - _sortOrigin).sqrMagnitude;
            return da.CompareTo(db);
        }

        private int CompareByAngle(int a, int b)
        {
            float angleA = AngleFromOrigin(a);
            float angleB = AngleFromOrigin(b);
            int comparison = angleA.CompareTo(angleB);

            // Desempate pelo índice: dois vizinhos exatamente no mesmo ângulo (raro, mas
            // acontece com nós empilhados) manteriam ordem indefinida sem isto.
            return comparison != 0 ? comparison : a.CompareTo(b);
        }

        private float AngleFromOrigin(int node)
        {
            Vector3 delta = _graph.NodePosition(node) - _sortOrigin;
            return Mathf.Atan2(delta.z, delta.x);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_episodeEnding)
                return;

            AddReward(_rewardSystem.EvaluateStep(BuildStepContext()));

            // Consumidas depois de cobradas. A memória volta a acumular a partir do próximo
            // step de física.
            _hadFrontierAtLastDecision = _memory.HasFrontier;
            _frontierDistanceAtLastDecision = _memory.FrontierDistance;
            RememberFrontierApproach();
            _memory.ClearStepFlags();
            _touchingWall = false;

            Vector3 direction = new(actions.ContinuousActions[0], 0f, actions.ContinuousActions[1]);
            _movementSystem.Move(direction);

            if (CurrentCoverage >= _arenaController.CoverageTarget)
            {
                AddReward(_rewardSystem.FullCoverageReward);
                FinishEpisode(covered: true);
                return;
            }

            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps)
                FinishEpisode(covered: false);
        }

        private GraphStepContext BuildStepContext()
        {
            // O delta só é comparável quando os dois lados mediram a distância até o mesmo
            // alvo. Ao visitar um nó novo a fronteira salta para outro lugar do mapa, e sem
            // este cuidado o shaping cobraria como retrocesso exatamente o step em que o agente
            // acertou. A descoberta já é paga pelo _newNodeReward; aqui o termo se cala.
            bool frontierComparable =
                _hadFrontierAtLastDecision && _memory.HasFrontier && !_memory.EnteredNewNode;

            int delta = frontierComparable
                ? _frontierDistanceAtLastDecision - _memory.FrontierDistance
                : 0;

            // Aproximação em metros do próximo passo da fronteira. Aqui a porteira é OUTRA: não
            // interessa se o agente entrou num nó novo, e sim se os dois steps mediram a
            // distância até o MESMO nó. Enquanto o alvo não muda, cada centímetro andado na
            // direção dele conta — é isso que dá gradiente no meio de uma aresta longa, onde o
            // delta em arestas acima vale zero do começo ao fim da travessia.
            int frontierNextStep = FrontierNextStepOrNone();

            bool approachComparable =
                frontierNextStep >= 0 && frontierNextStep == _frontierNextStepAtLastDecision;

            float approachDelta = approachComparable
                ? _frontierApproachDistanceAtLastDecision - PlanarDistanceToNode(frontierNextStep)
                : 0f;

            return new GraphStepContext(
                _maxEpisodeSteps,
                _memory.EnteredNewNode,
                _memory.EnteredNewNodeValue,
                _memory.TraversedNewEdge,
                _memory.EnteredNewRegionBudget,
                _memory.ChangedNode,
                _memory.CurrentNodeVisitCount,
                delta,
                frontierComparable,
                approachDelta,
                approachComparable,
                _memory.StepsSinceNewNode,
                _touchingWall,
                _arenaController.FrontierHintScale);
        }

        /// <summary>
        /// Próximo passo do caminho até o não-visitado mais próximo, ou -1 quando não há
        /// fronteira. O peso da lição NÃO entra aqui: quem zera o shaping é a multiplicação por
        /// FrontierRewardScale na recompensa, num lugar só.
        /// </summary>
        private int FrontierNextStepOrNone() => _memory.HasFrontier ? _memory.FrontierNextStep : -1;

        // Planar (X/Z), pela mesma razão que FindNodeAt é planar: o mapa tem um andar só, e a
        // diferença de altura entre o agente e o nó entraria no cálculo como distância que
        // nenhuma ação consegue reduzir — um piso constante que só faria diluir o sinal.
        private float PlanarDistanceToNode(int node)
        {
            Vector3 delta = _graph.NodePosition(node) - transform.position;
            return new Vector2(delta.x, delta.z).magnitude;
        }

        private void RememberFrontierApproach()
        {
            _frontierNextStepAtLastDecision = FrontierNextStepOrNone();
            _frontierApproachDistanceAtLastDecision = _frontierNextStepAtLastDecision >= 0
                ? PlanarDistanceToNode(_frontierNextStepAtLastDecision)
                : 0f;
        }

        // Encostar em parede é condição contínua: OnCollisionStay dispara uma vez por step POR
        // collider, então aqui só marca a flag — quem cobra é a decisão, uma vez só, mesmo que o
        // agente esteja tocando três paredes numa quina.
        private void OnCollisionStay(Collision collision)
        {
            if (IsWall(collision.gameObject))
                _touchingWall = true;
        }

        // Parede é identificada por LAYER (a mesma máscara que valida as ligações do grafo), e
        // não por tag: uma segunda fonte de verdade para "isto é uma parede" já custou um termo
        // de recompensa morto e silencioso neste projeto.
        private bool IsWall(GameObject other) =>
            _graph != null && (_graph.WallLayer.value & (1 << other.layer)) != 0;

        private void FinishEpisode(bool covered)
        {
            _episodeEnding = true;
            _arenaController.ShowOutcome(covered);

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

#if ENABLE_LEGACY_INPUT_MANAGER
        // Dirigir na mão é a forma mais rápida de conferir se os nós registram visita e se as
        // ligações que você desenhou são percorríveis de verdade. Selecione o agente e olhe os
        // gizmos da memória enquanto anda.
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;
            continuous[0] = Input.GetAxisRaw("Horizontal");
            continuous[1] = Input.GetAxisRaw("Vertical");
        }
#endif

        // Erro de wiring em ML-Agents é silencioso e só aparece como treino que não converge.
        private void ValidateSetup()
        {
            if (_memory == null || _rewardSystem == null || _movementSystem == null)
            {
                Debug.LogError($"{name}: sistema do explorador faltando — confira os componentes filhos.", this);
                return;
            }

            if (_arenaController == null)
            {
                Debug.LogError($"{name}: GraphArenaController não encontrado nos pais.", this);
                return;
            }

            if (_graph == null)
            {
                Debug.LogError($"{name}: a arena não tem NavGraph atribuído.", this);
                return;
            }

            // Um nó com mais vizinhos que slots perde os excedentes na observação — o agente
            // simplesmente não enxerga aquelas saídas.
            int worst = 0;
            NavNode worstNode = null;
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                int degree = _graph.GetNeighbors(i).Length;
                if (degree > worst)
                {
                    worst = degree;
                    worstNode = _graph.GetNode(i);
                }
            }

            if (worst > _neighborSlots)
            {
                Debug.LogWarning(
                    $"{name}: '{worstNode.name}' tem {worst} vizinhos e só há {_neighborSlots} slots de observação. " +
                    "Aumente _neighborSlots (e o VectorObservationSize junto) ou pode as ligações redundantes.",
                    worstNode);
            }

            var behaviorParameters = GetComponent<BehaviorParameters>();
            if (behaviorParameters == null)
                return;

            int declared = behaviorParameters.BrainParameters.VectorObservationSize;
            if (declared != ObservationSize)
            {
                Debug.LogError(
                    $"{name}: VectorObservationSize = {declared} mas o agente emite {ObservationSize} observações " +
                    $"({_neighborSlots} slots x {FloatsPerNeighbor} + {GlobalObservations}). " +
                    "Ajuste no Behavior Parameters, senão o treino roda com o vetor truncado.", this);
            }

            int continuousActions = behaviorParameters.BrainParameters.ActionSpec.NumContinuousActions;
            if (continuousActions != 2)
            {
                Debug.LogError(
                    $"{name}: esperadas 2 ações contínuas (X, Z), encontradas {continuousActions}.", this);
            }
        }
    }
}
