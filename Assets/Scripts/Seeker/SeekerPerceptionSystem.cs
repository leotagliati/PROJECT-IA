using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerPerceptionSystem : MonoBehaviour
    {
        // Alcance da leitura de paredes. Precisa ser da ordem do alcance de visão do hider:
        // com 2 contra 10, o agente avistava um alvo sem ter ideia de como chegar até ele.
        [SerializeField] private float _detectionRange = 5f;
        [SerializeField] private LayerMask _wallLayer;
        [SerializeField] private float _originHeightOffset = 0.1f;

        [Header("Seeker Vision")]
        [SerializeField] private float _visionRange = 10f;
        [SerializeField] private float _visionAngle = 90f;
        [SerializeField] private int _rayCount = 7;
        [SerializeField] private string _hiderTag = "Goal";
        [SerializeField] private bool _isSeeingHider = false;

        // Distância para considerar que o agente "chegou" à última posição conhecida.
        [SerializeField] private float _arrivalThreshold = 0.5f;

        private bool _hasSeenHider;
        private Vector3 _lastKnownHiderPosition;
        private float _closestWallProximity;

        // Audição
        private bool _hasHeardHider;
        private Vector3 _lastHeardHiderPosition;

        public bool IsSeeingHider => _isSeeingHider;

        // Combina visão e audição: retorna true se viu OU ouviu
        public bool HasSeenHider => _hasSeenHider || _hasHeardHider;

        // Prioriza a posição vista, se disponível; caso contrário, a ouvida
        public Vector3 LastKnownHiderPosition => _hasSeenHider ? _lastKnownHiderPosition : _lastHeardHiderPosition;

        public float[] WallProximities => _wallProximities;

        public float ClosestWallProximity => _closestWallProximity;

        /// <summary>
        /// Oito direções no referencial do MUNDO — o mesmo das ações (X/Z), então o agente não
        /// precisa aprender nenhuma rotação entre o que sente e o que faz. As diagonais são o que
        /// permite perceber aberturas (e não só obstáculos), que é o mínimo para navegar corredor.
        /// </summary>
        private static readonly Vector3[] Directions =
        {
            new Vector3( 0f, 0f,  1f),              // frente
            new Vector3( 1f, 0f,  1f).normalized,   // frente-direita
            new Vector3( 1f, 0f,  0f),              // direita
            new Vector3( 1f, 0f, -1f).normalized,   // trás-direita
            new Vector3( 0f, 0f, -1f),              // trás
            new Vector3(-1f, 0f, -1f).normalized,   // trás-esquerda
            new Vector3(-1f, 0f,  0f),              // esquerda
            new Vector3(-1f, 0f,  1f).normalized,   // frente-esquerda
        };

        // 0 = livre, ~1 = parede perto
        private readonly float[] _wallProximities = new float[Directions.Length];

        /// <summary>
        /// Único ponto de amostragem do mundo. Varre paredes e hider uma vez por step de física e
        /// guarda o resultado; observação e recompensa leem esse snapshot em vez de consultar o
        /// mundo por conta própria, então os dois enxergam a mesma coisa mesmo com Decision Period > 1.
        /// </summary>
        public void Tick()
        {
            ScanForWalls();
            ScanForHider();
        }

        public int DirectionCount => Directions.Length;

        private void ScanForWalls()
        {
            _closestWallProximity = 0f;

            for (int i = 0; i < Directions.Length; i++)
            {
                _wallProximities[i] = GetWallProximity(Directions[i]);
                _closestWallProximity = Mathf.Max(_closestWallProximity, _wallProximities[i]);
            }
        }

        /// <summary>
        /// Varre um cone de visão à frente do agente. Para cada raio, o hider só conta se for a PRIMEIRA coisa
        /// atingida — assim uma parede no caminho bloqueia a visão. Guarda o hider mais
        /// próximo entre os raios.
        /// </summary>
        private void ScanForHider()
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
        /// Método público chamado pela AudioTrap quando o agente ouve o hider.
        /// </summary>
        public void SetHeardHiderPosition(Vector3 position)
        {
            _hasHeardHider = true;
            _lastHeardHiderPosition = position;
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

            // Limpa também a audição
            _hasHeardHider = false;
            _lastHeardHiderPosition = Vector3.zero;
        }

        /// <summary>
        /// Chegou à última posição conhecida e o hider não está mais lá: esquece e volta a procurar.
        /// A transição é de memória, então mora aqui — não no cálculo de recompensa.
        /// </summary>
        public void ForgetIfArrived(Vector3 seekerPosition)
        {
            if (_isSeeingHider || !HasSeenHider)
                return;

            if (Vector3.Distance(seekerPosition, LastKnownHiderPosition) <= _arrivalThreshold)
                ForgetHider();
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

            // Marca a última posição conhecida (vista ou ouvida).
            if (HasSeenHider)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(LastKnownHiderPosition, 0.3f);
            }
        }
    }
}