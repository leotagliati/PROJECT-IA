using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A camada de NÓ da memória do agente no episódio: âncora (nó em que ele "está"), nós pisados,
    /// visitas por nó e diagnósticos de loop. A camada de SALA (cobertura, portas, novidade) é a
    /// <see cref="GraphRoomMemory"/>, que lê esta a cada step.
    ///
    /// Um por agente; o <see cref="NavGraph"/> (estrutura) é compartilhado pela arena. Tick a cada
    /// step de FÍSICA (com Decision Period > 1 o agente pode cruzar um nó entre decisões), então as
    /// flags são acumulativas e o consumidor chama <see cref="ClearStepFlags"/> depois de cobrá-las.
    /// </summary>
    public class GraphExplorationMemory : MonoBehaviour
    {
        [Header("-----Gizmos (só em Play)-----")]
        // Legenda: traço verde = nó de sala pisado (esquenta para laranja com revisitas);
        // contorno cinza = nó de sala que falta; disco amarelo = âncora atual.
        // Estrutura é do NavGraph; portas e salas são da GraphRoomMemory.
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _drawVisitedNodes = true;
        [SerializeField] private bool _drawPendingNodes = true;

        // Revisitas que levam o gizmo ao topo da escala de calor.
        [SerializeField] private int _heatSaturationVisits = 5;

        [Header("-----Revisita precoce (loop)-----")]
        // Revisita precoce (detector de loop): chegar numa PORTA pisada há menos de isto (steps de
        // física; 750 = 15 s). O custo é decidido no GraphRewardSystem, só a partir da N-ésima seguida.
        [SerializeField, Min(1)] private int _earlyRevisitWindowSteps = 750;

        [Header("-----Diagnóstico de pisca-pisca-----")]
        // Pisca-pisca de âncora (A -> B -> A): voltar ao nó anterior em menos de _flickerWindowSteps
        // (steps de física; 100 = 2 s) andando menos de _flickerDistance (m). É o agente na borda de
        // dois ladrilhos, não loop; separado da revisita precoce. Só métrica.
        [SerializeField, Min(1)] private int _flickerWindowSteps = 100;
        [SerializeField, Min(0f)] private float _flickerDistance = 1f;

        private static readonly Color VisitedColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color RevisitedColor = new Color(1f, 0.5f, 0.05f, 0.9f);
        private static readonly Color PendingColor = new Color(0.45f, 0.45f, 0.5f, 0.35f);

        private NavGraph _graph;

        private bool[] _visited;
        private int[] _visitCount;

        // TickedSteps da última chegada a cada nó; só vale com _visited[node].
        private int[] _lastVisitStep;

        // Diagnóstico: loops e pisca-pisca por nó no episódio (log do manager, _logLoopNodes).
        private int[] _loopCount;

        // Quando e onde chegou à âncora atual (base do detector de pisca-pisca).
        private int _lastArrivalStep;
        private Vector3 _lastArrivalPosition;

        public int CurrentNodeIndex { get; private set; } = -1;

        public int PreviousNodeIndex { get; private set; } = -1;

        /// <summary>Se o agente está DENTRO da área de um nó agora (e não a caminho entre dois).</summary>
        public bool IsAtNode { get; private set; }

        /// <summary>A âncora mudou NESTE step de física (a GraphRoomMemory lê isto no mesmo step).</summary>
        public bool ArrivedThisTick { get; private set; }

        /// <summary>A chegada deste step foi num nó ainda não pisado no episódio.</summary>
        public bool ArrivalWasNew { get; private set; }

        /// <summary>Chegadas em porta pisada há menos de _earlyRevisitWindowSteps, desde o último ClearStepFlags.</summary>
        public int EarlyRevisitArrivals { get; private set; }

        /// <summary>Revisitas precoces SEGUIDAS (zera ao chegar numa porta não pisada há tempo).</summary>
        public int EarlyRevisitStreak { get; private set; }

        /// <summary>Revisitas precoces no episódio inteiro (métrica).</summary>
        public int EarlyRevisitCount { get; private set; }

        /// <summary>Pisca-pisca de âncora no episódio (métrica Exploration/AnchorFlicker; ver _flickerWindowSteps).</summary>
        public int AnchorFlickers { get; private set; }

        /// <summary>
        /// Steps de física fora de qualquer área de nó (Exploration/OffNodeFraction): mede se o grafo
        /// cobre o chão; fora de nó a observação de vizinhos fica presa na âncora antiga.
        /// </summary>
        public int OffNodeSteps { get; private set; }

        public int TickedSteps { get; private set; }

        public void Configure(NavGraph graph)
        {
            _graph = graph;
            _graph.EnsureBaked();

            _visited = new bool[_graph.NodeCount];
            _visitCount = new int[_graph.NodeCount];
            _lastVisitStep = new int[_graph.NodeCount];
            _loopCount = new int[_graph.NodeCount];
        }

        public void ResetEpisode()
        {
            System.Array.Clear(_visited, 0, _visited.Length);
            System.Array.Clear(_visitCount, 0, _visitCount.Length);
            System.Array.Clear(_lastVisitStep, 0, _lastVisitStep.Length);
            System.Array.Clear(_loopCount, 0, _loopCount.Length);

            EarlyRevisitStreak = 0;
            EarlyRevisitCount = 0;
            AnchorFlickers = 0;
            _lastArrivalStep = int.MinValue / 2;

            CurrentNodeIndex = -1;
            PreviousNodeIndex = -1;
            IsAtNode = false;
            ArrivedThisTick = false;
            ArrivalWasNew = false;
            OffNodeSteps = 0;
            TickedSteps = 0;

            ClearStepFlags();
        }

        public void Tick(Vector3 worldPosition)
        {
            // Histerese: o nó atual segura a âncora enquanto o agente ainda estiver na área dele.
            int node = _graph.FindNodeAt(worldPosition, CurrentNodeIndex);
            IsAtNode = node >= 0;
            ArrivedThisTick = false;
            ArrivalWasNew = false;

            TickedSteps++;
            if (!IsAtNode)
                OffNodeSteps++;

            // Fora de qualquer área, CurrentNodeIndex NÃO volta a -1: segue o último nó alcançado.
            if (node < 0 || node == CurrentNodeIndex)
                return;

            if (node == PreviousNodeIndex
                && TickedSteps - _lastArrivalStep < _flickerWindowSteps
                && PlanarDistance(worldPosition, _lastArrivalPosition) < _flickerDistance)
            {
                AnchorFlickers++;
                _loopCount[node]++;
            }

            _lastArrivalStep = TickedSteps;
            _lastArrivalPosition = worldPosition;

            RegisterArrival(node);
        }

        /// <summary>Zera as flags acumuladas, depois de cobradas na recompensa; contadores não são afetados.</summary>
        public void ClearStepFlags()
        {
            EarlyRevisitArrivals = 0;
        }

        private void RegisterArrival(int node)
        {
            PreviousNodeIndex = CurrentNodeIndex;
            CurrentNodeIndex = node;
            ArrivedThisTick = true;
            _visitCount[node]++;

            // Ler antes de reiniciar o relógio do nó.
            bool wasVisited = _visited[node];
            int since = TickedSteps - _lastVisitStep[node];
            _lastVisitStep[node] = TickedSteps;

            ArrivalWasNew = !wasVisited;
            _visited[node] = true;

            if (!_graph.IsDoor(node))
                return;

            // Loop só conta em PORTA; dentro da sala, voltar por um ladrilho pisado é só o caminho.
            if (wasVisited && since < _earlyRevisitWindowSteps)
            {
                EarlyRevisitStreak++;
                EarlyRevisitArrivals++;
                EarlyRevisitCount++;
                _loopCount[node]++;
            }
            else
            {
                EarlyRevisitStreak = 0;
            }
        }

        /// <summary>
        /// Marca o nó como pisado sem passagem (sala pré-visitada); fora da janela de revisita precoce.
        /// </summary>
        public void MarkVisited(int node)
        {
            _visited[node] = true;
            _lastVisitStep[node] = -_earlyRevisitWindowSteps;
        }

        /// <summary>Esquece que o nó foi pisado (sala liberada pela GraphRoomMemory).</summary>
        public void Forget(int node)
        {
            _visited[node] = false;
        }

        public bool IsVisited(int node) => _visited[node];

        public int VisitCountOf(int node) => _visitCount[node];

        /// <summary>"Visitado" da observação: 1 pisado, 0 nunca pisado ou esquecido.</summary>
        public float VisitedObservation(int node) => _visited[node] ? 1f : 0f;

        /// <summary>
        /// Diagnóstico: os <paramref name="count"/> nós com mais loops/pisca-pisca, como "nome (n)".
        /// </summary>
        public string TopLoopNodes(int count)
        {
            var builder = new System.Text.StringBuilder();
            var used = new HashSet<int>();

            for (int k = 0; k < count; k++)
            {
                int best = -1;
                for (int i = 0; i < _loopCount.Length; i++)
                {
                    if (_loopCount[i] > 0 && !used.Contains(i) && (best < 0 || _loopCount[i] > _loopCount[best]))
                        best = i;
                }

                if (best < 0)
                    break;

                used.Add(best);
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(_graph.GetNode(best).name).Append(" (").Append(_loopCount[best]).Append(')');
            }

            return builder.ToString();
        }

        private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || _graph == null || _visited == null || !Application.isPlaying)
                return;

            if (_drawVisitedNodes || _drawPendingNodes)
                DrawNodeMemory();

            // Disco da âncora, no formato do gizmo de autoria: confere se a área registra a chegada onde deveria.
            if (CurrentNodeIndex >= 0)
            {
                Gizmos.color = Color.yellow;
                _graph.DrawNodeArea(CurrentNodeIndex, 0.12f, 1);
            }
        }

        /// <summary>Pinta a área de cada nó de sala (portas ficam com a GraphRoomMemory).</summary>
        private void DrawNodeMemory()
        {
            for (int i = 0; i < _visited.Length; i++)
            {
                if (!_graph.IsNodeEnabled(i) || _graph.IsDoor(i))
                    continue;

                if (!_visited[i])
                {
                    if (_drawPendingNodes)
                    {
                        Gizmos.color = PendingColor;
                        _graph.DrawNodeArea(i, 0.04f, 1);
                    }

                    continue;
                }

                if (!_drawVisitedNodes)
                    continue;

                // Verde -> laranja conforme as revisitas.
                float heat = Mathf.Clamp01((_visitCount[i] - 1f) / Mathf.Max(1, _heatSaturationVisits));
                Color visited = Color.Lerp(VisitedColor, RevisitedColor, heat);
                visited.a *= 0.5f;
                Gizmos.color = visited;
                _graph.DrawNodeArea(i, 0.05f, 2);
            }
        }
    }
}
