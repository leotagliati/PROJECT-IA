using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa da exploração por grafo. Recebe um
    /// <see cref="GraphStepContext"/> e devolve o delta do step. Todo o tuning mora aqui.
    ///
    /// ORÇAMENTO (faça a conta antes de treinar, é o que determina o comportamento):
    ///   total_positivo ~= Σ pesos_dos_primários x _nodeCoverageReward
    ///                     + arestas_do_grafo x _newEdgeReward
    ///                     + _fullCoverageReward
    /// O peso é declarado NÓ A NÓ (NavNode.ExplorationWeight), então a densidade de primários
    /// entra na conta: dois primários de peso 1 na mesma sala pagam o dobro de um. Ao adensar
    /// uma sala, reparta o peso entre os nós dela para o total do mapa não inflar.
    ///
    /// No mapa atual (23 primários de peso 1, 120 arestas), com os defaults abaixo:
    ///   23x0.75 = +17.25 | 120x0.02 = +2.4 | +5.0  =>  ~+24.6
    /// contra -2 de pressão existencial. A folga é enorme DE PROPÓSITO: aqui, diferente do
    /// seeker, explorar não compete com nenhum outro objetivo — explorar É o objetivo.
    ///
    /// O QUE NÃO EXISTE MAIS, de propósito:
    ///   - bônus por entrar/concluir REGIÃO (saíram com as regiões; o peso por nó faz o papel);
    ///   - shaping de FRONTEIRA (a "seta roxa": recompensa por se aproximar do não-visitado mais
    ///     próximo). Era um sinal DIRECIONAL — dizia para onde ir — e a política aprendia a
    ///     seguir a seta em vez de explorar. O único sinal denso agora é a aresta inédita, que
    ///     paga "ir por onde nunca fui" sem opinar sobre qual caminho. A direção fica por conta
    ///     da observação (valor descontado atrás de cada saída) e da política.
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
        // explode em corredor).
        //
        // O valor tem que ser lido junto com _maxEpisodeSteps, porque o que importa é o TETO:
        //   0.00025 x 8000 steps = -2.0 por episódio, a mesma ordem da existencial.
        // A existencial é diluída e vale -2 em qualquer duração; estas não são. Ao mudar a
        // duração do episódio, reescale as duas para manter o teto:
        //   4000 steps -> parede 0.0005,  estagnação 0.001
        //   8000 steps -> parede 0.00025, estagnação 0.0005
        //
        // Era 0.002, copiado do seeker. Lá o número está certo porque os episódios dele duram
        // ~150 steps (custo total ~0.3, irrelevante); aqui duram 4000, e o mesmo número virava
        // -8.0 — quatro vezes a existencial e da ordem de TODA a recompensa de cobertura do
        // mapa. Num labirinto, onde raspar parede é a condição normal de andar em corredor,
        // isso ensina a não entrar em corredor nenhum. Ao trocar de agente, reconfira o teto,
        // não o valor por step.
        [SerializeField] private float _wallContactPenalty = 0.00025f;

        // Antídoto para o agente que entala numa quina ou orbita um nó já visitado. Só entra
        // depois de _stagnationSteps sem NÓ NOVO — não sem movimento: andar em círculo por uma
        // sala inteira já explorada é exatamente o comportamento que queremos encarecer.
        // Mesma leitura por TETO: 0.0005 x (8000 - 1250) = -3.4 por episódio no pior caso, o
        // que a mantém como um empurrão contra entalar, e não como a maior força do sistema.
        [SerializeField] private float _stagnationPenalty = 0.0005f;

        // Em steps de FÍSICA (o DecisionRequester da cena usa TakeActionsBetweenDecisions, então
        // OnActionReceived roda todo FixedUpdate). 1250 steps = 25 s a 0.02 de timestep.
        //
        // O valor antigo, 250, foi calibrado como se fosse em decisões: davam 5 SEGUNDOS sem nó
        // novo. Uma aresta de 14 m a 5 u/s leva 2,8 s em linha reta perfeita e o dobro ou o
        // triplo com curva e porta — a penalidade disparava durante a viagem legítima entre dois
        // nós e cancelava o prêmio da chegada. O limiar tem que ser MAIOR que a travessia normal
        // do mapa: ele existe para punir quem entalou numa quina, não quem está a caminho.
        [SerializeField] private int _stagnationSteps = 1250;

        // Cobrada ao CHEGAR a um nó já visitado, escalada por 1/visitas. Default 0: com a
        // recompensa por aresta paga uma vez só, revisitar já rende zero, e voltar por onde veio
        // é obrigatório em corredor sem saída — punir isso ensina o agente a evitar becos, que
        // num mapa de salas significa não entrar em sala nenhuma. Suba só se o run mostrar
        // vai-e-vem crônico.
        [SerializeField] private float _revisitPenalty = 0f;

        [Header("-----Recompensas de exploração-----")]
        // O sinal principal, e o ÚNICO conversor de "peso" em "recompensa": descobrir um nó de
        // peso 1 rende exatamente este valor. Mexer aqui reescala o mapa inteiro de uma vez;
        // mexer no peso de um NavNode reescala só ele. Duas alavancas, dois escopos.
        [FormerlySerializedAs("_regionCoverageReward")]
        [SerializeField] private float _nodeCoverageReward = 0.75f;

        // Paga o TRAJETO inédito, não o destino: qualquer aresta do grafo, entre quaisquer dois
        // nós, uma vez por episódio. É o ÚNICO sinal denso do sistema desde que a seta saiu, e
        // é não-direcional de propósito — "andei por onde nunca andei" paga o mesmo em qualquer
        // rumo, então ele ensina a variar caminho, não a seguir um.
        //
        // Era 0.05 e só entre primários (para uma malha auxiliar densa não inflar a renda).
        // Agora vale para toda aresta, então o valor caiu para o TETO continuar pequeno:
        //   120 arestas x 0.02 = 2.4, contra 17.25 de cobertura — o trajeto guia, a chegada manda.
        // Num mapa com muito mais arestas, reduza; a conta é sempre arestas x valor << cobertura.
        [SerializeField] private float _newEdgeReward = 0.02f;

        // Prêmio por cobrir a fração-alvo do grafo (a lição define o alvo). Encerra o episódio.
        [SerializeField] private float _fullCoverageReward = 5f;

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

            // Peso dos nós descobertos neste intervalo (zero quando não houve nenhum).
            reward += _nodeCoverageReward * context.NewNodeValue;

            // Contagem, e não booleano: com Decision Period 5 e malha densa o agente pode cruzar
            // duas arestas curtas entre duas decisões, e cada uma tem que pagar a sua.
            reward += _newEdgeReward * context.NewEdgeCount;

            if (context.ChangedNode && !context.EnteredNewNode && _revisitPenalty > 0f)
                reward -= _revisitPenalty / Mathf.Max(1, context.CurrentNodeVisitCount);

            return reward;
        }
    }
}
