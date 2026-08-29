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

        // Zona morta: só pune a partir dessa proximidade. Desacopla o alcance da PUNICAO do
        // alcance da OBSERVACAO — sem isso, aumentar _detectionRange para enxergar melhor
        // aumenta junto o raio em que o agente e taxado, e navegar corredor vira prejuizo.
        // Com _detectionRange = 5, 0.6 equivale a comecar a punir a 2m da parede.
        [SerializeField, Range(0f, 1f)] private float _wallDangerThreshold = 0.6f;
        // Cobrada por STEP em contato, não por evento de colisão. Com 0.005: raspar de leve
        // numa quina (uns 5 steps) custa -0.025, irrelevante; ficar 500 steps preso contra a
        // parede custa -2.5, significativo. A magnitude passa a depender do tempo grudado,
        // e não de quantas vezes a física resolveu redisparar o contato.
        // Mantida na mesma ordem de grandeza da pressao existencial (-2/episodio): raspar
        // parede o episodio inteiro custa o mesmo que desperdicar o episodio inteiro. Acima
        // disso ela domina a decisao e o agente aprende a nao entrar em corredor nenhum.
        [SerializeField] private float _wallContactPenalty = 0.002f;

        [Header("-----Recompensas-----")]
        [SerializeField] private float _hiderSightReward = 0.1f;      // bônus único ao avistar
        [SerializeField] private float _hiderApproachReward = 0.3f;   // por aproximar da última posição
        // Precisa ser claramente maior que o custo de explorar. Com 1, um episodio inteiro
        // procurando ja custava -2 de existencial, entao achar o hider mal compensava o risco
        // e a politica racional era ficar parada.
        [SerializeField] private float _hiderFoundReward = 5f;

        [SerializeField] private float _newCellReward = 0.02f;

        // Estado de reward shaping: detecta a borda de subida do avistamento. É por episódio e
        // por agente — por isso o sistema é MonoBehaviour, e não um ScriptableObject compartilhado.
        private bool _wasSeeingHider;

        public float HiderFoundReward => _hiderFoundReward;

        public void ResetEpisode() => _wasSeeingHider = false;

        public float EvaluateStep(in SeekerStepContext context)
        {
            float reward = 0f;

            if (context.MaxEpisodeSteps > 0)
                reward -= _existentialPenalty / context.MaxEpisodeSteps;

            // Escalado pela lição: num labirinto estar perto de parede é a condição normal de
            // um corredor, então esse termo vira zero e só a colisão continua punida.
            float wallDanger = Mathf.InverseLerp(_wallDangerThreshold, 1f, context.ClosestWallProximity);
            reward -= _wallProximityPenalty * wallDanger * context.WallProximityScale;

            // Não escalada por lição: encostar é ruim nos dois cenários, e agora que é
            // proporcional ao tempo ela se auto-regula em vez de explodir no labirinto.
            if (context.IsTouchingWall)
                reward -= _wallContactPenalty;

            // Com ~120 celulas alcancaveis, cobrir o mapa inteiro rende ~+2.4: bate de longe
            // o -2 de ficar parado o episodio todo, e continua bem abaixo do +5 de achar o
            // hider, para explorar nao virar um objetivo em si.
            if (context.EnteredNewCell)
                reward += _newCellReward;

            // Bônus único no step em que passa a enxergar o hider (borda de subida).
            if (context.IsSeeingHider && !_wasSeeingHider)
                reward += _hiderSightReward;

            _wasSeeingHider = context.IsSeeingHider;

            if (context.HasSeenHider)
            {
                // Progresso do PRÓPRIO seeker rumo ao alvo atual: as duas distâncias usam a mesma
                // posição conhecida, então um salto do alvo (novo avistamento) não gera falso ganho.
                float previousDistance = Vector3.Distance(context.PreviousStepPosition, context.LastKnownHiderPosition);
                float currentDistance = Vector3.Distance(context.CurrentPosition, context.LastKnownHiderPosition);

                // A distância é euclidiana, então só equivale a "progresso" em espaço aberto.
                // No labirinto o peso cai, senão esse termo pune o contorno de uma parede.
                reward += _hiderApproachReward * (previousDistance - currentDistance) * context.ApproachRewardScale;
            }

            return reward;
        }
    }
}
