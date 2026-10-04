using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Desenho compartilhado dos gizmos do grafo. As áreas são PLANAS (X/Z), não esferas,
    /// porque <see cref="NavGraph.FindNodeAt"/> mede distância planar: o gizmo tem que
    /// desenhar a regra, já que o raio é calibrado olhando para ele.
    /// </summary>
    public static class GraphGizmos
    {
        /// <summary>Contorno da área de chegada na forma que o grafo usa (regra e desenho não podem divergir).</summary>
        public static void DrawGroundArea(NodeShape shape, Vector3 center, float radius, float height = 0.05f, int segments = 32)
        {
            if (shape == NodeShape.Square)
                DrawGroundSquare(center, radius, height);
            else
                DrawGroundCircle(center, radius, height, segments);
        }

        /// <summary>Área preenchida (aproximada por contornos concêntricos) na forma do grafo.</summary>
        public static void DrawGroundAreaFilled(NodeShape shape, Vector3 center, float radius, int rings = 3, float height = 0.05f, int segments = 20)
        {
            if (radius <= 0f || rings < 1)
                return;

            for (int ring = rings; ring > 0; ring--)
                DrawGroundArea(shape, center, radius * ring / rings, height, segments);
        }

        /// <summary>
        /// Quadrado alinhado aos eixos, de lado 2 x <paramref name="radius"/> (o raio vira meia-aresta:
        /// trocar a forma sem mudar o número aumenta a área em ~27%).
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

        /// <summary>Retângulo alinhado aos eixos, com meia-largura <paramref name="half"/> em X (x) e Z (y).</summary>
        public static void DrawGroundRect(Vector3 center, Vector2 half, float height = 0.05f)
        {
            if (half.x <= 0f || half.y <= 0f)
                return;

            Vector3 o = new(center.x, center.y + height, center.z);

            Vector3 a = o + new Vector3(-half.x, 0f, -half.y);
            Vector3 b = o + new Vector3(half.x, 0f, -half.y);
            Vector3 c = o + new Vector3(half.x, 0f, half.y);
            Vector3 d = o + new Vector3(-half.x, 0f, half.y);

            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }

        /// <summary>Círculo no plano do chão; <paramref name="height"/> evita z-fighting com o piso.</summary>
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
    }
}
