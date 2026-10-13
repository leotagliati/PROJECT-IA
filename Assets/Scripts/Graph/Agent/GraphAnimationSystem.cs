using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Traduz o step do GraphExplorer no float moveSpeed do Animator, o mesmo contrato do SeekerAnimationSystem e do
    /// player: blend tree 1D com 0 = idle, 1 = walk, 2 = run. O alvo vem de duas perguntas que o manager já
    /// responde: está se movendo (Walk) e está em PERSEGUIÇÃO, vendo o alvo (Run). Patrulha e Alerta andam (Walk);
    /// só a perseguição corre, que é quando o monstro fica rápido (GraphLocomotion).
    ///
    /// É cosmético de ponta a ponta: nada aqui pode alterar física, recompensa ou observação.
    /// </summary>
    public class GraphAnimationSystem : MonoBehaviour
    {
        // Mesmos thresholds do PlayerAC/SeekerAC: se mudar lá, muda aqui.
        private static readonly int MoveSpeedHash = Animator.StringToHash("moveSpeed");
        private const float AnimIdle = 0f;
        private const float AnimWalk = 1f;
        private const float AnimRun = 2f;

        [Header("-----Referências-----")]
        [SerializeField] private Animator _animator;

        /// <summary>
        /// Magnitude mínima da ação para valer como movimento: a mesma zona morta do GraphLocomotion, para o
        /// monstro não andar parado nem ficar em Idle andando.
        /// </summary>
        [Header("-----Locomoção-----")]
        [SerializeField, Range(0f, 1f)] private float _moveThreshold = 0.1f;

        [Tooltip("Tempo do damp entre um alvo e outro. 0 = troca seca.")]
        [SerializeField, Min(0f)] private float _blendDampTime = 0.1f;

        /// <summary>
        /// Durante o treino são várias arenas em paralelo com timeScale alto, e animar todas custa CPU sem
        /// influenciar em nada o aprendizado. Por padrão o sistema se desliga sozinho quando há comunicador
        /// Python conectado.
        /// </summary>
        [Header("-----Treino-----")]
        [SerializeField] private bool _animateDuringTraining = false;

        [Header("-----Cabeça (v8)-----")]
        // O olhar da IA vira só a cabeça (GraphLocomotion.HeadYaw, até 60° do corpo), e com ele o cone de visão. Sem
        // isto o modelo olhava sempre reto enquanto via de lado. Vazio = o osso Head do Animator humanoide; sem osso,
        // a cabeça não vira. 0 = desligado.
        [SerializeField] private Transform _headBone;
        [SerializeField, Range(0f, 1f)] private float _headTurnWeight = 1f;

        // Suavização (s) do giro mostrado: o olhar da política muda a cada decisão e a cabeça tremeria junto.
        [SerializeField, Min(0f)] private float _headTurnSmoothing = 0.15f;

        private float _target = AnimIdle;
        private bool _active;
        private float _headYawTarget;
        private float _headYawShown;
        private float _headYawVelocity;

        public void Initialize()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            if (_animator == null)
            {
                Debug.LogWarning($"{name}: nenhum Animator encontrado — animação do monstro desligada.", this);
                return;
            }

            if (!_animateDuringTraining && Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
            {
                _animator.enabled = false;
                return;
            }

            _active = true;
            if (_headBone == null)
                _headBone = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Head) : FindHeadBone(_animator.transform);
            ResetEpisode();
        }

        // Rig genérico (o SeekerModel não tem Avatar): o primeiro osso com "head" no nome, fora pontas e luzes
        // ("HeadTop_End", "HeadLight"). Sem nenhum, a cabeça não vira; arraste o osso no campo.
        private static Transform FindHeadBone(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string lower = t.name.ToLowerInvariant();
                if (lower.Contains("head") && !lower.Contains("end") && !lower.Contains("top") && !lower.Contains("light"))
                    return t;
            }

            return null;
        }

        public void ResetEpisode()
        {
            _target = AnimIdle;
            _headYawTarget = 0f;
            _headYawShown = 0f;
            _headYawVelocity = 0f;

            // Sem damp: o respawn teleporta o agente, e um blend de Run para Idle atravessando o corte ficaria com
            // o monstro "freando" no ponto de spawn.
            if (_active)
                _animator.SetFloat(MoveSpeedHash, AnimIdle);
        }

        /// <summary>
        /// Chamado uma vez por step, com a ação já montada. Recebe o movimento PEDIDO em vez de medir o
        /// deslocamento real porque a diferença entre os dois é o caso de empurrar parede, e aí Walk/Run é a leitura
        /// certa: o monstro está tentando andar. Só grava o alvo; quem escreve no Animator é o Update.
        /// </summary>
        public void Tick(Vector3 requestedMove, GraphLocomotion.Awareness state)
        {
            Vector2 flat = new(requestedMove.x, requestedMove.z);
            bool moving = flat.sqrMagnitude >= _moveThreshold * _moveThreshold;

            // Perseguição só vira Run se ele também estiver se movendo: parado, Run seria correr no lugar.
            if (!moving)
                _target = AnimIdle;
            else
                _target = state == GraphLocomotion.Awareness.Chase ? AnimRun : AnimWalk;
        }

        /// <summary>
        /// Pela velocidade REAL do corpo (v8): a blend tree segue o que o corpo faz, não a ação pedida. Até a velocidade
        /// de andar vai de Idle a Walk (acelerando e freando o passo acompanha o corpo, sem deslizar parado); daí até a
        /// de correr mistura Walk e Run (o Alerta, entre as duas, fica no meio, em vez de andar a 9.5 m/s com o clipe de
        /// 7). Empurrando parede o corpo perde velocidade, e o passo encurta junto.
        /// </summary>
        /// <param name="speed">Velocidade do corpo (m/s), a do GraphLocomotion.Speed.</param>
        /// <param name="walkSpeed">Velocidade que casa com o clipe de andar (a da patrulha).</param>
        /// <param name="runSpeed">Velocidade que casa com o clipe de correr (a da perseguição).</param>
        public void TickBySpeed(float speed, float walkSpeed, float runSpeed)
        {
            if (speed <= walkSpeed || runSpeed <= walkSpeed)
                _target = walkSpeed > 0f ? Mathf.Clamp01(speed / walkSpeed) * AnimWalk : AnimIdle;
            else
                _target = Mathf.Lerp(AnimWalk, AnimRun, Mathf.Clamp01((speed - walkSpeed) / (runSpeed - walkSpeed)));
        }

        /// <summary>Giro da cabeça em relação ao corpo (graus, + = direita), o GraphLocomotion.HeadYaw. Só grava o alvo.</summary>
        public void SetHeadYaw(float yaw) => _headYawTarget = yaw;

        private void Update()
        {
            if (!_active)
                return;

            _animator.SetFloat(MoveSpeedHash, _target, _blendDampTime, Time.deltaTime);
        }

        // Depois do Animator, que reescreve a pose do osso todo frame: o giro entra por cima, em volta do eixo vertical.
        private void LateUpdate()
        {
            if (!_active || _headBone == null || _headTurnWeight <= 0f)
                return;

            _headYawShown = Mathf.SmoothDampAngle(_headYawShown, _headYawTarget, ref _headYawVelocity, _headTurnSmoothing);
            _headBone.rotation = Quaternion.AngleAxis(_headYawShown * _headTurnWeight, Vector3.up) * _headBone.rotation;
        }
    }
}
