using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O que ESTE agente já pisou do grafo, neste episódio: a âncora (o nó em que ele "está"),
    /// os nós visitados, quantas vezes passou em cada um e os diagnósticos de loop. É a camada de
    /// NÓ. A camada de SALA — cobertura por sala, travessia de porta, novidade, liberação — é a
    /// <see cref="GraphRoomMemory"/>, que lê esta aqui a cada step.
    ///
    /// Peso por nó, pré-visitados por nó, patrulha por tempo, tédio de sala e a seta de fronteira
    /// moravam aqui e saíram com o plano de salas (docs/graph/salas-e-portas.md): o valor agora é
    /// por sala, e a sala é quem decide o que ainda vale.
    ///
    /// Uma instância por agente (é estado de episódio); o <see cref="NavGraph"/>, que é
    /// estrutura, é compartilhado pela arena.
    ///
    /// O Tick é chamado a cada step de FÍSICA, e não a cada decisão: com Decision Period > 1 o
    /// agente anda vários steps entre duas decisões e pode atravessar a área de um nó inteiro no
    /// meio — a visita simplesmente não seria registrada. Por isso as flags que a recompensa lê
    /// são ACUMULATIVAS e o consumidor as zera com <see cref="ClearStepFlags"/> depois de cobrá-las.
    /// </summary>
    public class GraphExplorationMemory : MonoBehaviour
    {
        [Header("-----Gizmos (só em Play)-----")]
        // A memória é estado de EPISÓDIO: fora do Play não existe nada para desenhar. Quem
        // desenha a estrutura do mapa (nós, raios, ligações) é o NavGraph, e quem desenha portas e
        // salas (novidade, concluída) é a GraphRoomMemory.
        //
        // Legenda:
        //   traço verde      nó de sala já pisado neste episódio (esquenta para laranja com as
        //                    revisitas)
        //   contorno cinza   nó de sala que ainda falta
        //   disco amarelo    nó âncora atual
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _drawVisitedNodes = true;
        [SerializeField] private bool _drawPendingNodes = true;

        // Quantas revisitas levam a cor ao topo da escala de calor.
        [SerializeField] private int _heatSaturationVisits = 5;

        [Header("-----Revisita precoce (loop)-----")]
        // REVISITA PRECOCE: chegar numa PORTA pisada há menos disto (steps de física; 750 = 15 s).
        // É o detector de loop: voltar e voltar pelo mesmo vão. Quem decide quanto custa é o
        // GraphRewardSystem (só a partir da N-ésima seguida, para não punir voltar de um beco —
        // sair de uma sala de uma porta só passa duas vezes pelo mesmo vão, legitimamente).
        // Era em primário; com os primários virando portas, a régua continua a mesma.
        [SerializeField, Min(1)] private int _earlyRevisitWindowSteps = 750;

        [Header("-----Diagnóstico de pisca-pisca-----")]
        // PISCA-PISCA de âncora: voltar ao nó de onde acabou de sair (A -> B -> A) em menos de
        // _flickerWindowSteps (100 = 2 s) tendo andado menos de _flickerDistance (m) desde que
        // chegou em B. Não é uma decisão do agente: é ele em cima da borda de dois ladrilhos.
        // Separado da revisita precoce (que é loop de verdade) para dizer qual correção aplicar:
        // folga na borda (isto) ou navegação (aquilo). Só métrica.
        [SerializeField, Min(1)] private int _flickerWindowSteps = 100;
        [SerializeField, Min(0f)] private float _flickerDistance = 1f;

        private static readonly Color VisitedColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color RevisitedColor = new Color(1f, 0.5f, 0.05f, 0.9f);
        private static readonly Color PendingColor = new Color(0.45f, 0.45f, 0.5f, 0.35f);

        private NavGraph _graph;

        private bool[] _visited;
        private int[] _visitCount;

        // TickedSteps da última chegada a cada nó. Só tem sentido com _visited[node].
        private int[] _lastVisitStep;

        // DIAGNÓSTICO: loops e pisca-pisca por nó neste episódio (log do manager, _logLoopNodes).
        private int[] _loopCount;

        // Quando e onde o agente chegou ao nó âncora atual. Base do detector de pisca-pisca.
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
        /// Steps de física do episódio em que o agente estava FORA de qualquer área de nó. É a
        /// métrica que diz se o grafo cobre o chão (Exploration/OffNodeFraction no TensorBoard):
        /// fora de nó a observação de vizinhos fica presa na âncora antiga.
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
            // Com histerese: o nó atual segura a âncora enquanto o agente ainda estiver na área dele.
            int node = _graph.FindNodeAt(worldPosition, CurrentNodeIndex);
            IsAtNode = node >= 0;
            ArrivedThisTick = false;
            ArrivalWasNew = false;

            TickedSteps++;
            if (!IsAtNode)
                OffNodeSteps++;

            // Fora de qualquer área, CurrentNodeIndex NÃO volta para -1: ele continua sendo o
            // último nó alcançado. É essa persistência que dá uma âncora no grafo enquanto o
            // agente atravessa um vão sem ladrilho.
            if (node < 0 || node == CurrentNodeIndex)
                return;

            // Pisca-pisca: voltou para o nó de onde acabou de sair, rápido e quase sem andar.
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

        /// <summary>
        /// Zera as flags acumuladas. Chamar DEPOIS de cobrá-las na recompensa, uma vez por
        /// decisão. Os contadores (visitas, loops) não são afetados.
        /// </summary>
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

            // Lido ANTES de reiniciar o relógio do nó.
            bool wasVisited = _visited[node];
            int since = TickedSteps - _lastVisitStep[node];
            _lastVisitStep[node] = TickedSteps;

            ArrivalWasNew = !wasVisited;
            _visited[node] = true;

            if (!_graph.IsDoor(node))
                return;

            // Loop é medido em PORTA: entre salas é onde o vai-e-vem acontece (e custa tempo); dentro
            // de uma sala, voltar por um ladrilho já pisado é só o caminho.
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
        /// Marca o nó como pisado sem o agente ter passado por ele (sala que já nasce concluída,
        /// o anti-decoreba). "Há um tempo": fora da janela de revisita precoce.
        /// </summary>
        public void MarkVisited(int node)
        {
            _visited[node] = true;
            _lastVisitStep[node] = -_earlyRevisitWindowSteps;
        }

        /// <summary>Esquece que o nó foi pisado (sala liberada pela GraphRoomMemory: ela volta a valer).</summary>
        public void Forget(int node)
        {
            _visited[node] = false;
        }

        public bool IsVisited(int node) => _visited[node];

        public int VisitCountOf(int node) => _visitCount[node];

        /// <summary>O que a observação chama de "visitado": 0 nunca pisado (ou esquecido), 1 pisado.</summary>
        public float VisitedObservation(int node) => _visited[node] ? 1f : 0f;

        /// <summary>
        /// DIAGNÓSTICO: os <paramref name="count"/> nós com mais loops/pisca-pisca no episódio, como
        /// "nome (n)". Vazio se não houve nenhum.
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

            // O disco do nó atual, no mesmo formato do gizmo de autoria: dá para ver ao vivo se
            // a área que você calibrou está registrando a chegada onde você achou que ia.
            if (CurrentNodeIndex >= 0)
            {
                Gizmos.color = Color.yellow;
                _graph.DrawNodeArea(CurrentNodeIndex, 0.12f, 1);
            }
        }

        /// <summary>
        /// Pinta a área de cada nó de SALA com o estado dele neste episódio. Portas ficam de fora:
        /// quem as desenha é a GraphRoomMemory, com a novidade (que é o que importa nelas).
        /// </summary>
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

                // Verde -> laranja conforme as revisitas: cor quente é tempo gasto sem retorno.
                float heat = Mathf.Clamp01((_visitCount[i] - 1f) / Mathf.Max(1, _heatSaturationVisits));
                Color visited = Color.Lerp(VisitedColor, RevisitedColor, heat);
                visited.a *= 0.5f;
                Gizmos.color = visited;
                _graph.DrawNodeArea(i, 0.05f, 2);
            }
        }
    }
}
