using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// O monstro da v8 VENDO PEGADAS do alvo (<see cref="FreeFootprintTrail"/>), um por agente. Sem ver o alvo, a cada
    /// _scanInterval steps de física olha as pegadas mais novas: no alcance, no cone em volta de para onde a cabeça
    /// olha e com linha livre do olho até ela (parede esconde pegada). A fraca (agachado, ou quase sumindo) só de perto.
    /// A mais NOVA que viu fica lembrada (Has/Position/Heading/Intensity) até esmaecer, e vira:
    ///   - observação: onde está a pegada e para onde o alvo ia (o "seguir o rastro");
    ///   - estado de ALERTA no GraphLocomotion (pista nova = 9.5 m/s), pelo Manager;
    ///   - FOCO: com pegada fresca a exploração para de pagar e some da observação (FreeObservations/Manager).
    /// Nada paga ver ou seguir pegada: é informação, quem paga é a captura.
    /// </summary>
    public class FreeTrackSense : MonoBehaviour
    {
        [Header("-----Ver pegadas-----")]
        // Alcance (m). Menor que a visão do alvo: pegada é coisa pequena no chão.
        [SerializeField, Min(1f)] private float _viewDistance = 12f;

        // Abertura do cone (graus), o mesmo da visão dos pontos.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;

        // Pegada mais fraca que isto (0..1) só é vista a até _faintDistance.
        [SerializeField, Range(0f, 1f)] private float _faintIntensity = 0.35f;
        [SerializeField, Min(0f)] private float _faintDistance = 5f;

        // Abaixo disto a pegada lembrada é esquecida.
        [SerializeField, Range(0f, 1f)] private float _minIntensity = 0.05f;

        // Olhada no chão a cada N steps de física (5 = uma por decisão) e no máximo N raycasts por olhada.
        [SerializeField, Min(1)] private int _scanInterval = 5;
        [SerializeField, Min(1)] private int _maxRaycasts = 12;

        // Olho acima do PISO (m), como na FreeExplorationMemory.
        [SerializeField, Min(0.1f)] private float _eyeHeight = 2.5f;

        private FreeFootprintTrail _trail;
        private LayerMask _wallLayer;
        private float _floorY;
        private int _tick;
        private float _trackStrength;
        private float _trackLifetime;

        /// <summary>Viu uma pegada do alvo e ainda lembra dela (Position/Heading/Intensity valem só com isto).</summary>
        public bool HasTrack => Intensity > _minIntensity;

        public Vector3 Position { get; private set; }

        /// <summary>Para onde o alvo ia ao deixar a pegada (planar).</summary>
        public Vector3 Heading { get; private set; }

        public float Intensity => _trackLifetime > 0f
            ? _trackStrength * Mathf.Clamp01(1f - (Time.time - _trackTime) / _trackLifetime)
            : 0f;

        /// <summary>Achou uma pegada MAIS NOVA que a lembrada neste step (o Manager liga o alerta).</summary>
        public bool NewTrackThisStep { get; private set; }

        /// <summary>Pegadas novas achadas no episódio (Hunt/TracksSeen).</summary>
        public int EpisodeTracksSeen { get; private set; }

        private float _trackTime = float.NegativeInfinity;

        public void Configure(FreeFootprintTrail trail, LayerMask wallLayer, float floorY)
        {
            _trail = trail;
            _wallLayer = wallLayer;
            _floorY = floorY;
        }

        public void ResetEpisode()
        {
            _tick = 0;
            _trackStrength = 0f;
            _trackLifetime = 0f;
            _trackTime = float.NegativeInfinity;
            NewTrackThisStep = false;
            EpisodeTracksSeen = 0;
        }

        public void ClearStepFlags() => NewTrackThisStep = false;

        /// <summary>Um step de física. Vendo o alvo não olha o chão (a pegada não acrescenta nada).</summary>
        public void Tick(Vector3 position, Vector3 view, bool seeingTarget)
        {
            if (_trail == null || seeingTarget || _tick++ % _scanInterval != 0)
                return;

            var eye = new Vector3(position.x, _floorY + _eyeHeight, position.z);
            var forward = new Vector2(view.x, view.z);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector2.up;
            float cosHalf = Mathf.Cos(_viewAngle * 0.5f * Mathf.Deg2Rad);
            float now = _trail.Now;
            int raycasts = 0;

            // Da mais nova para a mais velha: a primeira vista é a melhor pista. Mais velha que a lembrada não serve.
            for (int i = 0; i < _trail.Count && raycasts < _maxRaycasts; i++)
            {
                FreeFootprint print = _trail.GetNewest(i);
                if (print.Time <= _trackTime)
                    break;

                float intensity = print.IntensityAt(now);
                if (intensity <= _minIntensity)
                    continue;

                var planar = new Vector2(print.Position.x - position.x, print.Position.z - position.z);
                float distance = planar.magnitude;
                float reach = intensity < _faintIntensity ? _faintDistance : _viewDistance;
                if (distance > reach || (distance > 1e-3f && Vector2.Dot(planar / distance, forward) < cosHalf))
                    continue;

                raycasts++;
                if (Physics.Linecast(eye, print.Position + Vector3.up * 0.1f, _wallLayer, QueryTriggerInteraction.Ignore))
                    continue;

                Position = print.Position;
                Heading = print.Heading;
                _trackTime = print.Time;
                _trackStrength = print.Strength;
                _trackLifetime = print.Lifetime;
                NewTrackThisStep = true;
                EpisodeTracksSeen++;
                return;
            }
        }

        // Gizmo marrom-claro: a pegada lembrada e para onde o alvo ia.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || !HasTrack)
                return;

            Gizmos.color = new Color(0.8f, 0.55f, 0.3f, 0.9f);
            Gizmos.DrawWireSphere(Position, 0.4f);
            Gizmos.DrawLine(Position, Position + Heading * 2f);
        }
    }
}
