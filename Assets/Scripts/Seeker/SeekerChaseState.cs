using UnityEngine;

namespace Assets.Scripts.Seeker
{
    [DefaultExecutionOrder(-10)]
    public class SeekerChaseState : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float _holdTime = 2.5f;
        [SerializeField, Min(0.01f)] private float _fadeIn = 0.3f;
        [SerializeField, Min(0.01f)] private float _fadeOut = 1.5f;

        private float _lastSeenTime = float.NegativeInfinity;
        private float _blend;

        /// <summary>0 = patrulha, 1 = perseguição.</summary>
        public float Blend => _blend;

        public bool IsChasing => Time.time - _lastSeenTime <= _holdTime;

        /// <summary>Chamado uma vez por step com o mesmo bool que vira Run na animação.</summary>
        public void Tick(bool isSeeingHider)
        {
            if (isSeeingHider)
                _lastSeenTime = Time.time;
        }

        private void Update()
        {
            bool chasing = IsChasing;
            float speed = 1f / (chasing ? _fadeIn : _fadeOut);
            _blend = Mathf.MoveTowards(_blend, chasing ? 1f : 0f, Time.deltaTime * speed);
        }

        public void ResetEpisode()
        {
            _lastSeenTime = float.NegativeInfinity;
            _blend = 0f;
        }
    }
}
