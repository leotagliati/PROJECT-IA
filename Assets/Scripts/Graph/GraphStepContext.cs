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

        /// <summary>
        /// Soma dos orçamentos das regiões CONCLUÍDAS neste intervalo (todos os pontos de
        /// vantagem visitados). Separado de <see cref="NewRegionBudget"/> de propósito: entrar
        /// numa sala e ter visto a sala inteira são conquistas diferentes e devem ter preços
        /// diferentes — pagar só a entrada torna espiar a porta tão bom quanto varrer o cômodo.
        /// </summary>
        public readonly float CompletedRegionBudget;

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

        public GraphStepContext(
            int maxEpisodeSteps,
            bool enteredNewNode,
            float newNodeValue,
            bool traversedNewEdge,
            float newRegionBudget,
            float completedRegionBudget,
            bool changedNode,
            int currentNodeVisitCount,
            int frontierDistanceDelta,
            bool hasFrontierProgress,
            float frontierApproachDelta,
            bool hasFrontierApproach,
            int stepsSinceNewNode,
            bool isTouchingWall,
            float frontierRewardScale)
        {
            MaxEpisodeSteps = maxEpisodeSteps;
            EnteredNewNode = enteredNewNode;
            NewNodeValue = newNodeValue;
            TraversedNewEdge = traversedNewEdge;
            NewRegionBudget = newRegionBudget;
            CompletedRegionBudget = completedRegionBudget;
            ChangedNode = changedNode;
            CurrentNodeVisitCount = currentNodeVisitCount;
            FrontierDistanceDelta = frontierDistanceDelta;
            HasFrontierProgress = hasFrontierProgress;
            FrontierApproachDelta = frontierApproachDelta;
            HasFrontierApproach = hasFrontierApproach;
            StepsSinceNewNode = stepsSinceNewNode;
            IsTouchingWall = isTouchingWall;
            FrontierRewardScale = frontierRewardScale;
        }
    }
}
