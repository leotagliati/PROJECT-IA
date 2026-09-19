using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// A VISÃO do seeker sobre o hider: cone à frente + linha de visão livre de parede. É a
    /// única percepção do hider que não passa pelo grafo — e é LOCAL de propósito: fora do cone
    /// ou atrás de uma parede, o seeker não sabe onde ele está. O que fica é a MEMÓRIA da
    /// última posição em que o viu, que é o que uma pessoa também teria.
    ///
    /// O QUE O AGENTE RECEBE (bloco [16..20] da observação, que estava reservado para isto):
    ///   vendo (0/1), já viu neste episódio (0/1), direção X/Z + distância à ÚLTIMA POSIÇÃO
    ///   VISTA. Enquanto vê, a última posição é a atual; ao perder, ela congela — é para lá
    ///   que ele vai procurar.
    ///
    /// O QUE PAGA (GraphRewardSystem): um bônus ao AVISTAR (transição não-vendo -> vendo, com
    /// cooldown para não render piscando numa quina) e por METRO de aproximação enquanto vê.
    ///
    /// Uma instância por agente; Tick a cada step de física (como a memória e o ping), para a
    /// aquisição/perda de visão ser vista no step em que acontece.
    /// </summary>
    public class GraphHiderPerception : MonoBehaviour
    {
        [Header("-----Cone-----")]
        // Abertura TOTAL do cone, em graus. 100 = 50 para cada lado do frente do corpo, que gira
        // na direção do movimento (SeekerMovementSystem) — ele olha para onde anda.
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 100f;

        // Alcance, em metros. Da ordem de uma sala grande + corredor; acima disso a linha de
        // visão num escritório raramente é livre de qualquer jeito.
        [SerializeField, Min(1f)] private float _viewDistance = 15f;

        // Altura dos "olhos" e do ponto olhado, acima da posição de cada transform. Zero rasparia
        // no chão e o raio acusaria degrau como parede.
        [SerializeField] private float _eyeHeight = 0.5f;

        [Header("-----Avistar-----")]
        // Steps de física sem ver que precisam passar para uma nova aquisição de visão contar
        // como "avistou de novo" (e pagar de novo). 250 = 5 s. Sem isto, ficar numa quina
        // entrando e saindo do cone seria uma máquina de bônus.
        [SerializeField, Min(0)] private int _respotCooldownSteps = 250;

        [Header("-----Referências-----")]
        [SerializeField] private GraphHider _hider;

        private NavGraph _graph;
        private int _lastSeenStep = int.MinValue;
        private int _step;

        /// <summary>Vendo o hider agora (dentro do cone, com linha de visão livre).</summary>
        public bool IsSeeing { get; private set; }

        /// <summary>Já viu o hider neste episódio.</summary>
        public bool HasSeen { get; private set; }

        /// <summary>Última posição em que viu (a atual, enquanto vê). Válida só com HasSeen.</summary>
        public Vector3 LastSeenPosition { get; private set; }

        /// <summary>Distância planar ao hider AGORA. Válida só com IsSeeing.</summary>
        public float CurrentDistance { get; private set; }

        /// <summary>
        /// Avistou (não-vendo -> vendo, fora do cooldown) desde o último <see cref="ClearStepFlags"/>.
        /// </summary>
        public bool Spotted { get; private set; }

        public void Configure(NavGraph graph)
        {
            _graph = graph;

            if (_hider == null)
            {
                GraphArenaController arena = GetComponentInParent<GraphArenaController>();
                if (arena != null)
                    _hider = arena.GetComponentInChildren<GraphHider>(includeInactive: true);
            }
        }

        public void ResetEpisode()
        {
            IsSeeing = false;
            HasSeen = false;
            LastSeenPosition = Vector3.zero;
            CurrentDistance = 0f;
            _lastSeenStep = int.MinValue;
            _step = 0;
            ClearStepFlags();
        }

        public void ClearStepFlags() => Spotted = false;

        /// <summary>Chamar a cada step de física, com o transform do seeker.</summary>
        public void Tick(Transform seeker)
        {
            _step++;

            bool seeing = _hider != null && _hider.IsActive && CanSee(seeker, _hider.transform.position);

            if (seeing)
            {
                Vector3 delta = _hider.transform.position - seeker.position;
                CurrentDistance = new Vector2(delta.x, delta.z).magnitude;
                LastSeenPosition = _hider.transform.position;
                HasSeen = true;

                // Aquisição: não via, passou a ver, e faz tempo o bastante desde a última vez.
                if (!IsSeeing && _step - _lastSeenStep > _respotCooldownSteps)
                    Spotted = true;

                _lastSeenStep = _step;
            }

            IsSeeing = seeing;
        }

        private bool CanSee(Transform seeker, Vector3 hiderPosition)
        {
            Vector3 eye = seeker.position + Vector3.up * _eyeHeight;
            Vector3 target = hiderPosition + Vector3.up * _eyeHeight;
            Vector3 delta = target - eye;

            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            float distance = planar.magnitude;
            if (distance > _viewDistance || distance < 1e-3f)
                return false;

            Vector3 forward = new Vector3(seeker.forward.x, 0f, seeker.forward.z);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;

            if (Vector3.Angle(forward, planar) > _viewAngle * 0.5f)
                return false;

            // Só PAREDE bloqueia: é a mesma máscara que valida as ligações do grafo. Mobília não
            // entra (ainda) — quando tiver layer própria, some aqui.
            LayerMask walls = _graph != null ? _graph.WallLayer : (LayerMask)0;
            if (walls.value == 0)
                return true;

            return !Physics.Raycast(eye, delta.normalized, delta.magnitude, walls, QueryTriggerInteraction.Ignore);
        }

        // Cone em azul-claro (não é cor de nenhum outro gizmo); linha até o hider enquanto vê,
        // e um X na última posição vista quando não vê.
        private void OnDrawGizmosSelected()
        {
            Vector3 eye = transform.position + Vector3.up * _eyeHeight;
            Vector3 forward = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            Quaternion left = Quaternion.Euler(0f, -_viewAngle * 0.5f, 0f);
            Quaternion right = Quaternion.Euler(0f, _viewAngle * 0.5f, 0f);
            Gizmos.DrawLine(eye, eye + left * forward * _viewDistance);
            Gizmos.DrawLine(eye, eye + right * forward * _viewDistance);

            if (!Application.isPlaying)
                return;

            if (IsSeeing && _hider != null)
            {
                Gizmos.DrawLine(eye, _hider.transform.position + Vector3.up * _eyeHeight);
            }
            else if (HasSeen)
            {
                Vector3 p = LastSeenPosition + Vector3.up * _eyeHeight;
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, -0.4f), p + new Vector3(0.4f, 0f, 0.4f));
                Gizmos.DrawLine(p + new Vector3(-0.4f, 0f, 0.4f), p + new Vector3(0.4f, 0f, -0.4f));
            }
        }
    }
}
