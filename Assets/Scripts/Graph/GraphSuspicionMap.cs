using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A PROCURA: a crença do seeker sobre ONDE O HIDER PODE ESTAR AGORA — uma probabilidade por
    /// nó do grafo, somando 1. Tudo o que ele sabe do hider entra por aqui (docs/graph/procura-e-ping.md):
    ///   começo do episódio      uniforme (pode estar em qualquer lugar)
    ///   o tempo passa           ESPALHA pelas arestas na velocidade do hider (ele pode ter andado)
    ///   ver/passar num nó vazio o nó vai a zero e o resto é renormalizado
    ///   ping (barulho)          quase toda a crença vai para o nó do barulho
    ///   ver o hider             toda a crença vai para o nó dele
    ///   zerou tudo              volta a uniforme nos nós que ele não está vendo
    ///
    /// NÃO É TRAPAÇA: a posição real do hider só entra quando o seeker o VÊ (GraphHiderPerception)
    /// ou quando ele faz barulho (GraphPingSystem). Entre uma pista e outra, é só dedução.
    ///
    /// O QUE O AGENTE RECEBE: por vizinho, a suspeita alcançável por aquela saída (descontada por
    /// metro, NavGraph.ScoreBeyond — o mesmo "o que tem atrás desta porta" da exploração); e três
    /// globais: ativa, CERTEZA (a maior suspeita de um nó) e tempo desde a última pista.
    ///
    /// O QUE PAGA (GraphRewardSystem._suspicionClearedReward): a suspeita ZERADA ao ver ou visitar
    /// nós vazios — procurar onde ele provavelmente está vale mais que olhar onde ele não pode
    /// estar. Com carência por nó (_reclearCooldownSteps) contra ficar parado olhando para um
    /// lugar por onde a suspeita escorre.
    ///
    /// Substitui, nas lições de procura, a patrulha e o tédio de sala: "voltar a um lugar que não
    /// olho há tempo" é a suspeita crescendo de novo ali, sem relógio artificial.
    ///
    /// Uma instância por agente (é estado de episódio). Tick a cada step de física; a conta pesada
    /// (espalhar + raycasts de visão) roda a cada _updateIntervalSteps.
    /// </summary>
    public class GraphSuspicionMap : MonoBehaviour
    {
        [Header("-----Atualização-----")]
        // Espalhar e limpar rodam a cada N steps de física. 5 = uma vez por decisão (Decision
        // Period 5): mais que isso a rede não vê mesmo. O ping é lido todo step (não pode perder).
        [SerializeField, Min(1)] private int _updateIntervalSteps = 5;

        [Header("-----Espalhar-----")]
        // Velocidade (m/s) que o seeker SUPÕE para o hider quando o currículo não diz (hider_speed
        // 0) — no jogo, a do player andando. Hider parado (hider_mode 1) usa 0: não espalha.
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = 1.5f;

        // Fração máxima da suspeita de um nó que sai para os vizinhos por atualização. Sem teto,
        // num nó com arestas curtas a conta (velocidade x dt / aresta) passaria de 1 e a crença
        // oscilaria entre nós em vez de espalhar.
        [SerializeField, Range(0.05f, 0.5f)] private float _maxSpreadPerUpdate = 0.5f;

        [Header("-----Limpar-----")]
        // Nós a menos disto (m, no plano) contam como vistos mesmo fora do cone: a esta distância
        // ele ouviria alguém respirando. Da ordem do raio de captura (2.5).
        [SerializeField, Min(0f)] private float _touchRadius = 2f;

        // Um nó só PAGA de novo por ser limpo depois disto (steps de física; 500 = 10 s) desde a
        // última vez em que foi visto. Olhar sem parar para o mesmo corredor zera a suspeita que
        // escorre para ele a cada atualização — sem carência isso seria renda de ficar parado.
        // A limpeza em si acontece sempre (a crença tem que ser honesta); só o pagamento espera.
        [SerializeField, Min(0)] private int _reclearCooldownSteps = 500;

        [Header("-----Pistas-----")]
        // Quanto da crença vai para o nó do PING (o resto fica onde estava). 0.9: barulho é
        // pista forte, mas não GPS — com 1.0, um ping velho apagaria tudo o que ele já deduziu.
        [SerializeField, Range(0f, 1f)] private float _pingConfidence = 0.9f;

        // Normalizador do "tempo desde a última pista" na observação, em segundos. 60 = um ping
        // inteiro (GraphPingSystem._duration); acima disso a pista é velha e satura em 1.
        [SerializeField, Min(1f)] private float _evidenceHorizonSeconds = 60f;

        [Header("-----Saídas-----")]
        // Meia-vida (m pelo grafo) do desconto da "suspeita por esta saída". A mesma da exploração
        // (GraphExplorationMemory._exitHalfLifeMeters): uma sala + o corredor até a próxima.
        [SerializeField, Min(0.1f)] private float _exitHalfLifeMeters = 20f;

        [Header("-----Gizmos (só em Play)-----")]
        // Barra vermelho-escura em cada nó, altura proporcional à suspeita (relativa ao nó mais
        // suspeito). Não é cor de nenhum outro gizmo (verde/laranja/amarelo/magenta/branco/rosa).
        [SerializeField] private bool _drawGizmos = true;

        private static readonly Color SuspicionColor = new Color(0.7f, 0.08f, 0.12f, 0.85f);

        private NavGraph _graph;
        private GraphHiderPerception _perception;
        private GraphPingSystem _ping;
        private GraphHider _hider;

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

        public void Configure(NavGraph graph, GraphHiderPerception perception, GraphPingSystem ping)
        {
            _graph = graph;
            _perception = perception;
            _ping = ping;
            _graph.EnsureBaked();

            if (_hider == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                if (arena != null)
                    _hider = arena.GetComponentInChildren<GraphHider>(includeInactive: true);
            }

            int count = _graph.NodeCount;
            _belief = new float[count];
            _scratch = new float[count];
            _meanEdgeLength = new float[count];
            _lastSeenStep = new int[count];
            _seenThisUpdate = new bool[count];
            _exitValue = new float[count];

            // Comprimento médio das arestas de cada nó, no plano: é o que converte "o hider anda
            // v m/s" em "fração da suspeita que sai deste nó por atualização".
            for (int i = 0; i < count; i++)
            {
                int[] neighbors = _graph.GetNeighbors(i);
                float sum = 0f;
                foreach (int neighbor in neighbors)
                    sum += PlanarDistance(_graph.NodePosition(i), _graph.NodePosition(neighbor));

                _meanEdgeLength[i] = neighbors.Length > 0 ? Mathf.Max(0.5f, sum / neighbors.Length) : 1f;
            }
        }

        /// <param name="active">Tem hider neste episódio.</param>
        /// <param name="hiderSpeed">m/s que o seeker supõe; &lt; 0 usa _defaultHiderSpeed, 0 = parado.</param>
        public void ResetEpisode(bool active, float hiderSpeed)
        {
            IsActive = active && _graph != null;
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

            // Longe no passado: a primeira limpeza de cada nó paga.
            for (int i = 0; i < _lastSeenStep.Length; i++)
                _lastSeenStep[i] = -_reclearCooldownSteps;

            System.Array.Clear(_seenThisUpdate, 0, _seenThisUpdate.Length);
            ResetUniform(excludeSeen: false);
        }

        public void ClearStepFlags() => ClearedMass = 0f;

        /// <summary>
        /// Chamar a cada step de física, DEPOIS da memória, do ping e da percepção (usa o que eles
        /// acabaram de medir).
        /// </summary>
        public void Tick(Transform seeker, int currentNode)
        {
            if (!IsActive)
                return;

            _step++;

            // Ping lido todo step: um barulho que toca e é substituído entre duas atualizações
            // ainda é uma pista.
            int pingNode = _ping != null && _ping.IsActive ? _ping.TargetNode : -1;
            if (pingNode >= 0 && pingNode != _lastPingNode)
                ApplyPing(pingNode);
            _lastPingNode = pingNode;

            if (_step % _updateIntervalSteps != 0)
                return;

            Spread(_updateIntervalSteps * Time.fixedDeltaTime);

            // Vendo o hider, a crença é o nó dele — e nada paga por "limpar" nesse step: a
            // recompensa de ver já é o _hiderSpottedReward/_hiderApproachReward.
            if (_perception != null && _perception.IsSeeing && _hider != null && _hider.CurrentNode >= 0)
            {
                ConcentrateOn(_hider.CurrentNode, 1f);
                MarkEvidence();
            }
            else
            {
                ClearVisible(seeker, currentNode);
            }

            UpdateCertainty();
        }

        // O hider pode ter andado: cada nó manda uma fração da suspeita para os vizinhos,
        // proporcional a quanto do comprimento médio das arestas dele o hider cobriria em dt.
        // Conserva a soma (é redistribuição, não criação).
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

        // Todo nó visível (cone + linha livre, GraphHiderPerception.CanSeePoint), perto demais
        // para não perceber, ou o nó em que ele está: se o hider não está lá (e não está, senão
        // estaria vendo), a suspeita dali vai a zero.
        private void ClearVisible(Transform seeker, int currentNode)
        {
            Vector3 position = seeker.position;
            float removed = 0f;
            float paid = 0f;

            for (int i = 0; i < _belief.Length; i++)
            {
                _seenThisUpdate[i] = false;

                // Só testa quem tem o que limpar: nó sem suspeita não muda nada e custaria um raycast.
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
                // Olhou em todo lugar possível e não achou: a crença não pode sumir. Ele pode estar
                // em qualquer canto que não estou vendo agora.
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

        // Mistura: confiança no nó, o resto mantém a forma de antes. Soma continua 1.
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

            // Vendo o mapa inteiro de uma vez (não acontece num escritório, mas): uniforme em tudo.
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
        /// Suspeita por saída do nó âncora (ver <see cref="ExitScore"/>). Uma vez por decisão: a
        /// crença muda com o tempo, então não há cache entre decisões.
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
