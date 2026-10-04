using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A PROCURA: crença do seeker sobre onde o hider pode estar, uma probabilidade por nó (soma 1;
    /// docs/graph/procura-e-ping.md). Espalha pelas arestas na velocidade suposta do hider, zera os
    /// nós que o seeker vê ou pisa, concentra no nó do ping (_pingConfidence) e no hider visto;
    /// zerou tudo, volta a uniforme no que não está vendo. Só usa pistas legítimas (visão e ping).
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

            // Ping lido todo step: um ping substituído entre duas atualizações ainda é pista.
            int pingNode = _ping != null && _ping.IsActive ? _ping.TargetNode : -1;
            if (pingNode >= 0 && pingNode != _lastPingNode)
                ApplyPing(pingNode);
            _lastPingNode = pingNode;

            if (_step % _updateIntervalSteps != 0)
                return;

            Spread(_updateIntervalSteps * Time.fixedDeltaTime);

            // Vendo o hider, nada paga por limpar: ver já tem recompensa própria.
            if (_perception != null && _perception.IsSeeing && GraphTarget.IsLive(_target) && _target.CurrentNode >= 0)
            {
                ConcentrateOn(_target.CurrentNode, 1f);
                MarkEvidence();
            }
            else
            {
                ClearVisible(seeker, currentNode);
            }

            UpdateCertainty();
        }

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
            Vector3 position = seeker.position;
            float removed = 0f;
            float paid = 0f;

            for (int i = 0; i < _belief.Length; i++)
            {
                _seenThisUpdate[i] = false;

                // Nó sem suspeita não muda nada e custaria um raycast.
                if (_belief[i] <= 1e-6f || !_graph.IsNodeEnabled(i))
                    continue;

                Vector3 node = _graph.NodePosition(i);
                bool seen = i == currentNode
                            || PlanarDistance(position, node) <= _touchRadius
                            || (_perception != null && _perception.CanSeePoint(seeker, node));

                if (!seen)
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
