using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Traduz o step do agente nos bools do Animator (isWalking / isRunning), os mesmos do
    /// player. Três estados, decididos por duas perguntas que o manager já responde: está
    /// enxergando o hider (Run) e está se movendo (Walk); nenhum dos dois, Idle.
    ///
    /// É cosmético de ponta a ponta: nada aqui pode alterar física, recompensa ou observação.
    /// </summary>
    public class SeekerAnimationSystem : MonoBehaviour
    {
        private static readonly int IsWalkingHash = Animator.StringToHash("isWalking");
        private static readonly int IsRunningHash = Animator.StringToHash("isRunning");

        [Header("-----Referências-----")]
        [SerializeField] private Animator _animator;

        /// <summary>
        /// Magnitude mínima da ação para valer como movimento. A política contínua raramente
        /// emite zero exato, então sem uma banda morta o agente nunca chega em Idle.
        /// </summary>
        [Header("-----Locomoção-----")]
        [SerializeField, Range(0f, 1f)] private float _moveThreshold = 0.1f;

        /// <summary>
        /// Durante o treino são várias arenas em paralelo com timeScale alto, e animar todas
        /// custa CPU sem influenciar em nada o aprendizado. Por padrão o sistema se desliga
        /// sozinho quando há comunicador Python conectado.
        /// </summary>
        [Header("-----Treino-----")]
        [SerializeField] private bool _animateDuringTraining = false;

        private bool _isWalking;
        private bool _isRunning;

        // Os bools só são reescritos quando mudam. Como o estado é recalculado todo step,
        // sem esse guarda seriam duas chamadas por step por agente só para reafirmar o que
        // já estava lá.
        private bool _stateSynced;

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
            _stateSynced = false;
            Apply(walking: false, running: false);
        }

        /// <summary>
        /// Chamado uma vez por step, com a ação já montada. Recebe o movimento PEDIDO em vez de
        /// medir o deslocamento real porque a diferença entre os dois é justamente o caso de
        /// empurrar parede — e aí Walk é a leitura certa: o agente está tentando andar.
        /// </summary>
        public void Tick(Vector3 requestedMove, bool isSeeingHider)
        {
            if (!_active)
                return;

            Vector2 flat = new(requestedMove.x, requestedMove.z);
            bool moving = flat.sqrMagnitude >= _moveThreshold * _moveThreshold;

            // Ver o hider só vira Run se o agente também estiver se movendo — parado, Run seria
            // correr no lugar. Idle ganha.
            bool running = moving && isSeeingHider;
            bool walking = moving && !running;

            Apply(walking, running);
        }

        private void Apply(bool walking, bool running)
        {
            if (!_active || (_stateSynced && walking == _isWalking && running == _isRunning))
                return;

            _isWalking = walking;
            _isRunning = running;
            _stateSynced = true;

            _animator.SetBool(IsWalkingHash, walking);
            _animator.SetBool(IsRunningHash, running);
        }
    }
}
