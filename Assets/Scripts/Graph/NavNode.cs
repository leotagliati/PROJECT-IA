using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Um ponto de interesse do mapa, posicionado À MÃO na cena (doorway, canto de sala,
    /// bifurcação de corredor). Guarda só o que é do nó: onde ele está, com quem ele conversa,
    /// se está ativo e a que área pertence. Nenhuma lógica de agente, recompensa ou busca mora
    /// aqui — quem consolida isso num grafo utilizável é o <see cref="NavGraph"/>.
    ///
    /// A ligação é declarativa e em linha reta: você arrasta os vizinhos no Inspector e a
    /// promessa (que você garante ao posicionar, e o gizmo do NavGraph confere) é que o
    /// segmento entre os dois não atravessa parede.
    /// </summary>
    [DisallowMultipleComponent]
    public class NavNode : MonoBehaviour
    {
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

        [Header("-----Região-----")]
        // A que parte do mapa este nó pertence. É uma referência, e não um id solto, porque o
        // ORÇAMENTO de exploração mora na região (ver NavRegion) e precisa de um lugar só —
        // replicar o valor em cada nó seria a forma mais fácil de dois nós da mesma sala
        // discordarem sobre quanto ela vale.
        //
        // Usos: (1) o nó paga orcamento_da_regiao / nós_da_regiao ao ser descoberto;
        //       (2) bônus ao entrar numa região inédita; (3) observação da fração da região
        //       atual já coberta.
        // Nós de doorway podem ficar de qualquer um dos dois lados; o que importa é que a
        // travessia troque de região em algum ponto.
        [SerializeField] private NavRegion _region;

        // Raio de chegada SÓ DESTE NÓ. Deixe em 0 (o normal) para usar o valor único do NavGraph
        // — o raio é o mecanismo que transforma posição contínua em índice discreto, e ele não
        // pode sumir, mas quase nunca precisa variar de nó para nó.
        //
        // Use override nos casos em que a geometria manda: um saguão enorme onde o nó deve
        // cobrir mais chão, ou um doorway apertado onde um raio grande invadiria a sala vizinha
        // e daria visita de graça sem o agente ter cruzado a porta.
        [SerializeField] private float _radiusOverride;

        // Atribuído pelo NavGraph no bake, não serializado: o índice é a posição no array de
        // adjacência daquele grafo, e serializar convidaria a dois nós carregarem o mesmo
        // número depois de um copy/paste.
        private int _index = -1;

        public int Index => _index;

        public Vector3 Position => transform.position;

        public IReadOnlyList<NavNode> Neighbors => _neighbors;

        /// <summary>Raio próprio, ou 0 quando o nó usa o padrão do grafo.</summary>
        public float RadiusOverride => _radiusOverride;

        public NavRegion Region => _region;

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

        internal void SetRegion(NavRegion region) => _region = region;

        /// <summary>Cor da região, para o gizmo. Magenta gritante quando falta região.</summary>
        public Color RegionColor => _region != null ? _region.GizmoColor : Color.magenta;

        // Só o pontinho. O raio de chegada é desenhado pelo NavGraph, que é quem conhece o valor
        // efetivo (padrão do grafo ou override) — resolver isso aqui exigiria o nó ser filho do
        // grafo, e amarrar a corretude do gizmo à hierarquia é o tipo de dependência que quebra
        // em silêncio no dia em que você reorganiza a cena.
        //
        // Efeito colateral útil: um nó que aparece como ponto SEM círculo e SEM ligações não
        // está na lista do grafo. É o sintoma de ter esquecido de rodar "Coletar nós filhos".
        private void OnDrawGizmos()
        {
            Gizmos.color = _isEnabled ? RegionColor : new Color(0.35f, 0.35f, 0.35f, 1f);
            Gizmos.DrawSphere(Position, 0.18f);
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
