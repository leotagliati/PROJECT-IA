using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// As PISTAS do monstro da v8, um por agente: pegada VISTA e barulho OUVIDO, numa memória só.
    ///   PEGADA  (<see cref="FreeFootprintTrail"/>) sem ver o alvo, a cada _scanInterval steps de física olha as pegadas
    ///           mais novas: no alcance, no cone em volta de para onde a cabeça olha e com linha livre do olho até ela
    ///           (parede esconde pegada). A fraca (agachado) dura pouco, mas é vista igual.
    ///   SOM     (<see cref="FreeNoise"/>) ouvido se o caminho NavMesh até ele cabe no raio do som. Diz ONDE, não para
    ///           onde o alvo ia (Heading zero). Vendo o alvo, som é ignorado.
    /// A pista mais NOVA fica lembrada (Has/Position/Heading/Intensity) até esmaecer, e vira:
    ///   - observação: onde está a pista e para onde o alvo ia (o "seguir o rastro");
    ///   - estado de ALERTA no GraphLocomotion (pista nova = 9.5 m/s), pelo Manager;
    ///   - FOCO: com pista fresca a exploração para de pagar e some da observação (FreeObservations/Manager).
    /// Som e pegada usam a MESMA memória e a mesma observação de propósito (11/10): som entrou com o treino em
    /// andamento, e uma entrada nova no cérebro obrigaria a treinar do zero.
    /// Nada paga ver pegada ou ouvir: é informação, quem paga é a captura.
    /// </summary>
    public class FreeTrackSense : MonoBehaviour
    {
        [Header("-----Ver pegadas-----")]
        // Alcance (m). Menor que a visão do alvo: pegada é coisa pequena no chão. Vale para TODA pegada, fraca ou
        // forte (11/10; antes a mais fraca que 0.35 só era vista a até 5 m): agachar deixa pegada fraca e curta, não
        // invisível. Quem esconde é a parede (a linha até a pegada), não a distância.
        [SerializeField, Min(1f)] private float _viewDistance = 12f;

        // Abertura do cone (graus), o mesmo da visão dos pontos.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;

        // Abaixo disto a pista lembrada é esquecida.
        [SerializeField, Range(0f, 1f)] private float _minIntensity = 0.05f;

        // Olhada no chão a cada N steps de física (5 = uma por decisão) e no máximo N raycasts por olhada.
        [SerializeField, Min(1)] private int _scanInterval = 5;
        [SerializeField, Min(1)] private int _maxRaycasts = 12;

        // Olho acima do PISO (m), como na FreeExplorationMemory.
        [SerializeField, Min(0.1f)] private float _eyeHeight = 2.5f;

        [Header("-----Ouvir (FreeNoise)-----")]
        // Força (0..1) e duração (s) da pista de um som ouvido. Como a pegada de corrida (1, 12 s), mas mais curta:
        // som é um instante, quem faz barulho já saiu dali.
        [SerializeField, Range(0f, 1f)] private float _noiseStrength = 1f;
        [SerializeField, Min(0.1f)] private float _noiseLifetime = 8f;

        // Até onde (m) procura o NavMesh debaixo do som (item que caiu em cima de móvel, armadilha na parede) e do
        // próprio monstro (encostado na parede ele fica fora da borda do NavMesh).
        [SerializeField, Min(0.1f)] private float _noiseSnap = 3f;

        private FreeFootprintTrail _trail;
        private LayerMask _wallLayer;
        private float _floorY;
        private int _tick;
        private float _trackStrength;
        private float _trackLifetime;
        private float _trackTime = float.NegativeInfinity;

        // Onde o monstro estava no último step e se via o alvo: o som pode chegar fora do FixedUpdate (jogo).
        private Vector3 _listener;
        private bool _hasListener;
        private bool _seeingTarget;

        // Som ouvido entre dois steps: vira NewTrackThisStep no próximo Tick, ANTES de o Manager conferir o alerta (se
        // ligasse direto, um som chegando depois do Tick seria apagado pelo ClearStepFlags sem nunca dar Alerta).
        private bool _heardPending;

        // Criado no Awake: NavMeshPath no inicializador de campo de MonoBehaviour dá UnityException (e fica nulo).
        private NavMeshPath _path;

        /// <summary>Tem uma pista (pegada ou som) e ainda lembra dela (Position/Heading/Intensity valem só com isto).</summary>
        public bool HasTrack => Intensity > _minIntensity;

        public Vector3 Position { get; private set; }

        /// <summary>Para onde o alvo ia ao deixar a pegada (planar). Zero para som.</summary>
        public Vector3 Heading { get; private set; }

        public float Intensity => _trackLifetime > 0f
            ? _trackStrength * Mathf.Clamp01(1f - (Time.time - _trackTime) / _trackLifetime)
            : 0f;

        /// <summary>Achou uma pista MAIS NOVA que a lembrada neste step (o Manager liga o alerta).</summary>
        public bool NewTrackThisStep { get; private set; }

        /// <summary>Pegadas novas achadas no episódio (Hunt/TracksSeen).</summary>
        public int EpisodeTracksSeen { get; private set; }

        /// <summary>Sons ouvidos no episódio (Hunt/NoisesHeard).</summary>
        public int EpisodeNoisesHeard { get; private set; }

        /// <summary>Um som virou pista neste step (o Manager registra o evento "som" na FreeEventLog).</summary>
        public bool HeardThisStep { get; private set; }

        private bool _heardEventPending;

        private void Awake() => _path = new NavMeshPath();

        private void OnEnable() => FreeNoise.Emitted += Hear;

        private void OnDisable() => FreeNoise.Emitted -= Hear;

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
            EpisodeNoisesHeard = 0;
            _heardPending = false;
            _heardEventPending = false;
            HeardThisStep = false;

            // Depois do respawn a posição guardada é a do episódio anterior: só ouve de novo depois do 1º Tick.
            _hasListener = false;
        }

        public void ClearStepFlags()
        {
            NewTrackThisStep = false;
            HeardThisStep = false;
        }

        /// <summary>
        /// Pista entregue por outro sentido (o clone vendo o alvo, v9): onde e para onde ia. Mesmo caminho do som: vira
        /// NewTrackThisStep no próximo Tick (Alerta, foco na caça, observação [30..35]).
        /// </summary>
        public void Report(Vector3 position, Vector3 heading, float strength, float lifetime)
        {
            Position = position;
            heading.y = 0f;
            Heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.zero;
            _trackTime = Time.time;
            _trackStrength = Mathf.Clamp01(strength);
            _trackLifetime = Mathf.Max(0.1f, lifetime);
            _heardPending = true;
        }

        /// <summary>Um step de física. Vendo o alvo não olha o chão (a pegada não acrescenta nada).</summary>
        public void Tick(Vector3 position, Vector3 view, bool seeingTarget)
        {
            _listener = position;
            _hasListener = true;
            _seeingTarget = seeingTarget;
            if (_heardPending)
            {
                _heardPending = false;
                NewTrackThisStep = true;
            }

            if (_heardEventPending)
            {
                _heardEventPending = false;
                HeardThisStep = true;
            }

            if (_trail == null || seeingTarget || _tick++ % _scanInterval != 0)
                return;

            var eye = new Vector3(position.x, _floorY + _eyeHeight, position.z);
            var forward = new Vector2(view.x, view.z);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector2.up;
            float cosHalf = Mathf.Cos(_viewAngle * 0.5f * Mathf.Deg2Rad);
            float now = _trail.Now;
            int raycasts = 0;

            // Da mais nova para a mais velha: a primeira vista é a melhor pista. Mais velha que a lembrada (pegada ou
            // som) não serve.
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
                if (distance > _viewDistance || (distance > 1e-3f && Vector2.Dot(planar / distance, forward) < cosHalf))
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

        // Ouvido = o caminho NavMesh até o som cabe no raio dele. O corte em linha reta vem antes e é o que descarta
        // o som das OUTRAS arenas do treino (todas no mesmo mundo, a ~150 m umas das outras) sem calcular caminho.
        private void Hear(Vector3 source, float radius)
        {
            if (!_hasListener || _seeingTarget)
                return;

            var straight = new Vector2(source.x - _listener.x, source.z - _listener.z);
            if (straight.sqrMagnitude > radius * radius)
                return;

            if (!NavMesh.SamplePosition(source, out NavMeshHit sound, _noiseSnap, NavMesh.AllAreas)
                || !NavMesh.SamplePosition(_listener, out NavMeshHit self, _noiseSnap, NavMesh.AllAreas)
                || !NavMesh.CalculatePath(self.position, sound.position, NavMesh.AllAreas, _path)
                || _path.status != NavMeshPathStatus.PathComplete)
                return;

            Vector3[] corners = _path.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length && length <= radius; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            if (length > radius)
                return;

            Position = sound.position;
            Heading = Vector3.zero;
            _trackTime = Time.time;
            _trackStrength = _noiseStrength;
            _trackLifetime = _noiseLifetime;
            _heardPending = true;
            _heardEventPending = true;
            EpisodeNoisesHeard++;
        }

        // Gizmo marrom-claro: a pista lembrada e para onde o alvo ia.
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
