using UnityEngine;
using UnityEngine.Serialization;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Calculadora pura de recompensa: recebe um <see cref="GraphStepContext"/>, devolve o delta do
    /// step. Todo o tuning mora aqui; o Manager nunca soma recompensa por fora. Toda sala vale o
    /// MESMO; a GraphRoomMemory entrega unidades (já com calor/suspeita) e aqui cada uma vira
    /// recompensa. Nenhum termo paga aproximar-se de alvo escolhido por algoritmo: só eventos que
    /// o agente causa (a aproximação do hider só conta com ele em vista).
    ///
    /// ORÇAMENTO por episódio (8000 steps de física = 1600 decisões; "x escala" = discovery_reward_scale
    /// ou ping_reward_scale). Teto de penalidade só no pior caso; a existencial é o piso (-2):
    ///   Termo (valor)                              Teto / total
    ///   existencial (2 / steps do episódio)        -2.0
    ///   contato com parede (0.00075/step)          -6.0 encostado o tempo todo
    ///   batida (0.03 x batidas x até 5)            -0.9 (30 isoladas) a -4.5 (rajada)
    ///   suavidade do andar (0.0005 x |Δ|²)         -0.2 típico; -6.4 patológico
    ///   suavidade do olhar (0.00025 x |Δ|²)        -0.1 típico; -3.2 patológico
    ///   estagnação (0.0005/step após 1250 steps)   -3.4
    ///   revisita precoce (0.05, após 3 seguidas)   ~-2.0 (40 chegadas)
    ///   ping perdido (0.5)                         -2.0 (4 pings)
    ///   sala explorada (0.25/sala, cauda x0.2)     +6.5 (26 salas) x escala
    ///   sala concluída (0.25/sala)                 +6.5 x escala
    ///   porta (0.1 x novidade)                     +3.5 (35 portas; vai-e-vem até 2x) x escala
    ///   saída de sala concluída (0.15 x novidade)  ~+3.9 x escala
    ///   cobertura (5, encerra)                     +5
    ///   ping atendido (5 x PingValue)              +20 (4 pings) x escala
    ///   avistar hider (2, cooldown 5 s)            ~+8 (4 avistamentos)
    ///   aproximar vendo (0.4/m)                    +6 por 15 m em linha reta
    ///   hider em vista (0.004/step)                +32 o episódio todo
    ///   suspeita zerada (1 x massa de 0 a 1)       ~1 por crença inteira limpa
    ///   captura (20 + 25 x fração restante)        20 a 45 (encerra)
    /// NodeTraining5 (26 salas, 35 portas) coberto por inteiro: ~25 de exploração.
    /// </summary>
    public class GraphRewardSystem : MonoBehaviour
    {
        [Header("-----Penalidades-----")]
        // Diluída por step (custo total = este valor por episódio): dá pressa sem punir ação
        // nenhuma, então nunca ensina imobilidade.
        [SerializeField] private float _existentialPenalty = 2f;

        // Por STEP em contato, não por evento (o Unity re-dispara a colisão ao deslizar). Ao mudar
        // _maxEpisodeSteps, reescale pelo teto (valor x steps), não pelo valor por step.
        // Subir este preço ensinou o agente a não passar em porta; prefira mexer no movimento (freio).
        [SerializeField] private float _wallContactPenalty = 0.00075f;

        // Por BATIDA (início de contato, WallHitTracker) x batidas nos últimos ~5 s (até
        // _wallHitEscalationCap): 0.03, 0.06, 0.09... Cobra o ricochete, que o contínuo mal vê.
        [SerializeField] private float _wallHitPenalty = 0.03f;
        [SerializeField, Min(1)] private int _wallHitEscalationCap = 5;

        // Custo por |mudança de ação|² a cada decisão, contra o "beyblade". Ir reto custa zero;
        // pagar por ir reto seria farmável.
        [SerializeField] private float _actionChangePenalty = 0.0005f;

        // O mesmo para as ações de olhar [2..3], metade do preço porque varrer a sala com o olhar é legítimo.
        [SerializeField] private float _lookChangePenalty = 0.00025f;

        // Contra entalar numa quina ou orbitar sala já vista. Só entra após _stagnationSteps sem
        // PROGRESSO DE SALA (nó novo, sala concluída ou porta com novidade >= 0.25), não sem movimento.
        [SerializeField] private float _stagnationPenalty = 0.0005f;

        // Em steps de FÍSICA (1250 = 25 s). Tem que passar da travessia normal entre dois nós,
        // senão pune a viagem legítima e cancela o prêmio da chegada.
        [SerializeField] private int _stagnationSteps = 1250;

        // LOOP: custo por chegada numa PORTA pisada há < ~15 s (GraphExplorationMemory._earlyRevisitWindowSteps),
        // só após _earlyRevisitGrace revisitas SEGUIDAS (sair de um beco passa pela porta de entrada).
        [SerializeField] private float _earlyRevisitPenalty = 0.05f;
        [SerializeField, Min(0)] private int _earlyRevisitGrace = 3;

        [Header("-----Salas e portas-----")]
        // A sala inteira (fatias por nó até 80% dela) vale isto, qualquer que seja o tamanho.
        [SerializeField] private float _roomExploreReward = 0.25f;

        // Fração do valor de um nó que a CAUDA paga (nós não pisados de sala já concluída). Baixo
        // para varrer o último canto perder para ir à próxima sala.
        [SerializeField, Range(0f, 1f)] private float _completedRoomNodeFraction = 0.2f;

        // Concluir a sala (room_complete_threshold dos nós). Uma vez por sala, de novo (valendo menos) se liberada.
        [SerializeField] private float _roomCompletedReward = 0.25f;

        // Atravessar uma porta x novidade (1, 0.5, 0.25...). Pequeno: porta é meio, não fim.
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
        // só avistar. 0.004 x 8000 = 32 tem que ficar abaixo de pegar cedo (45), senão perseguir para
        // sempre pagaria mais que capturar.
        [SerializeField, Min(0f)] private float _hiderInViewReward = 0.004f;

        // Paga a massa de crença zerada (fração de 1) ao ver ou pisar onde o hider poderia estar
        // (GraphSuspicionMap.ClearedMass). A carência de 10 s por nó (_reclearCooldownSteps) segue
        // contra ficar olhando o mesmo lugar.
        [SerializeField, Min(0f)] private float _suspicionClearedReward = 1f;

        // Somado à captura x fração do episódio que SOBRA, para pegar encerrar o episódio não
        // compensar a renda cortada: pegar no início = 45, no fim = 20.
        [SerializeField, Min(0f)] private float _hiderCaughtEarlyBonus = 25f;

        public float FullCoverageReward => _fullCoverageReward;

        /// <summary>Captura + bônus pela fração do episódio que ainda restava (0..1).</summary>
        public float HiderCaughtReward(float remainingFraction) =>
            _hiderCaughtReward + _hiderCaughtEarlyBonus * Mathf.Clamp01(remainingFraction);

        public void ResetEpisode()
        {
            // Sem estado entre steps por enquanto; termo com histórico nasce aqui, não no Manager.
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

            // Exploração escala com discovery_reward_scale (na caça, explorar é meio).
            float discovery = context.DiscoveryRewardScale;
            reward += _roomExploreReward * (context.RoomNodeValue + _completedRoomNodeFraction * context.RoomTailValue) * discovery;
            reward += _roomCompletedReward * context.RoomCompletedValue * discovery;
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

            return reward;
        }
    }
}
