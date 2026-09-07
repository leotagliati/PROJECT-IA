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
        /// Valor dos nós inéditos deste intervalo, já normalizado por região
        /// (orçamento / nós da região). A recompensa multiplica isto por um peso e pronto — a
        /// contagem de nós não aparece em lugar nenhum do cálculo, de propósito.
        /// </summary>
        public readonly float NewNodeValue;

        /// <summary>Percorreu uma aresta inédita. Paga o CAMINHO, não só o destino.</summary>
        public readonly bool TraversedNewEdge;

        /// <summary>Soma dos orçamentos das regiões inéditas alcançadas neste intervalo.</summary>
        public readonly float NewRegionBudget;

        /// <summary>Trocou de nó neste step, visitado ou não.</summary>
        public readonly bool ChangedNode;

        /// <summary>Visitas ao nó atual neste episódio. 1 na primeira; cresce ao revisitar.</summary>
        public readonly int CurrentNodeVisitCount;

        /// <summary>
        /// Redução da distância EM ARESTAS até o não-visitado mais próximo. Positivo = andou na
        /// direção certa. Só é válido quando <see cref="HasFrontierProgress"/> é true.
        /// </summary>
        public readonly int FrontierDistanceDelta;

        /// <summary>
        /// Se o delta de fronteira é comparável entre os dois steps. Vira false quando o alvo
        /// mudou (o agente acabou de visitar um nó e a fronteira pulou para outro lugar) — sem
        /// esse cuidado o shaping cobraria como retrocesso justamente o step em que o agente
        /// acertou.
        /// </summary>
        public readonly bool HasFrontierProgress;

        public readonly int StepsSinceNewNode;

        public readonly bool IsTouchingWall;

        /// <summary>Peso do sinal de fronteira nesta lição (ver currículo). 0 desliga o shaping.</summary>
        public readonly float FrontierRewardScale;

        public GraphStepContext(
            int maxEpisodeSteps,
            bool enteredNewNode,
            float newNodeValue,
            bool traversedNewEdge,
            float newRegionBudget,
            bool changedNode,
            int currentNodeVisitCount,
            int frontierDistanceDelta,
            bool hasFrontierProgress,
            int stepsSinceNewNode,
            bool isTouchingWall,
            float frontierRewardScale)
        {
            MaxEpisodeSteps = maxEpisodeSteps;
            EnteredNewNode = enteredNewNode;
            NewNodeValue = newNodeValue;
            TraversedNewEdge = traversedNewEdge;
            NewRegionBudget = newRegionBudget;
            ChangedNode = changedNode;
            CurrentNodeVisitCount = currentNodeVisitCount;
            FrontierDistanceDelta = frontierDistanceDelta;
            HasFrontierProgress = hasFrontierProgress;
            StepsSinceNewNode = stepsSinceNewNode;
            IsTouchingWall = isTouchingWall;
            FrontierRewardScale = frontierRewardScale;
        }
    }
}
