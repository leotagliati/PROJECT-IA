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
    /// foram visitados, quanto do que falta está atrás de cada um) e um resumo escalar (fração
    /// do grafo já coberta).
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
    /// estar todos visitados e a resposta certa estar a cinco nós dali. Quem tapa esse buraco
    /// é o VALOR DE CADA SAÍDA (por vizinho: quanto inexplorado há por ali, descontado pela
    /// distância, relativo à melhor saída). É DADO, não resposta: "atrás daquela porta ainda
    /// tem coisa" — a política decide o que fazer com isso, e fica ligado sempre, inclusive no
    /// jogo final, porque é o que uma pessoa que conhece o prédio sabe.
    ///
    /// NÃO existe mais a "seta roxa" (direção do próximo passo até o não-visitado mais próximo,
    /// com shaping por aproximação). Ela era RESPOSTA, e o run 11 mostrou o que isso dá: a
    /// política aprende a seguir a seta e a decorar rotas, não a explorar. O que sobra de
    /// denso na recompensa é a aresta inédita, que não tem direção.
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
        // Por vizinho: direção X, direção Z, distância normalizada, visitado, valor da saída
        // (inexplorado atrás dela, relativo à melhor), slot válido.
        private const int FloatsPerNeighbor = 6;

        // LAYOUT DAS OBSERVAÇÕES GLOBAIS (21):
        //   [0]      está dentro do raio de algum nó
        //   [1..3]   direção + distância ao nó âncora
        //   [4..6]   direção + distância ao nó mais próximo COM LINHA LIVRE
        //   [7]      cobertura total
        //   [8]      RESERVADO — era a cobertura da região atual; regiões saíram do sistema
        //   [9..12]  RESERVADO — era a seta de fronteira (direção, distância, arestas); saiu
        //   [13..15] RESERVADO — alerta: ativo, distância em arestas, delta quente/frio
        //   [16..20] RESERVADO — visão: vendo, já viu, direção + distância à última posição
        //
        // Os blocos reservados emitem ZERO. Os de alerta/visão esperam a branch de busca; os
        // [8] e [9..12] são cicatrizes de coisas removidas e ficam pelo mesmo motivo: toda
        // mudança neste número invalida os .onnx treinados, e encolher o vetor só para tirar
        // zeros custaria todos os modelos. Quando a busca entrar, ela pode reocupá-los.
        private const int GlobalObservations = 21;

        // Quantos zeros cada bloco reservado emite.
        private const int ReservedFrontierObservations = 4;
        private const int ReservedAlertObservations = 3;
        private const int ReservedVisionObservations = 5;

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
            /// Fração da soma dos pesos dos nós (NavNode.ExplorationWeight) coletada. Coerente
            /// com a recompensa: um nó de peso 0.2 conta pouco, um de peso 2.0 conta muito.
            /// </summary>
            NodeWeight,
        }

        [Header("-----Cobertura-----")]
        // Qual medida encerra o episódio (contra o coverage_target do currículo) E é observada
        // pelo agente. Os dois de propósito na mesma chave: observar uma barra de progresso
        // diferente da que julga o episódio é a receita de um agente que "acha" que está indo bem.
        //
        // NodeWeight é o default porque é a mesma conta da recompensa: o agente observa a
        // barra que decide o que ele ganha. NodeFraction ignora os pesos e trata todo primário
        // como igual — serve para medir cobertura GEOMÉTRICA num mapa de pesos desiguais.
        [SerializeField] private CoverageMeasure _coverageMeasure = CoverageMeasure.NodeWeight;

        [Header("-----Observação-----")]
        // Quantos vizinhos cabem na observação. Nós com mais vizinhos que isto têm os excedentes
        // (os mais distantes) cortados — o aviso no Play te diz se está acontecendo. 6 cobre
        // com folga cruzamentos de corredor; salas muito auto-ligadas passam disso e é sinal de
        // que faltou podar ligações redundantes na autoria.
        //
        // 8, e não 6: a malha auxiliar sobe o grau dos nós, e um nó que estoura os slots perde
        // vizinhos em silêncio. Os 2 slots extras custam 10 entradas e evitam uma segunda
        // retreinada no dia em que um cruzamento ganhar mais uma saída.
        [SerializeField] private int _neighborSlots = 8;

        // Normalizador da distância em METROS até um nó. Da ordem da MAIOR aresta do mapa —
        // acima dele toda distância satura em 1.0 e a observação morre. Com 16 num mapa cuja
        // maior aresta é 34, um terço das arestas chegava à rede como o mesmo número.
        // No NodeTraining atual a maior aresta é 20.6 m; o prefab usa 25.
        [SerializeField] private float _maxNodeDistance = 35f;

        // O desconto do valor por saída (_lookaheadDecay) mora na GraphExplorationMemory, que é
        // quem tem o estado de visitados e calcula o valor — aqui só se lê o resultado.

        [Header("-----Settings-----")]
        // Em steps de FÍSICA, não em decisões: com TakeActionsBetweenDecisions ligado no
        // DecisionRequester (que é como a cena está montada), OnActionReceived roda todo
        // FixedUpdate e é ele quem incrementa o contador. 4000 steps = 80 s a 0.02 de timestep,
        // ou 800 decisões com Decision Period 5 — que é o número que aparece no
        // "Environment/Episode Length" do TensorBoard.
        //
        // ATENÇÃO ao mexer aqui: a pressão existencial é diluída (_existentialPenalty / este
        // valor) e não muda, mas o contato com parede e a estagnação são cobrados POR STEP, e o
        // teto deles é (valor_por_step x steps). Dobrar a duração dobra as duas penalidades.
        // Ver a tabela no cabeçalho do GraphRewardSystem antes de alterar.
        [SerializeField] private int _maxEpisodeSteps = 8000;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private NavGraph _graph;

        private int _elapsedSteps;
        private bool _episodeEnding;
        private bool _touchingWall;

        private readonly List<int> _neighborBuffer = new List<int>();
        private Comparison<int> _byDistanceFromNode;
        private Comparison<int> _byAngleFromNode;
        private Vector3 _sortOrigin;

        public int ObservationSize => _neighborSlots * FloatsPerNeighbor + GlobalObservations;

        private float CurrentCoverage => _coverageMeasure == CoverageMeasure.NodeWeight
            ? _memory.VisitedWeightFraction
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
            // A arena já leu o currículo em ResetEpisode() acima; a fração vale para este episódio.
            _memory.ResetEpisode(_arenaController.PrevisitedFraction);
            _rewardSystem.ResetEpisode();

            // Registra de imediato o nó do spawn: sem isto o primeiro nó do episódio pagaria
            // recompensa de descoberta por o agente simplesmente ter nascido em cima dele.
            _memory.Tick(transform.position);
            _memory.ClearStepFlags();
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

            // ---- Nó mais próximo alcançável (3) ----
            // A âncora responde "de onde eu vim"; isto responde "onde a malha está AGORA". Sem
            // ele, o agente que se afastou do grafo só recebe a direção de um nó que já ficou
            // para trás — e é exatamente essa a situação em que ele se perdia.
            //
            // Roda uma vez por DECISÃO (não por step de física): com Decision Period 5 são ~10
            // consultas por segundo por agente, e o SphereCast dentro dela só dispara enquanto
            // pode melhorar a resposta.
            int nearest = _graph.FindNearestReachableNode(position);
            AddDirectionAndDistance(sensor, position, nearest >= 0 ? _graph.NodePosition(nearest) : position, nearest >= 0);

            // ---- Cobertura (1) + reservado (1) ----
            // O quanto falta explorar no geral. O segundo float era a fração da região atual;
            // sem regiões ele emite zero, mantido no lugar para não invalidar os .onnx.
            sensor.AddObservation(CurrentCoverage);
            sensor.AddObservation(0f);

            // ---- Reservado: fronteira (4), alerta (3) e visão (5) ----
            // Zeros. Ficam ANTES dos vizinhos para que o bloco de vizinhos continue no fim do
            // vetor: assim, mudar _neighborSlots no futuro não desloca o significado de nenhuma
            // posição anterior.
            for (int i = 0; i < ReservedFrontierObservations + ReservedAlertObservations + ReservedVisionObservations; i++)
                sensor.AddObservation(0f);

            // ---- Vizinhos (6 x _neighborSlots) ----
            // O valor de cada saída é calculado aqui, uma vez por DECISÃO (é uma BFS por vizinho),
            // e lido slot a slot abaixo. O gizmo rosa da memória mostra os mesmos números.
            _memory.ScoreExits();
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
                    sensor.AddObservation(0f);
                    continue;
                }

                int neighbor = _neighborBuffer[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(neighbor), true);
                sensor.AddObservation(_memory.IsVisited(neighbor) ? 1f : 0f);
                sensor.AddObservation(_memory.ExitScore(neighbor));
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
            return new GraphStepContext(
                _maxEpisodeSteps,
                _memory.EnteredNewNode,
                _memory.EnteredNewNodeValue,
                _memory.NewEdgeCount,
                _memory.ChangedNode,
                _memory.CurrentNodeVisitCount,
                _memory.StepsSinceNewNode,
                _touchingWall);
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
            RecordExplorationStats(covered);

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

        /// <summary>
        /// Métricas de EXPLORAÇÃO, separadas da recompensa. "Cumulative Reward" mistura shaping
        /// com objetivo e muda a cada ajuste de peso ou de lição; estas três medem o
        /// comportamento e valem para comparar runs com funções de recompensa diferentes:
        ///   Exploration/Coverage         cobertura ao fim do episódio (a medida da lição)
        ///   Exploration/RevisitRatio     chegadas repetidas / chegadas a primários — vai-e-vem
        ///   Exploration/NewNodesPerMin   primários inéditos por minuto simulado — ritmo
        ///   Exploration/StepsToTarget    steps até bater o alvo, só nos episódios que bateram
        /// Aparecem no TensorBoard junto das do ambiente.
        /// </summary>
        private void RecordExplorationStats(bool covered)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;

            stats.Add("Exploration/Coverage", CurrentCoverage);

            int arrivals = _memory.PrimaryArrivalCount;
            int fresh = _memory.VisitedNodeCount;
            stats.Add("Exploration/RevisitRatio", arrivals > 0 ? (float)(arrivals - fresh) / arrivals : 0f);

            float minutes = Mathf.Max(1, _elapsedSteps) * Time.fixedDeltaTime / 60f;
            stats.Add("Exploration/NewNodesPerMin", fresh / minutes);

            if (covered)
                stats.Add("Exploration/StepsToTarget", _elapsedSteps);
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
