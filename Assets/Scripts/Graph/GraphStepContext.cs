namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Snapshot imutável de um step, montado pelo <see cref="GraphExplorerManager"/>. É o único
    /// input do <see cref="GraphRewardSystem"/>: assim o cálculo de recompensa é função pura do
    /// step e não sai consultando grafo, memória nem arena por conta própria.
    ///
    /// Os valores de exploração vêm da <see cref="GraphRoomMemory"/> em UNIDADES (sala inteira = 1,
    /// porta nova = 1); o reward system converte cada uma com o seu fator.
    /// </summary>
    public readonly struct GraphStepContext
    {
        public readonly int MaxEpisodeSteps;

        /// <summary>Fatia de sala descoberta antes de ela ser concluída (a sala inteira soma 1).</summary>
        public readonly float RoomNodeValue;

        /// <summary>Fatia de sala descoberta depois de concluída (a "cauda", que paga pouco).</summary>
        public readonly float RoomTailValue;

        /// <summary>Salas concluídas neste intervalo (liberada conta menos que 1).</summary>
        public readonly float RoomCompletedValue;

        /// <summary>Soma das novidades das portas atravessadas (1 = porta nunca usada).</summary>
        public readonly float DoorCrossValue;

        /// <summary>Soma das novidades das portas atravessadas saindo de sala concluída.</summary>
        public readonly float RoomExitValue;

        /// <summary>Steps de física sem progresso de sala (nó novo, conclusão, porta com novidade).</summary>
        public readonly int StepsSinceProgress;

        public readonly bool IsTouchingWall;

        /// <summary>Chegou ao nó do ping neste intervalo.</summary>
        public readonly bool PingReached;

        /// <summary>Valor do(s) ping(s) atendido(s) neste intervalo (NavGraph.PingValue). 0 quando não houve.</summary>
        public readonly float PingReachedValue;

        /// <summary>Um ping expirou sem visita neste intervalo.</summary>
        public readonly bool PingMissed;

        /// <summary>Avistou o hider neste intervalo (aquisição de visão, fora do cooldown).</summary>
        public readonly bool HiderSpotted;

        /// <summary>
        /// Redução da distância planar (m) ao hider desde a decisão anterior, medida só quando
        /// o seeker o VIA nas duas. Positivo = aproximou. Válido com <see cref="HasHiderApproach"/>.
        /// </summary>
        public readonly float HiderApproachDelta;

        public readonly bool HasHiderApproach;

        /// <summary>O hider está no cone de visão agora (com linha livre de parede).</summary>
        public readonly bool HiderInView;

        /// <summary>Chegadas em porta pisada há pouco (loop), neste intervalo.</summary>
        public readonly int EarlyRevisitArrivals;

        /// <summary>Quantas revisitas precoces SEGUIDAS até agora (ver GraphExplorationMemory).</summary>
        public readonly int EarlyRevisitStreak;

        /// <summary>Batidas NOVAS em parede neste step (início de contato; ver WallHitTracker).</summary>
        public readonly int WallHits;

        /// <summary>Batidas nos últimos ~5 s, incluindo a deste step. Escala o custo de cada batida.</summary>
        public readonly int RecentWallHits;

        /// <summary>
        /// |ação agora - ação anterior|² (0..8). Só muda nas decisões: entre elas a ação se repete
        /// (TakeActionsBetweenDecisions) e o valor é zero.
        /// </summary>
        public readonly float ActionChangeSq;

        /// <summary>|olhar agora - olhar anterior|² (0..8), das ações [2..3]. Mesma cadência do de cima.</summary>
        public readonly float LookChangeSq;

        /// <summary>Escala dos termos de ping nesta lição (ping_reward_scale; 0 = ping só informa).</summary>
        public readonly float PingRewardScale;

        /// <summary>Escala da recompensa de exploração nesta lição (discovery_reward_scale).</summary>
        public readonly float DiscoveryRewardScale;

        public GraphStepContext(
            int maxEpisodeSteps,
            float roomNodeValue,
            float roomTailValue,
            float roomCompletedValue,
            float doorCrossValue,
            float roomExitValue,
            int stepsSinceProgress,
            bool isTouchingWall,
            bool pingReached,
            float pingReachedValue,
            bool pingMissed,
            bool hiderSpotted,
            float hiderApproachDelta,
            bool hasHiderApproach,
            int earlyRevisitArrivals,
            int earlyRevisitStreak,
            int wallHits,
            int recentWallHits,
            float actionChangeSq,
            float lookChangeSq,
            float pingRewardScale,
            float discoveryRewardScale,
            bool hiderInView)
        {
            HiderInView = hiderInView;
            MaxEpisodeSteps = maxEpisodeSteps;
            RoomNodeValue = roomNodeValue;
            RoomTailValue = roomTailValue;
            RoomCompletedValue = roomCompletedValue;
            DoorCrossValue = doorCrossValue;
            RoomExitValue = roomExitValue;
            StepsSinceProgress = stepsSinceProgress;
            IsTouchingWall = isTouchingWall;
            PingReached = pingReached;
            PingReachedValue = pingReachedValue;
            PingMissed = pingMissed;
            HiderSpotted = hiderSpotted;
            HiderApproachDelta = hiderApproachDelta;
            HasHiderApproach = hasHiderApproach;
            EarlyRevisitArrivals = earlyRevisitArrivals;
            EarlyRevisitStreak = earlyRevisitStreak;
            WallHits = wallHits;
            RecentWallHits = recentWallHits;
            ActionChangeSq = actionChangeSq;
            LookChangeSq = lookChangeSq;
            PingRewardScale = pingRewardScale;
            DiscoveryRewardScale = discoveryRewardScale;
        }
    }
}
