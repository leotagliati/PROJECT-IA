using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Forma da área de chegada de um nó. Vale para o grafo inteiro: é a regra que transforma
    /// posição contínua em índice discreto, e ter duas formas convivendo no mesmo mapa tornaria
    /// impossível calibrar espaçamento olhando o gizmo.
    /// </summary>
    public enum NodeShape
    {
        /// <summary>
        /// Disco. Isotrópico: a chegada acontece à mesma distância venha o agente de onde vier.
        /// É o que faz sentido para PRIMÁRIO, onde a área certifica presença num ponto.
        /// </summary>
        Circle,

        /// <summary>
        /// Quadrado alinhado aos eixos, de lado 2 x raio. LADRILHA: uma cadeia de quadrados
        /// cobre um corredor inteiro sem as folgas em forma de lente que sobram entre discos
        /// tangentes. É o que faz sentido para uma malha de guia, onde o objetivo é não deixar
        /// buraco em que o agente fique sem âncora.
        /// </summary>
        Square,

        /// <summary>
        /// Retângulo POR NÓ (NavNode._areaSize/_areaOffset), alinhado aos eixos do mundo. É a
        /// forma do ladrilhamento (NavGraphPlacer, menu 9): os retângulos cobrem o chão sem se
        /// sobrepor, então todo ponto em que o agente pode estar cai em exatamente um nó — sem
        /// desempate, sem buraco. Nó sem retângulo cai num quadrado de meia-aresta = raio.
        /// Os menus de raio do placer (1 a 7) não servem para esta forma.
        /// </summary>
        Rectangle,
    }

    /// <summary>
    /// Consolida os <see cref="NavNode"/> de UMA arena num grafo consultável: índices,
    /// adjacência, SALAS, áreas e caminho mais curto. É estrutura do mapa, não estado de agente — o
    /// que o agente já visitou mora na <see cref="GraphExplorationMemory"/> e na
    /// <see cref="GraphRoomMemory"/>, uma de cada por agente.
    ///
    /// SALAS (docs/graph/salas-e-portas.md): no bake, as PORTAS (NodeKind.Door) são tiradas e cada
    /// pedaço conexo que sobra vira uma sala (ou corredor). Ninguém numera sala à mão: mexeu nos
    /// nós ou nas ligações, o próximo Play recalcula.
    ///
    /// Existe um NavGraph por cópia da arena; os índices são locais a ele, então nove arenas na
    /// cena não compartilham nada (mesmo motivo pelo qual a grade do seeker é relativa à arena).
    /// </summary>
    public class NavGraph : MonoBehaviour
    {
        [Header("-----Nós-----")]
        // Preencha com "Coletar nós filhos" no menu de contexto do componente.
        [SerializeField] private List<NavNode> _nodes = new List<NavNode>();

        // Espelha as ligações no bake. Ligar A->B na mão e esquecer B->A produz um grafo
        // dirigido silencioso: o agente entra numa ala e a busca de fronteira não acha caminho
        // de volta. Com isto ligado, a autoria fica à prova disso.
        [SerializeField] private bool _makeLinksBidirectional = true;

        // Só no editor: a cada mudança na hierarquia, (1) todo NavNode da MESMA ARENA que não está
        // debaixo de nenhum NavGraph vira filho deste grafo e (2) a lista acima é recoletada se os
        // filhos mudaram. Tira da autoria o "esqueci de rodar Coletar nós filhos" (nó sem círculo
        // e sem ligação no gizmo). Não grava Undo de propósito: desfazer a criação de um nó
        // dispararia outra coleta, e a coleta com Undo apagaria o Redo.
        // Desligue se quiser um nó fora do grafo de propósito (ele desenha a própria área).
#pragma warning disable CS0414
        [SerializeField] private bool _autoCollectNodes = true;
#pragma warning restore CS0414

        // Forma da área de chegada, para o mapa inteiro. No quadrado os dois raios abaixo deixam
        // de ser raio e passam a ser MEIA-ARESTA: trocar a forma sem mexer nos números aumenta a
        // área em ~27% e estica o alcance da diagonal em 41%. Reveja o espaçamento ao trocar.
        // Retângulo: cada nó traz o seu (NavNode._areaSize); os raios abaixo só valem para nó
        // sem retângulo.
        [SerializeField] private NodeShape _nodeShape = NodeShape.Circle;

        [Header("-----Ping-----")]
        // Multiplica o prêmio de ATENDER o ping (chegar no nó enquanto ele toca). 1 = 2.0 por
        // ping (GraphRewardSystem._pingReachedReward). A pontuação por TIPO de nó e o peso por
        // nó (descoberta = pontuação x peso, com o peso gravado pela área do ladrilho) saíram com
        // as salas: quem paga exploração agora é a GraphRoomMemory, e toda sala vale o mesmo.
        [SerializeField, Min(0f)] private float _pingNodeScore = 1f;

        [Header("-----Raio padrão (Círculo/Quadrado)-----")]
        // UM RAIO PADRÃO POR PAPEL, porque os dois têm fórmulas de calibração opostas. Deixar os
        // dois no mesmo campo obrigaria a corrigir um deles nó a nó, no override — e replicar um
        // valor por nó é a forma mais rápida de dois nós discordarem sobre ele.
        //
        // PRIMÁRIO (porta e ping) — certifica presença, então quer ficar APERTADO:
        //   - grande demais: o raio do vizinho encosta no dele, a chegada acontece no meio do
        //     caminho e "visitado" deixa de significar "estive lá";
        //   - pequeno demais: ele passa reto e a visita nunca é registrada.
        // Ponto de partida: metade da MENOR aresta entre dois primários, nunca menor que o raio
        // do corpo do agente. O aviso do bake mede exatamente isso.
        [FormerlySerializedAs("_defaultNodeRadius")]
        [SerializeField] private float _defaultPrimaryRadius = 1.8f;

        // AUXILIAR — o papel dele é PEGAR o agente, então quer ficar GENEROSO. Discos se
        // sobrepondo ao longo da cadeia é o desenho pretendido: é o que garante que o agente
        // nunca fique sem âncora no meio de um corredor. Por isso o aviso de sobreposição do
        // bake ignora arestas que envolvem auxiliar — aqui não há nada a proteger.
        // Ponto de partida: um pouco acima do espaçamento da cadeia dividido por dois.
        [SerializeField] private float _defaultAuxiliaryRadius = 3f;

        [Header("-----Checagem de parede-----")]
        // Tudo que o corpo não atravessa: paredes E mobília (Map_Objects também fica na layer Wall).
        [SerializeField] private LayerMask _wallLayer;

        // Altura em que a ARESTA é desenhada no gizmo. Não entra mais na checagem: quem decide
        // se o corpo passa é a coluna _bodyBottom.._bodyTop abaixo.
        [SerializeField] private float _linkProbeHeight = 0.5f;

        // Raio da sonda: da ordem do raio do agente. Com 0 a checagem é uma linha, e uma aresta
        // que passa raspando na quina de uma parede é aprovada — o agente então tenta segui-la
        // e entala. Com folga, a aresta só é válida se o CORPO dele passa.
        //
        // A regra é METADE DA LARGURA DO AGENTE. Com o corpo em 1.7 de largura, 0.85. O valor
        // antigo (0.3) aprovava passagens por onde ele não cabe, e o sintoma era ele tentar
        // seguir a aresta e travar na quina — indistinguível, de fora, de "a política é ruim".
        [SerializeField] private float _linkClearance = 0.85f;

        // COLUNA DO CORPO, em metros relativos à ALTURA DO NÓ (não do chão). A sonda é uma
        // cápsula que vai de _bodyBottom a _bodyTop; antes era uma esfera só, a +0.5 do nó.
        //
        // Por que mudou: os nós do NodeTraining ficam a ~1.84 m do chão, então a esfera antiga
        // varria de 1.5 a 3.2 m de altura — e mesa, sofá, bancada e baia (0.8 a 1.5 m) passavam
        // por BAIXO dela. Com os Map_Objects ligados, arestas atravessando mesa eram aprovadas.
        //
        // A conta, com o corpo do NodeSeekerAgent (caixa 1.7 x 3.63 x 1.7) apoiado no chão:
        // ele vai de nó - 1.84 a nó + 1.79. Base em -1.6 ignora os 24 cm de baixo (rodapé,
        // soleira, objeto largado no chão, que o corpo empurra ou sobe); topo em +1.8 é a cabeça.
        [SerializeField] private float _bodyBottom = -1.6f;
        [SerializeField] private float _bodyTop = 1.8f;

        // Folga que o corpo precisa PARADO em cima de um nó para ele servir de SPAWN (do seeker
        // e do hider). Maior que a de passagem (_linkClearance) porque o spawn sorteia a
        // rotação: a caixa de 1.7 x 1.7 girada a 45 graus ocupa meia-diagonal 1.2; +5 cm = 1.25.
        //
        // Nó mais apertado que isto (corredor estreito, vão de porta) continua valendo como
        // âncora e caminho — ele existe justamente para o agente não ficar sem nó ali —, só não
        // é sorteado como ponto de nascimento. É a mesma régua que o NavGraphPlacer usa.
        [SerializeField] private float _spawnClearance = 1.25f;

        // MEDIR O CORPO em vez de digitar: ligado, a folga de passagem e a de spawn saem do
        // CapsuleCollider do agente desta arena (raio x maior escala em X/Z) + as margens abaixo,
        // e os dois campos manuais acima são ignorados. É o que mantém o grafo honesto quando a
        // escala do agente muda: o NodeSeekerAgent estava em escala 1.7 (raio real 1.17 m) com
        // _linkClearance 0.85 — o grafo aprovava vãos por onde o corpo não cabia, e o agente
        // entalava seguindo uma aresta "válida". A coluna vertical (_bodyBottom/_bodyTop) continua
        // manual: ela depende de onde os nós ficam em relação ao chão, e errar ali bloqueia tudo.
        [SerializeField] private bool _useAgentBodySize = true;

        // Somada ao raio medido. 5 cm: a sonda não pode ser exatamente o corpo, senão uma aresta
        // que raspa a quina ainda passa e o agente entala nela.
        [SerializeField, Min(0f)] private float _linkClearanceMargin = 0.05f;

        // Somada ao raio medido para o SPAWN: nascer encostado na parede já começa o episódio
        // pagando contato. 0.4 é da ordem da folga antiga (1.25 - 0.85).
        [SerializeField, Min(0f)] private float _spawnClearanceMargin = 0.4f;

        // A ÁREA DE CHEGADA PARA NA PAREDE. Ligado: um ponto só está na área de um nó se, além de
        // estar dentro do raio, houver LINHA LIVRE (na altura do nó, contra a layer de parede) do
        // centro do nó até ele. Sem isto a área era um quadrado cego: atravessava parede e o
        // agente "chegava" num nó da sala vizinha sem ter entrado nela — visita de graça e
        // observação mentindo. O NavGraphPlacer mede a cobertura com a mesma regra, e o gizmo
        // desenha a área já cortada pelas paredes.
        //
        // Custo: um raycast por nó candidato (os que contêm o ponto no raio, do mais perto para o
        // mais longe, até o primeiro com linha livre) — 1 a 3 por step de física por agente.
        // Muda a regra de chegada: os .onnx treinados sem isto não servem com isto ligado.
        [SerializeField] private bool _areasStopAtWalls = true;

        // FOLGA DA ÂNCORA (m, só na forma Retângulo): o nó atual continua sendo a âncora até o agente
        // sair MAIS que isto da área dele — e o vizinho só assume depois que ele entrou nele esta
        // mesma profundidade (ou até a METADE do vizinho, o que for menor). Os ladrilhos se tocam
        // sem sobreposição, então sem folga a âncora trocava a 1 cm da linha: andando em cima da
        // borda ela piscava A-B-A, e a lista inteira de saídas que o agente vê mudava junto
        // (node4_e2_05: ~7 pisca-piscas por episódio, Exploration/AnchorFlicker, subindo).
        //
        // O limite pela metade é pelos nós de PORTA: são estreitos (a espessura da parede), e com
        // a folga cheia a âncora da sala atravessaria o nó da porta sem nunca trocar para ele.
        // 0 = sem folga (o comportamento antigo). Não muda observação nem vetor.
        [SerializeField, Min(0f)] private float _anchorHysteresis = 0.5f;

        [Header("-----Auto-ligação-----")]
        // Usado só pelo menu de contexto "Auto-ligar por linha de visão".
        [SerializeField] private float _autoLinkMaxDistance = 8f;

        [Header("-----Gizmos-----")]
        [SerializeField] private bool _drawGizmos = true;

        // Discos de chegada de todos os nós. É o gizmo para calibrar os raios padrão:
        // dois discos de PRIMÁRIO encostando significa que a chegada acontece no meio do caminho.
        [SerializeField] private bool _drawNodeRadii = true;

        // Rótulo "S#" no centro de cada sala calculada (o número que o Console e a memória usam).
        // Só lido no editor (Handles), daí o pragma no build.
#pragma warning disable CS0414
        [SerializeField] private bool _drawRoomLabels = true;
#pragma warning restore CS0414

        // Cor do disco por PAPEL. São dois canais visuais diferentes de propósito:
        //   o PONTO do nó continua na cor da REGIÃO (quem desenha é o NavNode),
        //   o DISCO diz o papel — e o disco é o que se lê olhando o mapa de cima, porque é
        //   dezenas de vezes maior que o ponto.
        // Assim as duas informações cabem na mesma figura sem disputarem o mesmo canal.
        //
        // Evite verde, laranja, amarelo, magenta e branco: essas cinco já significam outra coisa
        // nos gizmos de Play (visitado, revisitado, âncora, fronteira, aresta percorrida). Uma
        // cor com dois significados é pior que nenhuma cor, porque você calibra olhando pra ela.
        // Azul-claro = PORTA (o nome do campo é o antigo, para não perder o valor salvo).
        [SerializeField] private Color _primaryRadiusColor = new Color(0.25f, 0.75f, 0.95f, 0.55f);

        // Bem apagado: a malha é cenário. Se ela competir visualmente com os primários, você
        // perde de vista justamente os poucos nós que decidem a recompensa.
        [SerializeField] private Color _auxiliaryRadiusColor = new Color(0.55f, 0.60f, 0.72f, 0.22f);

        // Rosa, a cor do farol do GraphPingSystem: "rosa" já significa ping no vocabulário.
        [SerializeField] private Color _pingRadiusColor = new Color(1f, 0.45f, 0.8f, 0.5f);

        // Colore as arestas conforme atravessam parede ou não. É um spherecast por aresta por
        // frame de editor: desligue se a cena ficar pesada.
        [SerializeField] private bool _validateLinksInGizmos = true;

        private int[][] _adjacency;

        // Comprimento PLANAR de cada aresta, no mesmo arranjo de _adjacency. É o peso do caminho
        // mais curto: distância em METROS pelo grafo, não em número de arestas.
        private float[][] _adjacencyLength;
        private bool _isBaked;

        // Rascunho do caminho mais curto (Dijkstra), alocado uma vez. O carimbo evita limpar os
        // arrays a cada busca — ela roda a cada troca de nó, por agente.
        //
        // POR QUE METROS E NÃO ARESTAS: contar arestas faz toda distância depender da DENSIDADE
        // do grafo. O placer agora põe nós até cobrir o chão inteiro, e um corredor que tinha 2
        // arestas passa a ter 6 — a mesma caminhada viraria "3x mais longe" para a observação e
        // para a recompensa por aproximação (que pagava por aresta). Em metros, regenerar o grafo
        // não muda nada do que o agente sente.
        private int[] _pathOrder;      // nós fechados, em ordem crescente de distância
        private int[] _pathParent;
        private float[] _pathCost;
        private int[] _pathStampOf;    // carimbo: o nó já foi alcançado nesta busca
        private bool[] _pathClosed;
        private int[] _heapNode;
        private float[] _heapCost;
        private int _heapCount;
        private int _pathStamp;
        private int _pathCount;

        private float _pathDiameter = -1f;
        private bool[] _spawnable;

        // Rascunho do OverlapCapsule. Por instância, não estático: cada arena tem o seu grafo.
        private readonly Collider[] _overlapBuffer = new Collider[1];

        public LayerMask WallLayer => _wallLayer;

        public NodeShape Shape => _nodeShape;

        /// <summary>Metade da largura do agente: o raio com que o corpo passa por uma aresta.</summary>
        public float LinkClearance => TryMeasureAgentRadius(out float radius) ? radius + _linkClearanceMargin : _linkClearance;

        /// <summary>Base da coluna do corpo, relativa à altura do nó.</summary>
        public float BodyBottom => _bodyBottom;

        /// <summary>Topo da coluna do corpo, relativo à altura do nó.</summary>
        public float BodyTop => _bodyTop;

        /// <summary>Folga que o corpo precisa parado num nó para nascer ali (ver _spawnClearance).</summary>
        public float SpawnClearance => TryMeasureAgentRadius(out float radius) ? radius + _spawnClearanceMargin : _spawnClearance;

        private CapsuleCollider _agentBody;
        private bool _loggedMeasuredBody;

        /// <summary>
        /// Raio real do corpo do agente desta arena: raio do CapsuleCollider x maior escala
        /// planar. Procura o GraphExplorerManager SÓ dentro da arena (nunca na cena): cada cópia
        /// mede o próprio agente, e uma arena sem agente cai nos valores manuais.
        /// </summary>
        public bool TryMeasureAgentRadius(out float radius)
        {
            radius = 0f;
            if (!_useAgentBodySize)
                return false;

            if (_agentBody == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                Transform root = arena != null ? arena.transform : transform.parent;
                GraphExplorerManager agent = root != null ? root.GetComponentInChildren<GraphExplorerManager>(true) : null;
                if (agent != null)
                    _agentBody = agent.GetComponent<CapsuleCollider>();

                if (_agentBody == null)
                    return false;
            }

            Vector3 scale = _agentBody.transform.lossyScale;
            float planar = _agentBody.direction == 1
                ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            radius = _agentBody.radius * planar;

            if (Application.isPlaying && !_loggedMeasuredBody)
            {
                _loggedMeasuredBody = true;
                Debug.Log(
                    $"{name}: corpo do agente medido — raio {radius:0.00} m, passagem {radius + _linkClearanceMargin:0.00}, " +
                    $"spawn {radius + _spawnClearanceMargin:0.00}.", this);
            }

            return radius > 0f;
        }

        /// <summary>A área de chegada é cortada pelas paredes (ver _areasStopAtWalls).</summary>
        public bool AreasStopAtWalls => _areasStopAtWalls;

        /// <summary>
        /// Linha livre, NA ALTURA DO NÓ, do centro do nó até o ponto (projetado nessa altura)?
        /// É o teste que corta a área de chegada na parede. Na altura do nó (~1.84 m) e não na do
        /// chão de propósito: mesa e sofá não cortam a área (o agente passa em volta e "vê" o nó
        /// por cima deles); parede, divisória alta e armário cortam.
        /// </summary>
        public bool CanSeeFromNode(Vector3 nodePosition, Vector3 point)
        {
            var target = new Vector3(point.x, nodePosition.y, point.z);
            Vector3 delta = target - nodePosition;
            float distance = delta.magnitude;
            if (distance < 1e-4f)
                return true;

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            return !physics.Raycast(nodePosition, delta / distance, distance, _wallLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Distância PLANAR na métrica da forma escolhida — euclidiana no círculo, Chebyshev
        /// (o maior dos dois eixos) no quadrado. Uma função só, usada pela detecção de chegada,
        /// pelo desempate do nó mais próximo e pela validação de áreas sobrepostas: assim trocar
        /// a forma no Inspector muda os três de uma vez, e nenhum deles pode discordar do gizmo.
        /// </summary>
        internal float AreaDistance(Vector3 a, Vector3 b)
        {
            float dx = Mathf.Abs(a.x - b.x);
            float dz = Mathf.Abs(a.z - b.z);

            return _nodeShape == NodeShape.Square
                ? Mathf.Max(dx, dz)
                : new Vector2(dx, dz).magnitude;
        }

        public float DefaultPrimaryRadius => _defaultPrimaryRadius;

        public float DefaultAuxiliaryRadius => _defaultAuxiliaryRadius;

        /// <summary>
        /// Raio efetivo de um nó: o override dele, ou o padrão DO PAPEL dele. O override fica
        /// para a exceção que a geometria exige — um saguão onde o nó deve cobrir mais chão, um
        /// doorway apertado onde o raio invadiria a sala vizinha — e não para corrigir em massa
        /// o valor de um papel inteiro; isso se faz aqui, num lugar só.
        /// </summary>
        public float NodeRadius(int index) => RadiusOf(_nodes[index]);

        // Exploração e ping ficam com o raio APERTADO: nos dois, chegar tem que significar "estive
        // lá". Só o auxiliar, que existe para pegar o agente, usa o generoso.
        internal float RadiusOf(NavNode node)
        {
            float over = node.RadiusOverride;
            if (over > 0f)
                return over;

            return node.IsTarget ? _defaultPrimaryRadius : _defaultAuxiliaryRadius;
        }

        /// <summary>
        /// Meia-largura (X, Z) da área do nó na forma Retângulo: o retângulo dele, ou um
        /// quadrado de meia-aresta = raio quando ele não tem um.
        /// </summary>
        internal Vector2 HalfExtentsOf(NavNode node)
        {
            if (node.HasArea)
                return node.AreaSize * 0.5f;

            float radius = RadiusOf(node);
            return new Vector2(radius, radius);
        }

        internal Vector3 AreaCenterOf(NavNode node) => node.HasArea ? node.AreaCenter : node.Position;

        /// <summary>
        /// Na forma Retângulo: o quão CENTRAL o ponto está na área do nó — 0 no centro, 1 na
        /// borda, acima de 1 fora. Normalizado pela meia-largura de cada eixo, para um ladrilho
        /// comprido e um curto serem comparáveis no desempate (que só acontece na borda comum).
        /// </summary>
        private float RectangleMetric(NavNode node, Vector3 point)
        {
            Vector2 half = HalfExtentsOf(node);
            Vector3 center = AreaCenterOf(node);
            float dx = Mathf.Abs(point.x - center.x) / Mathf.Max(1e-4f, half.x);
            float dz = Mathf.Abs(point.z - center.z) / Mathf.Max(1e-4f, half.y);
            return Mathf.Max(dx, dz);
        }

        public int NodeCount => _nodes.Count;

        public IReadOnlyList<NavNode> Nodes => _nodes;

        // Conjunto da lista, para o gizmo de cada NavNode perguntar "estou no grafo?" sem varrer
        // a lista inteira por nó por repaint. Refeito quando a lista muda de tamanho ou é coletada.
        private HashSet<NavNode> _nodeSet;
        private int _nodeSetCount = -1;

        /// <summary>O nó está na lista deste grafo (e não só pendurado debaixo dele)?</summary>
        public bool ContainsNode(NavNode node)
        {
            if (_nodeSet == null || _nodeSetCount != _nodes.Count)
            {
                _nodeSet = new HashSet<NavNode>(_nodes);
                _nodeSetCount = _nodes.Count;
            }

            return _nodeSet.Contains(node);
        }

        private void OnValidate()
        {
            _nodeSetCount = -1;
            _agentBody = null;
        }

        private void Awake() => EnsureBaked();

        /// <summary>
        /// Idempotente de propósito: o agente chama isto no Initialize e o Awake do grafo pode
        /// ou não ter rodado antes (a ordem entre componentes de objetos diferentes não é
        /// garantida). Quem chegar primeiro faz o bake.
        /// </summary>
        public void EnsureBaked()
        {
            if (_isBaked)
                return;

            _nodes.RemoveAll(node => node == null);

            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            BuildAdjacency();

            int directedEdges = 0;
            foreach (int[] neighbors in _adjacency)
                directedEdges += neighbors.Length;

            _pathOrder = new int[_nodes.Count];
            _pathParent = new int[_nodes.Count];
            _pathCost = new float[_nodes.Count];
            _pathStampOf = new int[_nodes.Count];
            _pathClosed = new bool[_nodes.Count];

            // Heap "preguiçoso": uma entrada por relaxamento, as velhas são puladas ao sair. O
            // teto é uma entrada por aresta dirigida + a origem.
            _heapNode = new int[directedEdges + 1];
            _heapCost = new float[directedEdges + 1];

            _pathDiameter = -1f;
            _spawnable = null;

            _hasPingNodes = HasPingNodes;
            BuildRooms();
            _isBaked = true;

            ValidateBakedGraph();
        }

        private void BuildAdjacency()
        {
            var sets = new List<HashSet<int>>(_nodes.Count);
            for (int i = 0; i < _nodes.Count; i++)
                sets.Add(new HashSet<int>());

            for (int i = 0; i < _nodes.Count; i++)
            {
                foreach (NavNode neighbor in _nodes[i].Neighbors)
                {
                    if (neighbor == null || neighbor.Index == i)
                        continue;

                    // A checagem de identidade (e não só de faixa) é o que impede uma referência
                    // vazada de outra cópia da arena de virar uma aresta silenciosamente errada:
                    // aquele nó TEM um índice válido — o dele, no grafo dele — e um teste de
                    // faixa aceitaria, ligando este grafo a um nó local qualquer que por acaso
                    // ocupe aquele número. O sintoma seria uma aresta que atravessa o mapa.
                    if (neighbor.Index < 0 || neighbor.Index >= _nodes.Count || _nodes[neighbor.Index] != neighbor)
                    {
                        Debug.LogWarning(
                            $"{name}: '{_nodes[i].name}' aponta para '{neighbor.name}', que não pertence a este " +
                            "grafo. Ligação ignorada — provavelmente uma referência vazada de outra arena.",
                            _nodes[i]);
                        continue;
                    }

                    sets[i].Add(neighbor.Index);

                    if (_makeLinksBidirectional)
                        sets[neighbor.Index].Add(i);
                }
            }

            _adjacency = new int[_nodes.Count][];
            _adjacencyLength = new float[_nodes.Count][];
            for (int i = 0; i < _nodes.Count; i++)
            {
                _adjacency[i] = new int[sets[i].Count];
                sets[i].CopyTo(_adjacency[i]);

                _adjacencyLength[i] = new float[_adjacency[i].Length];
                for (int k = 0; k < _adjacency[i].Length; k++)
                {
                    Vector3 delta = _nodes[_adjacency[i][k]].Position - _nodes[i].Position;
                    _adjacencyLength[i][k] = new Vector2(delta.x, delta.z).magnitude;
                }
            }
        }

        // ================================================================================
        // Salas e portas
        // ================================================================================

        // Sala de cada nó (-1 = porta). Calculada sobre TODOS os nós, ativos ou não: é estrutura do
        // mapa, e uma lição que desliga um nó não pode renumerar as salas no meio do treino.
        private int[] _roomOf;
        private int[][] _roomNodes;     // nós (não-porta) de cada sala
        private int[][] _roomDoors;     // portas que encostam em cada sala
        private int[][] _doorRooms;     // por nó: as salas que a porta liga (vazio para não-porta)
        private Vector3[] _roomCentroid;
        private int[][] _roomNeighbors; // salas ligadas por uma porta
        private static readonly int[] NoRooms = new int[0];

        /// <summary>Quantas salas (corredores incluídos) o grafo tem.</summary>
        public int RoomCount => _roomNodes != null ? _roomNodes.Length : 0;

        /// <summary>Sala do nó; -1 para porta.</summary>
        public int RoomOf(int index) => _roomOf[index];

        /// <summary>Nós (não-porta) da sala.</summary>
        public int[] NodesOfRoom(int room) => _roomNodes[room];

        /// <summary>Portas que encostam na sala (as entradas/saídas dela).</summary>
        public int[] DoorsOfRoom(int room) => _roomDoors[room];

        /// <summary>Salas que a porta liga (normalmente duas). Vazio para nó que não é porta.</summary>
        public int[] RoomsOfDoor(int door) => _doorRooms[door];

        /// <summary>Centro (média das posições dos nós) da sala — origem estável para ordenar as portas dela.</summary>
        public Vector3 RoomCentroid(int room) => _roomCentroid[room];

        /// <summary>
        /// A outra sala que a porta liga, vista de <paramref name="room"/>; -1 se a porta não liga
        /// essa sala a outra.
        /// </summary>
        public int OtherRoom(int door, int room)
        {
            int[] rooms = _doorRooms[door];
            if (rooms.Length != 2)
                return -1;

            if (rooms[0] == room)
                return rooms[1];

            return rooms[1] == room ? rooms[0] : -1;
        }

        // O nó pertence à sala ou é uma porta dela? É o filtro da busca dentro de uma sala.
        private bool TouchesRoom(int node, int room)
        {
            if (_roomOf[node] == room)
                return true;

            int[] rooms = _doorRooms[node];
            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i] == room)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Tira as portas e numera os pedaços conexos que sobram (BFS). A mesma ideia do antigo
        /// menu "11. Numerar salas" do NavGraphPlacer, agora em runtime e pela marcação de
        /// PORTA, não pelo nome do batente nem pelo tamanho do ladrilho.
        /// </summary>
        private void BuildRooms()
        {
            int count = _nodes.Count;
            _roomOf = new int[count];
            for (int i = 0; i < count; i++)
                _roomOf[i] = -1;

            var rooms = new List<List<int>>();
            var queue = new Queue<int>();
            for (int start = 0; start < count; start++)
            {
                if (_nodes[start].IsDoor || _roomOf[start] >= 0)
                    continue;

                var members = new List<int>();
                int room = rooms.Count;
                _roomOf[start] = room;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    members.Add(current);
                    foreach (int next in _adjacency[current])
                    {
                        if (_nodes[next].IsDoor || _roomOf[next] >= 0)
                            continue;

                        _roomOf[next] = room;
                        queue.Enqueue(next);
                    }
                }

                rooms.Add(members);
            }

            _roomNodes = new int[rooms.Count][];
            _roomCentroid = new Vector3[rooms.Count];
            var doorsOfRoom = new List<int>[rooms.Count];
            for (int r = 0; r < rooms.Count; r++)
            {
                _roomNodes[r] = rooms[r].ToArray();
                doorsOfRoom[r] = new List<int>();

                Vector3 sum = Vector3.zero;
                foreach (int node in _roomNodes[r])
                    sum += _nodes[node].Position;
                _roomCentroid[r] = sum / Mathf.Max(1, _roomNodes[r].Length);
            }

            _doorRooms = new int[count][];
            for (int i = 0; i < count; i++)
            {
                if (!_nodes[i].IsDoor)
                {
                    _doorRooms[i] = NoRooms;
                    continue;
                }

                var touching = new List<int>();
                foreach (int next in _adjacency[i])
                {
                    int room = _roomOf[next];
                    if (room >= 0 && !touching.Contains(room))
                        touching.Add(room);
                }

                _doorRooms[i] = touching.ToArray();
                foreach (int room in touching)
                    doorsOfRoom[room].Add(i);
            }

            _roomDoors = new int[rooms.Count][];
            var neighbors = new List<int>[rooms.Count];
            for (int r = 0; r < rooms.Count; r++)
            {
                _roomDoors[r] = doorsOfRoom[r].ToArray();
                neighbors[r] = new List<int>();
            }

            for (int r = 0; r < rooms.Count; r++)
            {
                foreach (int door in _roomDoors[r])
                {
                    int other = OtherRoom(door, r);
                    if (other >= 0 && !neighbors[r].Contains(other))
                        neighbors[r].Add(other);
                }
            }

            _roomNeighbors = new int[rooms.Count][];
            for (int r = 0; r < rooms.Count; r++)
                _roomNeighbors[r] = neighbors[r].ToArray();
        }

        /// <summary>
        /// Quantas PORTAS separam cada sala de <paramref name="fromRoom"/> (BFS no grafo de salas; -1 =
        /// inalcançável). É a "planta do prédio" que o agente conhece: a que distância, em salas, fica
        /// cada uma. Preenche <paramref name="hops"/> (tamanho RoomCount).
        /// </summary>
        public void RoomHops(int fromRoom, int[] hops)
        {
            for (int r = 0; r < hops.Length; r++)
                hops[r] = -1;

            if (fromRoom < 0 || fromRoom >= RoomCount)
                return;

            var queue = new Queue<int>();
            hops[fromRoom] = 0;
            queue.Enqueue(fromRoom);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in _roomNeighbors[current])
                {
                    if (hops[next] >= 0)
                        continue;

                    hops[next] = hops[current] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        // Erro de autoria em salas é silencioso do mesmo jeito: uma porta que não separa duas
        // salas não paga travessia nunca, e uma sala sem porta nunca é "saída". Grita no Play.
        private void ValidateRooms()
        {
            if (RoomCount == 0)
            {
                Debug.LogError($"{name}: nenhuma sala — todo nó é porta? Marque o chão como Auxiliar.", this);
                return;
            }

            int doors = 0;
            int maxDoors = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].IsDoor)
                    continue;

                doors++;
                int[] rooms = _doorRooms[i];
                if (rooms.Length != 2)
                {
                    string why = rooms.Length == 0 ? "não encosta em sala nenhuma"
                        : rooms.Length == 1 ? "tem os DOIS lados na mesma sala (uma ligação contorna o vão?) ou só um lado ligado"
                        : $"liga {rooms.Length} salas";
                    Debug.LogWarning($"{name}: a porta '{_nodes[i].name}' {why}. Ela não paga travessia como deveria.", _nodes[i]);
                }

                foreach (int next in _adjacency[i])
                {
                    if (_nodes[next].IsDoor && next > i)
                    {
                        Debug.LogWarning(
                            $"{name}: as portas '{_nodes[i].name}' e '{_nodes[next].name}' estão ligadas direto — " +
                            "entre duas portas tem que haver chão de sala.", _nodes[i]);
                    }
                }
            }

            for (int r = 0; r < RoomCount; r++)
            {
                maxDoors = Mathf.Max(maxDoors, _roomDoors[r].Length);
                if (_roomDoors[r].Length == 0 && RoomCount > 1)
                {
                    Debug.LogWarning(
                        $"{name}: a sala de '{_nodes[_roomNodes[r][0]].name}' não tem porta — está isolada do resto.",
                        _nodes[_roomNodes[r][0]]);
                }
            }

            if (doors == 0)
            {
                Debug.LogError(
                    $"{name}: nenhuma PORTA no grafo — o mapa inteiro vira uma sala só. Marque os nós dos vãos " +
                    "como \"Porta\".", this);
            }

            Debug.Log($"{name}: {RoomCount} sala(s), {doors} porta(s), no máximo {maxDoors} porta(s) por sala.", this);
        }

        /// <summary>
        /// Maior distância em METROS, pelo grafo, entre dois nós ativos (o "diâmetro" do mapa).
        /// É o normalizador das distâncias de caminho na observação (fronteira e ping): 1.0 =
        /// "o outro lado do mapa". Calculado uma vez, na primeira consulta, e automático de
        /// propósito — um número fixo no Inspector ficaria errado a cada grafo regenerado, e
        /// saturar em 1.0 apaga a informação (era o que acontecia com 25 arestas num mapa de 42).
        /// </summary>
        public float PathDiameter
        {
            get
            {
                if (_pathDiameter > 0f)
                    return _pathDiameter;

                EnsureBaked();
                float diameter = 0f;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (!_nodes[i].IsEnabled)
                        continue;

                    RunDijkstra(i, -1);
                    for (int k = 0; k < _pathCount; k++)
                        diameter = Mathf.Max(diameter, _pathCost[_pathOrder[k]]);
                }

                // Piso de 1 m: grafo de um nó só não pode virar divisão por zero.
                _pathDiameter = Mathf.Max(1f, diameter);
                return _pathDiameter;
            }
        }

        /// <summary>
        /// O nó serve de SPAWN? Ativo e com o corpo cabendo parado nele com _spawnClearance.
        /// Medido na primeira consulta (e não no bake, que roda no Awake, quando nem todo
        /// collider da cena foi registrado na física ainda) e guardado: mobília não se mexe
        /// durante o treino.
        /// </summary>
        public bool CanSpawnAt(int index)
        {
            if (!_nodes[index].IsEnabled)
                return false;

            if (_spawnable == null)
                MeasureSpawnable();

            return _spawnable[index];
        }

        private void MeasureSpawnable()
        {
            _spawnable = new bool[_nodes.Count];
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                _spawnable[i] = IsBodyClear(_nodes[i].Position, SpawnClearance);
                if (_spawnable[i])
                    count++;
            }

            // Sem nenhum nó com folga (layer errada, nós todos em corredor apertado): melhor
            // nascer encostado do que não nascer — e gritar, porque é quase certo erro de montagem.
            if (count == 0 && _nodes.Count > 0)
            {
                Debug.LogWarning(
                    $"{name}: nenhum nó tem folga de spawn ({SpawnClearance:0.00} m). Liberando todos — rode " +
                    "\"1. Diagnosticar\" no NavGraphPlacer.", this);
                for (int i = 0; i < _nodes.Count; i++)
                    _spawnable[i] = true;
            }
        }

        public Vector3 NodePosition(int index) => _nodes[index].Position;

        public NavNode GetNode(int index) => _nodes[index];

        public bool IsNodeEnabled(int index) => _nodes[index].IsEnabled;

        /// <summary>O nó é PORTA (corta o grafo em salas; paga ao ser atravessado)?</summary>
        public bool IsDoor(int index) => _nodes[index].IsDoor;

        public int[] GetNeighbors(int index) => _adjacency[index];

        /// <summary>
        /// Quanto vale ATENDER o ping neste nó. Igual para todo nó: o peso por nó saiu junto com
        /// a pontuação por área (ver o cabeçalho de _pingNodeScore).
        /// </summary>
        public float PingValue(int index) => _pingNodeScore;

        /// <summary>O grafo tem algum nó de PING (senão o ping usa os de exploração).</summary>
        public bool HasPingNodes
        {
            get
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_nodes[i] != null && _nodes[i].IsPing)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// O nó pode tocar (GraphPingSystem) e a chegada do hider nele vira rastro? Os nós de ping
        /// DO EPISÓDIO (<see cref="DrawEpisodePingNodes"/>); antes do primeiro sorteio, os marcados
        /// como Ping ou, sem nenhum, qualquer nó de sala (porta nunca: barulho é dentro de uma sala).
        /// </summary>
        public bool IsPingSource(int index) =>
            _episodePing != null ? _episodePing[index]
            : _hasPingNodes ? _nodes[index].IsPing
            : !_nodes[index].IsDoor;

        // Nós de ping deste episódio. Estado de EPISÓDIO num componente de estrutura, de propósito:
        // é do AMBIENTE da arena (o hider e o ping do agente têm que concordar sobre onde o barulho
        // pode acontecer), e o grafo é o único objeto que os dois já compartilham.
        private bool[] _episodePing;

        /// <summary>
        /// Sorteia os nós de ping do episódio: UM nó qualquer (não-porta, ativo) por sala. Sala de um
        /// nó só entra com <paramref name="singleNodeRoomChance"/> — numa sala de um ladrilho o ping
        /// seria sempre no mesmo lugar, então às vezes ela simplesmente não tem barulho. Variar por
        /// episódio é o que impede a política de decorar "o barulho é sempre ali". Chamado pela arena.
        /// </summary>
        public void DrawEpisodePingNodes(float singleNodeRoomChance)
        {
            EnsureBaked();
            if (_episodePing == null || _episodePing.Length != _nodes.Count)
                _episodePing = new bool[_nodes.Count];
            else
                System.Array.Clear(_episodePing, 0, _episodePing.Length);

            for (int r = 0; r < RoomCount; r++)
            {
                int[] members = _roomNodes[r];
                int enabled = 0;
                foreach (int node in members)
                {
                    if (_nodes[node].IsEnabled)
                        enabled++;
                }

                if (enabled == 0 || (enabled == 1 && Random.value >= singleNodeRoomChance))
                    continue;

                int pick = Random.Range(0, enabled);
                foreach (int node in members)
                {
                    if (!_nodes[node].IsEnabled)
                        continue;

                    if (pick-- == 0)
                    {
                        _episodePing[node] = true;
                        break;
                    }
                }
            }
        }

        // Medido no bake (o ping e o hider perguntam a cada chegada). Nó ligado/desligado por
        // lição não muda o TIPO, então a foto do bake continua certa.
        private bool _hasPingNodes;

        /// <summary>
        /// Todos os nós ativos, portas e salas. Usado pela checagem de conectividade do
        /// bake, onde o que importa é alcançabilidade — e um auxiliar isolado também é um bug.
        /// </summary>
        public int EnabledNodeCount()
        {
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].IsEnabled)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Nó ativo cuja área de chegada contém a posição; havendo mais de um, o mais central.
        /// Devolve -1 quando o agente está no meio do nada — o que é normal e não é erro: entre
        /// dois nós ele simplesmente não está em nenhum.
        ///
        /// A distância é PLANAR (X/Z) e vem de <see cref="AreaDistance"/>, então a forma
        /// escolhida no Inspector decide a área: disco no círculo, quadrado no Chebyshev. O
        /// mapa tem um andar só, e medir em 3D faria a altura do nó em relação ao agente comer
        /// parte da área — um nó desenhado no chão registraria visita numa área menor que a do
        /// gizmo, sem nada indicando o porquê.
        ///
        /// <paramref name="current"/> é a HISTERESE: enquanto a posição ainda estiver na área do
        /// nó atual, ele vence o desempate. Sem isso, andar em cima da borda comum de dois
        /// ladrilhos trocava de nó a cada step (A-B-A-B): a âncora piscava, o "nó anterior"
        /// alternava e a contagem de visitas inflava sozinha — um loop que o agente não fez.
        /// </summary>
        public int FindNodeAt(Vector3 position, int current = -1)
        {
            _areaCandidates.Clear();

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavNode node = _nodes[i];
                if (!node.IsEnabled)
                    continue;

                // Retângulo: a métrica é normalizada (0 no centro, 1 na borda). Os ladrilhos não
                // se sobrepõem, então o desempate só decide a borda comum de dois vizinhos.
                if (_nodeShape == NodeShape.Rectangle)
                {
                    float metric = RectangleMetric(node, position);
                    if (metric <= 1f)
                        _areaCandidates.Add((metric, i));
                    continue;
                }

                float distance = AreaDistance(node.Position, position);
                if (distance <= NodeRadius(i))
                    _areaCandidates.Add((distance, i));
            }

            if (_areaCandidates.Count == 0)
                return -1;

            if (_nodeShape == NodeShape.Rectangle && KeepAnchor(current, position))
                return current;

            // Do centro mais perto para o mais longe; o primeiro que ENXERGA o ponto vence. Com a
            // área cortada pela parede, o nó da sala vizinha (mais perto em linha reta, mas atrás
            // da parede) perde para o da sala em que o agente está de fato.
            //
            // No Retângulo não há corte: o retângulo JÁ É a área declarada (o ladrilhamento só o
            // desenha sobre chão livre), e o raycast só erraria — um ladrilho de corredor que
            // engloba um pilar perderia o chão atrás do pilar, onde o agente passa.
            _areaCandidates.Sort(_byAreaDistance);
            bool clip = _areasStopAtWalls && _nodeShape != NodeShape.Rectangle;
            foreach ((float _, int index) in _areaCandidates)
            {
                if (index == current && (!clip || CanSeeFromNode(_nodes[index].Position, position)))
                    return index;
            }

            foreach ((float _, int index) in _areaCandidates)
            {
                if (!clip || CanSeeFromNode(_nodes[index].Position, position))
                    return index;
            }

            return -1;
        }

        // Folga da âncora (ver _anchorHysteresis): o agente saiu do nó atual há menos da folga e
        // ainda não entrou fundo o bastante no vizinho mais central que o contém? Então fica.
        // Chamado só com candidatos na lista (o agente está dentro de algum ladrilho).
        private bool KeepAnchor(int current, Vector3 position)
        {
            if (_anchorHysteresis <= 0f || current < 0 || current >= _nodes.Count || !_nodes[current].IsEnabled)
                return false;

            NavNode currentNode = _nodes[current];
            float outside = RectangleOutside(currentNode, position);

            // Ainda dentro do atual: o desempate normal (abaixo) já o mantém.
            if (outside <= 0f || outside > _anchorHysteresis)
                return false;

            // O candidato mais central (menor métrica) é quem assumiria.
            int best = -1;
            float bestMetric = float.MaxValue;
            foreach ((float metric, int index) in _areaCandidates)
            {
                if (index != current && metric < bestMetric)
                {
                    bestMetric = metric;
                    best = index;
                }
            }

            if (best < 0)
                return true;

            NavNode candidate = _nodes[best];
            Vector2 half = HalfExtentsOf(candidate);
            float required = Mathf.Min(_anchorHysteresis, 0.5f * Mathf.Min(half.x, half.y));
            float depth = -RectangleOutside(candidate, position);
            return depth < required;
        }

        // Metros para FORA da borda do retângulo do nó (negativo = dentro; o módulo é a distância
        // até a borda mais próxima). Planar, como a métrica.
        private float RectangleOutside(NavNode node, Vector3 point)
        {
            Vector2 half = HalfExtentsOf(node);
            Vector3 center = AreaCenterOf(node);
            float ox = Mathf.Abs(point.x - center.x) - half.x;
            float oz = Mathf.Abs(point.z - center.z) - half.y;
            return Mathf.Max(ox, oz);
        }

        // Rascunho do FindNodeAt, alocado uma vez (roda a cada step de física, por agente).
        private readonly List<(float distance, int index)> _areaCandidates = new List<(float distance, int index)>();
        private static readonly System.Comparison<(float distance, int index)> _byAreaDistance =
            (a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.index.CompareTo(b.index);

        /// <summary>
        /// Nó ativo mais próximo ALCANÇÁVEL EM LINHA RETA, ignorando raio de chegada. Serve para
        /// o agente saber onde a malha está quando ele se afastou dela — sem isso a única
        /// referência que ele tem é a âncora, que é um nó que ele já deixou para trás e pode
        /// estar atrás dele.
        ///
        /// A checagem de linha livre NÃO é opcional: o nó geometricamente mais próximo pode
        /// estar do outro lado de uma parede, e apontar para ele ensinaria o agente a andar
        /// contra o concreto. Quando nenhum nó tem linha livre (o agente entalou numa quina,
        /// por exemplo), cai no mais próximo puro — uma referência ruim ainda é melhor que
        /// nenhuma, e a alternativa seria a observação piscar entre "tem alvo" e "não tem".
        /// </summary>
        public int FindNearestReachableNode(Vector3 position)
        {
            int nearestClear = -1;
            int nearestAny = -1;
            float bestClear = float.MaxValue;
            float bestAny = float.MaxValue;

            for (int i = 0; i < _nodes.Count; i++)
            {
                NavNode node = _nodes[i];
                if (!node.IsEnabled)
                    continue;

                Vector3 delta = node.Position - position;
                float distance = new Vector2(delta.x, delta.z).magnitude;

                if (distance < bestAny)
                {
                    bestAny = distance;
                    nearestAny = i;
                }

                // Só paga o custo do SphereCast enquanto ele pode mudar a resposta.
                if (distance < bestClear && IsSegmentClear(position, node.Position))
                {
                    bestClear = distance;
                    nearestClear = i;
                }
            }

            return nearestClear >= 0 ? nearestClear : nearestAny;
        }

        /// <summary>
        /// Caminho mais curto até um alvo JÁ ESCOLHIDO. É o que mantém a seta fixa num alvo
        /// entre dois sorteios: sem isto, cada troca de nó re-sortearia e a seta ficaria
        /// piscando entre candidatos. Falha se o alvo ficou inalcançável.
        /// </summary>
        public bool TryFindPathTo(int from, int target, out int nextStep, out float pathDistance)
        {
            nextStep = -1;
            pathDistance = 0f;

            if (from < 0 || from >= _nodes.Count || target < 0 || target >= _nodes.Count || from == target)
                return false;

            if (!_nodes[from].IsEnabled || !_nodes[target].IsEnabled)
                return false;

            RunDijkstra(from, target);

            if (_pathStampOf[target] != _pathStamp || !_pathClosed[target])
                return false;

            pathDistance = _pathCost[target];
            nextStep = FirstStepTowards(from, target);
            return true;
        }

        /// <summary>
        /// O QUE RESTA POR ESTA SAÍDA: entrando por <paramref name="via"/> a partir de
        /// <paramref name="from"/>, soma o <paramref name="value"/> dos nós alcançáveis (quem chama
        /// já zera o que não vale nada), cada um descontado pela distância em METROS pelo grafo:
        /// fator = 0.5^(metros / meia-vida). A busca NÃO passa por from — o que está do outro lado
        /// do nó atual pertence a outra saída.
        ///
        /// Com <paramref name="room"/> &gt;= 0 a busca fica DENTRO da sala: entra nas portas dela
        /// (elas contam o valor que tiverem) mas não as atravessa. É a visão "sala + portas" do
        /// agente: o que tem do outro lado de uma porta não entra na conta.
        ///
        /// É o "o que tem atrás desta porta?" que substitui a seta sem entregar um caminho: num
        /// beco com tudo visitado por perto, a saída que leva ao inexplorado ainda pontua mais
        /// que as outras. Sem isto o agente só enxergava 1 aresta à frente e rodava em círculo
        /// quando todos os vizinhos já estavam visitados (node4_noarrow_01: cobertura parada em
        /// 13% por 7M steps). Portado do NavGraph.ScoreBeyond da feature/node-unexplored, que
        /// descontava por ARESTA — aqui é por metro, pelo mesmo motivo do _pathOrder.
        ///
        /// Ciclos podem contar o mesmo nó para duas saídas. Aceitável: a observação é
        /// comparativa entre saídas, e o empate é a resposta certa.
        /// </summary>
        public float ScoreBeyond(int from, int via, float halfLifeMeters, float[] value, int room = -1)
        {
            if (from < 0 || from >= _nodes.Count || via < 0 || via >= _nodes.Count || from == via)
                return 0f;

            if (!_nodes[via].IsEnabled)
                return 0f;

            float entryCost = 0f;
            int[] fromNeighbors = _adjacency[from];
            for (int k = 0; k < fromNeighbors.Length; k++)
            {
                if (fromNeighbors[k] == via)
                    entryCost = _adjacencyLength[from][k];
            }

            RunDijkstra(via, -1, blocked: from, startCost: entryCost, room: room, expandDoorOrigin: false);

            float decayPerMeter = Mathf.Log(0.5f) / Mathf.Max(0.1f, halfLifeMeters);
            float total = 0f;
            for (int i = 0; i < _pathCount; i++)
            {
                int node = _pathOrder[i];
                if (value[node] <= 0f)
                    continue;

                total += value[node] * Mathf.Exp(decayPerMeter * _pathCost[node]);
            }

            return total;
        }

        /// <summary>
        /// DISTÂNCIA POR ESTA SAÍDA: entrando por <paramref name="via"/> a partir de
        /// <paramref name="from"/> (sem passar de volta por from), metros pelo grafo até o nó mais
        /// próximo com <paramref name="value"/> &gt; 0. -1 se não há nenhum por ali.
        ///
        /// O par do <see cref="ScoreBeyond"/>: aquele diz QUANTO tem atrás da porta (soma com
        /// desconto), este diz QUÃO PERTO está o primeiro. A soma com desconto não é um campo de
        /// distância — no nó A a melhor saída pode ser B e em B a melhor ser A de novo (as somas
        /// quase empatam e desempatam para lados diferentes), e com tudo visitado por perto todas
        /// as saídas valem quase zero e ruído vira instrução. A distância ao mais próximo é um
        /// campo de verdade: seguir sempre a menor só diminui, então não há loop, e num beco
        /// cercado de visitados ela continua dizendo "por aqui faltam 45 m, por ali 60 m".
        /// </summary>
        public float DistanceToNearestBeyond(int from, int via, float[] value, int room = -1)
        {
            if (from < 0 || from >= _nodes.Count || via < 0 || via >= _nodes.Count || from == via)
                return -1f;

            if (!_nodes[via].IsEnabled)
                return -1f;

            float entryCost = 0f;
            int[] fromNeighbors = _adjacency[from];
            for (int k = 0; k < fromNeighbors.Length; k++)
            {
                if (fromNeighbors[k] == via)
                    entryCost = _adjacencyLength[from][k];
            }

            RunDijkstra(via, -1, blocked: from, startCost: entryCost, room: room, expandDoorOrigin: false);

            // _pathOrder sai em ordem de distância: o primeiro com valor é o mais próximo.
            for (int i = 0; i < _pathCount; i++)
            {
                int node = _pathOrder[i];
                if (value[node] > 0f)
                    return _pathCost[node];
            }

            return -1f;
        }

        /// <summary>
        /// Caminho mais curto a partir de <paramref name="from"/> DENTRO da sala
        /// <paramref name="room"/>: os nós dela e as portas dela (que entram, mas não são
        /// atravessadas). Se from for uma porta da sala, a busca sai dela só para dentro da sala.
        ///
        /// Devolve quantos nós foram alcançados; leia-os, EM ORDEM DE DISTÂNCIA, com
        /// <see cref="SearchedNode"/>, <see cref="SearchedCost"/> e <see cref="SearchedFirstStep"/>
        /// ANTES de qualquer outra busca no grafo (os buffers são compartilhados).
        /// </summary>
        public int SearchRoom(int from, int room)
        {
            if (from < 0 || from >= _nodes.Count || room < 0 || room >= RoomCount)
                return 0;

            RunDijkstra(from, -1, room: room, expandDoorOrigin: true);
            return _pathCount;
        }

        /// <summary>O i-ésimo nó da última <see cref="SearchRoom"/>, do mais perto para o mais longe.</summary>
        public int SearchedNode(int i) => _pathOrder[i];

        /// <summary>Distância (m pelo grafo) até o nó na última <see cref="SearchRoom"/>.</summary>
        public float SearchedCost(int node) => _pathCost[node];

        /// <summary>Primeiro passo de from até o nó na última <see cref="SearchRoom"/> (from = a origem dela).</summary>
        public int SearchedFirstStep(int from, int node) => node == from ? from : FirstStepTowards(from, node);

        /// <summary>
        /// Caminho mais curto em METROS a partir de <paramref name="from"/>, só por nós ativos
        /// (Dijkstra com heap). Deixa em _pathOrder[0.._pathCount) os nós fechados em ordem de
        /// distância e em _pathParent/_pathCost o caminho de cada um. Com <paramref name="stopAt"/>
        /// &gt;= 0 para assim que esse nó fecha — a distância dele já é a final.
        /// </summary>
        // blocked: nó que a busca não atravessa (-1 = nenhum). startCost: custo já pago até from.
        // Os dois só são usados pelo ScoreBeyond, que isola uma saída do nó atual.
        // room >= 0: só entra em nós dessa sala e nas portas dela, e PORTA NÃO É EXPANDIDA (a busca
        // para no vão). A exceção é a própria origem quando expandDoorOrigin: o agente parado num
        // vão ainda precisa achar o caminho para dentro da sala.
        private void RunDijkstra(int from, int stopAt, int blocked = -1, float startCost = 0f,
            int room = -1, bool expandDoorOrigin = true)
        {
            _pathStamp++;
            _pathCount = 0;
            _heapCount = 0;

            // Carimbado como fechado sem entrar na lista: é a parede que separa esta saída das
            // outras.
            if (blocked >= 0)
            {
                _pathStampOf[blocked] = _pathStamp;
                _pathClosed[blocked] = true;
            }

            _pathStampOf[from] = _pathStamp;
            _pathClosed[from] = false;
            _pathParent[from] = -1;
            _pathCost[from] = startCost;
            HeapPush(from, startCost);

            while (_heapCount > 0)
            {
                HeapPop(out int current, out float cost);

                // Entrada velha: o nó já fechou, ou foi relaxado de novo com custo menor.
                if (_pathClosed[current] || cost > _pathCost[current])
                    continue;

                _pathClosed[current] = true;
                _pathOrder[_pathCount++] = current;

                if (current == stopAt)
                    return;

                if (room >= 0 && _nodes[current].IsDoor && (current != from || !expandDoorOrigin))
                    continue;

                int[] neighbors = _adjacency[current];
                float[] lengths = _adjacencyLength[current];
                for (int k = 0; k < neighbors.Length; k++)
                {
                    int next = neighbors[k];
                    if (!_nodes[next].IsEnabled)
                        continue;

                    if (room >= 0 && !TouchesRoom(next, room))
                        continue;

                    float nextCost = cost + lengths[k];
                    bool seen = _pathStampOf[next] == _pathStamp;
                    if (seen && (_pathClosed[next] || nextCost >= _pathCost[next]))
                        continue;

                    if (!seen)
                    {
                        _pathStampOf[next] = _pathStamp;
                        _pathClosed[next] = false;
                    }

                    _pathCost[next] = nextCost;
                    _pathParent[next] = current;
                    HeapPush(next, nextCost);
                }
            }
        }

        private void HeapPush(int node, float cost)
        {
            int i = _heapCount++;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapCost[parent] <= cost)
                    break;

                _heapNode[i] = _heapNode[parent];
                _heapCost[i] = _heapCost[parent];
                i = parent;
            }

            _heapNode[i] = node;
            _heapCost[i] = cost;
        }

        private void HeapPop(out int node, out float cost)
        {
            node = _heapNode[0];
            cost = _heapCost[0];

            int lastNode = _heapNode[--_heapCount];
            float lastCost = _heapCost[_heapCount];
            int i = 0;
            while (true)
            {
                int child = i * 2 + 1;
                if (child >= _heapCount)
                    break;

                if (child + 1 < _heapCount && _heapCost[child + 1] < _heapCost[child])
                    child++;

                if (_heapCost[child] >= lastCost)
                    break;

                _heapNode[i] = _heapNode[child];
                _heapCost[i] = _heapCost[child];
                i = child;
            }

            if (_heapCount > 0)
            {
                _heapNode[i] = lastNode;
                _heapCost[i] = lastCost;
            }
        }

        // Volta pelos pais até o nó imediatamente após a origem. Só vale logo após RunDijkstra(from).
        private int FirstStepTowards(int from, int target)
        {
            int step = target;
            while (_pathParent[step] != from && _pathParent[step] != -1)
                step = _pathParent[step];

            return step;
        }

        /// <summary>
        /// O segmento entre dois pontos passa livre? Usado pelo gizmo de validação, pela
        /// auto-ligação, pelo nó alcançável mais próximo e pelo <see cref="NavGraphPlacer"/>. É a
        /// única definição de "não atravessa parede" do sistema.
        ///
        /// Varre a COLUNA do corpo (cápsula de _bodyBottom a _bodyTop), não uma altura só. Como
        /// todo cast do Unity, ignora collider que já envolve o ponto de partida — de propósito:
        /// em runtime a partida é o agente, e um agente encostado na parede não pode perder a
        /// linha livre para o mapa inteiro. Para "o ponto em si está livre?" use IsBodyClear.
        ///
        /// Usa a física da CENA do grafo, e não a global: no Prefab Mode o prefab vive numa cena
        /// de preview com física própria, e a consulta global olharia para a cena errada.
        /// </summary>
        public bool IsSegmentClear(Vector3 a, Vector3 b)
        {
            Vector3 delta = b - a;
            float distance = delta.magnitude;
            if (distance < 1e-4f)
                return true;

            Vector3 direction = delta / distance;
            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            float clearance = LinkClearance;

            if (clearance <= 0f)
            {
                Vector3 from = a + Vector3.up * _linkProbeHeight;
                return !physics.Raycast(from, direction, distance, _wallLayer, QueryTriggerInteraction.Ignore);
            }

            BodyCapsule(a, clearance, out Vector3 bottom, out Vector3 top);
            return !physics.CapsuleCast(bottom, top, clearance, direction, out _, distance, _wallLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// O corpo, com este raio, cabe parado em <paramref name="position"/> sem encostar em
        /// nada da layer de parede? Mesma coluna do <see cref="IsSegmentClear"/>. É o teste de
        /// "o nó está dentro de um móvel?" — que o cast não responde, porque ignora o collider
        /// em que ele começa.
        /// </summary>
        public bool IsBodyClear(Vector3 position, float radius)
        {
            BodyCapsule(position, radius, out Vector3 bottom, out Vector3 top);
            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            return physics.OverlapCapsule(bottom, top, radius, _overlapBuffer, _wallLayer, QueryTriggerInteraction.Ignore) == 0;
        }

        // Centros das semiesferas da cápsula. Se o raio for maior que meia coluna, as duas
        // colapsam no meio (vira uma esfera), em vez de inverterem.
        private void BodyCapsule(Vector3 position, float radius, out Vector3 bottom, out Vector3 top)
        {
            float low = _bodyBottom + radius;
            float high = _bodyTop - radius;
            if (high < low)
                low = high = (_bodyBottom + _bodyTop) * 0.5f;

            bottom = position + Vector3.up * low;
            top = position + Vector3.up * high;
        }

        // Erro de autoria em grafo é silencioso do mesmo jeito que erro de wiring em ML-Agents:
        // o treino roda, só não converge. Melhor gritar no Play.
        private void ValidateBakedGraph()
        {
            if (_nodes.Count == 0)
            {
                Debug.LogError($"{name}: NavGraph sem nós. Use \"Coletar nós filhos\" no menu de contexto.", this);
                return;
            }

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_adjacency[i].Length == 0)
                    Debug.LogWarning($"{name}: nó {i} ({_nodes[i].name}) não tem vizinhos — inalcançável.", _nodes[i]);
            }

            ValidateRooms();

            if (_nodeShape == NodeShape.Rectangle)
            {
                ValidateRectangles();
                ValidateConnectivity();
                return;
            }

            // Áreas de PRIMÁRIOS que se sobrepõem: o agente entra na área do outro antes de sair
            // da deste, e a "chegada" passa a acontecer no meio do caminho. Funciona, mas a
            // visita deixa de significar "estive lá" — e num doorway isso vira crédito por
            // entrar numa sala em que ele nunca pôs o pé.
            //
            // TODOS os pares, não só os ligados: dois primários em salas vizinhas não têm aresta
            // entre si (a parede está no meio), e é justamente aí que a área de um atravessa a
            // parede e cobre a sala do outro. Antes o aviso só olhava arestas e isso passava.
            int overlapping = 0;
            NavNode worstA = null;
            NavNode worstB = null;
            float worstRatio = 0f;

            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    // Só entre PRIMÁRIOS. O aviso existe para proteger o significado de
                    // "cheguei", e isso só importa onde a chegada paga alguma coisa. Numa malha
                    // auxiliar os discos se tocando é o desenho pretendido — é assim que ela
                    // pega o agente sem buracos — e avisar aqui encheria o Console de ruído a
                    // cada elo da cadeia, escondendo os avisos que importam.
                    if (!_nodes[i].IsDoor || !_nodes[j].IsDoor || !_nodes[i].IsEnabled || !_nodes[j].IsEnabled)
                        continue;

                    // Na métrica da forma: dois quadrados se tocam quando a distância de
                    // Chebyshev iguala a soma das meia-arestas, não quando a euclidiana iguala.
                    float length = AreaDistance(_nodes[i].Position, _nodes[j].Position);
                    float sum = NodeRadius(i) + NodeRadius(j);

                    if (sum <= length)
                        continue;

                    overlapping++;
                    float ratio = sum / Mathf.Max(length, 1e-4f);
                    if (ratio > worstRatio)
                    {
                        worstRatio = ratio;
                        worstA = _nodes[i];
                        worstB = _nodes[j];
                    }
                }
            }

            if (overlapping > 0)
            {
                Debug.LogWarning(
                    $"{name}: {overlapping} par(es) de portas com áreas sobrepostas. " +
                    $"Pior caso: '{worstA.name}' <-> '{worstB.name}'. Reduza o Default Primary Radius, afaste " +
                    "os nós ou rode NavGraphPlacer > \"Ajustar raios\" — a chegada está sendo registrada antes " +
                    "da travessia.", this);
            }

            ValidateShadowedPrimaries();
            ValidateConnectivity();
        }

        /// <summary>
        /// AUXILIAR EM CIMA DE PRIMÁRIO. FindNodeAt devolve o nó mais próximo do CENTRO, não o
        /// primário: onde as duas áreas se cruzam, quem ganha é quem tem o centro mais perto.
        ///
        /// Áreas se cruzando é normal e desejado — a malha existe justamente para levar até o
        /// primário, então ela tem que chegar perto. O que quebra é o centro do auxiliar ficar
        /// EM CIMA do centro do primário: aí a região em que o primário ganha encolhe até um
        /// sliver, e com o agente andando 0.1 m por step de física ele passa por cima sem
        /// registrar. Nos centros exatamente coincidentes o desempate vira a ordem da lista —
        /// silencioso e arbitrário — e o primário pode ficar INALCANÇÁVEL: a cobertura-alvo
        /// nunca é atingida e nenhum episódio termina em sucesso.
        ///
        /// Não é a mesma checagem do laço acima: aquela só olha pares LIGADOS e ignora
        /// auxiliares de propósito (a cadeia deve mesmo ter áreas sobrepostas). Esta olha todos
        /// os pares, porque um auxiliar não precisa estar ligado ao primário para eclipsá-lo.
        /// </summary>
        private void ValidateShadowedPrimaries()
        {
            for (int p = 0; p < _nodes.Count; p++)
            {
                if (!_nodes[p].IsEnabled || !_nodes[p].IsDoor)
                    continue;

                // Metade do raio: deixa a região de vitória do primário com pelo menos um quarto
                // do raio dele, que a 0.1 m por step são vários steps de margem.
                float minSeparation = NodeRadius(p) * 0.5f;

                for (int a = 0; a < _nodes.Count; a++)
                {
                    if (a == p || !_nodes[a].IsEnabled || _nodes[a].IsDoor)
                        continue;

                    float separation = AreaDistance(_nodes[p].Position, _nodes[a].Position);
                    if (separation >= minSeparation)
                        continue;

                    Debug.LogError(
                        $"{name}: o auxiliar '{_nodes[a].name}' está a {separation:0.00} da porta " +
                        $"'{_nodes[p].name}' (mínimo {minSeparation:0.00}). Ele eclipsa a porta: a chegada " +
                        "vai ser registrada no auxiliar, e a travessia da porta some. " +
                        "Afaste o auxiliar ou apague-o — a porta já serve de âncora ali.",
                        _nodes[p]);
                }
            }
        }

        /// <summary>
        /// Forma Retângulo: nenhum par de áreas pode se SOBREPOR (a promessa do ladrilhamento é
        /// "cada ponto em exatamente um nó"). Sobreposição de até 2 cm é tolerada — é arredondamento
        /// da grade, não autoria. Todos os pares, todos os tipos: aqui não há o "auxiliar pode
        /// encostar" dos discos, porque o retângulo não é mais raio de captura, é o chão do nó.
        /// </summary>
        private void ValidateRectangles()
        {
            const float tolerance = 0.02f;
            int overlapping = 0;
            int withoutArea = 0;
            NavNode worstA = null;
            NavNode worstB = null;

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].IsEnabled)
                    continue;

                if (!_nodes[i].HasArea)
                    withoutArea++;

                Vector3 ci = AreaCenterOf(_nodes[i]);
                Vector2 hi = HalfExtentsOf(_nodes[i]);

                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    if (!_nodes[j].IsEnabled)
                        continue;

                    Vector3 cj = AreaCenterOf(_nodes[j]);
                    Vector2 hj = HalfExtentsOf(_nodes[j]);
                    float overlapX = hi.x + hj.x - Mathf.Abs(ci.x - cj.x);
                    float overlapZ = hi.y + hj.y - Mathf.Abs(ci.z - cj.z);
                    if (overlapX <= tolerance || overlapZ <= tolerance)
                        continue;

                    overlapping++;
                    if (worstA == null)
                    {
                        worstA = _nodes[i];
                        worstB = _nodes[j];
                    }
                }
            }

            if (overlapping > 0)
            {
                Debug.LogWarning(
                    $"{name}: {overlapping} par(es) de retângulos sobrepostos (ex.: '{worstA.name}' <-> '{worstB.name}'). " +
                    "Onde dois se cruzam vence o de centro mais perto — rode o ladrilhamento de novo (NavGraphPlacer, " +
                    "menu 9) ou acerte o Area Size à mão.", worstA);
            }

            if (withoutArea > 0)
            {
                Debug.LogWarning(
                    $"{name}: {withoutArea} nó(s) sem retângulo na forma Retângulo — viram um quadrado de meia-aresta " +
                    "= raio padrão do papel, que provavelmente se sobrepõe aos ladrilhos em volta.", this);
            }
        }

        /// <summary>
        /// Um grafo desconexo faz a cobertura total ser inatingível a partir de metade dos
        /// spawns, e o episódio nunca termina em sucesso. Vale detectar na autoria.
        /// </summary>
        private void ValidateConnectivity()
        {
            var reachable = new bool[_nodes.Count];
            int start = -1;
            for (int i = 0; i < _nodes.Count && start < 0; i++)
            {
                if (_nodes[i].IsEnabled)
                    start = i;
            }

            if (start < 0)
                return;

            var queue = new Queue<int>();
            queue.Enqueue(start);
            reachable[start] = true;
            int reached = 1;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in _adjacency[current])
                {
                    if (reachable[neighbor] || !_nodes[neighbor].IsEnabled)
                        continue;

                    reachable[neighbor] = true;
                    reached++;
                    queue.Enqueue(neighbor);
                }
            }

            int enabled = EnabledNodeCount();
            if (reached < enabled)
            {
                Debug.LogError(
                    $"{name}: grafo desconexo — {reached}/{enabled} nós ativos alcançáveis a partir de " +
                    $"'{_nodes[start].name}'. A cobertura total fica impossível.", this);
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Refaz o bake e escreve no Console cada sala (nós, portas) e cada porta problemática.
        /// Depois disso o gizmo mostra o rótulo "S#" de cada sala (fora do Play também, até o
        /// próximo recarregamento de scripts).
        /// </summary>
        [ContextMenu("Relatório de salas e portas")]
        internal void LogRoomReport()
        {
            _isBaked = false;
            EnsureBaked();

            var builder = new System.Text.StringBuilder();
            builder.Append($"{name}: {RoomCount} sala(s)\n");
            for (int r = 0; r < RoomCount; r++)
            {
                builder.Append($"  S{r}: {_roomNodes[r].Length} nó(s), {_roomDoors[r].Length} porta(s) [");
                for (int k = 0; k < _roomDoors[r].Length; k++)
                {
                    if (k > 0)
                        builder.Append(", ");
                    builder.Append(_nodes[_roomDoors[r][k]].name);
                }

                builder.Append("]\n");
            }

            Debug.Log(builder.ToString(), this);
        }

        [ContextMenu("Coletar nós filhos")]
        internal void CollectChildNodes()
        {
            UnityEditor.Undo.RecordObject(this, "Coletar nós");
            _nodes.Clear();
            _nodes.AddRange(GetComponentsInChildren<NavNode>(includeInactive: true));
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"{name}: {_nodes.Count} nós coletados.", this);
        }

        // Só para o ladrilhamento do NavGraphPlacer, que grava o Undo antes.
        internal void SetShape(NodeShape shape) => _nodeShape = shape;

        /// <summary>
        /// Traz para dentro deste grafo todo NavNode da MESMA ARENA que não está debaixo de
        /// nenhum NavGraph (criado solto, arrastado para fora do Graph, colado na raiz da arena)
        /// e recoleta a lista. Com Undo: é o que o menu faz; a versão automática não grava.
        /// </summary>
        [ContextMenu("Adotar nós soltos da arena e coletar")]
        private void AdoptAndCollect()
        {
            int adopted = AdoptStrayNodes(recordUndo: true);
            CollectChildNodes();
            if (adopted > 0)
                Debug.Log($"{name}: {adopted} nó(s) solto(s) da arena agora são filhos deste grafo.", this);
        }

        /// <summary>
        /// Chamado pelo editor a cada mudança de hierarquia (ver NavGraphAutoCollect), com
        /// _autoCollectNodes ligado: adota os nós soltos da arena e recoleta SE a lista mudou.
        /// A comparação evita marcar as 9 instâncias da arena como modificadas a cada clique.
        /// </summary>
        internal void AutoCollect()
        {
            if (!_autoCollectNodes || Application.isPlaying)
                return;

            AdoptStrayNodes(recordUndo: false);

            NavNode[] children = GetComponentsInChildren<NavNode>(includeInactive: true);
            _nodes.RemoveAll(node => node == null);
            if (children.Length == _nodes.Count && new HashSet<NavNode>(children).SetEquals(_nodes))
                return;

            // A ordem dos filhos manda na ordem da lista, a mesma do "Coletar nós filhos".
            _nodes.Clear();
            _nodes.AddRange(children);
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }

        // Nós SOLTOS = na mesma arena (GraphArenaController, ou a raiz do objeto quando não há
        // arena) e sem NavGraph acima. Nó de outro grafo nunca é roubado. Nó que é parte de uma
        // instância de prefab não pode trocar de pai pela cena (o Unity não deixa reestruturar
        // instância): esse fica onde está, desenhando a própria área, até você abrir o prefab.
        private int AdoptStrayNodes(bool recordUndo)
        {
            GraphArenaController arena = GetComponentInParent<GraphArenaController>(true);
            Transform root = arena != null ? arena.transform : transform.root;
            int adopted = 0;

            foreach (NavNode node in root.GetComponentsInChildren<NavNode>(includeInactive: true))
            {
                if (node.GetComponentInParent<NavGraph>(true) != null)
                    continue;

                GameObject go = node.gameObject;
                if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go) && !UnityEditor.PrefabUtility.IsAddedGameObjectOverride(go))
                    continue;

                if (recordUndo)
                    UnityEditor.Undo.SetTransformParent(node.transform, transform, "Adotar nós soltos");
                else
                    node.transform.SetParent(transform, worldPositionStays: true);

                adopted++;
            }

            return adopted;
        }

        /// <summary>
        /// Liga automaticamente todo par de nós dentro de <see cref="_autoLinkMaxDistance"/> com
        /// linha de visão livre. Ponto de PARTIDA da autoria, não substituto dela: gera ligações
        /// redundantes em sala aberta (onde três nós em linha viram um triângulo) e você limpa na
        /// mão. Só adiciona — nunca remove uma ligação que você fez.
        /// </summary>
        [ContextMenu("Auto-ligar por linha de visão")]
        private void AutoLinkByLineOfSight()
        {
            _nodes.RemoveAll(node => node == null);

            int created = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    NavNode a = _nodes[i];
                    NavNode b = _nodes[j];

                    if (Vector3.Distance(a.Position, b.Position) > _autoLinkMaxDistance)
                        continue;

                    if (!IsSegmentClear(a.Position, b.Position))
                        continue;

                    if (a.IsNeighbor(b) || b.IsNeighbor(a))
                        continue;

                    UnityEditor.Undo.RecordObject(a, "Auto-ligar nós");
                    a.EditableNeighbors.Add(b);
                    UnityEditor.EditorUtility.SetDirty(a);
                    created++;
                }
            }

            Debug.Log($"{name}: {created} ligações criadas.", this);
        }

        [ContextMenu("Validar ligações")]
        private void ValidateLinks()
        {
            _nodes.RemoveAll(node => node == null);
            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            int blocked = 0;
            foreach (NavNode node in _nodes)
            {
                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null)
                    {
                        Debug.LogWarning($"{node.name}: ligação vazia na lista de vizinhos.", node);
                        continue;
                    }

                    if (!IsSegmentClear(node.Position, neighbor.Position))
                    {
                        Debug.LogWarning($"{node.name} -> {neighbor.name}: a ligação atravessa parede.", node);
                        blocked++;
                    }
                }
            }

            Debug.Log($"{name}: validação concluída — {blocked} ligação(ões) bloqueada(s) em {_nodes.Count} nós.", this);
        }
#endif

        private void OnDrawGizmos()
        {
            if (!_drawGizmos)
                return;

            if (_drawNodeRadii)
                DrawNodeRadii();

#if UNITY_EDITOR
            // As salas só existem depois do bake (Play, ou o menu "Relatório de salas e portas").
            if (_drawRoomLabels && _isBaked)
            {
                for (int r = 0; r < RoomCount; r++)
                    UnityEditor.Handles.Label(_roomCentroid[r] + Vector3.up * 2f, $"S{r}");
            }
#endif

            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null)
                        continue;

                    // Desenha cada aresta uma vez só quando ela é recíproca.
                    if (neighbor.IsNeighbor(node) && neighbor.GetInstanceID() < node.GetInstanceID())
                        continue;

                    bool clear = !_validateLinksInGizmos || IsSegmentClear(node.Position, neighbor.Position);
                    bool active = node.IsEnabled && neighbor.IsEnabled;

                    Gizmos.color = !clear ? Color.red
                        : active ? new Color(0.2f, 1f, 0.4f, 0.9f)
                        : new Color(0.4f, 0.4f, 0.4f, 0.6f);

                    Vector3 offset = Vector3.up * _linkProbeHeight;
                    Gizmos.DrawLine(node.Position + offset, neighbor.Position + offset);
                }
            }
        }

        // Roda fora do Play também, então resolve o raio pelo NÓ (RadiusOf) em vez de por índice
        // (NodeRadius): antes do bake os índices ainda não existem.
        private void DrawNodeRadii()
        {
            foreach (NavNode node in _nodes)
            {
                if (node == null)
                    continue;

                bool hasOverride = node.RadiusOverride > 0f;
                float radius = RadiusOf(node);

                if (!node.IsEnabled)
                {
                    Gizmos.color = new Color(0.4f, 0.4f, 0.4f, 0.4f);
                }
                else
                {
                    // O disco SEMPRE fica na cor do papel: o override não pode trocar a matiz,
                    // senão um primário com raio ajustado deixa de parecer primário e você perde
                    // a leitura do mapa de cima (que é o motivo de o disco ter cor por papel).
                    // A decisão de autoria continua visível pela OPACIDADE: disco cheio é
                    // override, disco apagado é o padrão do grafo.
                    Color color = node.IsDoor ? _primaryRadiusColor : node.IsPing ? _pingRadiusColor : _auxiliaryRadiusColor;
                    if (hasOverride)
                        color.a = Mathf.Min(1f, color.a * 1.6f);

                    Gizmos.color = color;
                }

                DrawArea(node, radius, 0.05f, 1);
            }
        }

        // ================================================================================
        // Desenho da área (cortada pelas paredes)
        // ================================================================================

        // Direções amostradas no contorno. 48 = a cada 7.5 graus, incluindo os múltiplos de 45 (os
        // cantos do quadrado caem exatamente numa amostra).
        private const int OutlineSamples = 48;

        private sealed class AreaOutline
        {
            public Vector3 Position;
            public float Radius;
            public NodeShape Shape;
            public float Time;
            public readonly float[] Reach = new float[OutlineSamples];
            public readonly float[] Boundary = new float[OutlineSamples];
        }

        // Um raycast por direção por nó é caro para refazer a cada repaint (e a memória desenha
        // todos os nós das 9 arenas em Play). Guardado por nó; refeito quando o nó ou o raio
        // mudam e, fora do Play, a cada 2 s (para acompanhar parede/móvel arrastado).
        private readonly Dictionary<NavNode, AreaOutline> _outlines = new Dictionary<NavNode, AreaOutline>();

        /// <summary>Desenha a área do nó <paramref name="index"/> (contorno, ou cheia com anéis).</summary>
        public void DrawNodeArea(int index, float height, int rings) =>
            DrawArea(_nodes[index], NodeRadius(index), height, rings);

        private void DrawArea(NavNode node, float radius, float height, int rings)
        {
            // Retângulo: sem corte pela parede (FindNodeAt também não corta), anéis encolhendo
            // para o centro do retângulo — o "cheio" da memória em Play.
            if (_nodeShape == NodeShape.Rectangle)
            {
                Vector3 center = AreaCenterOf(node);
                Vector2 half = HalfExtentsOf(node);
                int rectRings = Mathf.Max(1, rings);
                for (int ring = rectRings; ring > 0; ring--)
                    GraphGizmos.DrawGroundRect(center, half * ((float)ring / rectRings), height);
                return;
            }

            if (!_areasStopAtWalls)
            {
                if (rings <= 1)
                    GraphGizmos.DrawGroundArea(_nodeShape, node.Position, radius, height);
                else
                    GraphGizmos.DrawGroundAreaFilled(_nodeShape, node.Position, radius, rings, height);
                return;
            }

            AreaOutline outline = OutlineOf(node, radius);
            Vector3 origin = node.Position + Vector3.up * height;
            int count = Mathf.Max(1, rings);

            for (int ring = count; ring > 0; ring--)
            {
                float scale = (float)ring / count;
                Vector3 previous = Vector3.zero;
                for (int i = 0; i <= OutlineSamples; i++)
                {
                    int k = i % OutlineSamples;
                    float angle = k * Mathf.PI * 2f / OutlineSamples;
                    float reach = Mathf.Min(outline.Reach[k], outline.Boundary[k] * scale);
                    Vector3 point = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * reach;
                    if (i > 0)
                        Gizmos.DrawLine(previous, point);
                    previous = point;
                }
            }
        }

        private AreaOutline OutlineOf(NavNode node, float radius)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_outlines.TryGetValue(node, out AreaOutline cached)
                && cached.Position == node.Position && Mathf.Approximately(cached.Radius, radius) && cached.Shape == _nodeShape
                && (Application.isPlaying || now - cached.Time < 2f))
                return cached;

            AreaOutline outline = cached ?? new AreaOutline();
            outline.Position = node.Position;
            outline.Radius = radius;
            outline.Shape = _nodeShape;
            outline.Time = now;

            PhysicsScene physics = gameObject.scene.GetPhysicsScene();
            for (int k = 0; k < OutlineSamples; k++)
            {
                float angle = k * Mathf.PI * 2f / OutlineSamples;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                // Até onde a forma vai nesta direção: o raio no círculo; no quadrado (Chebyshev),
                // raio / o maior componente da direção.
                float boundary = _nodeShape == NodeShape.Square
                    ? radius / Mathf.Max(Mathf.Abs(direction.x), Mathf.Abs(direction.z))
                    : radius;

                outline.Boundary[k] = boundary;
                outline.Reach[k] = physics.Raycast(node.Position, direction, out RaycastHit hit, boundary, _wallLayer, QueryTriggerInteraction.Ignore)
                    ? hit.distance
                    : boundary;
            }

            _outlines[node] = outline;
            return outline;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Liga o "Coletar nós automaticamente" do <see cref="NavGraph"/> à hierarquia do editor. Um
    /// gancho estático, e não OnEnable no componente: fora do Play o Unity não chama OnEnable de
    /// um MonoBehaviour comum. Adiado para o próximo tick (delayCall) porque mexer na hierarquia
    /// DENTRO do evento de hierarquia dispararia o evento de novo no meio da coleta.
    /// Olha só o estágio aberto: no Prefab Mode, o prefab; fora dele, as cenas.
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    internal static class NavGraphAutoCollect
    {
        private static bool _queued;

        static NavGraphAutoCollect()
        {
            UnityEditor.EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            if (_queued || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            _queued = true;
            UnityEditor.EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            _queued = false;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            UnityEditor.SceneManagement.StageHandle stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle();
            foreach (NavGraph graph in stage.FindComponentsOfType<NavGraph>())
            {
                if (graph != null)
                    graph.AutoCollect();
            }
        }
    }
#endif
}
