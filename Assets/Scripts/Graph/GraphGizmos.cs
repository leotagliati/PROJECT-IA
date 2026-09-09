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
        /// Contorno da área de chegada, na forma que o grafo usa. Todo desenho de área passa por
        /// aqui para que mudar a forma no NavGraph mude o gizmo junto — a regra e o desenho dela
        /// não podem divergir.
        /// </summary>
        public static void DrawGroundArea(NodeShape shape, Vector3 center, float radius, float height = 0.05f, int segments = 32)
        {
            if (shape == NodeShape.Square)
                DrawGroundSquare(center, radius, height);
            else
                DrawGroundCircle(center, radius, height, segments);
        }

        /// <summary>Versão preenchida (aproximada) da área, na forma do grafo.</summary>
        public static void DrawGroundAreaFilled(NodeShape shape, Vector3 center, float radius, int rings = 3, float height = 0.05f, int segments = 20)
        {
            if (radius <= 0f || rings < 1)
                return;

            for (int ring = rings; ring > 0; ring--)
                DrawGroundArea(shape, center, radius * ring / rings, height, segments);
        }

        /// <summary>
        /// Quadrado alinhado aos eixos, de lado 2 x <paramref name="radius"/>. O mesmo número
        /// que serve de raio no círculo vira MEIA-ARESTA aqui — então trocar a forma sem mexer
        /// no número aumenta a área em ~27% e estica o alcance da diagonal em 41%.
        /// </summary>
        public static void DrawGroundSquare(Vector3 center, float radius, float height = 0.05f)
        {
            if (radius <= 0f)
                return;

            Vector3 o = new(center.x, center.y + height, center.z);

            Vector3 a = o + new Vector3(-radius, 0f, -radius);
            Vector3 b = o + new Vector3(radius, 0f, -radius);
            Vector3 c = o + new Vector3(radius, 0f, radius);
            Vector3 d = o + new Vector3(-radius, 0f, radius);

            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }

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
