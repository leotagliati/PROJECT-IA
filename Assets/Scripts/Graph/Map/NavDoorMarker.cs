using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// PORTA VIRTUAL: um corte de sala onde não há batente (peça Door_Hole). O gerador "Gerar pelo NavMesh" (v7) do
    /// <see cref="NavGraph"/> trata isto como mais um vão: põe um nó Porta aqui e não deixa ligação de chão
    /// atravessar a caixa. Serve para partir uma sala grande demais para a visão concluir: o anel S24 (20 nós,
    /// cantos a ~18 m das portas) ficou em 0% na v5.1 até ser cortado em S24 + S26 por dois nós Porta postos à mão.
    ///
    /// A caixa é a do vão: X local = ao longo da "porta" (largura do corredor cortado), Z local = a travessia
    /// (profundidade). Gire o objeto no Y para alinhar. Na primeira geração o gerador cria um destes para cada nó
    /// Porta antigo que não tem batente perto, então em geral não é preciso pôr nenhum à mão.
    /// </summary>
    public class NavDoorMarker : MonoBehaviour
    {
        // Largura (X) e profundidade (Z) do vão, em metros; a altura só serve ao gizmo.
        [SerializeField] private Vector2 _size = new Vector2(3f, 1f);

        /// <summary>Meia-largura do vão (ao longo da porta).</summary>
        public float HalfWidth => Mathf.Max(0.1f, _size.x * 0.5f);

        /// <summary>Meia-profundidade (a travessia).</summary>
        public float HalfDepth => Mathf.Max(0.05f, _size.y * 0.5f);

        /// <summary>Direção planar ao longo da porta (X local no mundo).</summary>
        public Vector3 Along
        {
            get
            {
                Vector3 right = transform.right;
                right.y = 0f;
                return right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            }
        }

        internal void SetSize(Vector2 size) => _size = new Vector2(Mathf.Max(0.2f, size.x), Mathf.Max(0.1f, size.y));

        // Ciano-escuro, a cor das portas no NavGraph, em caixa de arame para não confundir com nó.
        private void OnDrawGizmos()
        {
            Vector3 along = Along;
            Vector3 normal = new Vector3(-along.z, 0f, along.x);
            Gizmos.color = new Color(0.25f, 0.75f, 0.95f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.LookRotation(normal, Vector3.up), Vector3.one);
            Gizmos.DrawWireCube(Vector3.up * 1.5f, new Vector3(_size.x, 3f, _size.y));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
