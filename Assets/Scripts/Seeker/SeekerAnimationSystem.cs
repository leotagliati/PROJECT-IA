using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Traduz o step do agente no float moveSpeed do Animator, o mesmo contrato do player:
    /// blend tree 1D com 0 = idle, 1 = walk, 2 = run. O alvo é decidido por duas perguntas que
    /// o manager já responde: está se movendo (Walk) e está enxergando o hider (Run).
    ///
    /// É cosmético de ponta a ponta: nada aqui pode alterar física, recompensa ou observação.
    /// </summary>
    public class SeekerAnimationSystem : MonoBehaviour
    {
        // Mesmos thresholds do PlayerAC: se mudar lá, muda aqui.
        private static readonly int MoveSpeedHash = Animator.StringToHash("moveSpeed");
        private const float AnimIdle = 0f;
        private const float AnimWalk = 1f;
        private const float AnimRun = 2f;

        [Header("-----Referências-----")]
        [SerializeField] private Animator _animator;

        /// <summary>
        /// Magnitude mínima da ação para valer como movimento. A política contínua raramente
        /// emite zero exato, então sem uma banda morta o agente nunca chega em Idle.
        /// </summary>
        [Header("-----Locomoção-----")]
        [SerializeField, Range(0f, 1f)] private float _moveThreshold = 0.1f;

        [Tooltip("Tempo do damp entre um alvo e outro. 0 = troca seca.")]
        [SerializeField, Min(0f)] private float _blendDampTime = 0.1f;

        /// <summary>
        /// Durante o treino são várias arenas em paralelo com timeScale alto, e animar todas
        /// custa CPU sem influenciar em nada o aprendizado. Por padrão o sistema se desliga
        /// sozinho quando há comunicador Python conectado.
        /// </summary>
        [Header("-----Treino-----")]
        [SerializeField] private bool _animateDuringTraining = false;

        private float _target = AnimIdle;
        private bool _active;

        public void Initialize()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            if (_animator == null)
            {
                Debug.LogWarning($"{name}: nenhum Animator encontrado — animação do seeker desligada.", this);
                return;
            }

            if (!_animateDuringTraining && Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
            {
                _animator.enabled = false;
                return;
            }

            _active = true;
            ResetEpisode();
        }

        public void ResetEpisode()
        {
            _target = AnimIdle;

            // Sem damp: o respawn teleporta o agente, e um blend de Run para Idle atravessando
            // o corte ficaria com o seeker "freando" no ponto de spawn.
            if (_active)
                _animator.SetFloat(MoveSpeedHash, AnimIdle);
        }

        /// <summary>
        /// Chamado uma vez por step, com a ação já montada. Recebe o movimento PEDIDO em vez de
        /// medir o deslocamento real porque a diferença entre os dois é justamente o caso de
        /// empurrar parede — e aí Walk é a leitura certa: o agente está tentando andar.
        ///
        /// Só grava o alvo; quem escreve no Animator é o Update. O step roda em cadência de
        /// física e pode parar de rodar de vez (fim de jogo), e o SetFloat com damp só converge
        /// se for chamado todo frame com deltaTime.
        /// </summary>
        public void Tick(Vector3 requestedMove, bool isSeeingHider)
        {
            Vector2 flat = new(requestedMove.x, requestedMove.z);
            bool moving = flat.sqrMagnitude >= _moveThreshold * _moveThreshold;

            // Ver o hider só vira Run se o agente também estiver se movendo — parado, Run seria
            // correr no lugar. Idle ganha.
            if (!moving)
                _target = AnimIdle;
            else
                _target = isSeeingHider ? AnimRun : AnimWalk;
        }

        private void Update()
        {
            if (!_active)
                return;

            _animator.SetFloat(MoveSpeedHash, _target, _blendDampTime, Time.deltaTime);
        }
    }
}
