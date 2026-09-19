using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O papel do nó. A separação existe porque um tipo só estava fazendo dois trabalhos com
    /// requisitos opostos: a MEMÓRIA quer poucos nós, cada um com significado, e a NAVEGAÇÃO
    /// quer muitos, formando uma malha que ensina os caminhos. Com um tipo só, adensar a malha
    /// para o agente não se perder mexia junto no que "explorado" significa.
    /// </summary>
    public enum NodeKind
    {
        /// <summary>
        /// PONTO DE VANTAGEM: parado aqui, a visão do agente cobre a sala/corredor. É ele que
        /// paga cobertura (o peso dele) e pode ser alvo da fronteira.
        /// Poucos por área (1 a 3) e com raio apertado, porque nele "visitado" precisa
        /// significar "estive lá e vi daqui".
        /// </summary>
        Primary,

        /// <summary>
        /// GUIA: não vale nada, não conta para cobertura, não é alvo de nada. Existe só para o
        /// agente ter uma âncora por perto e um caminho a seguir entre dois primários. Raio
        /// generoso de propósito — o papel dele é PEGAR o agente, não certificar presença.
        /// </summary>
        Auxiliary,
    }

    /// <summary>
    /// Um ponto de interesse do mapa, posicionado À MÃO na cena (doorway, canto de sala,
    /// bifurcação de corredor). Guarda só o que é do nó: onde ele está, com quem ele conversa,
    /// se está ativo e quanto vale. Nenhuma lógica de agente, recompensa ou busca mora aqui —
    /// quem consolida isso num grafo utilizável é o <see cref="NavGraph"/>.
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
        // Quanto vale DESCOBRIR este nó, em unidades relativas. 1 é a referência; 2 é "este
        // ponto vale o dobro". A conversão para recompensa acontece uma vez só, no
        // GraphRewardSystem (_nodeCoverageReward) — aqui você só declara a importância relativa.
        //
        // O peso mora no nó, e não numa região que o agrupa: cada ponto de vantagem é uma
        // decisão de autoria ("daqui se vê a sala"), então o valor dele é decidido no mesmo
        // lugar em que ele é colocado. O preço disso é que a DENSIDADE da malha vira função de
        // recompensa: dois primários de peso 1 na mesma sala pagam o dobro de um. Ao adensar
        // uma sala, divida o peso entre os nós dela para o total da sala não mudar.
        //
        // Só conta em nó PRIMÁRIO. Num auxiliar o campo é ignorado — ele não paga nada.
        [SerializeField] private float _explorationWeight = 1f;

        // Raio de chegada SÓ DESTE NÓ. Deixe em 0 (o normal): o NavGraph tem um padrão por PAPEL
        // (apertado para primário, generoso para auxiliar), e é lá que se calibra o mapa inteiro.
        //
        // Este campo é para a EXCEÇÃO que a geometria exige — um saguão enorme onde o nó deve
        // cobrir mais chão, ou um doorway apertado onde o raio invadiria a sala vizinha e daria
        // visita de graça sem o agente ter cruzado a porta. Se você se pegar preenchendo isto em
        // muitos nós do mesmo papel, o valor errado é o padrão do grafo, não o de cada nó.
        [SerializeField] private float _radiusOverride;

        // Atribuído pelo NavGraph no bake, não serializado: o índice é a posição no array de
        // adjacência daquele grafo, e serializar convidaria a dois nós carregarem o mesmo
        // número depois de um copy/paste.
        private int _index = -1;

        public int Index => _index;

        public NodeKind Kind => _kind;

        /// <summary>
        /// Atalho do teste que aparece em todo lugar: cobertura, peso, alvo da
        /// fronteira e recompensa de aresta são todos privilégio de nó primário.
        /// </summary>
        public bool IsPrimary => _kind == NodeKind.Primary;

        public Vector3 Position => transform.position;

        public IReadOnlyList<NavNode> Neighbors => _neighbors;

        /// <summary>Raio próprio, ou 0 quando o nó usa o padrão do grafo.</summary>
        public float RadiusOverride => _radiusOverride;

        /// <summary>Peso de exploração declarado na autoria. Nunca negativo; 0 é "não paga".</summary>
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

        public bool IsNeighbor(NavNode other) => _neighbors.Contains(other);

        // Cor do PONTO, por papel. Mesma matiz que o disco padrão do NavGraph desenha para cada
        // papel, então ponto e disco contam a mesma história — e um nó sem disco (esquecido
        // fora do grafo) ainda diz o que ele é.
        private static readonly Color PrimaryColor = new Color(0.25f, 0.75f, 0.95f, 1f);
        private static readonly Color AuxiliaryColor = new Color(0.55f, 0.60f, 0.72f, 1f);
        private static readonly Color DisabledColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        // Só o pontinho. O raio de chegada é desenhado pelo NavGraph, que é quem conhece o valor
        // efetivo (padrão do grafo ou override) — resolver isso aqui exigiria o nó ser filho do
        // grafo, e amarrar a corretude do gizmo à hierarquia é o tipo de dependência que quebra
        // em silêncio no dia em que você reorganiza a cena.
        //
        // Efeito colateral útil: um nó que aparece como ponto SEM círculo e SEM ligações não
        // está na lista do grafo. É o sintoma de ter esquecido de rodar "Coletar nós filhos".
        private void OnDrawGizmos()
        {
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
            Gizmos.color = _isEnabled ? PrimaryColor : DisabledColor;
            Gizmos.DrawSphere(Position, 0.18f * Mathf.Sqrt(Mathf.Max(0.25f, ExplorationWeight)));
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
