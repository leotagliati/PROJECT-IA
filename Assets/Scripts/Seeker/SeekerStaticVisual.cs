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

        [Header("-----Treino-----")]
        [SerializeField] private bool _animateDuringTraining = false;

        private MaterialPropertyBlock _block;
        private bool _active;
        private bool _chasing;

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

            if (_displayRenderer == null)
                Debug.LogWarning($"{name}: nenhum renderer com _Tint — só os lights vão trocar de cor.", this);

            _block = new MaterialPropertyBlock();
            _active = true;

            _chasing = false;
            Apply(false);
        }

        private void Update()
        {
            if (!_active)
                return;

            bool chasing = _chaseState.IsChasing;
            if (chasing == _chasing)
                return;

            _chasing = chasing;
            Apply(chasing);
        }

        private void Apply(bool chasing)
        {
            if (_displayRenderer != null)
            {
                _displayRenderer.GetPropertyBlock(_block);
                _block.SetColor(TintId, chasing ? _chaseTint : _patrolTint);
                _displayRenderer.SetPropertyBlock(_block);
            }

            Color lightColor = chasing ? _chaseLightColor : _patrolLightColor;

            foreach (Light light in _lights)
                light.color = lightColor;
        }
    }
}
