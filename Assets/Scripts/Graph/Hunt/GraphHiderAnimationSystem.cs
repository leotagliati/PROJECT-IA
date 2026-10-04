using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Traduz o passo do GraphHider no float moveSpeed do Animator, o mesmo contrato do PlayerAC (o hider de treino usa
    /// o corpo do PlayerDummy): blend tree 1D com 0 = idle, 1 = walk, 2 = run. Andando = walk; fugindo com fôlego = run.
    /// Mesmo molde do GraphAnimationSystem do seeker: o GraphHider chama Initialize/ResetEpisode/Tick; quem escreve no
    /// Animator é o Update.
    ///
    /// É cosmético de ponta a ponta: nada aqui pode alterar física, recompensa ou observação.
    /// </summary>
    public class GraphHiderAnimationSystem : MonoBehaviour
    {
        // Mesmos thresholds do PlayerAC: se mudar lá, muda aqui.
        private static readonly int MoveSpeedHash = Animator.StringToHash("moveSpeed");
        private const float AnimIdle = 0f;
        private const float AnimWalk = 1f;
        private const float AnimRun = 2f;

        [Header("-----Referências-----")]
        [SerializeField] private Animator _animator;

        [Tooltip("Tempo do damp entre um alvo e outro. 0 = troca seca.")]
        [SerializeField, Min(0f)] private float _blendDampTime = 0.1f;

        /// <summary>
        /// Animar 6 arenas com o Python conectado custa CPU e não ensina nada; por padrão desliga sozinho no treino.
        /// Ligue para assistir o treino.
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
                Debug.LogWarning($"{name}: nenhum Animator encontrado — animação do hider desligada.", this);
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

            // Sem damp: o respawn teleporta o hider, e um blend atravessando o corte o mostraria freando no spawn.
            if (_active)
                _animator.SetFloat(MoveSpeedHash, AnimIdle);
        }

        /// <summary>Chamado uma vez por step de física pelo GraphHider. Só grava o alvo.</summary>
        public void Tick(bool moving, bool running)
        {
            _target = !moving ? AnimIdle : running ? AnimRun : AnimWalk;
        }

        private void Update()
        {
            if (!_active)
                return;

            _animator.SetFloat(MoveSpeedHash, _target, _blendDampTime, Time.deltaTime);
        }
    }
}
