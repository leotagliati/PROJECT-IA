using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Papel do nó. Os inteiros são os serializados: não reordene.
    /// O NavGraph tira as portas e cada pedaço conexo que sobra é uma sala (docs/graph/salas-e-portas.md).
    /// </summary>
    public enum NodeKind
    {
        /// <summary>
        /// PORTA: o vão entre duas salas, num ladrilho estreito do tamanho do vão. Corta o grafo
        /// em salas e paga ao ser atravessada. Tem que ligar EXATAMENTE duas salas (o bake avisa).
        /// </summary>
        [InspectorName("Porta (vão entre salas)")]
        Door = 0,

        /// <summary>
        /// SALA/CORREDOR: o chão. Não paga sozinho, só conta para a cobertura da sala dele.
        /// É o tipo que o ladrilhamento (NavGraphPlacer) cria em todo o chão.
        /// </summary>
        [InspectorName("Auxiliar (chão de sala)")]
        Auxiliary = 1,

        /// <summary>
        /// PING (legado): ponto que pode "tocar" (GraphPingSystem) e por onde os passos do hider
        /// viram rastro; fora isso é chão de sala. Sem nenhum no grafo, o ping sorteia entre os nós de sala.
        /// </summary>
        [InspectorName("Ping (pode tocar)")]
        Ping = 2,
    }

    /// <summary>
    /// Ponto do mapa (posto à mão ou gerado pelo NavGraphPlacer): posição, papel, vizinhos,
    /// estado ativo e área de chegada. Sem lógica de agente: quem consolida os nós é o
    /// <see cref="NavGraph"/>, e "Coletar nós filhos" é obrigatório depois de mexer nos nós.
    /// A ligação é declarativa e em linha reta: o segmento entre dois vizinhos não pode
    /// atravessar parede (o NavGraph valida).
    /// </summary>
    [DisallowMultipleComponent]
    public class NavNode : MonoBehaviour
    {
        [Header("-----Papel-----")]
        // Default Porta (0): é o que prefabs antigos, sem o campo, sempre desserializaram.
        [SerializeField] private NodeKind _kind = NodeKind.Door;

        [Header("-----Ligações-----")]
        // O bake espelha as ligações (A->B implica B->A): basta declarar cada aresta de um lado.
        [SerializeField] private List<NavNode> _neighbors = new List<NavNode>();

        [Header("-----Estado-----")]
        // Dado do mapa (porta fechada, ala bloqueada), não o `enabled` do componente: desligar o
        // componente apagaria também o gizmo do nó que se quis desativar.
        [SerializeField] private bool _isEnabled = true;

        [Header("-----Área de chegada-----")]
        // Raio de chegada só deste nó (formas Círculo/Quadrado), em metros. 0 = padrão por papel
        // do NavGraph; use só para exceção de geometria (saguão grande, porta apertada).
        [SerializeField] private float _radiusOverride;

        // Retângulo de chegada (forma Retângulo do NavGraph): tamanho total em X e Z, em metros,
        // alinhado aos eixos do MUNDO (rotação ignorada). 0 = sem retângulo (cai no quadrado do raio).
        // O offset leva do nó ao centro do retângulo: o nó fica por onde o corpo passa, o retângulo cobre o chão até a parede.
        [SerializeField] private Vector2 _areaSize;
        [SerializeField] private Vector2 _areaOffset;

        // Posição no array do grafo, atribuída no bake. Não serializado: copy/paste duplicaria o número.
        private int _index = -1;

        public int Index => _index;

        public NodeKind Kind => _kind;

        /// <summary>PORTA: corta o grafo em salas e paga ao ser atravessada.</summary>
        public bool IsDoor => _kind == NodeKind.Door;

        public bool IsPing => _kind == NodeKind.Ping;

        /// <summary>Porta ou ping: usam a área apertada (NavGraph._defaultPrimaryRadius); o auxiliar usa a generosa (_defaultAuxiliaryRadius).</summary>
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

        /// <summary>
        /// Nó desativado deixa de existir para o agente. Trocar NO MEIO do episódio muda o
        /// denominador da cobertura: ligue/desligue no reset.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }

        internal void AssignIndex(int index) => _index = index;

        internal List<NavNode> EditableNeighbors => _neighbors;

        // Só para ferramentas de autoria (NavGraphPlacer), que gravam Undo antes de chamar.
        internal void SetRadiusOverride(float radius) => _radiusOverride = Mathf.Max(0f, radius);

        internal void SetKind(NodeKind kind) => _kind = kind;

        /// <summary>Retângulo de chegada: centro deslocado do nó e tamanho total (X, Z).</summary>
        internal void SetArea(Vector2 offset, Vector2 size)
        {
            _areaOffset = offset;
            _areaSize = new Vector2(Mathf.Max(0f, size.x), Mathf.Max(0f, size.y));
        }

        public bool IsNeighbor(NavNode other) => _neighbors.Contains(other);

        // Cor do ponto por papel, a mesma matiz do disco do NavGraph; ping é rosa como o farol do GraphPingSystem.
        internal static readonly Color DoorColor = new Color(0.25f, 0.75f, 0.95f, 1f);
        internal static readonly Color AuxiliaryColor = new Color(0.55f, 0.60f, 0.72f, 1f);
        internal static readonly Color PingColor = new Color(1f, 0.45f, 0.8f, 1f);
        private static readonly Color DisabledColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        internal static Color ColorOf(NodeKind kind) =>
            kind == NodeKind.Door ? DoorColor : kind == NodeKind.Ping ? PingColor : AuxiliaryColor;

        // Desenha o ponto; fora da lista de qualquer grafo desenha também a própria área, com um
        // traço vertical ("este nó ainda não conta"). Dentro do grafo a área é do NavGraph.
        private void OnDrawGizmos()
        {
            NavGraph graph = GetComponentInParent<NavGraph>(true);
            bool inGraph = graph != null && graph.ContainsNode(this);

            if (!inGraph)
                DrawOwnArea(graph);

            // Auxiliar menor e apagado, para as portas se destacarem na malha densa.
            if (_kind == NodeKind.Auxiliary)
            {
                Color aux = _isEnabled ? AuxiliaryColor : DisabledColor;
                aux.a *= 0.45f;
                Gizmos.color = aux;
                Gizmos.DrawSphere(Position, 0.09f);
                return;
            }

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
        }

        private void OnDrawGizmosSelected()
        {
            // Ligações do nó em destaque (a validação contra parede é do NavGraph).
            Gizmos.color = Color.yellow;
            foreach (NavNode neighbor in _neighbors)
            {
                if (neighbor != null)
                    Gizmos.DrawLine(Position, neighbor.Position);
            }
        }
    }
}
