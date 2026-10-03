namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Snapshot imutável de um step de física, montado pelo <see cref="GraphExplorerManager"/>.
    /// É o único input do <see cref="GraphRewardSystem"/>, que o consome como função pura (não
    /// consulta grafo, memória nem arena). Os valores de exploração vêm da
    /// <see cref="GraphRoomMemory"/> em UNIDADES (sala inteira = 1, porta nova = 1); o reward
    /// system aplica os fatores. Campo novo aqui = parâmetro novo no construtor e no Manager.
    /// </summary>
    public readonly struct GraphStepContext
    {
        public readonly int MaxEpisodeSteps;

        /// <summary>Fatia de sala descoberta antes de concluir (a sala inteira soma 1).</summary>
        public readonly float RoomNodeValue;

        /// <summary>Fatia descoberta depois de concluída (a "cauda", que paga pouco).</summary>
        public readonly float RoomTailValue;

        /// <summary>Salas concluídas neste step (sala liberada conta menos que 1).</summary>
        public readonly float RoomCompletedValue;

        /// <summary>Soma das novidades das portas atravessadas (1 = nunca usada).</summary>
        public readonly float DoorCrossValue;

        /// <summary>Soma das novidades das portas atravessadas saindo de sala concluída.</summary>
        public readonly float RoomExitValue;

        /// <summary>Steps de física sem progresso de sala (nó novo, conclusão, porta com novidade).</summary>
        public readonly int StepsSinceProgress;

        public readonly bool IsTouchingWall;

        /// <summary>Chegou ao nó do ping neste step.</summary>
        public readonly bool PingReached;

        /// <summary>Valor dos pings atendidos neste step (NavGraph.PingValue); 0 se nenhum.</summary>
        public readonly float PingReachedValue;

        /// <summary>Um ping expirou sem visita neste step.</summary>
        public readonly bool PingMissed;

        /// <summary>Avistou o hider neste step (aquisição de visão, fora do cooldown).</summary>
        public readonly bool HiderSpotted;

        /// <summary>
        /// Redução da distância planar (m) ao hider desde a decisão anterior, só se o seeker o VIA
        /// nas duas; positivo = aproximou. Válido com <see cref="HasHiderApproach"/>.
        /// </summary>
        public readonly float HiderApproachDelta;

        public readonly bool HasHiderApproach;

        /// <summary>O hider está no cone de visão agora (com linha livre de parede).</summary>
        public readonly bool HiderInView;

        /// <summary>Suspeita zerada que paga neste step (fração de 1; GraphSuspicionMap.ClearedMass).</summary>
        public readonly float SuspicionClearedMass;

        /// <summary>Chegadas em porta pisada há pouco (loop) neste step.</summary>
        public readonly int EarlyRevisitArrivals;

        /// <summary>Revisitas precoces SEGUIDAS até agora (ver GraphExplorationMemory).</summary>
        public readonly int EarlyRevisitStreak;

        /// <summary>Batidas NOVAS em parede neste step (início de contato; ver WallHitTracker).</summary>
        public readonly int WallHits;

        /// <summary>Batidas nos últimos ~5 s, incluindo a deste step; escala o custo de cada batida.</summary>
        public readonly int RecentWallHits;

        /// <summary>
        /// |ação - ação anterior|² (0..8). Só é não-nulo nas decisões; entre elas a ação se repete
        /// (TakeActionsBetweenDecisions) e vale zero.
        /// </summary>
        public readonly float ActionChangeSq;

        /// <summary>|olhar - olhar anterior|² (0..8), das ações [2..3]; mesma cadência.</summary>
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
            bool hiderInView,
            float suspicionClearedMass)
        {
            HiderInView = hiderInView;
            SuspicionClearedMass = suspicionClearedMass;
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
