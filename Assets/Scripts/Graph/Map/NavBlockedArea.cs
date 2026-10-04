using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Marca vermelha de chão livre onde o corpo do agente NÃO CABE (vão entre móveis, nicho
    /// fino, porta estreita). Criada pelo ladrilhamento do <see cref="NavGraphPlacer"/>, para
    /// todo chão ser um nó ou uma destas.
    /// NÃO é nó: fora do NavGraph ("Coletar nós filhos" só pega NavNode) e nada do treino a lê.
    /// Área vermelha onde se esperava passagem é problema de geometria, não do grafo.
    /// </summary>
    [DisallowMultipleComponent]
    public class NavBlockedArea : MonoBehaviour
    {
        // Tamanho total em X e Z (m), centrado no objeto, alinhado aos eixos do mundo.
        [SerializeField] private Vector2 _size = Vector2.one;

        // Vermelho = problema de geometria (mesmo sentido da aresta bloqueada do NavGraph).
        private static readonly Color BlockedColor = new Color(0.9f, 0.1f, 0.1f, 0.8f);

        public Vector2 Size => _size;

        internal void SetSize(Vector2 size) => _size = new Vector2(Mathf.Max(0f, size.x), Mathf.Max(0f, size.y));

        private void OnDrawGizmos()
        {
            // Contorno + X: cheio pareceria nó visitado de longe.
            Vector2 half = _size * 0.5f;
            Gizmos.color = BlockedColor;
            GraphGizmos.DrawGroundRect(transform.position, half, 0.04f);

            Vector3 o = transform.position + Vector3.up * 0.04f;
            Gizmos.DrawLine(o + new Vector3(-half.x, 0f, -half.y), o + new Vector3(half.x, 0f, half.y));
            Gizmos.DrawLine(o + new Vector3(-half.x, 0f, half.y), o + new Vector3(half.x, 0f, -half.y));
        }
    }
}
