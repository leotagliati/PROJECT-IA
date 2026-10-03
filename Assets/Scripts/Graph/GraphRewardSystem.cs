using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa da exploração por grafo. Recebe um
    /// <see cref="GraphStepContext"/> e devolve o delta do step. Todo o tuning mora aqui.
    ///
    /// EXPLORAÇÃO POR SALAS E PORTAS (docs/graph/salas-e-portas.md). A GraphRoomMemory entrega
    /// UNIDADES (sala inteira = 1, porta nova = 1) e aqui cada uma vira recompensa com um fator só.
    /// Toda sala vale o MESMO, do armário de 1 ladrilho ao corredor de 20: o peso por área do nó
    /// saiu. O que diferencia as salas para o agente é o que falta nelas e quantas portas novas têm.
    ///
    /// ORÇAMENTO (faça a conta antes de treinar, é o que determina o comportamento):
    ///   total_positivo ~= salas x (_roomExploreReward + _roomCompletedReward)
    ///                     + travessias_novas x _doorCrossReward (+ repetições, que somam no máximo 2x)
    ///                     + saídas_de_sala_concluída x _roomExitReward
    ///                     + _fullCoverageReward
    ///                     + pings x _pingReachedReward
    ///                     + avistamentos x _hiderSpottedReward + metros_aproximados_vendo x _hiderApproachReward
    ///                     + _hiderCaughtReward (terminal)
    /// A SUSPEITA não paga sozinha (saiu o _suspicionClearedReward, 02/10): ela multiplica o valor de VER
    /// a sala onde o hider provavelmente está (GraphRoomMemory). Um sinal só — ver tudo —, senão
    /// "limpar suspeita" e "explorar" competiam e a suspeita ganhava (v4_noite_01).
    /// (exploração multiplicada por discovery_reward_scale; ping por ping_reward_scale)
    ///
    /// No NodeTraining5 (26 salas, 35 portas), com os defaults abaixo e o mapa inteiro coberto:
    ///   26 x (0.25 + 0.25) = 13.0 | 35 x 0.1 = 3.5 | ~26 x 0.15 = 3.9 | +5
    ///   => ~25 (com o alvo de 80% das salas, ~19), contra -2 de pressão existencial e ~-3 de
    ///   parede num episódio ruim. A mesma ordem do orçamento por nó que saiu (~17 + 5), então as
    ///   penalidades ficaram como estavam.
    ///
    /// NENHUM termo paga "aproximar-se de um alvo escolhido por algoritmo" (a seta, ou o alvo
    /// local que existiu por um dia): tudo aqui é EVENTO que o agente causa — pisar chão novo,
    /// concluir sala, atravessar porta. O gradiente dentro da sala vem da fatia por nó novo.
    /// Nenhum termo conta NÓS de caminho: a fatia de sala é 1/⌈0.8 x N⌉ por nó, então re-ladrilhar
    /// uma sala muda a fatia, não o total dela.
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
        //   0.00075 x 8000 steps = -6.0 por episódio no pior caso (encostado o tempo TODO).
        // A existencial é diluída e vale -2 em qualquer duração; estas não são. Ao mudar a
        // duração do episódio, reescale as duas para manter o teto:
        //   4000 steps -> parede 0.0005,  estagnação 0.001
        //   8000 steps -> parede 0.00075, estagnação 0.0005
        //
        // Subiu de 0.00025 para 0.00075 depois do node4_patrol_02: WallContactFraction 0.43 (43%
        // do episódio encostado) custava só ~-0.9 — barato demais para ele se importar. Com
        // 0.00075 o mesmo contato custa ~-2.6, e a métrica diz se funcionou. O teto de -6 só
        // acontece encostado o episódio inteiro, o que nenhuma política boa faz. Além deste custo
        // CONTÍNUO há o custo por BATIDA, escalonado (_wallHitPenalty, abaixo).
        //
        // Era 0.002, copiado do seeker. Lá o número está certo porque os episódios dele duram
        // ~150 steps (custo total ~0.3, irrelevante); aqui duram 4000, e o mesmo número virava
        // -8.0 — quatro vezes a existencial e da ordem de TODA a recompensa de cobertura do
        // mapa. Num labirinto, onde raspar parede é a condição normal de andar em corredor,
        // isso ensina a não entrar em corredor nenhum. Ao trocar de agente, reconfira o teto,
        // não o valor por step.
        [SerializeField] private float _wallContactPenalty = 0.00075f;

        // Por BATIDA (início de contato, WallHitTracker), vezes quantas batidas houve nos
        // últimos ~5 s (até _wallHitEscalationCap): a 1ª custa 0.03, a 2ª seguida 0.06, a 3ª 0.09...
        // O contínuo acima cobra "ficar encostado"; este cobra "ricochetear" — bater, desgrudar e
        // bater de novo, que o contínuo mal vê (cada contato dura poucos steps).
        // TETO: ~30 batidas isoladas num episódio = -0.9; todas em rajada (x5) = -4.5.
        [SerializeField] private float _wallHitPenalty = 0.03f;
        [SerializeField, Min(1)] private int _wallHitEscalationCap = 5;

        // SUAVIDADE: custo por |mudança de ação|² a cada decisão. O agente mudava de direção à toa
        // (o "beyblade"): nada no treino cobrava isso, e com a ação sorteada em volta da média o
        // corpo tremia. Ir reto custa zero; curva suave, quase nada. Preferido a PAGAR por ir
        // reto, que viraria fonte de pontos (correr reto num corredor, ou contra a parede).
        // TETO: tremor típico (|Δ| ~0.5) x 1600 decisões = -0.2/episódio; inverter a direção
        // TODA decisão (|Δ|² = 8) = -6.4 — caso patológico.
        [SerializeField] private float _actionChangePenalty = 0.0005f;

        // SUAVIDADE DO OLHAR: o mesmo custo para as ações de olhar [2..3]. Com o olhar separado
        // do andar, um olhar que treme faz o cone de visão piscar — e o beyblade volta pela
        // cabeça. Metade do de movimento porque olhar em volta (varrer a sala da porta) é
        // legítimo e queremos barato.
        // TETO: tremor típico (|Δ| ~0.5) x 1600 decisões = -0.1/episódio; inverter o olhar TODA
        // decisão (|Δ|² = 8) = -3.2 — caso patológico.
        [SerializeField] private float _lookChangePenalty = 0.00025f;

        // Antídoto para o agente que entala numa quina ou orbita uma sala já vista. Só entra
        // depois de _stagnationSteps sem PROGRESSO DE SALA (nó novo de sala, sala concluída ou
        // porta com novidade >= 0.25) — não sem movimento: andar em círculo por uma sala inteira
        // já explorada é exatamente o comportamento que queremos encarecer.
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

        // LOOP: custo por chegada numa PORTA pisada há menos de ~15 s
        // (GraphExplorationMemory._earlyRevisitWindowSteps), mas só depois de
        // _earlyRevisitGrace revisitas assim SEGUIDAS. A tolerância existe porque sair de um beco
        // passa legitimamente pela porta por onde entrou — punir isso ensina a não entrar em sala
        // nenhuma. A partir da 4ª seguida é rodar em círculo entre vãos.
        //
        // TETO: ~40 chegadas precoces num episódio muito ruim x 0.05 = -2, a mesma ordem da
        // existencial. É um empurrão, não a força dominante.
        [SerializeField] private float _earlyRevisitPenalty = 0.05f;
        [SerializeField, Min(0)] private int _earlyRevisitGrace = 3;

        [Header("-----Salas e portas-----")]
        // DESCOBRIR A SALA: a sala inteira (as fatias até 80% dos nós dela) vale isto, qualquer que
        // seja o tamanho. Paga aos poucos, por nó novo — é o gradiente dentro da sala.
        [SerializeField] private float _roomExploreReward = 0.25f;

        // Fração do valor de um nó que a CAUDA paga: nós ainda não pisados de uma sala já
        // concluída. Baixo de propósito ("a partir de 80% não compensa tanto"): varrer o último
        // canto de uma sala tem que perder para ir à próxima.
        [SerializeField, Range(0f, 1f)] private float _completedRoomNodeFraction = 0.2f;

        // CONCLUIR A SALA (80% dos nós pisados, room_complete_threshold). Uma vez por sala (e de
        // novo, valendo menos, se ela for liberada na patrulha).
        [SerializeField] private float _roomCompletedReward = 0.25f;

        // ATRAVESSAR UMA PORTA, vezes a novidade dela (1 nova, 0.5 na 2ª vez, 0.25...). Pequeno:
        // passar por portas é meio, não fim — sozinho não pode valer mais que descobrir a sala
        // do outro lado. TETO de vai-e-vem num vão: 2 x isto.
        [SerializeField] private float _doorCrossReward = 0.1f;

        // SAIR DE SALA CONCLUÍDA, vezes a novidade da porta. É o "terminei aqui, vá para outra",
        // e é a novidade que faz "sair por outra porta" valer mais que voltar por onde entrou.
        [SerializeField] private float _roomExitReward = 0.15f;

        // Prêmio por concluir a fração-alvo das SALAS (coverage_target da lição). Encerra o episódio.
        [SerializeField] private float _fullCoverageReward = 5f;

        [Header("-----Ping-----")]
        // (Saiu em 01/10 o _pingApproachPerMeter, que pagava por metro de aproximação do ping PELO
        // GRAFO: era seguir o caminho que o algoritmo calcula até o alvo. O caminho até o barulho
        // agora é incentivado pela SALA quente — explorar a sala do ping vale mais —, e o agente só
        // recebe a distância e o quente/frio como informação.)

        // Chegou ao nó do ping enquanto ele ainda tocava, vezes NavGraph.PingValue (1 no padrão).
        // Maior que uma sala inteira (0.25 + 0.25):
        // atender o ping tem que valer mais que continuar explorando ali perto, senão a
        // política aprende a ignorá-lo. Menor que a conclusão (5): não é o objetivo do episódio.
        [SerializeField] private float _pingReachedReward = 2f;

        // Ping expirou sem visita. Cobra a omissão uma vez, e é da ordem do que a chegada
        // pagaria — deixar de ir tem que ser pior que tentar e chegar tarde (que custa só o
        // vai-e-vem). TETO: com um ping a cada ~2000 steps são até 4 por episódio = -2.0 no
        // pior caso, a mesma ordem da existencial.
        [SerializeField] private float _pingMissedPenalty = 0.5f;

        [Header("-----Visão do hider-----")]
        // Bônus por AVISTAR o hider (entrar no cone com linha de visão livre), com cooldown de
        // 5 s entre aquisições (GraphHiderPerception) para não render piscando numa quina. Da
        // ordem de um nó de cobertura: ver o hider vale, mas não vale abandonar o mapa por
        // qualquer sombra. TETO: ~4 avistamentos por episódio = +2.
        // 1.0 na fuga (era 0.5); TETO ~4 avistamentos = +4.
        [SerializeField] private float _hiderSpottedReward = 1f;

        // Por METRO de aproximação ENQUANTO VÊ. Diferença de distâncias: afastar cobra o que
        // aproximar pagou, então não é farmável. Só conta quando via nas duas decisões — no
        // step em que perde ou ganha visão a distância salta e não é progresso. 0.05/m a 15 m
        // = +0.75 por aproximação completa, a mesma ordem de um nó: chegar perto do hider vale
        // tanto quanto descobrir uma sala.
        // 0.1 na fuga (era 0.05): +1.5 por aproximação completa.
        [SerializeField] private float _hiderApproachReward = 0.1f;

        // PEGOU o hider (GraphHiderPerception.Caught): paga e ENCERRA o episódio, como a
        // cobertura. Sem este termo a perseguição não tinha fim — o agente ganhava por ver e por
        // se aproximar, mas o episódio só acabava por cobertura ou tempo, então nas lições de caça
        // o incentivo final continuava sendo varrer o mapa.
        //
        // 10, o dobro da conclusão por cobertura (5): nas lições de caça pegar tem que valer mais
        // que o resto do mapa que ele deixaria de explorar ao encerrar. Na conta das salas, metade do
        // NodeTraining5 por explorar é ~13 x (0.5 + 0.1 + 0.15) ≈ 9.8 no máximo (com a caça em
        // discovery_reward_scale 0.3, ~3), e na prática ele já cobriu parte
        // disso até achar o hider — 10 + fim da pressão existencial ganha de "explorar e depois pegar".
        //
        // 20 na fuga (03/10, pedido do Arthur: "caça bem mais valiosa"). Com discovery_reward_scale 0.5
        // o mapa inteiro rende ~12 no máximo, então pegar (20 a 45) é de longe o maior prêmio do episódio.
        [SerializeField] private float _hiderCaughtReward = 20f;

        // MANTER O HIDER EM VISÃO: por decisão com ele no cone e linha livre. É o que faz o seeker
        // seguir o alvo em vez de só avistar e perder. TETO: 1600 decisões x 0.003 = 4.8 se o visse o
        // episódio todo — abaixo da captura (20+), então perseguir sem pegar nunca compensa mais
        // que pegar. Não é por distância nem por caminho: é só o evento "estou vendo".
        [SerializeField, Min(0f)] private float _hiderInViewReward = 0.003f;

        // BÔNUS POR PEGAR CEDO: somado à captura, proporcional à fração do episódio que SOBRA.
        // Pegar encerra o episódio — e com a patrulha ligada isso corta a renda que ele ainda
        // teria. Medido no node4_patrol_02: ~+16.6 por episódio de 160 s (reward 14.6 + a
        // existencial 2) = ~0.1/s. Pegar aos 60 s deixava ~10 na mesa, empatando com os 10 da
        // captura: caçar não valia a pena. Com 15 x fração restante, a renda perdida é coberta e
        // a captura (10) fica sempre como lucro: pegar no início = 25, no fim = 10.
        // 25 na fuga (era 15): pegar no início = 45, no fim = 20.
        [SerializeField, Min(0f)] private float _hiderCaughtEarlyBonus = 25f;

        public float FullCoverageReward => _fullCoverageReward;

        /// <summary>Captura + bônus pela fração do episódio que ainda restava (0..1).</summary>
        public float HiderCaughtReward(float remainingFraction) =>
            _hiderCaughtReward + _hiderCaughtEarlyBonus * Mathf.Clamp01(remainingFraction);

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

            if (context.WallHits > 0)
                reward -= _wallHitPenalty * context.WallHits * Mathf.Min(context.RecentWallHits, _wallHitEscalationCap);

            reward -= _actionChangePenalty * context.ActionChangeSq;
            reward -= _lookChangePenalty * context.LookChangeSq;

            if (context.StepsSinceProgress > _stagnationSteps)
                reward -= _stagnationPenalty;

            // Exploração por salas. Tudo escala com discovery_reward_scale (na caça, explorar é meio).
            float discovery = context.DiscoveryRewardScale;
            reward += _roomExploreReward * (context.RoomNodeValue + _completedRoomNodeFraction * context.RoomTailValue) * discovery;
            reward += _roomCompletedReward * context.RoomCompletedValue * discovery;
            reward += _doorCrossReward * context.DoorCrossValue * discovery;
            reward += _roomExitReward * context.RoomExitValue * discovery;

            if (context.EarlyRevisitArrivals > 0 && context.EarlyRevisitStreak > _earlyRevisitGrace)
                reward -= _earlyRevisitPenalty * context.EarlyRevisitArrivals;

            // Ping: objetivo, não muleta. Escala com ping_reward_scale (0 = o ping só informa).
            // Escalado pelo valor do ping (NavGraph.PingValue, a pontuação do ping; 1 = 2.0 por ping).
            if (context.PingReached)
                reward += _pingReachedReward * context.PingReachedValue * context.PingRewardScale;

            if (context.PingMissed)
                reward -= _pingMissedPenalty * context.PingRewardScale;

            // Visão: também não escala com a lição.
            if (context.HiderSpotted)
                reward += _hiderSpottedReward;

            if (context.HasHiderApproach)
                reward += _hiderApproachReward * context.HiderApproachDelta;

            if (context.HiderInView)
                reward += _hiderInViewReward;

            return reward;
        }
    }
}
