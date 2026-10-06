using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A PROCURA: crença do seeker sobre onde o hider pode estar, uma probabilidade por nó (soma 1;
    /// docs/graph/procura-e-ping.md). Espalha pelas arestas na velocidade suposta do hider, zera os
    /// nós que o seeker vê ou pisa e concentra no nó do ping (_pingConfidence); zerou tudo, volta a
    /// uniforme no que não está vendo. Só usa pistas legítimas (visão e ping).
    ///
    /// VENDO o hider a crença fica ZERADA: não há o que supor, nada paga e nenhuma sala reabre por
    /// suspeita (antes ela ficava 100% no nó dele e as salas vizinhas reabriam valendo até 8x, renda
    /// maior que pegar). Ao PERDER de vista, ela nasce no nó mais perto de onde ele sumiu que o seeker não
    /// está vendo, puxada para a direção em que ele ia (SeedFromLoss), e daí espalha pelo grafo.
    ///
    /// Observação: por vizinho, a suspeita alcançável pela saída (NavGraph.ScoreBeyond) e 3 globais
    /// (ativa, certeza, tempo desde a última pista). Paga _suspicionClearedReward x massa zerada,
    /// com carência por nó; a GraphRoomMemory também usa RoomRatio para valorizar ver a sala suspeita.
    ///
    /// Um por agente (estado de episódio); Tick a cada step de física, com a conta pesada a cada
    /// _updateIntervalSteps.
    /// </summary>
    public class GraphSuspicionMap : MonoBehaviour
    {
        [Header("-----Atualização-----")]
        // Steps de física entre espalhar/limpar (5 = uma vez por decisão). O ping é lido todo step.
        [SerializeField, Min(1)] private int _updateIntervalSteps = 5;

        [Header("-----Espalhar-----")]
        // Velocidade (m/s) suposta para o hider quando o currículo não diz (6 = o jogador andando); 0 (hider
        // parado) não espalha.
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = 6f;

        // Teto da fração da suspeita de um nó que sai por atualização; sem ele, arestas curtas passariam de 1 e a crença oscilaria.
        [SerializeField, Range(0.05f, 0.5f)] private float _maxSpreadPerUpdate = 0.5f;

        [Header("-----Limpar-----")]
        // Nós a menos disto (m, no plano) contam como vistos mesmo fora do cone.
        [SerializeField, Min(0f)] private float _touchRadius = 2f;

        // Steps de física (500 = 10 s) desde a última vez visto para um nó pagar de novo ao ser limpo; a
        // limpeza em si acontece sempre. Sem carência, olhar parado para um corredor seria renda.
        [SerializeField, Min(0)] private int _reclearCooldownSteps = 500;

        [Header("-----Pistas-----")]
        // Fração da crença que vai para o nó do ping; com 1.0 um ping velho apagaria a dedução anterior.
        [SerializeField, Range(0f, 1f)] private float _pingConfidence = 0.9f;

        // Ao perder de vista, quanto pesa a mais um nó na direção em que o hider corria: peso = 1 + isto x
        // alinhamento (0 de lado ou atrás, 1 bem na frente). 2 = o nó à frente vale 3x o de trás; a crença não
        // some de trás porque ele pode ter dado meia-volta logo depois de sair do cone.
        [SerializeField, Min(0f)] private float _headingBias = 2f;

        // Segundos que normalizam o "tempo desde a última pista" na observação (satura em 1; 60 = GraphPingSystem._duration).
        [SerializeField, Min(1f)] private float _evidenceHorizonSeconds = 60f;

        [Header("-----Saídas-----")]
        // Meia-vida (m pelo grafo) do desconto da suspeita por saída; igual à de GraphRoomMemory._exitHalfLifeMeters.
        [SerializeField, Min(0.1f)] private float _exitHalfLifeMeters = 20f;

        [Header("-----Gizmos (só em Play)-----")]
        // Gizmo: barra vermelho-escura por nó, altura proporcional à suspeita relativa ao nó mais suspeito.
        [SerializeField] private bool _drawGizmos = true;

        private static readonly Color SuspicionColor = new Color(0.7f, 0.08f, 0.12f, 0.85f);

        private NavGraph _graph;
        private GraphHiderPerception _perception;
        private GraphPingSystem _ping;
        private IGraphTarget _target;

        private float[] _belief;
        private float[] _scratch;
        private float[] _meanEdgeLength;
        private int[] _lastSeenStep;
        private bool[] _seenThisUpdate;
        private float[] _exitValue;

        private int _step;
        private float _speed;
        private int _lastPingNode = -1;
        private int _lastEvidenceStep;
        private bool _hasEvidence;
        private float _bestExitValue;

        // Rastreio: vendo o hider agora; nó e direção (planar, unitária ou zero) da última vez que foi visto.
        private bool _tracking;
        private int _lostNode = -1;
        private Vector3 _lostHeading;
        private int[] _bfsQueue;
        private int[] _bfsDepth;

        /// <summary>Ligada neste episódio (tem hider). Desligada, a observação é zero e nada paga.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Maior suspeita de um nó (0..1): 1 = sei exatamente onde ele está.</summary>
        public float Certainty { get; private set; }

        /// <summary>Tempo desde a última pista (ping ou visão), normalizado (0..1). 1 sem pista nenhuma.</summary>
        public float EvidenceAge => _hasEvidence
            ? Mathf.Clamp01((_step - _lastEvidenceStep) * Time.fixedDeltaTime / _evidenceHorizonSeconds)
            : 1f;

        /// <summary>Suspeita zerada que PAGA desde o último <see cref="ClearStepFlags"/> (fração de 1).</summary>
        public float ClearedMass { get; private set; }

        /// <summary>Suspeita paga no episódio inteiro (métrica Search/Cleared).</summary>
        public float EpisodeCleared { get; private set; }

        /// <param name="target">O hider no treino, o jogador no modo de jogo (GraphArenaController.Target).</param>
        public void Configure(NavGraph graph, GraphHiderPerception perception, GraphPingSystem ping, IGraphTarget target)
        {
            _graph = graph;
            _perception = perception;
            _ping = ping;
            _target = target;
            _graph.EnsureBaked();

            int count = _graph.NodeCount;
            _belief = new float[count];
            _scratch = new float[count];
            _meanEdgeLength = new float[count];
            _lastSeenStep = new int[count];
            _seenThisUpdate = new bool[count];
            _exitValue = new float[count];
            _bfsQueue = new int[count];
            _bfsDepth = new int[count];

            // Comprimento médio das arestas do nó: converte velocidade do hider em fração espalhada.
            for (int i = 0; i < count; i++)
            {
                int[] neighbors = _graph.GetNeighbors(i);
                float sum = 0f;
                foreach (int neighbor in neighbors)
                    sum += PlanarDistance(_graph.NodePosition(i), _graph.NodePosition(neighbor));

                _meanEdgeLength[i] = neighbors.Length > 0 ? Mathf.Max(0.5f, sum / neighbors.Length) : 1f;
            }
        }

        /// <summary>Ligada só com hider; a velocidade suposta vem da lição (&lt; 0 = _defaultHiderSpeed, 0 = parado).</summary>
        public void ResetEpisode(in GraphEpisodeSettings settings)
        {
            IsActive = settings.HasHider && _graph != null;
            float hiderSpeed = settings.AssumedHiderSpeed;
            _speed = hiderSpeed < 0f ? _defaultHiderSpeed : hiderSpeed;
            _step = 0;
            _lastPingNode = -1;
            _hasEvidence = false;
            _lastEvidenceStep = 0;
            _bestExitValue = 0f;
            _tracking = false;
            _lostNode = -1;
            _lostHeading = Vector3.zero;
            EpisodeCleared = 0f;
            ClearStepFlags();

            if (_belief == null)
                return;

            // A primeira limpeza de cada nó paga.
            for (int i = 0; i < _lastSeenStep.Length; i++)
                _lastSeenStep[i] = -_reclearCooldownSteps;

            System.Array.Clear(_seenThisUpdate, 0, _seenThisUpdate.Length);
            ResetUniform(excludeSeen: false);
        }

        public void ClearStepFlags() => ClearedMass = 0f;

        /// <summary>
        /// Quão mais suspeita a sala está que a média (1 = como qualquer lugar); 0 sem procura. A
        /// GraphRoomMemory multiplica por isso o valor de ver a sala e reabre sala concluída.
        /// </summary>
        public float RoomRatio(int room)
        {
            if (!IsActive || _belief == null || room < 0)
                return 0f;

            int[] nodes = _graph.NodesOfRoom(room);
            if (nodes.Length == 0)
                return 0f;

            float mass = 0f;
            foreach (int node in nodes)
                mass += _belief[node];

            return mass * _belief.Length / nodes.Length;
        }

        /// <summary>
        /// Chamar a cada step de física, DEPOIS da memória, do ping e da percepção (usa o que eles medem).
        /// </summary>
        public void Tick(Transform seeker, int currentNode)
        {
            if (!IsActive)
                return;

            _step++;

            // Vendo: crença zerada, conferida todo step para a perda de vista ser semeada no step exato.
            if (_perception != null && _perception.IsSeeing && GraphTarget.IsLive(_target))
            {
                Track();
                return;
            }

            if (_tracking)
            {
                _tracking = false;
                SeedFromLoss(seeker, currentNode);
            }

            // Ping lido todo step: um ping substituído entre duas atualizações ainda é pista.
            int pingNode = _ping != null && _ping.IsActive ? _ping.TargetNode : -1;
            if (pingNode >= 0 && pingNode != _lastPingNode)
                ApplyPing(pingNode);
            _lastPingNode = pingNode;

            if (_step % _updateIntervalSteps != 0)
                return;

            Spread(_updateIntervalSteps * Time.fixedDeltaTime);
            ClearVisible(seeker, currentNode);
            UpdateCertainty();
        }

        // Um step vendo o hider: zera a crença (na entrada) e guarda onde ele está e para onde vai. O ping fica
        // mudo enquanto isso (GraphPingSystem), então _lastPingNode volta a -1 e o próximo barulho é pista nova.
        private void Track()
        {
            if (!_tracking)
            {
                System.Array.Clear(_belief, 0, _belief.Length);
                Certainty = 0f;
                _lostHeading = Vector3.zero;
                _tracking = true;
            }

            if (_target.CurrentNode >= 0)
                _lostNode = _target.CurrentNode;

            // HiderVelocity é zero no 1º step de visão: guarda a última direção medida, não a apaga. 0.5 m/s de
            // corte: parado ou girando no lugar não tem direção.
            Vector3 velocity = _perception.HiderVelocity;
            velocity.y = 0f;
            if (velocity.sqrMagnitude > 0.25f)
                _lostHeading = velocity.normalized;

            _lastPingNode = -1;
            MarkEvidence();
        }

        // Perdeu de vista: se o hider estivesse num nó visível, estaria sendo visto. Então ele está no "anel" mais
        // próximo, pelo grafo, de nós FORA da vista a partir de onde sumiu (busca em largura que atravessa os
        // visíveis), com peso maior na direção em que corria. Daí o Spread espalha e a visão limpa: a procura
        // começa onde ele sumiu, não no mapa todo. Os nós do anel não estão à vista, então nada paga na hora;
        // paga ir olhar (com a carência por nó de sempre).
        private void SeedFromLoss(Transform seeker, int currentNode)
        {
            System.Array.Clear(_belief, 0, _belief.Length);
            MarkEvidence();

            if (_lostNode < 0 || !_graph.IsNodeEnabled(_lostNode))
            {
                ResetUniform(excludeSeen: false);
                return;
            }

            for (int i = 0; i < _bfsDepth.Length; i++)
                _bfsDepth[i] = -1;

            Vector3 origin = _graph.NodePosition(_lostNode);
            int head = 0, tail = 0, ringDepth = -1;
            float total = 0f;
            _bfsQueue[tail++] = _lostNode;
            _bfsDepth[_lostNode] = 0;

            while (head < tail)
            {
                int node = _bfsQueue[head++];
                int depth = _bfsDepth[node];
                if (ringDepth >= 0 && depth > ringDepth)
                    break;

                if (!IsVisible(seeker, currentNode, node))
                {
                    float weight = 1f;
                    Vector3 offset = _graph.NodePosition(node) - origin;
                    offset.y = 0f;
                    if (_lostHeading.sqrMagnitude > 0f && offset.sqrMagnitude > 1e-4f)
                        weight += _headingBias * Mathf.Max(0f, Vector3.Dot(offset.normalized, _lostHeading));

                    _belief[node] = weight;
                    total += weight;
                    ringDepth = depth;
                    continue;
                }

                foreach (int neighbor in _graph.GetNeighbors(node))
                {
                    if (_bfsDepth[neighbor] >= 0 || !_graph.IsNodeEnabled(neighbor))
                        continue;

                    _bfsDepth[neighbor] = depth + 1;
                    _bfsQueue[tail++] = neighbor;
                }
            }

            // Tudo que se alcança está à vista (não deveria acontecer): fica no nó em que sumiu.
            if (total <= 0f)
            {
                _belief[_lostNode] = 1f;
                total = 1f;
            }

            for (int i = 0; i < _belief.Length; i++)
                _belief[i] /= total;

            UpdateCertainty();
        }

        // Nó que o seeker vê, pisa ou tem a menos de _touchRadius: se o hider estivesse ali, seria visto.
        private bool IsVisible(Transform seeker, int currentNode, int node) =>
            node == currentNode
            || PlanarDistance(seeker.position, _graph.NodePosition(node)) <= _touchRadius
            || (_perception != null && _perception.CanSeePoint(seeker, _graph.NodePosition(node)));

        // Cada nó manda aos vizinhos a fração da suspeita que o hider andaria em dt; conserva a soma.
        private void Spread(float dt)
        {
            if (_speed <= 0f)
                return;

            System.Array.Clear(_scratch, 0, _scratch.Length);

            for (int i = 0; i < _belief.Length; i++)
            {
                float mass = _belief[i];
                if (mass <= 0f)
                    continue;

                int[] neighbors = _graph.GetNeighbors(i);
                int enabled = 0;
                foreach (int neighbor in neighbors)
                {
                    if (_graph.IsNodeEnabled(neighbor))
                        enabled++;
                }

                if (enabled == 0)
                {
                    _scratch[i] += mass;
                    continue;
                }

                float outgoing = mass * Mathf.Min(_maxSpreadPerUpdate, _speed * dt / _meanEdgeLength[i]);
                _scratch[i] += mass - outgoing;

                float share = outgoing / enabled;
                foreach (int neighbor in neighbors)
                {
                    if (_graph.IsNodeEnabled(neighbor))
                        _scratch[neighbor] += share;
                }
            }

            (_belief, _scratch) = (_scratch, _belief);
        }

        // Zera a suspeita dos nós visíveis (GraphHiderPerception.CanSeePoint), muito próximos ou da
        // âncora: se o hider estivesse ali, estaria sendo visto. Renormaliza o resto.
        private void ClearVisible(Transform seeker, int currentNode)
        {
            float removed = 0f;
            float paid = 0f;

            for (int i = 0; i < _belief.Length; i++)
            {
                _seenThisUpdate[i] = false;

                // Nó sem suspeita não muda nada e custaria um raycast.
                if (_belief[i] <= 1e-6f || !_graph.IsNodeEnabled(i))
                    continue;

                if (!IsVisible(seeker, currentNode, i))
                    continue;

                _seenThisUpdate[i] = true;
                removed += _belief[i];

                if (_step - _lastSeenStep[i] >= _reclearCooldownSteps)
                    paid += _belief[i];

                _lastSeenStep[i] = _step;
                _belief[i] = 0f;
            }

            if (removed <= 0f)
                return;

            ClearedMass += paid;
            EpisodeCleared += paid;

            float remaining = 1f - removed;
            if (remaining <= 1e-4f)
            {
                // Olhou em todo lugar possível: a crença não pode sumir, volta a uniforme no que não vê.
                ResetUniform(excludeSeen: true);
                return;
            }

            float scale = 1f / remaining;
            for (int i = 0; i < _belief.Length; i++)
                _belief[i] *= scale;
        }

        private void ApplyPing(int node)
        {
            ConcentrateOn(node, _pingConfidence);
            MarkEvidence();
            UpdateCertainty();
        }

        // Move a fração confidence da crença para o nó; a soma continua 1.
        private void ConcentrateOn(int node, float confidence)
        {
            float keep = 1f - confidence;
            for (int i = 0; i < _belief.Length; i++)
                _belief[i] *= keep;

            _belief[node] += confidence;
        }

        private void ResetUniform(bool excludeSeen)
        {
            int count = 0;
            for (int i = 0; i < _belief.Length; i++)
            {
                bool eligible = _graph.IsNodeEnabled(i) && !(excludeSeen && _seenThisUpdate[i]);
                _belief[i] = eligible ? 1f : 0f;
                if (eligible)
                    count++;
            }

            // Mapa inteiro à vista: uniforme em tudo.
            if (count == 0 && excludeSeen)
            {
                ResetUniform(excludeSeen: false);
                return;
            }

            float value = count > 0 ? 1f / count : 0f;
            for (int i = 0; i < _belief.Length; i++)
            {
                if (_belief[i] > 0f)
                    _belief[i] = value;
            }

            UpdateCertainty();
        }

        private void MarkEvidence()
        {
            _hasEvidence = true;
            _lastEvidenceStep = _step;
        }

        private void UpdateCertainty()
        {
            float max = 0f;
            for (int i = 0; i < _belief.Length; i++)
                max = Mathf.Max(max, _belief[i]);

            Certainty = max;
        }

        /// <summary>
        /// Calcula a suspeita por saída do nó âncora (ver <see cref="ExitScore"/>); uma vez por decisão, sem cache.
        /// </summary>
        public void ScoreExits(int currentNode)
        {
            _bestExitValue = 0f;

            if (!IsActive || currentNode < 0)
                return;

            foreach (int neighbor in _graph.GetNeighbors(currentNode))
            {
                float value = _graph.ScoreBeyond(currentNode, neighbor, _exitHalfLifeMeters, _belief);
                _exitValue[neighbor] = value;
                _bestExitValue = Mathf.Max(_bestExitValue, value);
            }
        }

        /// <summary>Suspeita por esta saída RELATIVA à mais suspeita (0..1), como o "quanto resta".</summary>
        public float ExitScore(int neighbor)
        {
            if (!IsActive || _bestExitValue <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(_exitValue[neighbor] / _bestExitValue);
        }

        private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || !Application.isPlaying || !IsActive || _graph == null || Certainty <= 1e-6f)
                return;

            Gizmos.color = SuspicionColor;
            for (int i = 0; i < _belief.Length; i++)
            {
                if (_belief[i] <= 1e-4f)
                    continue;

                Vector3 p = _graph.NodePosition(i);
                float height = 3f * _belief[i] / Certainty;
                Gizmos.DrawLine(p, p + Vector3.up * height);
                Gizmos.DrawWireCube(p + Vector3.up * height, Vector3.one * 0.25f);
            }
        }
    }
}
