using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Uma parte do mapa (uma sala, um corredor, um saguão) e QUANTO ela vale explorar.
    ///
    /// O ponto todo é o <see cref="_explorationBudget"/> ser um orçamento da REGIÃO, e não uma
    /// recompensa por nó: os nós dividem esse orçamento entre si. Sem isso, uma sala torta que
    /// precisa de 9 nós para ser descrita pagaria três vezes mais que um corredor reto de 3 nós
    /// — e o agente aprenderia a preferir a sala torta, que é uma preferência por DENSIDADE DE
    /// NÓS, não por exploração. A granularidade da sua autoria viraria função de recompensa
    /// sem querer.
    ///
    /// Com a divisão, você fica livre para colocar quantos nós forem necessários para descrever
    /// a geometria, e a decisão de "isto aqui vale mais a pena" continua explícita e num campo só.
    /// </summary>
    public class NavRegion : MonoBehaviour
    {
        // Quanto vale cobrir esta região INTEIRA, em unidades relativas. 1 é a referência;
        // 2 é "esta região vale o dobro". A conversão para recompensa é feita uma vez só, no
        // GraphRewardSystem — aqui você só declara a importância relativa.
        [SerializeField] private float _explorationBudget = 1f;

        [SerializeField] private Color _gizmoColor = new Color(0.2f, 0.8f, 1f);

        public float ExplorationBudget => Mathf.Max(0f, _explorationBudget);

        public Color GizmoColor => _gizmoColor;

#if UNITY_EDITOR
        /// <summary>
        /// Atalho de autoria: coloque a região como pai dos nós dela e use isto em vez de
        /// arrastar a referência em cada NavNode.
        /// </summary>
        [ContextMenu("Atribuir esta região aos nós filhos")]
        private void AssignToChildNodes()
        {
            NavNode[] nodes = GetComponentsInChildren<NavNode>(includeInactive: true);
            foreach (NavNode node in nodes)
            {
                UnityEditor.Undo.RecordObject(node, "Atribuir região");
                node.SetRegion(this);
                UnityEditor.EditorUtility.SetDirty(node);
            }

            Debug.Log($"{name}: região atribuída a {nodes.Length} nó(s).", this);
        }
#endif
    }
}
