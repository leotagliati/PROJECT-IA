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
        /// Chegou a um nó PRIMÁRIO onde ainda não tinha estado neste episódio. Booleano, usado
        /// só como porteira — quem paga é o <see cref="NewNodeValue"/>.
        /// </summary>
        public readonly bool EnteredNewNode;

        /// <summary>
        /// Soma dos PESOS (NavNode.ExplorationWeight) dos nós inéditos deste intervalo. A
        /// recompensa multiplica isto por um fator e pronto — a contagem de nós não aparece
        /// em lugar nenhum do cálculo, de propósito.
        /// </summary>
        public readonly float NewNodeValue;

        /// <summary>
        /// Quantas arestas inéditas (entre quaisquer dois nós) foram percorridas neste
        /// intervalo. Paga o CAMINHO, não só o destino — é o único sinal denso do sistema.
        /// </summary>
        public readonly int NewEdgeCount;

        /// <summary>Trocou de nó neste step, visitado ou não.</summary>
        public readonly bool ChangedNode;

        /// <summary>Visitas ao nó atual neste episódio. 1 na primeira; cresce ao revisitar.</summary>
        public readonly int CurrentNodeVisitCount;

        public readonly int StepsSinceNewNode;

        public readonly bool IsTouchingWall;

        public GraphStepContext(
            int maxEpisodeSteps,
            bool enteredNewNode,
            float newNodeValue,
            int newEdgeCount,
            bool changedNode,
            int currentNodeVisitCount,
            int stepsSinceNewNode,
            bool isTouchingWall)
        {
            MaxEpisodeSteps = maxEpisodeSteps;
            EnteredNewNode = enteredNewNode;
            NewNodeValue = newNodeValue;
            NewEdgeCount = newEdgeCount;
            ChangedNode = changedNode;
            CurrentNodeVisitCount = currentNodeVisitCount;
            StepsSinceNewNode = stepsSinceNewNode;
            IsTouchingWall = isTouchingWall;
        }
    }
}
