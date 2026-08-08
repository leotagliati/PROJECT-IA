using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Calculadora pura de recompensa. Recebe um SeekerStepContext e devolve o delta do step;
    /// não consulta outros sistemas, não decide fim de episódio e não assina eventos de física.
    /// Todo o tuning do agente mora aqui.
    /// </summary>
    public class SeekerRewardSystem : MonoBehaviour
    {
        [Header("-----Penalidades-----")]
        [SerializeField] private float _existentialPenalty = 2f;
        [SerializeField] private float _wallProximityPenalty = 0.01f;
        [SerializeField] private float _wallCollisionPenalty = 0.5f;

        [Header("-----Recompensas-----")]
        [SerializeField] private float _hiderSightReward = 0.1f;      // bônus único ao avistar
        [SerializeField] private float _hiderApproachReward = 0.3f;   // por aproximar da última posição
        [SerializeField] private float _hiderFoundReward = 1f;

        // Estado de reward shaping: detecta a borda de subida do avistamento. É por episódio e
        // por agente — por isso o sistema é MonoBehaviour, e não um ScriptableObject compartilhado.
        private bool _wasSeeingHider;

        public float WallCollisionPenalty => -_wallCollisionPenalty;

        public float HiderFoundReward => _hiderFoundReward;

        public void ResetEpisode() => _wasSeeingHider = false;

        public float EvaluateStep(in SeekerStepContext context)
        {
            float reward = 0f;

            if (context.MaxEpisodeSteps > 0)
                reward -= _existentialPenalty / context.MaxEpisodeSteps;

            reward -= _wallProximityPenalty * context.ClosestWallProximity;

            // Bônus único no step em que passa a enxergar o hider (borda de subida).
            if (context.IsSeeingHider && !_wasSeeingHider)
                reward += _hiderSightReward;

            _wasSeeingHider = context.IsSeeingHider;

            if (context.HasSeenHider)
            {
                // Progresso do PRÓPRIO seeker rumo ao alvo atual: as duas distâncias usam a mesma
                // posição conhecida, então um salto do alvo (novo avistamento) não gera falso ganho.
                float previousDistance = Vector3.Distance(context.PreMovePosition, context.LastKnownHiderPosition);
                float currentDistance = Vector3.Distance(context.CurrentPosition, context.LastKnownHiderPosition);
                reward += _hiderApproachReward * (previousDistance - currentDistance);
            }

            return reward;
        }
    }
}
