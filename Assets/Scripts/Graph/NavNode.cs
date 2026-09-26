using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O papel do nó. A separação existe porque um tipo só estava fazendo trabalhos com
    /// requisitos opostos: a MEMÓRIA quer poucos nós, cada um com significado, e a NAVEGAÇÃO
    /// quer muitos, formando uma malha que ensina os caminhos. Com um tipo só, adensar a malha
    /// para o agente não se perder mexia junto no que "explorado" significa.
    ///
    /// A PONTUAÇÃO de cada papel mora no <see cref="NavGraph"/> (um número por tipo); aqui o nó
    /// só diz o que ele é. Os valores inteiros são os serializados: não reordene.
    /// </summary>
    public enum NodeKind
    {
        /// <summary>
        /// EXPLORAÇÃO (ponto de vantagem): parado aqui, a visão do agente cobre a sala/corredor.
        /// É ele que paga cobertura (o peso dele) e pode ser alvo da fronteira.
        /// Poucos por área (1 a 3) e com área apertada, porque nele "visitado" precisa
        /// significar "estive lá e vi daqui". O nome no código continua Primary (é o que os
        /// prefabs e todo o resto do código já usam); no Inspector aparece como Exploração.
        /// </summary>
        [InspectorName("Exploração (paga ao descobrir)")]
        Primary = 0,

        /// <summary>
        /// GUIA: não conta para cobertura e não é alvo de nada. Existe para o agente ter uma
        /// âncora por perto e um caminho a seguir. Paga só o que o NavGraph der ao tipo (0 por
        /// padrão). É o tipo que o ladrilhamento (NavGraphPlacer, menu 9) cria em todo o chão.
        /// </summary>
        [InspectorName("Auxiliar (guia)")]
        Auxiliary = 1,

        /// <summary>
        /// PING: um dos pontos que podem "tocar" (GraphPingSystem) e por onde os passos do hider
        /// viram rastro. Não paga ao ser descoberto nem conta para cobertura: o que ele vale é o
        /// prêmio de ATENDER o ping nele. Sem nenhum nó deste tipo no grafo, o ping continua
        /// sorteando entre os de exploração (o comportamento de antes deste tipo existir).
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
        // Default Primary de propósito: é o que os nós que já existem na cena eram antes deste
        // campo existir, então um nó desserializado sem o campo continua valendo o que valia.
        [SerializeField] private NodeKind _kind = NodeKind.Primary;

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

        [Header("-----Valor-----")]
        // Quanto vale este nó, em unidades relativas. 1 é a referência; 2 é "este ponto vale o
        // dobro". A conversão para recompensa acontece em dois lugares só: a pontuação do TIPO
        // no NavGraph e o fator do GraphRewardSystem — aqui você só declara a importância
        // relativa DENTRO do tipo.
        //
        // O peso mora no nó, e não numa região que o agrupa: cada ponto de vantagem é uma
        // decisão de autoria ("daqui se vê a sala"), então o valor dele é decidido no mesmo
        // lugar em que ele é colocado. O preço disso é que a DENSIDADE da malha vira função de
        // recompensa: dois nós de exploração de peso 1 na mesma sala pagam o dobro de um. Ao
        // adensar uma sala, divida o peso entre os nós dela para o total da sala não mudar.
        //
        // Conta em nó de EXPLORAÇÃO (descoberta) e de PING (atender o ping). Num auxiliar é
        // ignorado — o auxiliar vale a pontuação do tipo, igual para todos.
        [SerializeField] private float _explorationWeight = 1f;

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
        /// Nó de EXPLORAÇÃO. Atalho do teste que aparece em todo lugar: cobertura, alvo da
        /// fronteira e recompensa de aresta são privilégio dele.
        /// </summary>
        public bool IsPrimary => _kind == NodeKind.Primary;

        public bool IsPing => _kind == NodeKind.Ping;

        /// <summary>
        /// Exploração ou ping: os nós que alguém manda o agente ALCANÇAR, e onde a chegada tem
        /// que significar "estive lá". Os dois usam a área apertada (raio padrão de primário) e
        /// o NavGraphPlacer não apaga nenhum deles; o auxiliar usa a área generosa.
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

        /// <summary>Peso declarado na autoria. Nunca negativo; 0 é "não paga".</summary>
        public float ExplorationWeight => Mathf.Max(0f, _explorationWeight);

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
        internal static readonly Color PrimaryColor = new Color(0.25f, 0.75f, 0.95f, 1f);
        internal static readonly Color AuxiliaryColor = new Color(0.55f, 0.60f, 0.72f, 1f);
        internal static readonly Color PingColor = new Color(1f, 0.45f, 0.8f, 1f);
        private static readonly Color DisabledColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        internal static Color ColorOf(NodeKind kind) =>
            kind == NodeKind.Primary ? PrimaryColor : kind == NodeKind.Ping ? PingColor : AuxiliaryColor;

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

            // O tamanho do ponto cresce com o peso (raiz, para peso 4 não virar uma bola de
            // 4x): dá para ver de cima onde estão os nós que valem mais sem abrir o Inspector.
            Gizmos.color = _isEnabled ? ColorOf(_kind) : DisabledColor;
            Gizmos.DrawSphere(Position, 0.18f * Mathf.Sqrt(Mathf.Max(0.25f, ExplorationWeight)));
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
