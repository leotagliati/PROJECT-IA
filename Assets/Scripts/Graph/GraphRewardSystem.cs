using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa da exploração por grafo. Recebe um
    /// <see cref="GraphStepContext"/> e devolve o delta do step. Todo o tuning mora aqui.
    ///
    /// ORÇAMENTO (faça a conta antes de treinar, é o que determina o comportamento):
    ///   total_positivo ~= Σ pesos_dos_primários x pontuação_exploração x _nodeCoverageReward
    ///                     + auxiliares x pontuação_auxiliar x _nodeCoverageReward (0 no padrão)
    ///                     + arestas_entre_primários x _newEdgeReward
    ///                     + caminho_percorrido_em_metros x (_frontierApproachReward + _frontierProgressPerMeter)
    ///                     + _fullCoverageReward
    ///                     + pings_por_episódio x (_pingReachedReward x valor_do_ping + metros_de_caminho x _pingApproachPerMeter)
    /// (as três "pontuação_*" e o valor_do_ping = pontuação_ping x peso moram no NavGraph; 1, 0 e 1 no padrão)
    ///                     + avistamentos x _hiderSpottedReward + metros_aproximados_vendo x _hiderApproachReward
    ///                     + _hiderCaughtReward (terminal: pegou o hider)
    ///                     + suspeita_limpa x _suspicionClearedReward (procura, ~6 x 0.5 por episódio)
    /// (descoberta e ping multiplicados pelas escalas da lição: discovery_reward_scale, ping_reward_scale)
    /// Nenhum termo conta ARESTAS de caminho: todo shaping de distância é por metro, então
    /// adensar o grafo (o NavGraphPlacer cobre o chão inteiro com auxiliares) não mexe no
    /// orçamento. O que conta nós é só a cobertura, e ela conta PRIMÁRIOS — cujo peso total o
    /// placer mantém fixo (_primaryWeightBudget).
    /// O peso é declarado NÓ A NÓ (NavNode.ExplorationWeight), então a densidade de primários
    /// entra na conta: dois primários de peso 1 na mesma sala pagam o dobro de um. Ao adensar
    /// uma sala, reparta o peso entre os nós dela para o total do mapa não inflar.
    ///
    /// Num mapa com 12 primários de peso 1 e 20 arestas entre eles, com os defaults abaixo:
    ///   12x0.75 = +9.0 | 20x0.05 = +1.0 | +5.0  =>  ~+15.0
    /// contra -2 de pressão existencial. A folga é enorme DE PROPÓSITO: aqui, diferente do
    /// seeker, explorar não compete com nenhum outro objetivo — explorar É o objetivo.
    ///
    /// Os bônus por ENTRAR e por CONCLUIR uma região saíram junto com as regiões. O empurrão
    /// para trocar de cômodo em vez de esmiuçar o atual agora é só o peso dos nós de lá + a
    /// dica de fronteira; se o run mostrar o agente varrendo a mesma sala, suba o peso dos
    /// primários das salas vizinhas em vez de recriar um bônus de sala.
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

        // LOOP: custo por chegada num primário visitado há menos de ~15 s
        // (GraphExplorationMemory._earlyRevisitWindowSteps), mas só depois de
        // _earlyRevisitGrace revisitas assim SEGUIDAS. A tolerância existe porque voltar de um
        // beco passa legitimamente por 1–2 nós recém-visitados — punir isso ensina a não entrar em
        // sala nenhuma (a armadilha do _revisitPenalty acima). A partir da 4ª seguida é rodar em
        // círculo.
        //
        // TETO: ~40 chegadas precoces num episódio muito ruim x 0.05 = -2, a mesma ordem da
        // existencial. É um empurrão, não a força dominante.
        [SerializeField] private float _earlyRevisitPenalty = 0.05f;
        [SerializeField, Min(0)] private int _earlyRevisitGrace = 3;

        [Header("-----Tédio de sala-----")]
        // Por STEP de física enquanto a sala atual está com tédio acima do limiar (só com
        // area_boredom ligado). Mesma leitura por teto das outras penalidades por step:
        //   0.00025 x 8000 = -2.0 no pior caso (o episódio inteiro numa sala chata).
        // Com _boredomRiseSteps 1500, a sala passa de 0.7 depois de ~21 s dentro dela.
        [SerializeField] private float _boredomPenalty = 0.00025f;
        [SerializeField, Range(0f, 1f)] private float _boredomThreshold = 0.7f;

        [Header("-----Recompensas de exploração-----")]
        // O sinal principal, e o ÚNICO conversor de "valor de descoberta" em "recompensa":
        // descobrir um nó de valor 1 (pontuação do tipo no NavGraph x peso do nó) rende
        // exatamente este valor. Auxiliar só entra aqui se o NavGraph der pontuação ao tipo. Mexer aqui reescala o mapa inteiro de uma vez;
        // mexer no peso de um NavNode reescala só ele. Duas alavancas, dois escopos.
        [FormerlySerializedAs("_regionCoverageReward")]
        [SerializeField] private float _nodeCoverageReward = 0.75f;

        // Paga o TRAJETO inédito, não o destino. É o que dá gradiente dentro de um corredor
        // longo (onde só há dois nós e muitos steps entre eles) e o que diferencia "cheguei lá
        // por um caminho novo" de "cheguei lá de novo".
        [SerializeField] private float _newEdgeReward = 0.05f;

        // Prêmio por cobrir a fração-alvo do grafo (a lição define o alvo). Encerra o episódio.
        [SerializeField] private float _fullCoverageReward = 5f;

        [Header("-----Shaping de fronteira-----")]
        // Por METRO de aproximação do não-visitado mais próximo, medido PELO GRAFO a partir do nó
        // âncora (muda a cada troca de nó). Sem ele o agente só recebe algo ao chegar num nó
        // novo, e num mapa grande isso é esparso demais para o PPO ligar a ação ao resultado.
        //
        // Era 0.05 por ARESTA. 0.05 / 7.4 m (aresta mediana do mapa antigo) = 0.007 por metro:
        // o mesmo valor no mapa antigo, e o mesmo valor em qualquer densidade de nós. Por aresta,
        // o grafo coberto pelo placer (2–3x mais nós no corredor) pagaria 2–3x mais pela mesma
        // caminhada. Nome novo de propósito, para o 0.05 salvo no prefab não virar "0.05/m".
        //
        // Cuidado com a intensidade: alto demais e a política vira "seguir a seta" — funciona,
        // mas o que foi aprendido é seguir a dica, não explorar. O currículo abaixa esse peso
        // nas lições finais justamente para o comportamento sobreviver sem ela.
        [SerializeField] private float _frontierProgressPerMeter = 0.007f;

        // Por METRO de aproximação do próximo passo da fronteira. Este é o termo que faltava: o
        // _frontierProgressReward acima mede distância em ARESTAS, e distância em arestas só
        // muda quando o agente troca de nó — ou seja, ele é tão esparso quanto a chegada, e
        // durante a travessia inteira o agente só recebia penalidade.
        //
        // TETO deste valor, e a conta que você deve refazer a cada mapa novo:
        //
        //   chegada = peso_do_nó x _nodeCoverageReward
        //   teto    = chegada / aresta_mediana
        //
        // Acima do teto, percorrer a aresta paga mais que chegar ao nó, e o agente otimiza o
        // ANDAR em vez do CHEGAR. Abaixo dele, o gradual guia e os eventos continuam definindo
        // o objetivo — que é o arranjo que sobrevive ao currículo desligar a dica
        // (frontier_hint 0.5 -> 0.0 nas últimas lições).
        //
        // Não é farmável: é uma diferença de distâncias, então afastar cobra exatamente o que
        // aproximar pagou e o vai-e-vem rende zero. O limite acima é conceitual, não de exploit.
        //
        // Mesma família do _hiderApproachReward do seeker, que mede exatamente assim: a
        // diferença de distância euclidiana entre dois steps.
        [SerializeField] private float _frontierApproachReward = 0.02f;

        [Header("-----Navegação (sem seta)-----")]
        // Por METRO que a distância PELO GRAFO até o inexplorado mais próximo cai (e o mesmo cobrado
        // quando sobe). O shaping da seta (acima) morre com frontier_hint 0; este fica SEMPRE ligado.
        // Motivo: o mapa tem becos — para explorar mais, ele precisa VOLTAR por nós visitados, e essa
        // volta não pagava nada e ainda era punida pela estagnação. Com isto, voltar em direção ao
        // que falta paga; ir e voltar (ou o pisca-pisca de borda) soma zero, então não é farmável.
        // É a mesma distância da observação "quão perto por esta saída" — seguir o que ele vê é o
        // que paga, sem cabo de guerra.
        //
        // TETO: é diferença de potencial, então por alvo paga no máximo a distância até ele.
        // Somando os alvos de um episódio (~300 m andados em direção ao que falta) x 0.01 = ~+3,
        // contra ~17 da descoberta: guia sem mandar. Escala com discovery_reward_scale (é
        // exploração: na caça, 0.3).
        [SerializeField, Min(0f)] private float _unexploredProgressPerMeter = 0.01f;

        [Header("-----Ping-----")]
        // Por METRO de aproximação do nó que está tocando, medido PELO GRAFO (contornar parede
        // conta como progresso; linha reta não). Paga a cada troca de nó na direção certa, cobra
        // a cada troca na errada — vai-e-vem rende zero.
        //
        // Era 0.1 por ARESTA (~0.0135/m no mapa antigo). Por aresta, o mesmo trajeto num grafo
        // denso pagaria mais que a chegada. Com 0.015/m, um ping a 70 m de caminho rende ~1.0 no
        // trajeto, contra 2.0 pela chegada: chegar continua mandando, em qualquer densidade.
        [SerializeField] private float _pingApproachPerMeter = 0.015f;

        // Chegou ao nó do ping enquanto ele ainda tocava, vezes o valor do nó (NavGraph: pontuação
        // do tipo Ping x peso do nó; 1 no padrão). Maior que um nó de cobertura (0.75):
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
        [SerializeField] private float _hiderSpottedReward = 0.5f;

        // Por METRO de aproximação ENQUANTO VÊ. Diferença de distâncias: afastar cobra o que
        // aproximar pagou, então não é farmável. Só conta quando via nas duas decisões — no
        // step em que perde ou ganha visão a distância salta e não é progresso. 0.05/m a 15 m
        // = +0.75 por aproximação completa, a mesma ordem de um nó: chegar perto do hider vale
        // tanto quanto descobrir uma sala.
        [SerializeField] private float _hiderApproachReward = 0.05f;

        // PEGOU o hider (GraphHiderPerception.Caught): paga e ENCERRA o episódio, como a
        // cobertura. Sem este termo a perseguição não tinha fim — o agente ganhava por ver e por
        // se aproximar, mas o episódio só acabava por cobertura ou tempo, então nas lições de caça
        // o incentivo final continuava sendo varrer o mapa.
        //
        // 10, o dobro da conclusão por cobertura (5): nas lições de caça pegar tem que valer mais
        // que o resto do mapa que ele deixaria de explorar ao encerrar. Com previsited 0.5 o que
        // sobra de cobertura é ~23 x 0.5 x 0.75 ≈ 8.6 no máximo, e na prática ele já cobriu parte
        // disso até achar o hider — 10 + fim da pressão existencial ganha de "explorar e depois pegar".
        [SerializeField] private float _hiderCaughtReward = 10f;

        // BÔNUS POR PEGAR CEDO: somado à captura, proporcional à fração do episódio que SOBRA.
        // Pegar encerra o episódio — e com a patrulha ligada isso corta a renda que ele ainda
        // teria. Medido no node4_patrol_02: ~+16.6 por episódio de 160 s (reward 14.6 + a
        // existencial 2) = ~0.1/s. Pegar aos 60 s deixava ~10 na mesa, empatando com os 10 da
        // captura: caçar não valia a pena. Com 15 x fração restante, a renda perdida é coberta e
        // a captura (10) fica sempre como lucro: pegar no início = 25, no fim = 10.
        [SerializeField, Min(0f)] private float _hiderCaughtEarlyBonus = 15f;

        [Header("-----Procura-----")]
        // Por unidade de SUSPEITA zerada (GraphSuspicionMap): ver ou visitar nós vazios onde o
        // hider provavelmente estaria. A suspeita soma 1, então limpar a sala onde estava metade
        // da crença paga 0.25. Um episódio de procura boa zera ~6 vezes o que resta = ~+3. É o
        // shaping da busca; quem define o objetivo continua sendo a captura (10 a 25).
        // Não é farmável parado: cada nó só paga de novo 10 s depois de ser visto
        // (GraphSuspicionMap._reclearCooldownSteps), e olhar sem parar para o mesmo lugar reinicia
        // esse relógio.
        [SerializeField, Min(0f)] private float _suspicionClearedReward = 0.5f;

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

            if (context.StepsSinceNewNode > _stagnationSteps)
                reward -= _stagnationPenalty;

            // Peso dos nós descobertos neste intervalo (zero quando não houve nenhum). A escala da
            // lição (discovery_reward_scale) baixa isto na procura, onde explorar é meio, não fim.
            reward += _nodeCoverageReward * context.NewNodeValue * context.DiscoveryRewardScale;

            reward += _suspicionClearedReward * context.SuspicionCleared;

            reward += _unexploredProgressPerMeter * context.UnexploredProgress * context.DiscoveryRewardScale;

            // Patrulha: a revisita paga pelo MESMO conversor da descoberta. A diferença de valor
            // (no máximo 30%, recuperação², tédio) já vem da memória — um conversor só, para
            // "descobrir vale mais que voltar" não depender de dois números ficarem coerentes.
            reward += _nodeCoverageReward * context.RevisitValue;

            if (context.EarlyRevisitArrivals > 0 && context.EarlyRevisitStreak > _earlyRevisitGrace)
                reward -= _earlyRevisitPenalty * context.EarlyRevisitArrivals;

            if (context.CurrentAreaBoredom > _boredomThreshold)
                reward -= _boredomPenalty;

            if (context.TraversedNewEdge)
                reward += _newEdgeReward;

            if (context.ChangedNode && !context.EnteredNewNode && _revisitPenalty > 0f)
                reward -= _revisitPenalty / Mathf.Max(1, context.CurrentNodeVisitCount);

            // Só quando os dois steps mediram a distância até o MESMO alvo (ver
            // HasFrontierProgress). Esse cuidado é o que impede o shaping de virar ruído a cada
            // descoberta.
            if (context.HasFrontierProgress)
                reward += _frontierProgressPerMeter * context.FrontierDistanceDelta * context.FrontierRewardScale;

            // O sinal denso. Multiplicado pela mesma escala do currículo que o termo em arestas:
            // os dois são a MESMA muleta, e desligar só um deixaria metade da dependência de pé.
            if (context.HasFrontierApproach)
                reward += _frontierApproachReward * context.FrontierApproachDelta * context.FrontierRewardScale;

            // Ping: não escala com a DICA — ele é objetivo, não muleta. Escala com ping_reward_scale:
            // no config de procura é 0 e o ping vira só informação (a suspeita paga ir até lá).
            if (context.HasPingProgress)
                reward += _pingApproachPerMeter * context.PingDistanceDelta * context.PingRewardScale;

            // Escalado pelo valor do nó (pontuação do tipo Ping no NavGraph x peso do nó): com
            // pontuação 1 e peso 1, exatamente o prêmio de antes.
            if (context.PingReached)
                reward += _pingReachedReward * context.PingReachedValue * context.PingRewardScale;

            if (context.PingMissed)
                reward -= _pingMissedPenalty * context.PingRewardScale;

            // Visão: também não escala com a lição.
            if (context.HiderSpotted)
                reward += _hiderSpottedReward;

            if (context.HasHiderApproach)
                reward += _hiderApproachReward * context.HiderApproachDelta;

            return reward;
        }
    }
}
