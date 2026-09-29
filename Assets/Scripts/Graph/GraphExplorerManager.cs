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
    /// foram visitados) e um resumo escalar (fração do grafo já coberta).
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
        // Por vizinho: direção X, direção Z, distância normalizada, visitado (0 nunca / 0.5
        // recuperado / 1 acabou de passar — GraphExplorationMemory.VisitedObservation), PESO (relativo ao
        // nó que mais vale neste episódio), QUANTO RESTA por essa saída (relativo à melhor saída,
        // GraphExplorationMemory.ExitRemainingScore), VIM DAQUI (é o nó anterior), QUANTAS VEZES
        // já passei por ele (saturando em _revisitSaturation), TÉDIO da sala dele (0..1), slot válido.
        //
        // Era 6. O "quanto resta" entrou porque sem ele o agente só enxergava 1 aresta: com todos
        // os vizinhos visitados, nada dizia para onde ir e ele rodava em círculo (node4_noarrow_01,
        // cobertura parada em 13%). "Vim daqui" e "quantas vezes" entraram pelo mesmo motivo, do
        // lado da memória: a rede não tem estado entre decisões, então "estou em B vindo de A" e
        // "estou em B vindo de C" eram a mesma observação — e A-B-A-B não tinha como ser notado.
        // A estagnação já PUNIA o loop; faltava a informação para a rede saber por quê.
        // O tédio entrou com a patrulha: "esta saída leva a uma sala cansada, aquela a uma fresca"
        // — sem ele a punição por ficar na sala chata não diz para ONDE ir.
        // Invalidou os .onnx anteriores: VectorObservationSize 69 -> 93 -> 102 (-> 107 com o movimento livre).
        // O 11º float, SUSPEITA por esta saída (GraphSuspicionMap.ExitScore), entrou com a procura
        // (docs/graph/procura-e-ping.md): 107 -> 118. É o "quanto resta" da caça: onde o hider
        // provavelmente está, por qual porta.
        private const int FloatsPerNeighbor = 11;

        // LAYOUT DAS OBSERVAÇÕES GLOBAIS (30):
        //   [0]      está dentro do raio de algum nó
        //   [1..3]   direção + distância ao nó âncora
        //   [4..6]   direção + distância ao nó mais próximo COM LINHA LIVRE
        //   [7]      cobertura total
        //   [8]      encostado em parede (0/1) — era a cobertura da região; virou isto quando o
        //            vetor mudou de tamanho de qualquer jeito. Sem ele a rede não sabia que estava
        //            empurrando uma parede (os raios dizem a distância, não o contato)
        //   [9..11]  direção + distância ao próximo passo da fronteira
        //   [12]     distância PELO GRAFO até a fronteira, em metros / diâmetro do mapa
        //   [13..15] PING: ativo, distância pelo grafo até o nó que toca (metros / diâmetro),
        //            quente/frio (-1/0/+1)
        //
        // [12] e [14] eram em ARESTAS / _maxGraphDistance. Mudaram para METROS / diâmetro do
        // mapa (NavGraph.PathDiameter, calculado sozinho): o tamanho do vetor é o mesmo, mas o
        // SIGNIFICADO mudou — modelos .onnx treinados antes dessa mudança não servem mais.
        // Motivo: com o grafo coberto pelo NavGraphPlacer a densidade de nós muda a cada
        // regeneração, e contar arestas fazia a mesma distância valer outro número.
        //   [16..20] VISÃO: vendo, já viu, direção + distância à última posição em que viu o hider
        //   [21]     TÉDIO da sala atual (0..1): já está na hora de sair daqui?
        //   [22..23] para onde o CORPO está virado (X/Z no mundo). Com olhar e andar separados
        //            (ações [2..3]) e giro com velocidade limitada, é o que diz onde o cone está
        //   [24..25] VELOCIDADE do hider enquanto vê (X/Z, / _hiderVelocityScale): para
        //            interceptar em vez de só seguir atrás. Zero quando não vê
        //   [26]     força do STEERING ASSISTIDO nesta lição (0..1): a dinâmica muda com ela, e
        //            sem isto a rede veria o mesmo estado reagindo diferente
        //
        // [22..26] entraram com o movimento livre (docs/graph/movimento-livre.md): 102 -> 107.
        //   [27]     PROCURA ativa (tem hider neste episódio)
        //   [28]     CERTEZA: a maior suspeita de um nó (1 = sei onde ele está)
        //   [29]     tempo desde a última pista (ping ou visão), / 60 s; 1 = nenhuma pista
        // [27..29] entraram com a procura (docs/graph/procura-e-ping.md), junto com o 11º float
        // por vizinho: 107 -> 118.
        //
        // Os blocos reservados emitem ZERO até a branch de busca. Eles existem desde já porque
        // toda mudança neste número invalida os .onnx treinados: reservar custa 9 entradas numa
        // rede de 128 unidades e economiza uma retreinada inteira do zero. O [8] fica pelo
        // mesmo motivo: encolher o vetor só para tirar um zero custaria todos os modelos.
        private const int GlobalObservations = 30;


        [Header("-----Systems-----")]
        [SerializeField] private GraphExplorationMemory _memory;
        [SerializeField] private GraphRewardSystem _rewardSystem;
        // Reaproveitado do seeker de propósito: é um driver de Rigidbody sem nenhuma regra de
        // seeker dentro (Move / ResetMovement). Duplicá-lo criaria dois lugares para ajustar a
        // mesma física. Se um dia ele ganhar lógica específica do seeker, copie-o para cá.
        [SerializeField] private SeekerMovementSystem _movementSystem;
        [SerializeField] private GraphArenaController _arenaController;
        // Opcional: sem ele, o bloco de ping da observação emite zero e nada de ping é pago.
        [SerializeField] private GraphPingSystem _ping;
        // Opcional: sem ele, o bloco de visão emite zero e nada de visão é pago.
        [SerializeField] private GraphHiderPerception _perception;
        // Opcional: sem ele, o bloco de procura emite zero e nada de suspeita é pago (o
        // ValidateSetup avisa — sem a procura, as lições de caça treinam às cegas).
        [SerializeField] private GraphSuspicionMap _suspicion;

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
        [SerializeField] private float _maxNodeDistance = 35f;

        // Quantas passagens pelo vizinho levam a observação "quantas vezes" a 1.0. 4: ida e volta
        // num beco já são 2 (legítimo); de 3 em diante é repetição.
        [SerializeField, Min(1)] private int _revisitSaturation = 4;

        // Normalizador da velocidade do hider na observação [24..25], em m/s. 5: acima da fuga
        // mais rápida do currículo (3.2), então não satura; um player correndo pode passar e
        // satura em ±1, o que já diz "rápido".
        [SerializeField, Min(0.1f)] private float _hiderVelocityScale = 5f;

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

        [Header("-----Parede sem punição-----")]
        // Layers que são PAREDE para tudo — visão, grafo, steering, observação [8] "encostado" —
        // mas cujo contato NÃO é punido (nem contínuo, nem batida). Para os batentes: o corpo tem
        // 1.38 m de largura e o grafo só garante ~1.48 m de vão, então quase toda passagem de
        // porta raspava e contava batida — sair da sala custava. Pedido do Arthur.
        //
        // A layer precisa estar TAMBÉM no Wall Layer do NavGraph; aqui só se diz "esta não pune".
        // Esconder o batente da layer Wall em vez disso faria a visão atravessar a parede inteira
        // do Door_Hole (ela é a parede com o vão, não só o batente) e o grafo ignorá-la.
        // Ao usar num mapa, lembre: a parede toda do Door_Hole vira grátis, não só o batente.
        [SerializeField] private LayerMask _penaltyFreeWallLayer;

        [Header("-----Diagnóstico-----")]
        // No fim de cada episódio, escreve no Console os 3 nós com mais loop/pisca-pisca, pelo nome.
        // DESLIGADO no treino (9 arenas enchem o Console); ligue ao assistir um .onnx no Play.
        [SerializeField] private bool _logLoopNodes = false;

        private Vector3 _initialLocalPosition;
        private Quaternion _initialLocalRotation;
        private NavGraph _graph;

        private int _elapsedSteps;
        private bool _episodeEnding;
        // Encostado em parede PUNIDA (custo contínuo + batida) e em parede SEM punição (batente,
        // ver _penaltyFreeWallLayer). As duas entram na observação [8]; só a primeira custa.
        private bool _touchingWall;
        private bool _touchingFreeWall;

        // Steps de física encostado em parede no episódio (métrica Exploration/WallContactFraction).
        private int _wallContactSteps;

        // Steps de física PARADO (abaixo de IdleSpeed) no episódio (métrica Movement/IdleFraction).
        // Só medição: parar é permitido — no jogo, parar na porta para olhar é o comportamento que
        // assusta. O que a métrica pega é o ótimo local "fico quieto e não perco nada"
        // (search_03, e2_01: contato caindo com a cobertura parada).
        private const float IdleSpeed = 0.5f;
        private int _idleSteps;
        private Rigidbody _body;

        // Batidas em parede com janela recente (custo escalonado). Estado entre steps, fora do Manager.
        private readonly WallHitTracker _wallHits = new WallHitTracker();

        // Ação anterior, para o custo de suavidade. Sem ação anterior (1ª do episódio) não cobra.
        private Vector2 _lastAction;
        private Vector2 _lastLook;
        private bool _hasLastAction;
        private float _actionChangeSq;
        private float _lookChangeSq;

        // Soma de |Δação|² e |Δolhar|² e número de decisões no episódio (métricas
        // Movement/ActionJitter e Movement/LookJitter).
        private float _actionChangeSum;
        private float _lookChangeSum;
        private int _decisionCount;

        // Distância de fronteira na decisão anterior. O delta entre decisões é o que vira
        // shaping — medir isso dentro da memória daria o delta de um step de física, que é
        // uma fração do que o agente controla com uma ação.
        private float _frontierDistanceAtLastDecision;

        // Alvo da fronteira na decisão anterior. O delta de distância só é comparável se o alvo é
        // o MESMO: com a seta seguindo as saídas (GraphExplorationMemory._frontierFollowsExits) o
        // alvo muda a cada nó, e sem esta checagem a troca de alvo viraria progresso fantasma.
        private int _frontierTargetAtLastDecision = -1;
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

        /// <summary>Normalizador de distância a nó. O NavGraphPlacer usa como alcance do teste "tem nó à vista?".</summary>
        public float MaxNodeDistance => _maxNodeDistance;

        private float CurrentCoverage => _coverageMeasure == CoverageMeasure.NodeWeight
            ? _memory.VisitedWeightFraction
            : _memory.VisitedFraction;

        // Peso EFETIVO da dica neste step: a força da lição enquanto a dica dura, zero depois.
        // É o único ponto que combina força e duração — observação, shaping e gizmo leem daqui,
        // porque os três são a mesma muleta e têm que sumir juntos. _elapsedSteps é em steps de
        // física, a mesma unidade do limite.
        private float CurrentFrontierHint
        {
            get
            {
                int limit = _arenaController.FrontierHintSteps;
                if (limit > 0 && _elapsedSteps >= limit)
                    return 0f;

                return _arenaController.FrontierHintScale;
            }
        }

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

            if (_ping == null)
                _ping = GetComponentInChildren<GraphPingSystem>();

            if (_perception == null)
                _perception = GetComponentInChildren<GraphHiderPerception>();

            if (_suspicion == null)
                _suspicion = GetComponentInChildren<GraphSuspicionMap>();

            _byDistanceFromNode = CompareByDistance;
            _byAngleFromNode = CompareByAngle;

            _initialLocalPosition = transform.localPosition;
            _initialLocalRotation = transform.localRotation;
            _body = GetComponent<Rigidbody>();

            if (_arenaController != null)
                _graph = _arenaController.Graph;

            if (_graph != null && _memory != null)
            {
                _graph.EnsureBaked();
                _memory.Configure(_graph);

                if (_ping != null)
                    _ping.Configure(_graph);

                if (_perception != null)
                    _perception.Configure(_graph);

                if (_suspicion != null)
                    _suspicion.Configure(_graph, _perception, _ping);
            }

            ValidateSetup();
        }

        public override void OnEpisodeBegin()
        {
            _elapsedSteps = 0;
            _episodeEnding = false;
            _touchingWall = false;
            _touchingFreeWall = false;

            _arenaController.ResetEpisode();

            if (_arenaController.TryGetSpawn(out Vector3 position, out Quaternion rotation))
                transform.SetPositionAndRotation(position, rotation);
            else
                transform.SetLocalPositionAndRotation(_initialLocalPosition, _initialLocalRotation);

            _movementSystem.ResetMovement();

            // A arena já leu o currículo em ResetEpisode(); a parede é a mesma máscara do grafo.
            _movementSystem.ConfigureSteering(_arenaController.SteerAssist, _graph.WallLayer);

            // Depois do spawn: o hider nasce longe de onde o seeker nasceu.
            _arenaController.ResetHider(transform.position);

            // A arena já leu o currículo em ResetEpisode() acima; a fração vale para este episódio.
            _memory.ResetEpisode(
                _arenaController.PrevisitedFraction,
                _arenaController.WeightJitter,
                _arenaController.ValueRecoverySteps,
                _arenaController.AreaBoredom);
            _wallContactSteps = 0;
            _idleSteps = 0;
            _wallHits.Reset();
            _hasLastAction = false;
            _actionChangeSq = 0f;
            _lookChangeSq = 0f;
            _actionChangeSum = 0f;
            _lookChangeSum = 0f;
            _decisionCount = 0;
            _rewardSystem.ResetEpisode();

            if (_ping != null)
                _ping.ResetEpisode(_arenaController.PingInterval);

            if (_perception != null)
                _perception.ResetEpisode();

            // Procura só com hider. Velocidade que o seeker SUPÕE: a da lição; parado = 0 (não
            // espalha); 0 no currículo = o padrão da GraphSuspicionMap (-1).
            if (_suspicion != null)
            {
                GraphHider.Mode mode = _arenaController.HiderMode;
                float assumedSpeed = mode == GraphHider.Mode.Static ? 0f
                    : _arenaController.HiderSpeed > 0f ? _arenaController.HiderSpeed
                    : -1f;
                _suspicion.ResetEpisode(mode != GraphHider.Mode.None, assumedSpeed);
            }

            // Registra de imediato o nó do spawn: sem isto o primeiro nó do episódio pagaria
            // recompensa de descoberta por o agente simplesmente ter nascido em cima dele.
            _memory.Tick(transform.position);
            _memory.ClearStepFlags();

            _hadFrontierAtLastDecision = _memory.HasFrontier;
            _frontierDistanceAtLastDecision = _memory.FrontierDistance;
            _frontierTargetAtLastDecision = _memory.FrontierTarget;
            RememberUnexplored();
            RememberFrontierApproach();
            RememberPing();
            RememberHider();

            _memory.FrontierHintVisible = CurrentFrontierHint > 0f;
        }

        // A memória é amostrada a cada step de FÍSICA. Com Decision Period > 1 o agente percorre
        // vários steps entre duas decisões e pode cruzar o raio de um nó inteiro no meio — a
        // visita seria perdida se a amostragem acompanhasse a cadência das decisões.
        private void FixedUpdate()
        {
            if (_episodeEnding || _graph == null)
                return;

            _memory.Tick(transform.position);

            // Depois da memória: a chegada ao ping é "o nó âncora virou o nó do ping".
            if (_ping != null)
                _ping.Tick(_memory.CurrentNodeIndex, _elapsedSteps);

            if (_perception != null)
                _perception.Tick(transform);

            // Por último: usa o nó âncora, o ping e a visão deste step.
            if (_suspicion != null)
                _suspicion.Tick(transform, _memory.CurrentNodeIndex);
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
            // consultas por segundo por agente, e o CapsuleCast dentro dela só dispara enquanto
            // pode melhorar a resposta.
            int nearest = _graph.FindNearestReachableNode(position);
            AddDirectionAndDistance(sensor, position, nearest >= 0 ? _graph.NodePosition(nearest) : position, nearest >= 0);

            // ---- Cobertura (1) + encostado em parede (1) ----
            // O quanto falta explorar no geral, e se o último step de física terminou em contato
            // com parede (OnCollisionStay roda depois da decisão anterior, antes desta).
            sensor.AddObservation(CurrentCoverage);
            sensor.AddObservation(_touchingWall || _touchingFreeWall ? 1f : 0f);

            // ---- Fronteira (4) ----
            float hint = CurrentFrontierHint;
            bool showFrontier = _memory.HasFrontier && hint > 0f && _memory.FrontierNextStep >= 0;

            // O gizmo acompanha o que a rede recebe: seta no desenho = seta na observação.
            _memory.FrontierHintVisible = hint > 0f;

            AddDirectionAndDistance(
                sensor,
                position,
                showFrontier ? _graph.NodePosition(_memory.FrontierNextStep) : position,
                showFrontier);

            // Distância em ARESTAS até o alvo: diz se a fronteira é "logo ali" ou "do outro lado
            // do mapa", informação que a direção sozinha não carrega.
            sensor.AddObservation(showFrontier ? Mathf.Clamp01(_memory.FrontierDistance / _graph.PathDiameter) : 0f);

            // ---- Ping (3) ----
            // Ativo, distância em arestas (mesmo normalizador da fronteira) e quente/frio. Sem
            // direção de propósito: a política descobre por qual saída a distância cai lendo o
            // quente/frio a cada troca de nó — dado, não resposta.
            bool pingActive = _ping != null && _ping.IsActive;
            sensor.AddObservation(pingActive ? 1f : 0f);
            sensor.AddObservation(pingActive ? Mathf.Clamp01(_ping.Distance / _graph.PathDiameter) : 0f);
            sensor.AddObservation(pingActive ? _ping.HotCold : 0f);

            // ---- Visão (5) ----
            // Vendo, já viu, e direção + distância à ÚLTIMA POSIÇÃO VISTA (a atual enquanto vê;
            // congelada ao perder — é para lá que ele vai procurar). Fica ANTES dos vizinhos
            // para que o bloco de vizinhos continue no fim do vetor.
            bool seeing = _perception != null && _perception.IsSeeing;
            bool hasSeen = _perception != null && _perception.HasSeen;
            sensor.AddObservation(seeing ? 1f : 0f);
            sensor.AddObservation(hasSeen ? 1f : 0f);
            AddDirectionAndDistance(sensor, position, hasSeen ? _perception.LastSeenPosition : position, hasSeen);

            // ---- Tédio da sala atual (1) ----
            sensor.AddObservation(_memory.CurrentAreaBoredom);

            // ---- Corpo (5): para onde olha, velocidade do hider, força do assist ----
            Vector3 forward = transform.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);
            facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector2.up;
            sensor.AddObservation(facing.x);
            sensor.AddObservation(facing.y);

            Vector3 hiderVelocity = seeing ? _perception.HiderVelocity / _hiderVelocityScale : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(hiderVelocity.z, -1f, 1f));

            sensor.AddObservation(_arenaController.SteerAssist);

            // ---- Procura (3) ----
            bool searching = _suspicion != null && _suspicion.IsActive;
            sensor.AddObservation(searching ? 1f : 0f);
            sensor.AddObservation(searching ? _suspicion.Certainty : 0f);
            sensor.AddObservation(searching ? _suspicion.EvidenceAge : 0f);

            // ---- Vizinhos (FloatsPerNeighbor x _neighborSlots) ----
            FillNeighborBuffer(current);
            _memory.ScoreExits();
            if (_suspicion != null)
                _suspicion.ScoreExits(current);

            for (int slot = 0; slot < _neighborSlots; slot++)
            {
                if (slot >= _neighborBuffer.Count)
                {
                    // Slot vazio: zeros e a flag de validade em 0. O padding precisa ser
                    // distinguível de um vizinho real — senão "não existe saída aqui" e "existe
                    // uma saída exatamente na minha posição" chegam à rede como o mesmo vetor.
                    for (int k = 0; k < FloatsPerNeighbor; k++)
                        sensor.AddObservation(0f);
                    continue;
                }

                int neighbor = _neighborBuffer[slot];
                AddDirectionAndDistance(sensor, position, _graph.NodePosition(neighbor), true);
                sensor.AddObservation(_memory.VisitedObservation(neighbor));

                // Quanto vale esta saída, relativo ao nó mais valioso do episódio. Sem isto a
                // variação de peso do currículo (weight_jitter) seria só ruído na recompensa —
                // um sinal que a rede não pode usar não ensina nada.
                sensor.AddObservation(_memory.NormalizedEpisodeWeight(neighbor));

                // O que tem ATRÁS desta saída: o inexplorado alcançável por ela, descontado pela
                // distância. É o que substitui a seta sem entregar um caminho.
                sensor.AddObservation(_memory.ExitRemainingScore(neighbor));

                // Memória mínima contra loop: de onde vim e o quanto já rodei por ali.
                sensor.AddObservation(neighbor == _memory.PreviousNodeIndex ? 1f : 0f);
                sensor.AddObservation(Mathf.Clamp01((float)_memory.VisitCountOf(neighbor) / _revisitSaturation));

                // QUÃO PERTO está o que falta por esta saída (1 = a mais perto). Era o TÉDIO da sala
                // do vizinho, que valia sempre 0 nas etapas (tédio desligado): mesmo slot, então o
                // vetor fica em 118 e os cérebros E1/E2 continuam servindo. Contra o loop e o beco:
                // é um campo de distância (seguir o 1 só diminui), e o "quanto resta" acima, uma
                // soma com desconto, não é (ver GraphExplorationMemory.ExitProximityScore).
                sensor.AddObservation(_memory.ExitProximityScore(neighbor));

                // Onde o hider provavelmente está, por esta saída (0 sem procura).
                sensor.AddObservation(_suspicion != null ? _suspicion.ExitScore(neighbor) : 0f);
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

            // Antes do contexto: a batida e a mudança de ação DESTE step entram na conta dele.
            _wallHits.Step(_touchingWall);
            ActionSegment<float> continuous = actions.ContinuousActions;
            TrackActionChange(new Vector2(continuous[0], continuous[1]), new Vector2(continuous[2], continuous[3]));

            AddReward(_rewardSystem.EvaluateStep(BuildStepContext()));

            if (_touchingWall)
                _wallContactSteps++;

            if (_body != null)
            {
                Vector3 velocity = _body.linearVelocity;
                if (velocity.x * velocity.x + velocity.z * velocity.z < IdleSpeed * IdleSpeed)
                    _idleSteps++;
            }

            // Consumidas depois de cobradas. A memória volta a acumular a partir do próximo
            // step de física.
            _hadFrontierAtLastDecision = _memory.HasFrontier;
            _frontierDistanceAtLastDecision = _memory.FrontierDistance;
            _frontierTargetAtLastDecision = _memory.FrontierTarget;
            RememberUnexplored();
            RememberFrontierApproach();
            RememberPing();
            RememberHider();
            _memory.ClearStepFlags();
            if (_ping != null)
                _ping.ClearStepFlags();
            if (_perception != null)
                _perception.ClearStepFlags();
            if (_suspicion != null)
                _suspicion.ClearStepFlags();
            _touchingWall = false;
            _touchingFreeWall = false;

            // Andar [0..1] e olhar [2..3] separados, os dois no referencial do mundo.
            Vector3 direction = new(continuous[0], 0f, continuous[1]);
            Vector3 look = new(continuous[2], 0f, continuous[3]);
            _movementSystem.Move(direction, look);

            // Pegou o hider: o outro bônus terminal (ver GraphRewardSystem._hiderCaughtReward).
            // Antes da cobertura: nas lições de caça a captura é o objetivo, e se as duas
            // acontecerem no mesmo step ela é a que explica o fim do episódio.
            if (_perception != null && _perception.Caught)
            {
                float remaining = 1f - (float)_elapsedSteps / Mathf.Max(1, _maxEpisodeSteps);
                AddReward(_rewardSystem.HiderCaughtReward(remaining));
                FinishEpisode(covered: true);
                return;
            }

            // Nas lições de patrulha (alvo > 1) não há fim por cobertura: vai até o timeout.
            if (_arenaController.EndsOnCoverage && CurrentCoverage >= _arenaController.CoverageTarget)
            {
                AddReward(_rewardSystem.FullCoverageReward);
                FinishEpisode(covered: true);
                return;
            }

            _elapsedSteps++;
            if (_elapsedSteps >= _maxEpisodeSteps)
                FinishEpisode(covered: false);
        }

        // |Δação|² desde a ação anterior. Entre decisões a ação se repete e isto dá zero, então o
        // custo de suavidade só aparece nas decisões — que é onde a política escolhe.
        private void TrackActionChange(Vector2 action, Vector2 look)
        {
            _actionChangeSq = _hasLastAction ? (action - _lastAction).sqrMagnitude : 0f;
            _lookChangeSq = _hasLastAction ? (look - _lastLook).sqrMagnitude : 0f;
            if (_hasLastAction && (_actionChangeSq > 0f || _lookChangeSq > 0f))
            {
                _actionChangeSum += _actionChangeSq;
                _lookChangeSum += _lookChangeSq;
                _decisionCount++;
            }

            _lastAction = action;
            _lastLook = look;
            _hasLastAction = true;
        }

        private GraphStepContext BuildStepContext()
        {
            // O delta só é comparável quando os dois lados mediram a distância até o mesmo
            // alvo. Ao visitar um nó novo a fronteira salta para outro lugar do mapa, e sem
            // este cuidado o shaping cobraria como retrocesso exatamente o step em que o agente
            // acertou. A descoberta já é paga pelo _newNodeReward; aqui o termo se cala.
            bool frontierComparable =
                _hadFrontierAtLastDecision && _memory.HasFrontier && !_memory.EnteredNewNode
                && _memory.FrontierTarget == _frontierTargetAtLastDecision;

            float delta = frontierComparable
                ? _frontierDistanceAtLastDecision - _memory.FrontierDistance
                : 0f;

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

            bool hiderComparable =
                _perception != null && _perception.IsSeeing && _wasSeeingAtLastDecision;

            float hiderDelta = hiderComparable ? _hiderDistanceAtLastDecision - _perception.CurrentDistance : 0f;

            bool pingComparable =
                _ping != null && _ping.IsActive && _ping.TargetNode == _pingTargetAtLastDecision;

            float pingDelta = pingComparable ? _pingDistanceAtLastDecision - _ping.Distance : 0f;

            // NAVEGAÇÃO: quanto a distância pelo grafo até o inexplorado mais próximo caiu desde a
            // decisão anterior. Só comparável com o MESMO alvo nas duas: quando ele é descoberto, o
            // alvo pula para outro lugar e o salto não é o agente andando (a descoberta já pagou).
            int unexploredTarget = _memory.NearestUnexploredTarget;
            float unexploredProgress = unexploredTarget >= 0 && unexploredTarget == _unexploredTargetAtLastDecision
                ? _unexploredDistanceAtLastDecision - _memory.NearestUnexploredDistance
                : 0f;

            return new GraphStepContext(
                _maxEpisodeSteps,
                _memory.EnteredNewNode,
                _memory.EnteredNewNodeValue,
                _memory.TraversedNewEdge,
                _memory.ChangedNode,
                _memory.CurrentNodeVisitCount,
                delta,
                frontierComparable,
                approachDelta,
                approachComparable,
                _memory.StepsSinceNewNode,
                _touchingWall,
                CurrentFrontierHint,
                pingDelta,
                pingComparable,
                _ping != null && _ping.Reached,
                _ping != null ? _ping.ReachedValue : 0f,
                _ping != null && _ping.Missed,
                _perception != null && _perception.Spotted,
                hiderDelta,
                hiderComparable,
                _memory.RevisitValue,
                _memory.EarlyRevisitArrivals,
                _memory.EarlyRevisitStreak,
                _memory.CurrentAreaBoredom,
                _wallHits.HitsThisStep,
                _wallHits.RecentHits,
                _actionChangeSq,
                _lookChangeSq,
                _suspicion != null ? _suspicion.ClearedMass : 0f,
                _arenaController.PingRewardScale,
                _arenaController.DiscoveryRewardScale,
                unexploredProgress);
        }

        // Alvo e distância do potencial de navegação na decisão anterior (ver BuildStepContext).
        private int _unexploredTargetAtLastDecision = -1;
        private float _unexploredDistanceAtLastDecision;

        private void RememberUnexplored()
        {
            _unexploredTargetAtLastDecision = _memory.NearestUnexploredTarget;
            _unexploredDistanceAtLastDecision = _memory.NearestUnexploredDistance;
        }

        // Visão na decisão anterior: via, e a que distância. O delta de aproximação só é
        // comparável quando VIA nas duas decisões — ganhar ou perder visão faz a distância
        // medida saltar, e isso não é o agente andando.
        private bool _wasSeeingAtLastDecision;
        private float _hiderDistanceAtLastDecision;

        private void RememberHider()
        {
            _wasSeeingAtLastDecision = _perception != null && _perception.IsSeeing;
            _hiderDistanceAtLastDecision = _wasSeeingAtLastDecision ? _perception.CurrentDistance : 0f;
        }

        // Ping na decisão anterior: qual nó tocava e a que distância em metros pelo grafo. O
        // delta só é comparável quando é o MESMO ping nas duas decisões — ao chegar (ou expirar)
        // o alvo some, e um ping novo em outro lugar do mapa faria a distância saltar.
        private int _pingTargetAtLastDecision = -1;
        private float _pingDistanceAtLastDecision;

        private void RememberPing()
        {
            _pingTargetAtLastDecision = _ping != null && _ping.IsActive ? _ping.TargetNode : -1;
            _pingDistanceAtLastDecision = _ping != null ? _ping.Distance : 0f;
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
            GameObject other = collision.gameObject;
            if (!IsWall(other))
                return;

            if (IsPenaltyFreeWall(other))
                _touchingFreeWall = true;
            else
                _touchingWall = true;
        }

        // Parede é identificada por LAYER (a mesma máscara que valida as ligações do grafo), e
        // não por tag: uma segunda fonte de verdade para "isto é uma parede" já custou um termo
        // de recompensa morto e silencioso neste projeto.
        private bool IsWall(GameObject other) =>
            _graph != null && (_graph.WallLayer.value & (1 << other.layer)) != 0;

        // Parede SEM punição (_penaltyFreeWallLayer). Só vale se a layer também estiver na máscara
        // de parede do NavGraph — senão ela nem é parede (visão atravessa, grafo ignora).
        private bool IsPenaltyFreeWall(GameObject other) =>
            (_penaltyFreeWallLayer.value & (1 << other.layer)) != 0;

        private void FinishEpisode(bool covered)
        {
            _episodeEnding = true;
            _arenaController.ShowOutcome(covered);
            RecordEpisodeStats();

            float delay = _arenaController.EpisodeEndDelay;
            if (delay <= 0f)
            {
                EndEpisode();
                return;
            }

            StartCoroutine(EndEpisodeAfterDelay(delay));
        }

        /// <summary>
        /// Métricas do episódio no TensorBoard, SEPARADAS da recompensa: o Cumulative Reward muda
        /// a cada ajuste de peso, estas não. Compare runs por elas.
        ///   Exploration/Coverage         cobertura ao fim do episódio (a medida que decide o alvo)
        ///   Exploration/OffNodeFraction  fração dos steps FORA de qualquer área de nó. Perto de 0
        ///                                = o grafo cobre o chão; alto = rode o NavGraphPlacer.
        ///   Exploration/WallContactFraction  fração dos steps encostado em parede (batendo muito = alto)
        ///   Exploration/EarlyRevisits    revisitas precoces (loop) no episódio
        ///   Exploration/AnchorFlicker    pisca-pisca de âncora (A-B-A em &lt; 2 s andando &lt; 1 m): borda de
        ///                                ladrilho, não decisão. Alto = folga na borda; EarlyRevisits alto = navegação
        ///   Exploration/WallHits         batidas em parede (início de contato) no episódio
        ///   Movement/ActionJitter        média de |Δação|² por decisão (tremor; 0 = sempre reto)
        ///   Movement/LookJitter          o mesmo para o olhar (cone piscando = alto)
        ///   Movement/IdleFraction        fração do episódio parado (&lt; 0.5 m/s). Alto com cobertura
        ///                                baixa = aprendeu a ficar quieto; moderado na caça = parando para olhar
        ///   Hunt/Seen, Hunt/Caught       só nos episódios com hider: viu alguma vez / pegou
        ///   Search/Cleared               suspeita limpa que pagou no episódio (procura)
        /// </summary>
        private void RecordEpisodeStats()
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            stats.Add("Exploration/Coverage", CurrentCoverage);

            if (_memory.TickedSteps > 0)
                stats.Add("Exploration/OffNodeFraction", (float)_memory.OffNodeSteps / _memory.TickedSteps);

            if (_elapsedSteps > 0)
            {
                stats.Add("Exploration/WallContactFraction", (float)_wallContactSteps / _elapsedSteps);
                stats.Add("Movement/IdleFraction", (float)_idleSteps / _elapsedSteps);
            }

            stats.Add("Exploration/EarlyRevisits", _memory.EarlyRevisitCount);
            stats.Add("Exploration/AnchorFlicker", _memory.AnchorFlickers);

            if (_logLoopNodes)
            {
                string top = _memory.TopLoopNodes(3);
                if (top.Length > 0)
                {
                    Debug.Log(
                        $"{name}: loops {_memory.EarlyRevisitCount}, pisca-pisca {_memory.AnchorFlickers} — " +
                        $"nós mais repetidos: {top}", this);
                }
            }
            stats.Add("Exploration/WallHits", _wallHits.EpisodeHits);

            if (_decisionCount > 0)
            {
                stats.Add("Movement/ActionJitter", _actionChangeSum / _decisionCount);
                stats.Add("Movement/LookJitter", _lookChangeSum / _decisionCount);
            }

            if (_perception != null && _arenaController.HiderMode != GraphHider.Mode.None)
            {
                stats.Add("Hunt/Seen", _perception.HasSeen ? 1f : 0f);
                stats.Add("Hunt/Caught", _perception.Caught ? 1f : 0f);
            }

            if (_suspicion != null && _suspicion.IsActive)
                stats.Add("Search/Cleared", _suspicion.EpisodeCleared);
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

            // Na mão, olha para onde anda (parado, o olhar zero mantém a direção).
            continuous[2] = continuous[0];
            continuous[3] = continuous[1];
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

            // Sem salas numeradas o tédio de área (area_boredom) não tem o que medir: liga no
            // currículo e não faz nada, em silêncio.
            if (_graph.MaxAreaId == 0)
            {
                Debug.LogWarning(
                    $"{name}: nenhum nó tem sala (_areaId). O tédio de área fica sem efeito — rode " +
                    "NavGraphPlacer > \"11. Numerar salas\" antes das lições de patrulha.", this);
            }

            // Layer "sem punição" fora da máscara de parede do NavGraph: ela nem conta como parede
            // (visão atravessa, grafo ignora, steering não desvia) — o contrário do que se quer.
            int freeOutsideWalls = _penaltyFreeWallLayer.value & ~_graph.WallLayer.value;
            if (freeOutsideWalls != 0)
            {
                Debug.LogWarning(
                    $"{name}: a layer de parede sem punição não está no Wall Layer do NavGraph — marque " +
                    "ela lá também, senão ela não é parede para visão, grafo e steering.", this);
            }

            if (_suspicion == null)
            {
                Debug.LogWarning(
                    $"{name}: sem GraphSuspicionMap no agente — a procura fica desligada (observação zero, " +
                    "nada pago). Add Component > Graph Suspicion Map antes das lições com hider.", this);
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
            if (continuousActions != 4)
            {
                Debug.LogError(
                    $"{name}: esperadas 4 ações contínuas (andar X/Z, olhar X/Z), encontradas {continuousActions}.", this);
            }
        }
    }
}
