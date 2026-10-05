using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa: recebe um <see cref="GraphStepContext"/>, devolve o delta do
    /// step. Todo o tuning mora aqui, INCLUSIVE os bônus que encerram o episódio (captura, cobertura): o
    /// Manager só chama AddReward(EvaluateStep(contexto)), nunca soma recompensa por fora. Toda sala vale
    /// o MESMO na exploração (a GraphRoomMemory entrega unidades: sala inteira = 1, já multiplicada pelo
    /// calor do ping e pela suspeita do hider quando há) e aqui cada unidade vira recompensa.
    /// Nenhum termo paga aproximar-se de alvo escolhido por algoritmo: só eventos que o agente causa (a
    /// aproximação do hider só conta com ele em vista).
    ///
    /// ORÇAMENTO por episódio (v5.1: 35000 steps de física = 700 s = 7000 decisões; "x escala" =
    /// discovery_reward_scale ou ping_reward_scale). Custos POR STEP divididos por 2.5 (8000 -> 20000 steps)
    /// e depois x 20000/35000 (400 s -> 700 s), para o TETO de cada um ficar igual ao da v4:
    ///   Termo (valor)                              Teto / total
    ///   existencial (2 / steps do episódio)        -2.0
    ///   contato com parede (0.00034/step)          -12.0 encostado o tempo todo (paredes e móveis; porta x0.25)
    ///   batida (0.06 x batidas x até 5)            -1.8 (30 isoladas) a -9.0 (rajada)
    ///   suavidade do andar (0.0002 x |Δ|²)         -0.2 típico; -6.4 patológico
    ///   suavidade do olhar (0.0001 x |Δ|²)         -0.1 típico; -3.2 patológico
    ///   estagnação (0.00011/step após 2500 steps)  -3.6
    ///   revisita precoce (0.05, após 3 seguidas)   ~-2.0 (40 chegadas)
    ///   ping perdido (0.5)                         -2.0 (4 pings)
    ///   sala explorada (fatias, 0 na v5.1)         0 (a sala paga UMA vez, ao concluir)
    ///   sala concluída (0.5 x crescente x rara)    ~+19 a ~+30 (26 salas; rara = 1..2) x escala
    ///   migalhas (0.5/sala grande, >= 10 nós)       ~+2.5 (S12, S16, S24 x valor) x escala
    ///   porta (0.1, só a 1ª travessia)             +3.5 (35 portas) x escala
    ///   saída de sala concluída (0.15, 1ª vez)     ~+2.5 x escala
    ///   cobertura (5, encerra)                     +5
    ///   ping atendido (5 x PingValue)              +20 (4 pings) x escala
    ///   avistar hider (2, cooldown 5 s)            ~+8 (4 avistamentos)
    ///   aproximar vendo (0.4/m)                    +6 por 15 m em linha reta
    ///   hider em vista (0.00086/step)              +30 o episódio todo
    ///   suspeita zerada (2 x massa de 0 a 1)       ~2 por crença inteira limpa
    ///   captura (20 + 25 x fração restante)        20 a 45 (encerra)
    /// NodeTraining5 (26 salas, 35 portas) coberto por inteiro: ~25 de exploração + 5 da cobertura.
    /// </summary>
    public class GraphRewardSystem : MonoBehaviour
    {
        [Header("-----Penalidades-----")]
        // Diluída por step (custo total = este valor por episódio): dá pressa sem punir ação
        // nenhuma, então nunca ensina imobilidade.
        [SerializeField] private float _existentialPenalty = 2f;

        // Por STEP em contato, não por evento (o Unity re-dispara a colisão ao deslizar). Ao mudar
        // _maxEpisodeSteps, reescale pelo teto (valor x steps), não pelo valor por step.
        // 0.00034 na v5.1 = 0.0015 da v4.4 x 8000 / 35000 (mesmo teto, -12). Vale para paredes e móveis; a porta
        // (layer Door, GraphExplorerManager._doorLayer) paga x _doorPenaltyScale.
        [SerializeField] private float _wallContactPenalty = 0.00034f;

        // Fração do custo de parede (contato e batida) que a PORTA paga. 0.25: o vão tem 2 m e o corpo 1.38, então
        // raspar o batente passando é quase inevitável; custo cheio ensinou a evitar portas na v4.2, e zero deixava
        // a parede inteira da peça Door_Hole grátis. Ainda custa, então mirar o meio do vão compensa. 1 = igual à parede.
        [SerializeField, Min(0f)] private float _doorPenaltyScale = 0.25f;

        // Por BATIDA (início de contato, GraphBodyTracker) x batidas nos últimos ~5 s (até
        // _wallHitEscalationCap): 0.06, 0.12, 0.18... Cobra o ricochete, que o contínuo mal vê.
        // 0.06 na v4.4 (era 0.03): a 15-20 m/s, bater é correr para a parede.
        [SerializeField] private float _wallHitPenalty = 0.06f;
        [SerializeField, Min(1)] private int _wallHitEscalationCap = 5;

        // Custo por |mudança de ação|² a cada decisão, contra o "beyblade". Ir reto custa zero;
        // pagar por ir reto seria farmável. 0.0002 na v5 (era 0.0005): 2.5x mais decisões por episódio.
        [SerializeField] private float _actionChangePenalty = 0.0002f;

        // O mesmo para as ações de olhar [2..3], metade do preço porque varrer a sala com o olhar é legítimo.
        [SerializeField] private float _lookChangePenalty = 0.0001f;

        // Contra entalar numa quina ou orbitar sala já vista. Só entra após _stagnationSteps sem
        // PROGRESSO DE SALA (nó novo, sala concluída ou porta com novidade >= 0.25), não sem movimento.
        // 0.00011 na v5.1 (0.0002 com 400 s): teto ~-3.6 com o episódio de 35000 steps.
        [SerializeField] private float _stagnationPenalty = 0.00011f;

        // Em steps de FÍSICA (2500 = 50 s; era 25 s a 15 m/s, agora anda a 6). Tem que passar da travessia
        // normal entre duas salas, senão pune a viagem legítima e cancela o prêmio da chegada.
        [SerializeField] private int _stagnationSteps = 2500;

        // LOOP: custo por chegada numa PORTA pisada há < ~15 s (GraphExplorationMemory._earlyRevisitWindowSteps),
        // só após _earlyRevisitGrace revisitas SEGUIDAS (sair de um beco passa pela porta de entrada).
        [SerializeField] private float _earlyRevisitPenalty = 0.05f;
        [SerializeField, Min(0)] private int _earlyRevisitGrace = 3;

        [Header("-----Salas e portas-----")]
        // Fatias por nó da sala (a sala inteira vale isto). 0 na v5.1: a sala paga UMA vez, ao concluir (pedido do
        // Arthur); com fatias, as salas de 1 nó e o começo das grandes rendiam sem concluir nada.
        [SerializeField] private float _roomExploreReward = 0f;

        // Fração do valor de um nó que a CAUDA paga (nós não pisados de sala já concluída). Baixo
        // para varrer o último canto perder para ir à próxima sala.
        [SerializeField, Range(0f, 1f)] private float _completedRoomNodeFraction = 0.2f;

        // Concluir a sala (room_complete_threshold dos nós). Uma vez por sala, de novo (valendo menos) se liberada.
        // 0.5 na v5.1 = as fatias (0.25) + a conclusão (0.25) de antes, agora tudo no evento de concluir; x o valor
        // crescente da GraphRoomMemory (1 na primeira sala, ~2 na última).
        [SerializeField] private float _roomCompletedReward = 0.5f;

        // Migalhas: por nó novo de sala GRANDE (GraphRoomMemory._crumbMinNodes) antes de concluir; a sala inteira
        // soma isto x o valor dela. 0.5 = metade do que a conclusão já paga, espalhado nos nós: na S24 (20 nós),
        // 0.025 x valor (1..4) por nó, ~0.05-0.1 por canto. É evento (ver um nó), não seta, e não farma: cada nó
        // paga uma vez por episódio (de novo só se a sala for liberada, como a conclusão).
        [SerializeField] private float _bigRoomCrumbReward = 0.5f;

        // Atravessar uma porta x novidade (só a 1ª travessia paga: GraphRoomMemory._doorPaidCrossings). Pequeno: porta é meio, não fim.
        [SerializeField] private float _doorCrossReward = 0.1f;

        // Sair de sala concluída x novidade da porta: faz "sair por outra porta" valer mais que voltar.
        [SerializeField] private float _roomExitReward = 0.15f;

        // Concluir coverage_target (fração das SALAS). Encerra o episódio.
        [SerializeField] private float _fullCoverageReward = 5f;

        [Header("-----Ping-----")]
        // Chegou ao nó do ping enquanto tocava, x NavGraph.PingValue (1). Maior que uma sala inteira
        // para atender o ping valer mais que explorar ali perto. Pago em excesso vira renda (seguir
        // rastro em vez de explorar): vigie cobertura x reward.
        [SerializeField] private float _pingReachedReward = 5f;

        // Cobra uma vez o ping que expirou sem visita. Tem que doer mais que chegar tarde.
        [SerializeField] private float _pingMissedPenalty = 0.5f;

        [Header("-----Visão do hider-----")]
        // Ao AVISTAR o hider (cone com linha livre), com cooldown de 250 steps (5 s) entre
        // aquisições (GraphHiderPerception) para não render piscando numa quina.
        [SerializeField] private float _hiderSpottedReward = 2f;

        // Por METRO de aproximação ENQUANTO VÊ. Só conta se via nas duas decisões, e afastar cobra
        // o que aproximar pagou: ir e voltar dá zero, não é farmável.
        [SerializeField] private float _hiderApproachReward = 0.4f;

        // Pegou o hider (GraphHiderPerception.Caught): paga e ENCERRA o episódio. Tem que valer mais
        // que o resto do mapa que ele deixa de explorar ao encerrar.
        [SerializeField] private float _hiderCaughtReward = 20f;

        // Por STEP DE FÍSICA com o hider no cone e linha livre: faz o seeker seguir o alvo em vez de
        // só avistar. 0.00086 x 35000 = 30 tem que ficar abaixo de pegar cedo (45), senão perseguir para
        // sempre pagaria mais que capturar (0.0015 com 400 s, 0.004 com 160 s).
        [SerializeField, Min(0f)] private float _hiderInViewReward = 0.00086f;

        // Paga a massa de crença zerada (fração de 1) ao ver ou pisar onde o hider poderia estar
        // (GraphSuspicionMap.ClearedMass). A carência de 10 s por nó (_reclearCooldownSteps) segue
        // contra ficar olhando o mesmo lugar. 2 na v4.4 (era 1): procurar onde ele PODE estar é o
        // caminho da captura; ainda bem abaixo de pegar (20-45).
        [SerializeField, Min(0f)] private float _suspicionClearedReward = 2f;

        // Somado à captura x fração do episódio que SOBRA, para pegar encerrar o episódio não
        // compensar a renda cortada: pegar no início = 45, no fim = 20.
        [SerializeField, Min(0f)] private float _hiderCaughtEarlyBonus = 25f;

        /// <summary>Steps sem progresso de sala a partir dos quais a estagnação cobra (normaliza a observação [35]).</summary>
        public int StagnationSteps => _stagnationSteps;

        // Sem estado entre steps: termo que precise de histórico guarda o estado no sistema dono dele
        // (ex.: a aproximação do hider mora na GraphHiderPerception), nunca aqui nem no Manager.
        public float EvaluateStep(in GraphStepContext context)
        {
            float reward = 0f;

            if (context.MaxEpisodeSteps > 0)
                reward -= _existentialPenalty / context.MaxEpisodeSteps;

            if (context.IsTouchingWall)
                reward -= _wallContactPenalty;
            else if (context.IsTouchingDoor)
                reward -= _wallContactPenalty * _doorPenaltyScale;

            if (context.WallHits > 0)
            {
                float scale = context.WallHitIsDoor ? _doorPenaltyScale : 1f;
                reward -= _wallHitPenalty * scale * context.WallHits * Mathf.Min(context.RecentWallHits, _wallHitEscalationCap);
            }

            reward -= _actionChangePenalty * context.ActionChangeSq;
            reward -= _lookChangePenalty * context.LookChangeSq;

            if (context.StepsSinceProgress > _stagnationSteps)
                reward -= _stagnationPenalty;

            // Exploração escala com discovery_reward_scale (na caça, explorar é meio).
            float discovery = context.DiscoveryRewardScale;
            reward += _roomExploreReward * (context.RoomNodeValue + _completedRoomNodeFraction * context.RoomTailValue) * discovery;
            reward += _roomCompletedReward * context.RoomCompletedValue * discovery;
            reward += _bigRoomCrumbReward * context.RoomCrumbValue * discovery;
            reward += _doorCrossReward * context.DoorCrossValue * discovery;
            reward += _roomExitReward * context.RoomExitValue * discovery;

            if (context.EarlyRevisitArrivals > 0 && context.EarlyRevisitStreak > _earlyRevisitGrace)
                reward -= _earlyRevisitPenalty * context.EarlyRevisitArrivals;

            // Ping escala com ping_reward_scale (0 = o ping só informa); visão não escala com a lição.
            if (context.PingReached)
                reward += _pingReachedReward * context.PingReachedValue * context.PingRewardScale;

            if (context.PingMissed)
                reward -= _pingMissedPenalty * context.PingRewardScale;

            if (context.HiderSpotted)
                reward += _hiderSpottedReward;

            if (context.HasHiderApproach)
                reward += _hiderApproachReward * context.HiderApproachDelta;

            if (context.HiderInView)
                reward += _hiderInViewReward;

            reward += _suspicionClearedReward * context.SuspicionClearedMass;

            // Fim do episódio: a captura tem prioridade (na caça é ela que explica o fim se as duas ocorrerem).
            if (context.HiderCaught)
            {
                float remaining = 1f - (float)context.ElapsedSteps / Mathf.Max(1, context.MaxEpisodeSteps);
                reward += _hiderCaughtReward + _hiderCaughtEarlyBonus * Mathf.Clamp01(remaining);
            }
            else if (context.CoverageReached)
            {
                reward += _fullCoverageReward;
            }

            return reward;
        }
    }
}
