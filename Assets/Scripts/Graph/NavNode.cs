using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O papel do nó. Os valores inteiros são os serializados: não reordene.
    ///
    /// Desde o mapa v4 (docs/graph/salas-e-portas.md) o grafo é lido como SALAS ligadas por
    /// PORTAS: o NavGraph tira as portas e cada pedaço conexo que sobra é uma sala (ou corredor).
    /// A exploração paga por sala coberta e por porta atravessada — nenhum nó carrega peso próprio.
    /// </summary>
    public enum NodeKind
    {
        /// <summary>
        /// PORTA: o vão entre duas salas (o jogo não tem portas, só buracos na parede). É o
        /// que corta o grafo em salas, e atravessá-la (de uma sala para a outra) paga, com
        /// novidade que cai a cada repetição. Um ladrilho estreito, do tamanho do vão: tem que
        /// ligar EXATAMENTE duas salas — o bake avisa quando não liga.
        ///
        /// Era o PRIMÁRIO/Exploração (ponto de vantagem que pagava o próprio peso). Valor 0
        /// mantido: os prefabs continuam carregando, e o que era primário vira porta — no
        /// NodeTraining5 os primários já estavam nos vãos.
        /// </summary>
        [InspectorName("Porta (vão entre salas)")]
        Door = 0,

        /// <summary>
        /// SALA/CORREDOR: o chão. Não paga sozinho; conta para a cobertura da sala a que pertence
        /// (80% dos nós da sala pisados = sala concluída). É o tipo que o ladrilhamento
        /// (NavGraphPlacer, menu 9) cria em todo o chão.
        /// </summary>
        [InspectorName("Auxiliar (chão de sala)")]
        Auxiliary = 1,

        /// <summary>
        /// PING: um dos pontos que podem "tocar" (GraphPingSystem) e por onde os passos do hider
        /// viram rastro. Fora isso é chão de sala como o auxiliar (conta para a cobertura dela).
        /// Sem nenhum nó deste tipo no grafo, o ping sorteia entre os nós de sala. LEGADO no
        /// plano de salas: a fase de ping vai sortear sala -> nó por episódio.
        /// </summary>
        [InspectorName("Ping (pode tocar)")]
        Ping = 2,
    }

    /// <summary>
    /// Um ponto de interesse do mapa, posicionado À MÃO na cena (doorway, canto de sala,
    /// bifurcação de corredor) ou gerado pelo NavGraphPlacer. Guarda só o que é do nó: onde ele
    /// está, com quem ele conversa, se está ativo, quanto vale e qual área ele cobre. Nenhuma
    /// lógica de agente, recompensa ou busca mora aqui — quem consolida isso num grafo
    /// utilizável é o <see cref="NavGraph"/>.
    ///
    /// A ligação é declarativa e em linha reta: você arrasta os vizinhos no Inspector e a
    /// promessa (que você garante ao posicionar, e o gizmo do NavGraph confere) é que o
    /// segmento entre os dois não atravessa parede.
    /// </summary>
    [DisallowMultipleComponent]
    public class NavNode : MonoBehaviour
    {
        [Header("-----Papel-----")]
        // Default 0 (Porta) de propósito: é o valor que um nó desserializado sem o campo sempre
        // teve, então os prefabs antigos continuam lendo o mesmo número.
        [SerializeField] private NodeKind _kind = NodeKind.Door;

        [Header("-----Ligações-----")]
        // Preenchido na mão. O NavGraph espelha as ligações no bake (A->B implica B->A), então
        // basta declarar cada aresta de um lado só.
        [SerializeField] private List<NavNode> _neighbors = new List<NavNode>();

        [Header("-----Estado-----")]
        // Nome diferente de MonoBehaviour.enabled de propósito: desligar o COMPONENTE não deve
        // ser a forma de tirar um nó do grafo, senão o gizmo some junto e você perde a
        // visualização justamente do que quis desativar. Isto aqui é dado do mapa (porta
        // fechada, ala bloqueada numa lição do currículo), não estado do componente.
        [SerializeField] private bool _isEnabled = true;

        [Header("-----Legado (não pontua mais)-----")]
        // LEGADO: era o peso de descoberta do nó (gravado pelo menu "10. Pesos por área", peso ∝
        // área^0.5). Saiu com as salas: toda sala vale o mesmo, independente do tamanho, e nada
        // no runtime lê este campo. Fica serializado só para os prefabs antigos não perderem o
        // dado; as ferramentas de geração ainda o preenchem.
        [SerializeField] private float _explorationWeight = 1f;

        // LEGADO: o ID de sala autorado à mão (menu "11. Numerar salas" antigo), usado pelo tédio
        // de sala que saiu. A sala agora é CALCULADA no bake do NavGraph (cortando nas portas),
        // então este número não é lido por nada do treino. Só o rótulo do gizmo de nó solto o usa.
        [SerializeField, Min(0)] private int _areaId;

        [Header("-----Área de chegada-----")]
        // Raio de chegada SÓ DESTE NÓ, nas formas Círculo/Quadrado do NavGraph. Deixe em 0 (o
        // normal): o NavGraph tem um padrão por PAPEL, e é lá que se calibra o mapa inteiro.
        //
        // Este campo é para a EXCEÇÃO que a geometria exige — um saguão enorme onde o nó deve
        // cobrir mais chão, ou um doorway apertado onde o raio invadiria a sala vizinha e daria
        // visita de graça sem o agente ter cruzado a porta. Se você se pegar preenchendo isto em
        // muitos nós do mesmo papel, o valor errado é o padrão do grafo, não o de cada nó.
        [SerializeField] private float _radiusOverride;

        // RETÂNGULO de chegada, para a forma Retângulo do NavGraph (a que o ladrilhamento usa).
        // Tamanho TOTAL em X e Z, em metros, alinhado aos eixos do MUNDO (a rotação do nó e da
        // arena é ignorada — as 9 cópias da arena não são giradas). 0 = sem retângulo: o nó cai
        // num quadrado de meia-aresta igual ao raio.
        //
        // O retângulo não precisa estar centrado no nó: o nó fica num ponto por onde o CORPO
        // passa (é dele que saem as ligações e a direção da observação), e o retângulo cobre o
        // CHÃO — que pode ir até a parede, onde o centro do corpo nunca chega. Por isso o
        // deslocamento abaixo, do nó até o centro do retângulo.
        [SerializeField] private Vector2 _areaSize;
        [SerializeField] private Vector2 _areaOffset;

        // Atribuído pelo NavGraph no bake, não serializado: o índice é a posição no array de
        // adjacência daquele grafo, e serializar convidaria a dois nós carregarem o mesmo
        // número depois de um copy/paste.
        private int _index = -1;

        public int Index => _index;

        public NodeKind Kind => _kind;

        /// <summary>
        /// PORTA: corta o grafo em salas e paga ao ser atravessada. Atalho do teste que aparece
        /// em todo lugar (segmentação, travessia, observação das portas).
        /// </summary>
        public bool IsDoor => _kind == NodeKind.Door;

        public bool IsPing => _kind == NodeKind.Ping;

        /// <summary>
        /// Porta ou ping: os nós em que a chegada tem que significar "estive lá". Os dois usam a
        /// área apertada (raio padrão de primário) e o NavGraphPlacer não apaga nenhum deles; o
        /// auxiliar usa a área generosa.
        /// </summary>
        public bool IsTarget => _kind != NodeKind.Auxiliary;

        public Vector3 Position => transform.position;

        public IReadOnlyList<NavNode> Neighbors => _neighbors;

        /// <summary>Raio próprio, ou 0 quando o nó usa o padrão do grafo.</summary>
        public float RadiusOverride => _radiusOverride;

        /// <summary>O nó tem retângulo próprio (forma Retângulo do NavGraph).</summary>
        public bool HasArea => _areaSize.x > 0f && _areaSize.y > 0f;

        /// <summary>Tamanho total do retângulo em X e Z (0 = sem retângulo).</summary>
        public Vector2 AreaSize => _areaSize;

        /// <summary>Centro do retângulo no mundo (na altura do nó).</summary>
        public Vector3 AreaCenter => Position + new Vector3(_areaOffset.x, 0f, _areaOffset.y);

        /// <summary>LEGADO: peso autorado (ver _explorationWeight). Nada do treino lê.</summary>
        public float ExplorationWeight => Mathf.Max(0f, _explorationWeight);

        /// <summary>LEGADO: ID de sala autorado (ver _areaId). A sala real é NavGraph.RoomOf.</summary>
        public int AreaId => Mathf.Max(0, _areaId);

        /// <summary>
        /// Nó ativo participa de tudo: observação, busca de fronteira e denominador da cobertura.
        /// Desativado, ele deixa de existir para o agente. Trocar isto NO MEIO de um episódio
        /// muda o denominador da cobertura no meio do caminho — ligue/desligue no reset.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }

        internal void AssignIndex(int index) => _index = index;

        internal void ClearIndex() => _index = -1;

        internal List<NavNode> EditableNeighbors => _neighbors;

        // Só para ferramentas de autoria (NavGraphPlacer), que gravam Undo antes de chamar.
        internal void SetRadiusOverride(float radius) => _radiusOverride = Mathf.Max(0f, radius);

        internal void SetKind(NodeKind kind) => _kind = kind;

        internal void SetExplorationWeight(float weight) => _explorationWeight = Mathf.Max(0f, weight);

        internal void SetAreaId(int areaId) => _areaId = Mathf.Max(0, areaId);

        /// <summary>Retângulo de chegada: centro deslocado do nó e tamanho total (X, Z).</summary>
        internal void SetArea(Vector2 offset, Vector2 size)
        {
            _areaOffset = offset;
            _areaSize = new Vector2(Mathf.Max(0f, size.x), Mathf.Max(0f, size.y));
        }

        public bool IsNeighbor(NavNode other) => _neighbors.Contains(other);

        // Cor do PONTO, por papel. Mesma matiz que o disco padrão do NavGraph desenha para cada
        // papel, então ponto e disco contam a mesma história — e um nó sem disco (esquecido
        // fora do grafo) ainda diz o que ele é. O ping é rosa: a mesma cor do farol do
        // GraphPingSystem quando ele toca, então "rosa" continua significando "ping".
        internal static readonly Color DoorColor = new Color(0.25f, 0.75f, 0.95f, 1f);
        internal static readonly Color AuxiliaryColor = new Color(0.55f, 0.60f, 0.72f, 1f);
        internal static readonly Color PingColor = new Color(1f, 0.45f, 0.8f, 1f);
        private static readonly Color DisabledColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        internal static Color ColorOf(NodeKind kind) =>
            kind == NodeKind.Door ? DoorColor : kind == NodeKind.Ping ? PingColor : AuxiliaryColor;

        // O pontinho, e — quando o nó NÃO está na lista de nenhum grafo — também a área dele.
        // Dentro do grafo quem desenha a área é o NavGraph, que conhece o valor efetivo (padrão
        // do grafo, override ou retângulo) e corta pela parede.
        //
        // Fora do grafo o nó desenha a própria área (tracejada por um traço vertical que sobe do
        // ponto): assim um nó recém-criado, arrastado para fora do Graph ou ainda não coletado
        // aparece inteiro na cena, e o traço diz "este nó ainda não conta". Com o "Coletar nós
        // automaticamente" do NavGraph ligado, isso só dura até a próxima mudança na hierarquia.
        private void OnDrawGizmos()
        {
            NavGraph graph = GetComponentInParent<NavGraph>(true);
            bool inGraph = graph != null && graph.ContainsNode(this);

            if (!inGraph)
                DrawOwnArea(graph);

            // Auxiliar desenha menor e apagado: numa malha densa, ponto cheio em cima de ponto
            // cheio deixa de dar para ver quais são os poucos nós que realmente valem alguma
            // coisa — que é a informação que você procura quando olha o mapa de cima.
            if (_kind == NodeKind.Auxiliary)
            {
                Color aux = _isEnabled ? AuxiliaryColor : DisabledColor;
                aux.a *= 0.45f;
                Gizmos.color = aux;
                Gizmos.DrawSphere(Position, 0.09f);
                return;
            }

            // Tamanho fixo: o peso por nó saiu (toda sala vale igual), então o ponto não tem mais
            // o que dizer pelo tamanho — só pela cor (azul = porta, rosa = ping).
            Gizmos.color = _isEnabled ? ColorOf(_kind) : DisabledColor;
            Gizmos.DrawSphere(Position, 0.18f);
        }

        private void DrawOwnArea(NavGraph graph)
        {
            Color color = _isEnabled ? ColorOf(_kind) : DisabledColor;
            color.a = 0.6f;
            Gizmos.color = color;

            if (HasArea)
            {
                GraphGizmos.DrawGroundRect(AreaCenter, _areaSize * 0.5f, 0.05f);
            }
            else
            {
                float radius = _radiusOverride > 0f ? _radiusOverride
                    : graph != null ? graph.RadiusOf(this)
                    : 1.5f;
                NodeShape shape = graph != null && graph.Shape == NodeShape.Square ? NodeShape.Square : NodeShape.Circle;
                GraphGizmos.DrawGroundArea(shape, Position, radius);
            }

            Gizmos.DrawLine(Position, Position + Vector3.up * 1.5f);

#if UNITY_EDITOR
            // O número da sala em texto, e não em cor: o vocabulário de cores dos gizmos já está
            // quase todo tomado, e um número se confere de relance contra a planta.
            if (AreaId > 0)
                UnityEditor.Handles.Label(Position + Vector3.up * 1.7f, $"S{AreaId}");
#endif
        }

        private void OnDrawGizmosSelected()
        {
            // As ligações deste nó em destaque. A validação contra parede é do NavGraph, que é
            // quem conhece a layer — aqui é só "com quem eu falo".
            Gizmos.color = Color.yellow;
            foreach (NavNode neighbor in _neighbors)
            {
                if (neighbor != null)
                    Gizmos.DrawLine(Position, neighbor.Position);
            }
        }
    }
}
