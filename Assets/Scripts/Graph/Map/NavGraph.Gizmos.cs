using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    // Gizmos do grafo: arestas (verde livre, vermelha atravessa parede, cinza desativada) e áreas de chegada.
    public partial class NavGraph
    {
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

        // Roda fora do Play também: resolve o raio pelo NÓ (RadiusOf), pois antes do bake não há índices.
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
                    // O disco fica sempre na cor do papel; o override aparece só pela opacidade (cheio = override).
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

        // Direções amostradas no contorno (48 = a cada 7.5 graus; os cantos do quadrado caem numa amostra).
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

        // Cache do contorno por nó (um raycast por direção é caro por repaint); refeito quando nó ou raio mudam
        // e, fora do Play, a cada 2 s.
        private readonly Dictionary<NavNode, AreaOutline> _outlines = new Dictionary<NavNode, AreaOutline>();

        /// <summary>Desenha a área do nó <paramref name="index"/> (contorno, ou cheia com anéis).</summary>
        public void DrawNodeArea(int index, float height, int rings) =>
            DrawArea(_nodes[index], NodeRadius(index), height, rings);

        private void DrawArea(NavNode node, float radius, float height, int rings)
        {
            // Retângulo: sem corte pela parede (como o FindNodeAt); anéis encolhem para o centro (o "cheio" da memória).
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

                // Até onde a forma vai nesta direção: o raio no círculo; raio / maior componente no quadrado.
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
}
