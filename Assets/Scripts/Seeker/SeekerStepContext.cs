using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Snapshot imutável de um único step, montado pelo SeekerManager. É o único input do
    /// SeekerRewardSystem — assim ele nunca precisa consultar percepção, movimento ou arena,
    /// e o cálculo de recompensa vira uma função pura do estado do step.
    /// </summary>
    public readonly struct SeekerStepContext
    {
        /// <summary>
        /// Posição no início do step ANTERIOR. O par com CurrentPosition tem que atravessar a
        /// fronteira do step: Rigidbody.MovePosition só chega ao transform depois da simulação,
        /// então medir deslocamento dentro de um mesmo step dá sempre zero.
        /// </summary>
        public readonly Vector3 PreviousStepPosition;

        /// <summary>Posição agora, já com o movimento pedido no step anterior simulado.</summary>
        public readonly Vector3 CurrentPosition;
        public readonly bool IsSeeingHider;
        public readonly bool HasSeenHider;
        public readonly Vector3 LastKnownHiderPosition;
        public readonly float ClosestWallProximity;
        public readonly int MaxEpisodeSteps;

        /// <summary>
        /// Se o agente estava encostado numa parede neste step. É por step, e não por evento
        /// de colisão: deslizando ao longo de uma parede o Unity re-dispara OnCollisionEnter
        /// dezenas de vezes por segundo, então uma penalidade por evento explode num labirinto
        /// enquanto passa despercebida em sala aberta. Por tempo, a magnitude é previsível.
        /// </summary>
        public readonly bool IsTouchingWall;

        /// <summary>
        /// Se este step levou o agente a uma célula inédita. É o único termo positivo enquanto
        /// ele procura — sem ele, todo sinal sensível à ação é punitivo e ficar parado passa a
        /// ser a política ótima.
        /// </summary>
        public readonly bool EnteredNewCell;

        /// <summary>
        /// Peso da recompensa de aproximação nesta lição. Em sala aberta vale 1 — reduzir a
        /// distância euclidiana É progresso. No labirinto cai, porque contornar uma parede
        /// exige se afastar do alvo, e em peso cheio esse termo puniria a manobra correta.
        /// </summary>
        public readonly float ApproachRewardScale;

        /// <summary>
        /// Peso da penalidade de proximidade de parede nesta lição. Em sala aberta vale 1
        /// ("não raspa na borda"); no labirinto vai a zero, já que estar perto de parede é a
        /// condição normal de um corredor. Bater continua penalizado à parte.
        /// </summary>
        public readonly float WallProximityScale;

        public SeekerStepContext(
            Vector3 previousStepPosition,
            Vector3 currentPosition,
            bool isSeeingHider,
            bool hasSeenHider,
            Vector3 lastKnownHiderPosition,
            float closestWallProximity,
            int maxEpisodeSteps,
            bool isTouchingWall,
            bool enteredNewCell,
            float approachRewardScale,
            float wallProximityScale)
        {
            IsTouchingWall = isTouchingWall;
            EnteredNewCell = enteredNewCell;
            PreviousStepPosition = previousStepPosition;
            CurrentPosition = currentPosition;
            IsSeeingHider = isSeeingHider;
            HasSeenHider = hasSeenHider;
            LastKnownHiderPosition = lastKnownHiderPosition;
            ClosestWallProximity = closestWallProximity;
            MaxEpisodeSteps = maxEpisodeSteps;
            ApproachRewardScale = approachRewardScale;
            WallProximityScale = wallProximityScale;
        }
    }
}
