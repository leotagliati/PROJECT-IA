using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O PING: de tempos em tempos, um nó de PING aleatório do mapa "toca" e o agente tem que
    /// ir até ele (NodeKind.Ping; num grafo sem nenhum, um de exploração — NavGraph.IsPingSource). É o embrião da fase de busca (um barulho numa sala = vá conferir), montado
    /// sobre o mesmo grafo e a mesma memória da exploração — nada aqui sabe de Hider.
    ///
    /// Uma instância POR AGENTE (é estado de episódio): quando o próximo ping toca, qual nó,
    /// a que distância em arestas o agente está dele agora, se chegou ou se deixou expirar.
    ///
    /// O QUE O AGENTE RECEBE (bloco [13..15] da observação, que estava reservado para isto):
    ///   ativo (0/1), distância em METROS pelo grafo, normalizada pelo diâmetro do mapa
    ///   (NavGraph.PathDiameter), quente/frio (-1, 0, +1: a última troca
    ///   de nó afastou, nada, aproximou). NÃO recebe direção nem caminho — a regra do projeto:
    ///   dado, não resposta. Ele tem que descobrir por qual saída a distância cai, e a
    ///   observação de quente/frio é o que torna isso aprendível sem decorar o mapa.
    ///
    /// O QUE PAGA (GraphRewardSystem): por METRO de aproximação pelo grafo, um bônus ao chegar,
    /// e uma penalidade se o ping expirar sem visita. A distância é PELO GRAFO, e não em linha
    /// reta — contornar uma parede para chegar a uma porta aumenta a euclidiana e diminui a de
    /// grafo, e a segunda é a que descreve progresso. Em metros, e não em arestas: contar
    /// arestas fazia o mesmo trajeto pagar 3x mais num corredor com 3x mais nós.
    ///
    /// Tick a cada step de FÍSICA (como a memória): chegada e expiração precisam ser vistas no
    /// step em que acontecem, não na próxima decisão. As flags são ACUMULATIVAS até
    /// <see cref="ClearStepFlags"/>.
    /// </summary>
    public class GraphPingSystem : MonoBehaviour
    {
        [Header("-----Ping-----")]
        // Fallback quando a cena roda sem trainer (inferência). No treino vem do currículo
        // (ping_interval), em steps de FÍSICA entre o fim de um ping e o começo do próximo.
        // 0 = sem ping. O intervalo real é sorteado em [0.5, 1.5] x este valor, para o ping
        // não virar relógio que a política decora ("aos 3000 steps toca").
        [SerializeField, Min(0)] private int _defaultInterval = 0;

        // Quanto tempo o ping fica ativo antes de expirar, em steps de física. 3000 = 60 s:
        // o bastante para atravessar metade do mapa (diâmetro ~42 arestas x 7 m a 5 m/s ≈ 60 s
        // em linha), apertado o suficiente para "ir depois" custar.
        [SerializeField, Min(1)] private int _duration = 3000;

        // Distância mínima, em METROS pelo grafo, do agente ao nó sorteado. Evita o ping que
        // "já chegou" (a sala ao lado: um passo e pronto, nada a aprender). 15 m ~ duas arestas
        // do mapa antigo (mediana 7.4 m), que era o valor em arestas antes.
        // Nome novo de propósito: o campo antigo (_minDistance) era em ARESTAS, e o valor salvo
        // no prefab viraria "2 metros" em silêncio se o nome fosse mantido.
        [SerializeField, Min(0f)] private float _minDistanceMeters = 15f;

        // Primeiro ping não toca antes disto, em steps: dá tempo de o agente sair do spawn e
        // pegar um rumo de exploração antes de ser interrompido.
        [SerializeField, Min(0)] private int _firstPingDelay = 1000;

        [Header("-----Fonte do ping-----")]
        // O hider desta arena. Com ele LIGADO (hider_mode != 0), os pings vêm dos passos dele
        // (toda chegada num primário toca) e o sorteio aleatório fica desligado — o ping vira
        // o rastro de alguém, que é o que a fase de perseguição precisa. Sem hider (ou com ele
        // desligado na lição), vale o sorteio por _interval. Fallback por GetComponentInChildren
        // no GraphArenaController.
        [SerializeField] private GraphHider _hider;

        [Header("-----Gizmos (só em Play)-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Rosa: não é verde/laranja/amarelo (estado da memória), magenta (fronteira), branco
        // (aresta) nem cinza (pendente). Farol vertical + esfera no nó que está tocando.
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
        /// houve troca ou não há ping. Recalculado a cada troca de nó, e é isso que a
        /// observação entrega — a política aprende "esta saída esquentou" sem ver o caminho.
        /// </summary>
        public int HotCold { get; private set; }

        /// <summary>Chegou ao nó do ping desde o último <see cref="ClearStepFlags"/>.</summary>
        public bool Reached { get; private set; }

        /// <summary>
        /// Valor do ping atendido (NavGraph.PingValue: pontuação do tipo x peso do nó), somado se
        /// mais de um for atendido no mesmo intervalo. O reward system multiplica pelo prêmio.
        /// </summary>
        public float ReachedValue { get; private set; }

        /// <summary>Um ping expirou sem visita desde o último <see cref="ClearStepFlags"/>.</summary>
        public bool Missed { get; private set; }

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
        }

        private bool HiderDrivesPings => _hider != null && _hider.IsActive;

        /// <param name="interval">Steps de física entre pings; 0 desliga. Vem do currículo.</param>
        public void ResetEpisode(int interval)
        {
            _interval = interval > 0 ? interval : _defaultInterval;
            IsActive = false;
            TargetNode = -1;
            Distance = 0f;
            HotCold = 0;
            ClearStepFlags();

            _nextPingStep = _interval > 0 ? _firstPingDelay + Jittered(_interval) : int.MaxValue;

            // O hider já pode ter nascido num primário (PendingArrival): o Tick do primeiro step
            // de física do episódio transforma isso no primeiro ping.
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

            // Passos do hider: cada chegada num nó de ping vira um ping ali — substitui o que
            // estiver ativo (o rastro se moveu) sem contar como perdido.
            if (HiderDrivesPings)
            {
                int arrival = _hider.ConsumeArrival();
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

            // Sorteio aleatório só quando o ping não tem dono.
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

        // Nó do qual a distância atual foi medida: só recalcula (e só atualiza quente/frio)
        // quando o agente TROCA de nó — a distância é medida a partir do nó âncora.
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

            // 1 cm de tolerância: dois caminhos de mesmo comprimento não podem piscar quente/frio
            // por arredondamento de float.
            if (_lastDistanceNode >= 0)
                HotCold = distance < Distance - 0.01f ? 1 : distance > Distance + 0.01f ? -1 : 0;

            Distance = distance;
            _lastDistanceNode = currentNode;
        }

        // Nó de ping ativo (ou de exploração, num grafo sem ping), a pelo menos
        // _minDistanceMeters do agente. Sorteio com rejeição:
        // até NodeCount tentativas, que num grafo conexo de 100 nós acha em duas ou três.
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
