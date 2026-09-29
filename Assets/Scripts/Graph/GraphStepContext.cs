namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Snapshot imutável de um step, montado pelo <see cref="GraphExplorerManager"/>. É o único
    /// input do <see cref="GraphRewardSystem"/>: assim o cálculo de recompensa é função pura do
    /// step e não sai consultando grafo, memória nem arena por conta própria.
    /// </summary>
    public readonly struct GraphStepContext
    {
        public readonly int MaxEpisodeSteps;

        /// <summary>
        /// Chegou a um nó onde ainda não tinha estado neste episódio. Booleano, usado só como
        /// PORTEIRA (do shaping de fronteira) — quem paga é o <see cref="NewNodeValue"/>.
        /// </summary>
        public readonly bool EnteredNewNode;

        /// <summary>
        /// Soma dos PESOS (NavNode.ExplorationWeight) dos nós inéditos deste intervalo. A
        /// recompensa multiplica isto por um fator e pronto — a contagem de nós não aparece
        /// em lugar nenhum do cálculo, de propósito.
        /// </summary>
        public readonly float NewNodeValue;

        /// <summary>Percorreu uma aresta inédita. Paga o CAMINHO, não só o destino.</summary>
        public readonly bool TraversedNewEdge;

        /// <summary>Trocou de nó neste step, visitado ou não.</summary>
        public readonly bool ChangedNode;

        /// <summary>Visitas ao nó atual neste episódio. 1 na primeira; cresce ao revisitar.</summary>
        public readonly int CurrentNodeVisitCount;

        /// <summary>
        /// Redução da distância em METROS PELO GRAFO (do nó âncora) até o alvo da fronteira.
        /// Positivo = andou na direção certa. Só muda quando o agente troca de nó. Válido só
        /// quando <see cref="HasFrontierProgress"/> é true. Em metros, e não em arestas, para
        /// o total pago não depender de quantos nós existem no caminho.
        /// </summary>
        public readonly float FrontierDistanceDelta;

        /// <summary>
        /// Se o delta de fronteira é comparável entre os dois steps. Vira false quando o alvo
        /// mudou (o agente acabou de visitar um nó e a fronteira pulou para outro lugar) — sem
        /// esse cuidado o shaping cobraria como retrocesso justamente o step em que o agente
        /// acertou.
        /// </summary>
        public readonly bool HasFrontierProgress;

        /// <summary>
        /// Redução da distância EM METROS até o PRÓXIMO PASSO da fronteira, medida no plano
        /// X/Z. Positivo = andou na direção certa neste step. Este é o termo DENSO: ao
        /// contrário do <see cref="FrontierDistanceDelta"/>, que só muda quando o agente troca
        /// de nó, este muda a cada step em que o agente se mexe — e é o que dá gradiente
        /// durante a travessia de uma aresta longa, onde antes só havia penalidade.
        /// Válido apenas quando <see cref="HasFrontierApproach"/> é true.
        /// </summary>
        public readonly float FrontierApproachDelta;

        /// <summary>
        /// Se o delta de aproximação é comparável: os dois steps mediram a distância até o
        /// MESMO nó. A checagem é de identidade do nó, e não de "tem fronteira": quando o
        /// agente chega num nó a BFS reaponta para outro lugar e a distância salta de forma
        /// descontínua — cobrar esse salto seria punir (ou premiar) o agente por uma mudança
        /// de alvo que ele não causou andando.
        /// </summary>
        public readonly bool HasFrontierApproach;

        public readonly int StepsSinceNewNode;

        public readonly bool IsTouchingWall;

        /// <summary>Peso do sinal de fronteira nesta lição (ver currículo). 0 desliga o shaping.</summary>
        public readonly float FrontierRewardScale;

        /// <summary>
        /// Redução da distância em METROS PELO GRAFO até o nó do ping desde a decisão anterior.
        /// Positivo = aproximou. Só é válido quando <see cref="HasPingProgress"/> é true (mesmo
        /// ping ativo nas duas decisões).
        /// </summary>
        public readonly float PingDistanceDelta;

        public readonly bool HasPingProgress;

        /// <summary>Chegou ao nó do ping neste intervalo.</summary>
        public readonly bool PingReached;

        /// <summary>
        /// Valor do(s) ping(s) atendido(s) neste intervalo: pontuação do tipo Ping no NavGraph x
        /// peso do nó (NavGraph.PingValue). 0 quando não houve. O reward system multiplica pelo
        /// prêmio de chegada.
        /// </summary>
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

        /// <summary>
        /// Renda de REVISITA do intervalo (patrulha): peso x fração de revisita x recuperação² x
        /// (1 - tédio da sala), já somada na memória. O reward multiplica pelo mesmo fator da
        /// descoberta. 0 com a patrulha desligada.
        /// </summary>
        public readonly float RevisitValue;

        /// <summary>Chegadas em primário visitado há pouco (loop), neste intervalo.</summary>
        public readonly int EarlyRevisitArrivals;

        /// <summary>Quantas revisitas precoces SEGUIDAS até agora (ver GraphExplorationMemory).</summary>
        public readonly int EarlyRevisitStreak;

        /// <summary>Tédio (0..1) da sala em que o agente está. 0 em corredor ou com o tédio desligado.</summary>
        public readonly float CurrentAreaBoredom;

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

        /// <summary>
        /// Suspeita zerada que paga neste intervalo (GraphSuspicionMap.ClearedMass, fração de 1):
        /// ver ou visitar nós vazios onde o hider provavelmente estaria. 0 sem hider.
        /// </summary>
        public readonly float SuspicionCleared;

        /// <summary>Escala dos termos de ping nesta lição (ping_reward_scale; 0 = ping só informa).</summary>
        public readonly float PingRewardScale;

        /// <summary>Escala da recompensa de descoberta nesta lição (discovery_reward_scale).</summary>
        public readonly float DiscoveryRewardScale;

        /// <summary>
        /// Quanto a distância PELO GRAFO (m) até o inexplorado mais próximo caiu desde a decisão
        /// anterior, com o mesmo alvo nas duas (GraphExplorationMemory.NearestUnexploredTarget).
        /// Positivo = aproximou. 0 quando o alvo mudou. Independente da seta.
        /// </summary>
        public readonly float UnexploredProgress;

        public GraphStepContext(
            int maxEpisodeSteps,
            bool enteredNewNode,
            float newNodeValue,
            bool traversedNewEdge,
            bool changedNode,
            int currentNodeVisitCount,
            float frontierDistanceDelta,
            bool hasFrontierProgress,
            float frontierApproachDelta,
            bool hasFrontierApproach,
            int stepsSinceNewNode,
            bool isTouchingWall,
            float frontierRewardScale,
            float pingDistanceDelta,
            bool hasPingProgress,
            bool pingReached,
            float pingReachedValue,
            bool pingMissed,
            bool hiderSpotted,
            float hiderApproachDelta,
            bool hasHiderApproach,
            float revisitValue,
            int earlyRevisitArrivals,
            int earlyRevisitStreak,
            float currentAreaBoredom,
            int wallHits,
            int recentWallHits,
            float actionChangeSq,
            float lookChangeSq,
            float suspicionCleared,
            float pingRewardScale,
            float discoveryRewardScale,
            float unexploredProgress)
        {
            MaxEpisodeSteps = maxEpisodeSteps;
            EnteredNewNode = enteredNewNode;
            NewNodeValue = newNodeValue;
            TraversedNewEdge = traversedNewEdge;
            ChangedNode = changedNode;
            CurrentNodeVisitCount = currentNodeVisitCount;
            FrontierDistanceDelta = frontierDistanceDelta;
            HasFrontierProgress = hasFrontierProgress;
            FrontierApproachDelta = frontierApproachDelta;
            HasFrontierApproach = hasFrontierApproach;
            StepsSinceNewNode = stepsSinceNewNode;
            IsTouchingWall = isTouchingWall;
            FrontierRewardScale = frontierRewardScale;
            PingDistanceDelta = pingDistanceDelta;
            HasPingProgress = hasPingProgress;
            PingReached = pingReached;
            PingReachedValue = pingReachedValue;
            PingMissed = pingMissed;
            HiderSpotted = hiderSpotted;
            HiderApproachDelta = hiderApproachDelta;
            HasHiderApproach = hasHiderApproach;
            RevisitValue = revisitValue;
            EarlyRevisitArrivals = earlyRevisitArrivals;
            EarlyRevisitStreak = earlyRevisitStreak;
            CurrentAreaBoredom = currentAreaBoredom;
            WallHits = wallHits;
            RecentWallHits = recentWallHits;
            ActionChangeSq = actionChangeSq;
            LookChangeSq = lookChangeSq;
            SuspicionCleared = suspicionCleared;
            PingRewardScale = pingRewardScale;
            DiscoveryRewardScale = discoveryRewardScale;
            UnexploredProgress = unexploredProgress;
        }
    }
}
