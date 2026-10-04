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

        private float _target = AnimIdle;
        private bool _active;

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
            ResetEpisode();
        }

        public void ResetEpisode()
        {
            _target = AnimIdle;

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

        private void Update()
        {
            if (!_active)
                return;

            _animator.SetFloat(MoveSpeedHash, _target, _blendDampTime, Time.deltaTime);
        }
    }
}
