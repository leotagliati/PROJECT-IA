using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Marca VERMELHA de chão livre onde o corpo do agente NÃO CABE: vão estreito entre móveis,
    /// nicho mais fino que o corpo, sala cuja porta é estreita demais. Criada pelo ladrilhamento
    /// do <see cref="NavGraphPlacer"/> (menu 9) para o mapa inteiro aparecer coberto: todo chão
    /// é ou um nó (onde o agente vai) ou uma destas (onde ele não vai).
    ///
    /// NÃO é nó: não tem índice, ligação nem entra no NavGraph (o "Coletar nós filhos" só pega
    /// NavNode). É só informação de autoria — nada do treino lê isto. Se uma área vermelha
    /// aparece onde você esperava passagem, o problema é de geometria (móvel no caminho, porta
    /// estreita), não do grafo.
    /// </summary>
    [DisallowMultipleComponent]
    public class NavBlockedArea : MonoBehaviour
    {
        // Tamanho total em X e Z, em metros, centrado no objeto, alinhado aos eixos do mundo.
        [SerializeField] private Vector2 _size = Vector2.one;

        // Vermelho = "problema de geometria", o mesmo significado da aresta bloqueada do NavGraph
        // e do X do NavGraphPlacer.
        private static readonly Color BlockedColor = new Color(0.9f, 0.1f, 0.1f, 0.8f);

        public Vector2 Size => _size;

        internal void SetSize(Vector2 size) => _size = new Vector2(Mathf.Max(0f, size.x), Mathf.Max(0f, size.y));

        private void OnDrawGizmos()
        {
            // Contorno + um X: cheio (anéis) ficaria parecendo nó visitado de longe.
            Vector2 half = _size * 0.5f;
            Gizmos.color = BlockedColor;
            GraphGizmos.DrawGroundRect(transform.position, half, 0.04f);

            Vector3 o = transform.position + Vector3.up * 0.04f;
            Gizmos.DrawLine(o + new Vector3(-half.x, 0f, -half.y), o + new Vector3(half.x, 0f, half.y));
            Gizmos.DrawLine(o + new Vector3(-half.x, 0f, half.y), o + new Vector3(half.x, 0f, -half.y));
        }
    }
}
