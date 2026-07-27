using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerPerceptionSystem : MonoBehaviour
    {
        [SerializeField] private float _detectionRange = 2f;
        [SerializeField] private LayerMask _wallLayer;
        [SerializeField] private float _originHeightOffset = 0.1f;

        [Header("Seeker Vision")]
        [SerializeField] private float _visionRange = 10f;
        [SerializeField] private float _visionAngle = 90f;
        [SerializeField] private int _rayCount = 7;
        [SerializeField] private string _hiderTag = "Goal";
        [SerializeField] private bool _isSeeingHider = false;

        private bool _hasSeenHider;
        private Vector3 _lastKnownHiderPosition;

        public bool IsSeeingHider => _isSeeingHider;

        public bool HasSeenHider => _hasSeenHider;

        public Vector3 LastKnownHiderPosition => _lastKnownHiderPosition;

        private static readonly Vector3[] Directions =
        {
        Vector3.forward, // frente
        Vector3.back,    // trás
        Vector3.left,    // esquerda
        Vector3.right,   // direita
    };

        // 0 = livre, ~1 = parede perto


        public void GetWallProximities(float[] results)
        {
            for (int i = 0; i < Directions.Length; i++)
                results[i] = GetWallProximity(Directions[i]);
        }

        public int DirectionCount => Directions.Length;

        /// <summary>
        /// Varre um cone de visão à frente do agente. Para cada raio, o hider só conta se for a PRIMEIRA coisa
        /// atingida — assim uma parede no caminho bloqueia a visão. Guarda o hider mais
        /// próximo entre os raios.
        /// </summary>
        public void ScanForHider()
        {
            _isSeeingHider = false;

            Vector3 origin = transform.position + Vector3.up * _originHeightOffset;
            float bestDistance = float.MaxValue;
            Vector3 bestPosition = Vector3.zero;

            for (int i = 0; i < _rayCount; i++)
            {
                Vector3 dir = ConeDirection(i);

                if (Physics.Raycast(origin, dir, out RaycastHit hit, _visionRange)
                    && hit.collider.CompareTag(_hiderTag)
                    && hit.distance < bestDistance)
                {
                    _isSeeingHider = true;
                    bestDistance = hit.distance;
                    bestPosition = hit.collider.transform.position;
                }
            }

            if (_isSeeingHider)
            {
                _hasSeenHider = true;
                _lastKnownHiderPosition = bestPosition;
            }
        }

        // Direção do i-ésimo raio do cone, distribuído simetricamente em torno de forward.
        private Vector3 ConeDirection(int index)
        {
            if (_rayCount <= 1)
                return transform.forward;

            float half = _visionAngle * 0.5f;
            float step = _visionAngle / (_rayCount - 1);
            float angle = -half + step * index;
            return Quaternion.AngleAxis(angle, Vector3.up) * transform.forward;
        }

        /// <summary>
        /// Esquece a última posição conhecida (HasSeenHider volta a false). Chamar quando o
        /// agente chega ao ponto e o hider não está mais lá, forçando uma nova busca.
        /// </summary>
        public void ForgetHider()
        {
            _isSeeingHider = false;
            _hasSeenHider = false;
            _lastKnownHiderPosition = Vector3.zero;
        }

        private float GetWallProximity(Vector3 direction)
        {
            Vector3 origin = transform.position + Vector3.up * _originHeightOffset;

            if (Physics.Raycast(origin, direction, out RaycastHit hit, _detectionRange, _wallLayer))
                return 1f - (hit.distance / _detectionRange);

            return 0f;
        }

        public void ResetHiderMemory() => ForgetHider();

        private void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position + Vector3.up * _originHeightOffset;
            foreach (var dir in Directions)
            {
                float prox = Application.isPlaying ? GetWallProximity(dir) : 0f;
                Gizmos.color = Color.Lerp(Color.green, Color.red, prox);
                Gizmos.DrawLine(origin, origin + dir * _detectionRange);
            }

            // Cone de visão do hider (ciano = vendo, cinza = procurando).
            Gizmos.color = _isSeeingHider ? Color.cyan : Color.gray;
            for (int i = 0; i < _rayCount; i++)
                Gizmos.DrawLine(origin, origin + ConeDirection(i) * _visionRange);

            // Marca a última posição conhecida.
            if (_hasSeenHider)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(_lastKnownHiderPosition, 0.3f);
            }
        }
    }
}