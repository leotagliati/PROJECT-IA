using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O PING: um nó de ping do mapa "toca" e o agente tem que ir até ele (NavGraph.IsPingSource).
    /// Com hider ligado, o ping é o rastro dele: cada chegada do hider num nó de ping toca ali;
    /// sem hider, o nó é sorteado a cada _defaultInterval/ping_interval steps. Nada aqui sabe de
    /// posição do hider além do nó da chegada.
    ///
    /// Observação [13..15]: ativo, distância em metros pelo grafo (normalizada por
    /// NavGraph.PathDiameter) e quente/frio (-1/0/+1 na última troca de nó). Sem direção nem
    /// caminho, de propósito: o agente descobre a saída em que a distância cai.
    ///
    /// Paga no GraphRewardSystem: chegada (+) e expiração (-). Aproximação não paga; o incentivo do
    /// caminho é a sala do ping ficar QUENTE (GraphRoomMemory.HeatRoom).
    ///
    /// Um por agente (estado de episódio); Tick a cada step de FÍSICA, e as flags acumulam até
    /// <see cref="ClearStepFlags"/>.
    /// </summary>
    public class GraphPingSystem : MonoBehaviour
    {
        [Header("-----Ping-----")]
        // Steps de física entre pings quando o currículo não manda (ping_interval); 0 = sem ping.
        // O intervalo real é sorteado em [0.5, 1.5] x este valor, para não virar relógio.
        [SerializeField, Min(0)] private int _defaultInterval = 0;

        // Tempo que o ping fica ativo antes de expirar, em steps de física (3000 = 60 s).
        [SerializeField, Min(1)] private int _duration = 3000;

        // Distância mínima (m pelo grafo) do agente ao nó sorteado; evita ping "já chegou".
        [SerializeField, Min(0f)] private float _minDistanceMeters = 15f;

        // Steps de física antes do primeiro ping, para o agente sair do spawn.
        [SerializeField, Min(0)] private int _firstPingDelay = 1000;

        [Header("-----Fonte do ping-----")]
        // O hider da arena. Ativo, ele dá os pings (cada chegada toca) e o sorteio aleatório fica
        // desligado. Fallback por GetComponentInChildren no GraphArenaController.
        [SerializeField] private GraphHider _hider;

        // O alvo de fato: o hider no treino, o jogador no modo de jogo (SetTarget).
        private IGraphTarget _target;

        [Header("-----Gizmos (só em Play)-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Gizmo rosa: farol vertical + esfera no nó que está tocando.
        private static readonly Color PingColor = new Color(1f, 0.45f, 0.8f, 0.95f);

        private NavGraph _graph;
        private int _interval;
        private int _nextPingStep;
        private int _expiresAtStep;

        public bool IsActive { get; private set; }

        /// <summary>Nó que está tocando, ou -1.</summary>
        public int TargetNode { get; private set; } = -1;

        /// <summary>Distância em METROS pelo grafo, do nó âncora do agente ao ping. Válido só com IsActive.</summary>
        public float Distance { get; private set; }

        /// <summary>
        /// Quente/frio: +1 se a última troca de nó aproximou do ping, -1 se afastou, 0 se não
        /// houve troca ou não há ping.
        /// </summary>
        public int HotCold { get; private set; }

        /// <summary>Chegou ao nó do ping desde o último <see cref="ClearStepFlags"/>.</summary>
        public bool Reached { get; private set; }

        /// <summary>
        /// Valor (NavGraph.PingValue) dos pings atendidos desde o último ClearStepFlags; o reward
        /// system multiplica pelo prêmio.
        /// </summary>
        public float ReachedValue { get; private set; }

        /// <summary>Um ping expirou sem visita desde o último <see cref="ClearStepFlags"/>.</summary>
        public bool Missed { get; private set; }

        private int _startedNode = -1;

        /// <summary>
        /// Nó em que um ping COMEÇOU desde a última consulta, ou -1. O manager consome a cada step
        /// para esquentar a sala do barulho.
        /// </summary>
        public int ConsumeStarted()
        {
            int node = _startedNode;
            _startedNode = -1;
            return node;
        }

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();

            if (_hider == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                if (arena != null)
                    _hider = arena.GetComponentInChildren<GraphHider>(includeInactive: true);
            }

            if (_target == null && _hider != null)
                _target = _hider;
        }

        public void SetTarget(IGraphTarget target) => _target = target;

        private bool HiderDrivesPings => GraphTarget.IsLive(_target);

        /// <param name="interval">Steps de física entre pings; 0 usa o padrão. Vem do currículo.</param>
        public void ResetEpisode(int interval)
        {
            _interval = interval > 0 ? interval : _defaultInterval;
            IsActive = false;
            TargetNode = -1;
            Distance = 0f;
            HotCold = 0;
            _startedNode = -1;
            ClearStepFlags();

            _nextPingStep = _interval > 0 ? _firstPingDelay + Jittered(_interval) : int.MaxValue;

            // Hider que nasceu num nó de ping deixa PendingArrival: o primeiro Tick vira o 1º ping.
        }

        public void ClearStepFlags()
        {
            Reached = false;
            ReachedValue = 0f;
            Missed = false;
        }

        /// <summary>
        /// Chamar a cada step de física, DEPOIS do Tick da memória (usa o nó âncora atualizado).
        /// </summary>
        public void Tick(int currentNode, int elapsedSteps)
        {
            if (_graph == null)
                return;

            // Chegada do hider substitui o ping ativo (o rastro se moveu) sem contar como perdido.
            if (HiderDrivesPings)
            {
                int arrival = _target.ConsumeArrival();
                if (arrival >= 0 && arrival != TargetNode)
                    StartPingAt(arrival, currentNode, elapsedSteps);
            }

            if (IsActive)
            {
                if (currentNode == TargetNode)
                {
                    Reached = true;
                    ReachedValue += _graph.PingValue(TargetNode);
                    EndPing(elapsedSteps);
                    return;
                }

                if (elapsedSteps >= _expiresAtStep)
                {
                    Missed = true;
                    EndPing(elapsedSteps);
                    return;
                }

                UpdateDistance(currentNode);
                return;
            }

            if (!HiderDrivesPings && elapsedSteps >= _nextPingStep)
                StartPing(currentNode, elapsedSteps);
        }

        private void StartPing(int currentNode, int elapsedSteps)
        {
            int target = PickTarget(currentNode);
            if (target < 0)
            {
                // Mapa pequeno demais ou tudo desligado: tenta de novo mais tarde.
                _nextPingStep = elapsedSteps + Jittered(_interval);
                return;
            }

            StartPingAt(target, currentNode, elapsedSteps);
        }

        private void StartPingAt(int target, int currentNode, int elapsedSteps)
        {
            IsActive = true;
            TargetNode = target;
            _startedNode = target;
            HotCold = 0;
            _expiresAtStep = elapsedSteps + _duration;
            _lastDistanceNode = -1;
            UpdateDistance(currentNode);
        }

        private void EndPing(int elapsedSteps)
        {
            IsActive = false;
            TargetNode = -1;
            Distance = 0f;
            HotCold = 0;
            _nextPingStep = elapsedSteps + Jittered(_interval);
        }

        // Nó do qual a distância foi medida: só recalcula (e atualiza quente/frio) quando o agente troca de nó.
        private int _lastDistanceNode = -1;

        private void UpdateDistance(int currentNode)
        {
            if (currentNode < 0 || currentNode == _lastDistanceNode)
                return;

            if (!_graph.TryFindPathTo(currentNode, TargetNode, out _, out float distance))
            {
                // Inalcançável daqui (não deveria acontecer num grafo conexo): mantém a última.
                _lastDistanceNode = currentNode;
                return;
            }

            // Tolerância de 1 cm: caminhos de mesmo comprimento não podem piscar por float.
            if (_lastDistanceNode >= 0)
                HotCold = distance < Distance - 0.01f ? 1 : distance > Distance + 0.01f ? -1 : 0;

            Distance = distance;
            _lastDistanceNode = currentNode;
        }

        // Sorteio com rejeição (até NodeCount tentativas) de um nó de ping a >= _minDistanceMeters do agente.
        private int PickTarget(int currentNode)
        {
            int count = _graph.NodeCount;
            for (int attempt = 0; attempt < count; attempt++)
            {
                int candidate = Random.Range(0, count);
                if (!_graph.IsNodeEnabled(candidate) || !_graph.IsPingSource(candidate) || candidate == currentNode)
                    continue;

                if (currentNode >= 0)
                {
                    if (!_graph.TryFindPathTo(currentNode, candidate, out _, out float distance) || distance < _minDistanceMeters)
                        continue;
                }

                return candidate;
            }

            return -1;
        }

        private static int Jittered(int interval) => Mathf.RoundToInt(interval * Random.Range(0.5f, 1.5f));

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || !Application.isPlaying || !IsActive || _graph == null)
                return;

            Vector3 p = _graph.NodePosition(TargetNode);
            Gizmos.color = PingColor;
            Gizmos.DrawWireSphere(p + Vector3.up * 0.5f, 0.6f);
            Gizmos.DrawLine(p, p + Vector3.up * 4f);
        }
    }
}
