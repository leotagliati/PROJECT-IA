namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Foto de um step de física, montada pelo <see cref="GraphExplorerManager"/> e o ÚNICO input do
    /// <see cref="GraphRewardSystem"/>, que a consome como função pura (não consulta grafo, memória nem arena).
    /// Os valores de exploração vêm da <see cref="GraphRoomMemory"/> em UNIDADES (sala inteira = 1, porta
    /// nova = 1); o reward system aplica os fatores.
    ///
    /// Campos públicos preenchidos POR NOME no Manager (inicializador de objeto): com 20+ termos, um construtor
    /// posicional deixava trocar dois bools vizinhos sem erro de compilação. Recebida com `in` (só leitura).
    /// Sinal novo = campo aqui + uma linha no BuildStepContext + o termo no GraphRewardSystem.
    /// </summary>
    public struct GraphStepContext
    {
        // ---- Episódio ----
        public int MaxEpisodeSteps;

        /// <summary>Steps de física já passados (a fração que sobra paga o bônus de captura cedo).</summary>
        public int ElapsedSteps;

        /// <summary>Escala da recompensa de exploração nesta lição (discovery_reward_scale).</summary>
        public float DiscoveryRewardScale;

        /// <summary>Escala dos termos de ping nesta lição (ping_reward_scale; 0 = ping só informa).</summary>
        public float PingRewardScale;

        // ---- Fim do episódio (no máximo um; a captura tem prioridade) ----

        /// <summary>Pegou o hider neste step (treino): paga e encerra.</summary>
        public bool HiderCaught;

        /// <summary>Concluiu o coverage_target neste step: paga e encerra.</summary>
        public bool CoverageReached;

        // ---- Salas e portas (GraphRoomMemory, em unidades) ----

        /// <summary>Fatia de sala descoberta antes de concluir (a sala inteira soma 1).</summary>
        public float RoomNodeValue;

        /// <summary>Fatia descoberta depois de concluída (a "cauda", que paga pouco).</summary>
        public float RoomTailValue;

        /// <summary>Migalha de sala grande antes de concluir (a sala inteira soma 1 x o valor dela).</summary>
        public float RoomCrumbValue;

        /// <summary>Salas concluídas neste step (1 cada; calor, suspeita e liberação mudam esse valor).</summary>
        public float RoomCompletedValue;

        /// <summary>Soma das novidades das portas atravessadas (1 = nunca usada).</summary>
        public float DoorCrossValue;

        /// <summary>Soma das novidades das portas atravessadas saindo de sala concluída.</summary>
        public float RoomExitValue;

        /// <summary>Steps de física sem progresso de sala (nó novo, conclusão, porta com novidade).</summary>
        public int StepsSinceProgress;

        /// <summary>Chegadas em porta pisada há pouco (loop) neste step.</summary>
        public int EarlyRevisitArrivals;

        /// <summary>Revisitas precoces SEGUIDAS até agora (ver GraphExplorationMemory).</summary>
        public int EarlyRevisitStreak;

        // ---- Corpo (GraphBodyTracker) ----

        /// <summary>Encostado em PAREDE (paredes e móveis): custo cheio.</summary>
        public bool IsTouchingWall;

        /// <summary>Encostado só em PORTA (layer Door, o batente): custo reduzido.</summary>
        public bool IsTouchingDoor;

        /// <summary>Batidas NOVAS em parede ou porta neste step (início de contato).</summary>
        public int WallHits;

        /// <summary>A batida deste step foi só em porta: custo reduzido.</summary>
        public bool WallHitIsDoor;

        /// <summary>Batidas nos últimos ~5 s, incluindo a deste step; escala o custo de cada batida.</summary>
        public int RecentWallHits;

        /// <summary>
        /// |ação - ação anterior|² (0..8). Só é não-nulo nas decisões; entre elas a ação se repete
        /// (TakeActionsBetweenDecisions) e vale zero.
        /// </summary>
        public float ActionChangeSq;

        /// <summary>|olhar - olhar anterior|² (0..8), das ações [2..3]; mesma cadência.</summary>
        public float LookChangeSq;

        // ---- Ping ----

        /// <summary>Chegou ao nó do ping neste step.</summary>
        public bool PingReached;

        /// <summary>Valor dos pings atendidos neste step (NavGraph.PingValue); 0 se nenhum.</summary>
        public float PingReachedValue;

        /// <summary>Um ping expirou sem visita neste step.</summary>
        public bool PingMissed;

        // ---- Hider (visão e procura) ----

        /// <summary>Avistou o hider neste step (aquisição de visão, fora do cooldown).</summary>
        public bool HiderSpotted;

        /// <summary>O hider está no cone de visão agora (com linha livre de parede).</summary>
        public bool HiderInView;

        /// <summary>Caçando: vendo o hider ou procurando ele depois de perdê-lo (GraphHiderPerception.IsSearching).</summary>
        public bool HuntingTarget;

        /// <summary>Dá para medir aproximação (vendo nesta decisão e na anterior).</summary>
        public bool HasHiderApproach;

        /// <summary>Metros que encurtou até o hider desde a decisão anterior; positivo = aproximou.</summary>
        public float HiderApproachDelta;

        /// <summary>Suspeita zerada que paga neste step (fração de 1; GraphSuspicionMap.ClearedMass).</summary>
        public float SuspicionClearedMass;
    }
}
