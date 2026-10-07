using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O PING: um nó de ping do mapa "toca" e o agente tem que ir até ele (NavGraph.IsPingSource).
    /// Com hider ligado, o ping é o rastro dele: cada chegada do hider num nó de ping toca ali;
    /// sem hider, o nó é sorteado a cada _defaultInterval/ping_interval steps. Nada aqui sabe de
    /// posição do hider além do nó da chegada. Com o alvo À VISTA o ping não existe: o ativo some (sem
    /// contar como perdido) e as chegadas não tocam. Barulho é pista de quem sumiu, não de quem está na frente.
    ///
    /// AUDIÇÃO (06/10): alvo CORRENDO a até _runHearingMeters pelo grafo é ouvido; o ping vai para o nó dele e
    /// segue o alvo enquanto ele corre e é ouvido (HeardRunning; o Manager põe o seeker em Perseguição). Ping
    /// de alvo (ouvido ou chegada) ESMAECE: a força (Strength, observação [13]) cai pela metade a cada
    /// _targetPingHalfLifeSteps desde a última renovação, e ele só some quando outro barulho o substitui, quando o
    /// seeker chega nele ou quando o alvo é visto. O ping aleatório (sem alvo) segue com força 1 e expira em _duration.
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

        [Header("-----Audição (alvo correndo)-----")]
        // Distância (m PELO GRAFO, o caminho pelas portas, não a reta através da parede) até onde o seeker ouve o
        // alvo CORRENDO. 30 m = uma ou duas salas. 0 = sem audição.
        [SerializeField, Min(0f)] private float _runHearingMeters = 30f;

        // Steps de física entre as checagens de audição (custa um Dijkstra). 10 = 0.2 s.
        [SerializeField, Min(1)] private int _hearCheckSteps = 10;

        // Meia-vida da FORÇA do ping do alvo (ouvido correndo ou chegada do hider), em steps de física, contada da
        // última renovação: 500 = 10 s (força 0.5 aos 10 s, 0.25 aos 20 s...). Não expira: um rastro velho é
        // fraco, não inexistente, e só some quando outro barulho toca, o seeker chega ou o alvo é visto.
        [SerializeField, Min(1)] private int _targetPingHalfLifeSteps = 500;

        // O hider no treino, o jogador no modo de jogo (GraphArenaController.Target). Ativo, ele dá os
        // pings (cada chegada toca) e o sorteio aleatório fica desligado.
        private IGraphTarget _target;

        [Header("-----Gizmos (só em Play)-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Gizmo rosa: farol vertical + esfera no nó que está tocando.
        private static readonly Color PingColor = new Color(1f, 0.45f, 0.8f, 0.95f);

        private NavGraph _graph;
        private int _interval;
        private int _nextPingStep;
        private int _expiresAtStep;     // < 0 = não expira (ping do alvo)
        private int _renewedAtStep;
        private int _lastElapsedStep;
        private bool _fades;

        public bool IsActive { get; private set; }

        /// <summary>
        /// Força do ping (0..1): 1 ao tocar ou ser renovado; o do alvo cai pela meia-vida, o aleatório fica em 1.
        /// 0 sem ping.
        /// </summary>
        public float Strength => !IsActive ? 0f
            : !_fades ? 1f
            : Mathf.Pow(0.5f, Mathf.Max(0, _lastElapsedStep - _renewedAtStep) / (float)_targetPingHalfLifeSteps);

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

        // Contagem do episódio, só para o TensorBoard (Ping/*): sem isso, "o ping funciona?" só se via de forma
        // indireta (AlertFraction e saltos na reward).
        public int EpisodeStarted { get; private set; }
        public int EpisodeReached { get; private set; }
        public int EpisodeMissed { get; private set; }
        public int EpisodeSilenced { get; private set; }
        public int EpisodeHeard { get; private set; }

        /// <summary>
        /// Ouviu o alvo correndo na última checagem de audição (vale até a próxima). O Manager usa para pôr o
        /// seeker em Perseguição (GraphLocomotion.NotifyChaseCue).
        /// </summary>
        public bool HeardRunning { get; private set; }

        private int _ticks;

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

        public void Configure(NavGraph graph, IGraphTarget target)
        {
            _graph = graph;
            _target = target;
            _graph.EnsureBaked();
        }

        private bool HiderDrivesPings => GraphTarget.IsLive(_target);

        /// <summary>Intervalo da lição (ping_interval); 0 lá usa o _defaultInterval.</summary>
        public void ResetEpisode(in GraphEpisodeSettings settings)
        {
            _interval = settings.PingInterval > 0 ? settings.PingInterval : _defaultInterval;
            IsActive = false;
            TargetNode = -1;
            Distance = 0f;
            HotCold = 0;
            _startedNode = -1;
            EpisodeStarted = 0;
            EpisodeReached = 0;
            EpisodeMissed = 0;
            EpisodeSilenced = 0;
            EpisodeHeard = 0;
            HeardRunning = false;
            _ticks = 0;
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
        /// Chamar a cada step de física, DEPOIS do Tick da memória (usa o nó âncora atualizado) e da
        /// visão (<paramref name="targetVisible"/> = GraphHiderPerception.IsSeeing deste step).
        /// </summary>
        public void Tick(int currentNode, int elapsedSteps, bool targetVisible)
        {
            if (_graph == null)
                return;

            _ticks++;
            _lastElapsedStep = elapsedSteps;

            if (HiderDrivesPings)
            {
                int arrival = _target.ConsumeArrival();

                // Vendo o alvo, o ping deixa de existir: ir ao nó do barulho pagaria por um rastro velho com o
                // hider na frente, e a sala quente (GraphRoomMemory) reabria e pagava de novo. A chegada é
                // consumida mesmo assim, senão viraria ping atrasado no instante em que ele some.
                if (targetVisible)
                {
                    HeardRunning = false;
                    if (IsActive)
                        Silence();
                    return;
                }

                // Chegada do hider substitui o ping ativo (o rastro se moveu) sem contar como perdido.
                if (arrival >= 0 && arrival != TargetNode)
                    StartPingAt(arrival, currentNode, elapsedSteps, duration: -1);

                if (_ticks % _hearCheckSteps == 0)
                    HearRunning(currentNode, elapsedSteps);
            }

            if (IsActive)
            {
                if (currentNode == TargetNode)
                {
                    Reached = true;
                    EpisodeReached++;
                    ReachedValue += _graph.PingValue(TargetNode);
                    EndPing(elapsedSteps);
                    return;
                }

                if (_expiresAtStep >= 0 && elapsedSteps >= _expiresAtStep)
                {
                    Missed = true;
                    EpisodeMissed++;
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

            StartPingAt(target, currentNode, elapsedSteps, _duration);
        }

        // Alvo correndo ao alcance (pelo grafo): o ping vai para o nó dele, ou só renova se já está lá.
        private void HearRunning(int currentNode, int elapsedSteps)
        {
            bool wasHeard = HeardRunning;
            HeardRunning = false;

            if (_runHearingMeters <= 0f || !_target.IsRunning || currentNode < 0)
                return;

            int node = _target.CurrentNode;
            if (node < 0)
                return;

            if (node != currentNode
                && (!_graph.TryFindPathTo(currentNode, node, out _, out float distance) || distance > _runHearingMeters))
                return;

            HeardRunning = true;
            if (!wasHeard)
                EpisodeHeard++;

            if (IsActive && TargetNode == node)
                _renewedAtStep = elapsedSteps;
            else
                StartPingAt(node, currentNode, elapsedSteps, duration: -1);
        }

        private void StartPingAt(int target, int currentNode, int elapsedSteps, int duration)
        {
            IsActive = true;
            TargetNode = target;
            _startedNode = target;
            EpisodeStarted++;
            HotCold = 0;
            // duration < 0: ping do alvo, não expira e esmaece (Strength).
            _expiresAtStep = duration >= 0 ? elapsedSteps + duration : -1;
            _fades = duration < 0;
            _renewedAtStep = elapsedSteps;
            _lastDistanceNode = -1;
            UpdateDistance(currentNode);
        }

        // Apaga o ping ativo sem chegada nem expiração (alvo à vista). Só existe com hider, que não usa _nextPingStep.
        private void Silence()
        {
            IsActive = false;
            TargetNode = -1;
            Distance = 0f;
            HotCold = 0;
            EpisodeSilenced++;
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
