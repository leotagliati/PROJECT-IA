namespace Assets.Scripts.Free
{
    /// <summary>
    /// Tudo que a recompensa da v8 precisa saber de um step, montado pelo Manager. Sinal novo = campo aqui + termo no
    /// <see cref="FreeRewardSystem"/>; nunca AddReward solto no Manager.
    /// </summary>
    public struct FreeStepContext
    {
        // Em steps de física; os custos por step são divididos por isto (o teto por episódio fica fixo).
        public int MaxEpisodeSteps;
        public int ElapsedSteps;

        // Pontos do mapa no total: a recompensa por ponto é o orçamento dividido por isto.
        public int PointCount;

        // Escala da exploração (currículo discovery_reward_scale; 0.3 na caça).
        public float DiscoveryRewardScale;

        // Exploração (FreeExplorationMemory).
        public int NewPoints;
        public int NewDoors;
        public int StepsSinceProgress;
        public bool CoverageReached;

        // Corpo (GraphBodyTracker + FreeStuckTracker): parede, batida, presa, suavidade.
        public bool IsTouchingWall;
        public int WallHits;
        public int RecentWallHits;
        public bool IsStuck;
        public float MoveChangeSq;
        public float LookChangeSq;

        // Caça (GraphHiderPerception + FreeTrackSense). CAÇANDO = vendo o alvo, procurando (perdeu de vista há < 20 s)
        // ou com pegada fresca: a exploração não paga e a estagnação não cobra (o FOCO na presa).
        public bool Hunting;
        public bool HiderSpotted;
        public float HiderApproachDelta;
        public bool HiderCaught;
    }
}
