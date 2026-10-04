using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerStaticVisual : MonoBehaviour
    {
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        [Header("-----Referências-----")]
        [SerializeField] private Renderer _displayRenderer;
        private Light[] _lights;

        [SerializeField] private SeekerChaseState _chaseState;

        [Header("-----Patrulha-----")]
        [SerializeField, ColorUsage(false, true)] private Color _patrolTint = Color.white;
        [SerializeField] private Color _patrolLightColor = new(1f, 0f, 0.11f);

        [Header("-----Perseguição-----")]
        [SerializeField, ColorUsage(false, true)] private Color _chaseTint = new(0.4f, 0.8f, 2f);
        [SerializeField] private Color _chaseLightColor = Color.white;

        // Só o GraphExplorer usa (SetState): ouviu um ping ou perdeu o alvo de vista há pouco. O Seeker antigo não tem
        // esse estado e nunca chama SetState, então para ele nada muda.
        [Header("-----Alerta (GraphExplorer)-----")]
        [SerializeField, ColorUsage(false, true)] private Color _alertTint = new(1f, 0.8f, 0.1f);
        [SerializeField] private Color _alertLightColor = new(1f, 0.8f, 0.1f);

        [Header("-----Treino-----")]
        [SerializeField] private bool _animateDuringTraining = false;

        private enum State { Patrol, Alert, Chase }

        private MaterialPropertyBlock _block;
        private bool _active;
        private State _state;

        // Estado vindo de fora (GraphExplorerManager.SetState); sem ele vale o SeekerChaseState, como antes.
        private bool _hasExternalState;
        private State _externalState;

        public void Initialize()
        {
            if (!_animateDuringTraining && Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
                return;

            if (_chaseState == null)
                _chaseState = GetComponentInParent<SeekerChaseState>();

            if (_lights == null || _lights.Length == 0)
                _lights = GetComponentsInChildren<Light>();

            if (_chaseState == null)
            {
                Debug.LogWarning($"{name}: SeekerChaseState não encontrado — canal da cabeça desligado.", this);
                return;
            }

            // Campo vazio: a tela é o renderer do modelo cujo material tem _Tint (o StaticShaderMAT). O renderer fica
            // dentro do .fbx, então referenciar à mão no prefab é frágil; achar pelo material não depende disso.
            if (_displayRenderer == null)
                _displayRenderer = FindTintRenderer();

            if (_displayRenderer == null)
                Debug.LogWarning($"{name}: nenhum renderer com _Tint — só os lights vão trocar de cor.", this);

            _block = new MaterialPropertyBlock();
            _active = true;

            _state = State.Patrol;
            Apply(State.Patrol);
        }

        /// <summary>
        /// Estado do GraphExplorer (vendo = perseguição, pista recente = alerta, senão patrulha). A partir da primeira
        /// chamada, ele substitui o SeekerChaseState na cor: o vermelho fica preso ao "estou vendo", sem a espera dele.
        /// </summary>
        public void SetState(bool chasing, bool alert)
        {
            _hasExternalState = true;
            _externalState = chasing ? State.Chase : alert ? State.Alert : State.Patrol;
        }

        private void Update()
        {
            if (!_active)
                return;

            State state = _hasExternalState ? _externalState : _chaseState.IsChasing ? State.Chase : State.Patrol;
            if (state == _state)
                return;

            _state = state;
            Apply(state);
        }

        private Renderer FindTintRenderer()
        {
            foreach (Renderer candidate in GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                foreach (Material material in candidate.sharedMaterials)
                {
                    if (material != null && material.HasProperty(TintId))
                        return candidate;
                }
            }

            return null;
        }

        private void Apply(State state)
        {
            if (_displayRenderer != null)
            {
                _displayRenderer.GetPropertyBlock(_block);
                _block.SetColor(TintId, state == State.Chase ? _chaseTint : state == State.Alert ? _alertTint : _patrolTint);
                _displayRenderer.SetPropertyBlock(_block);
            }

            Color lightColor = state == State.Chase ? _chaseLightColor : state == State.Alert ? _alertLightColor : _patrolLightColor;

            foreach (Light light in _lights)
                light.color = lightColor;
        }
    }
}
