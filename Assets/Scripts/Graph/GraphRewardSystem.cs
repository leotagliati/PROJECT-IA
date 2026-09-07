using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa da exploração por grafo. Recebe um
    /// <see cref="GraphStepContext"/> e devolve o delta do step. Todo o tuning mora aqui.
    ///
    /// ORÇAMENTO (faça a conta antes de treinar, é o que determina o comportamento):
    ///   total_positivo ~= Σ orçamentos x _regionCoverageReward
    ///                     + Σ orçamentos x _regionEntryReward
    ///                     + arestas x _newEdgeReward
    ///                     + _fullCoverageReward
    /// Repare no que SUMIU da conta: o número de nós. Cobrir uma região paga o orçamento dela,
    /// tenha ela 3 ou 30 nós (ver NavRegion) — então adensar a malha para descrever melhor uma
    /// sala torta não muda mais o valor daquela sala.
    ///
    /// Num mapa de 8 regiões de orçamento 1 e 90 arestas, com os defaults abaixo:
    ///   8x0.75 = +6.0 | 8x0.50 = +4.0 | 90x0.05 = +4.5 | +5.0  =>  ~+19.5
    /// contra -2 de pressão existencial. A folga é enorme DE PROPÓSITO: aqui, diferente do
    /// seeker, explorar não compete com nenhum outro objetivo — explorar É o objetivo.
    /// </summary>
    public class GraphRewardSystem : MonoBehaviour
    {
        [Header("-----Penalidades-----")]
        // Diluída por step (custo total = este valor por episódio). Dá pressa sem punir
        // nenhuma ação específica: ficar parado custa igual a andar, então ela nunca ensina
        // imobilidade — só torna cada step desperdiçado levemente caro.
        [SerializeField] private float _existentialPenalty = 2f;

        // Por STEP em contato, não por evento de colisão (o Unity re-dispara OnCollisionEnter
        // dezenas de vezes por segundo ao deslizar numa parede, e uma penalidade por evento
        // explode em corredor). Mesma ordem de grandeza da existencial.
        [SerializeField] private float _wallContactPenalty = 0.002f;

        // Antídoto para o agente que entala numa quina ou orbita um nó já visitado. Só entra
        // depois de _stagnationSteps sem NÓ NOVO — não sem movimento: andar em círculo por uma
        // sala inteira já explorada é exatamente o comportamento que queremos encarecer.
        [SerializeField] private float _stagnationPenalty = 0.002f;
        [SerializeField] private int _stagnationSteps = 250;

        // Cobrada ao CHEGAR a um nó já visitado, escalada por 1/visitas. Default 0: com a
        // recompensa por aresta paga uma vez só, revisitar já rende zero, e voltar por onde veio
        // é obrigatório em corredor sem saída — punir isso ensina o agente a evitar becos, que
        // num mapa de salas significa não entrar em sala nenhuma. Suba só se o run mostrar
        // vai-e-vem crônico.
        [SerializeField] private float _revisitPenalty = 0f;

        [Header("-----Recompensas de exploração-----")]
        // O sinal principal, e o ÚNICO conversor de "orçamento" em "recompensa": cobrir uma
        // região de orçamento 1 rende exatamente este valor, distribuído entre os nós ativos
        // dela. Mexer aqui reescala o mapa inteiro de uma vez; mexer no orçamento de uma
        // NavRegion reescala só ela. Duas alavancas, dois escopos.
        [SerializeField] private float _regionCoverageReward = 0.75f;

        // Paga o TRAJETO inédito, não o destino. É o que dá gradiente dentro de um corredor
        // longo (onde só há dois nós e muitos steps entre eles) e o que diferencia "cheguei lá
        // por um caminho novo" de "cheguei lá de novo".
        [SerializeField] private float _newEdgeReward = 0.05f;

        // Bônus por PISAR pela primeira vez numa região, escalado pelo orçamento dela. É o
        // termo que empurra para TROCAR DE CÔMODO em vez de esmiuçar o atual — pagar só por
        // cobertura torna os dois indiferentes, e varrer a sala em que já se está é sempre mais
        // barato que arriscar uma porta.
        [SerializeField] private float _regionEntryReward = 0.5f;

        // Prêmio por cobrir a fração-alvo do grafo (a lição define o alvo). Encerra o episódio.
        [SerializeField] private float _fullCoverageReward = 5f;

        [Header("-----Shaping de fronteira-----")]
        // Por aresta de aproximação do não-visitado mais próximo. É um sinal DENSO: sem ele o
        // agente só recebe algo ao chegar num nó novo, e num mapa grande isso é esparso demais
        // para o PPO ligar a ação ao resultado.
        //
        // Cuidado com a intensidade: alto demais e a política vira "seguir a seta" — funciona,
        // mas o que foi aprendido é seguir a dica, não explorar. O currículo abaixa esse peso
        // nas lições finais justamente para o comportamento sobreviver sem ela.
        [SerializeField] private float _frontierProgressReward = 0.05f;

        public float FullCoverageReward => _fullCoverageReward;

        public void ResetEpisode()
        {
            // Sem estado entre steps por enquanto — existe para espelhar o ciclo dos outros
            // sistemas e para que adicionar um termo com histórico não exija mexer no manager.
        }

        public float EvaluateStep(in GraphStepContext context)
        {
            float reward = 0f;

            if (context.MaxEpisodeSteps > 0)
                reward -= _existentialPenalty / context.MaxEpisodeSteps;

            if (context.IsTouchingWall)
                reward -= _wallContactPenalty;

            if (context.StepsSinceNewNode > _stagnationSteps)
                reward -= _stagnationPenalty;

            // Já vem normalizado por região: é a fração do orçamento daquela região que este nó
            // representa. Nenhuma contagem de nós entra aqui.
            reward += _regionCoverageReward * context.NewNodeValue;

            if (context.TraversedNewEdge)
                reward += _newEdgeReward;

            reward += _regionEntryReward * context.NewRegionBudget;

            if (context.ChangedNode && !context.EnteredNewNode && _revisitPenalty > 0f)
                reward -= _revisitPenalty / Mathf.Max(1, context.CurrentNodeVisitCount);

            // Só quando os dois steps mediram a distância até o MESMO alvo (ver
            // HasFrontierProgress). Esse cuidado é o que impede o shaping de virar ruído a cada
            // descoberta.
            if (context.HasFrontierProgress)
                reward += _frontierProgressReward * context.FrontierDistanceDelta * context.FrontierRewardScale;

            return reward;
        }
    }
}
