using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// O corpo que a visão do monstro mira no alvo (GraphHiderPerception), mais fino que o collider de física: num canto,
    /// a borda do CharacterController aparecendo não entrega o jogador. Só a largura é daqui; altura e centro seguem o
    /// collider (agachar continua valendo). Não é um Collider de propósito: não entra em raycast, trigger nem colisão.
    /// </summary>
    public class TargetVisionBody : MonoBehaviour
    {
        // Raio (m, no mundo) que o monstro enxerga. O CharacterController do PlayerDummy tem 0.52 (0.26 × escala 2).
        [SerializeField, Min(0.01f)] private float _radius = 0.15f;

        // De onde vêm altura e centro. Vazio = o collider deste objeto ou do pai mais próximo.
        [SerializeField] private Collider _body;

        public bool IsUsable => isActiveAndEnabled && Body != null && Body.enabled;

        public Bounds Bounds
        {
            get
            {
                Bounds bounds = Body.bounds;
                Vector3 extents = bounds.extents;
                bounds.extents = new Vector3(Mathf.Min(_radius, extents.x), extents.y, Mathf.Min(_radius, extents.z));
                return bounds;
            }
        }

        private Collider Body
        {
            get
            {
                if (_body == null)
                    _body = GetComponentInParent<Collider>();
                return _body;
            }
        }

        // Azul-claro, o da visão: o cilindro que os raios do monstro miram.
        private void OnDrawGizmosSelected()
        {
            if (Body == null || !Body.enabled)
                return;

            Bounds bounds = Bounds;
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.9f);
            Vector3 top = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            Vector3 bottom = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            const int segments = 16;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = Quaternion.Euler(0f, i * 360f / segments, 0f) * Vector3.forward * bounds.extents.x;
                Vector3 b = Quaternion.Euler(0f, (i + 1) * 360f / segments, 0f) * Vector3.forward * bounds.extents.x;
                Gizmos.DrawLine(top + a, top + b);
                Gizmos.DrawLine(bottom + a, bottom + b);
                if (i % 4 == 0)
                    Gizmos.DrawLine(top + a, bottom + a);
            }
        }
    }
}
