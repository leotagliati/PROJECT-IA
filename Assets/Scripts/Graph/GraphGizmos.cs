using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Desenho compartilhado dos gizmos do grafo.
    ///
    /// O círculo é PLANO (no plano X/Z), e não uma esfera, porque é isso que o código testa:
    /// <see cref="NavGraph.FindNodeAt"/> mede distância planar. Uma esfera de arame desenharia
    /// uma área de chegada que não existe — e gizmo que mente sobre a regra é pior que gizmo
    /// nenhum, porque você calibra o raio olhando pra ele.
    /// </summary>
    public static class GraphGizmos
    {
        /// <summary>
        /// Círculo no plano do chão. A altura é um empurrãozinho para cima para não brigar em
        /// z-fighting com o piso quando o nó está em y = 0.
        /// </summary>
        public static void DrawGroundCircle(Vector3 center, float radius, float height = 0.05f, int segments = 32)
        {
            if (radius <= 0f || segments < 3)
                return;

            Vector3 origin = new(center.x, center.y + height, center.z);
            float step = Mathf.PI * 2f / segments;

            Vector3 previous = origin + new Vector3(radius, 0f, 0f);

            for (int i = 1; i <= segments; i++)
            {
                float angle = step * i;
                Vector3 current = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        /// <summary>
        /// Aproximação de disco CHEIO por anéis concêntricos. O Gizmos não tem primitiva de
        /// disco preenchido, e o contorno sozinho some na vista de cima do mapa inteiro — que é
        /// justamente a vista em que a memória de nós precisa ser lida de relance.
        /// </summary>
        public static void DrawGroundDisc(Vector3 center, float radius, int rings = 3, float height = 0.05f, int segments = 20)
        {
            for (int ring = rings; ring > 0; ring--)
                DrawGroundCircle(center, radius * ring / rings, height, segments);
        }
    }
}
