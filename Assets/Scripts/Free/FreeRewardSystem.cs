using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// Recompensa da v8, função pura do <see cref="FreeStepContext"/>. A regra do Arthur para este modelo:
    ///   - ANDAR NÃO PAGA. Nenhum termo paga distância, velocidade ou aproximar-se de lugar do mapa.
    ///   - VER UM PONTO NOVO paga (uma vez por ponto por episódio).
    ///   - PORTA NOVA paga MAIS (a 1ª travessia de cada porta; repetir porta paga zero, senão ir e voltar no vão
    ///     viraria renda).
    ///   - PAREDE CUSTA, e ficar PRESO nela custa mais (o corpo é o da v5, com física: ele bate de verdade).
    ///   - CAÇA: avistar (só a 1ª vez), chegar mais perto que o recorde enquanto vê, e pegar (encerra).
    /// Tudo positivo é evento pago uma vez ou com teto: nada dá para repetir em loop e lucrar.
    ///
    /// ORÇAMENTO por episódio (35000 steps de física = 700 s = 7000 decisões com Decision Period 5). No V8 o
    /// gerador dá ~925 pontos e 35 portas por arena (o Console do FreeMap diz o número certo):
    ///   Termo                                         Teto / total
    ///   ver pontos (8 / nº de pontos, por ponto)      +8 vendo o mapa inteiro (~0.009 por ponto), x escala
    ///   porta nova (0.15)                             +5.25 (35 portas), x escala; uma porta = ~17 pontos
    ///   cobertura (5, encerra; só sem hider)          +5
    ///   avistar o hider (2, só a 1ª vista)            +2
    ///   aproximar vendo (1.0/m, só batendo o recorde) ~+20 (da 1ª vista, até 22 m, à captura)
    ///   captura (20 + 25 x fração restante)           20 a 45 (encerra)
    ///   existencial (2 / steps do episódio)           -2.0
    ///   contato com parede (0.00034/step)             -12 encostado o tempo todo (o valor da v5.1)
    ///   batida (0.06 x batidas recentes, até x5)      -1.8 (30 isoladas) a -9 (rajada)
    ///   PRESA na parede (0.0006/step, após 0.5 s)     -21 presa o tempo todo; ~-0.03 por segundo preso
    ///   estagnação (0.0001/step após 1500 sem nada)   -3.5 parado o episódio todo; ~0 explorando
    ///   suavidade (0.0002 andar, 0.0001 olhar, x|Δ|²) -0.3 típico; patológico ~-8
    /// Na caça a exploração vale x 0.3 (discovery_reward_scale): ~4 no mapa todo, bem abaixo da captura (20-45),
    /// para pegar mandar em tudo, e ainda o bastante para procurar sala por sala. E CAÇANDO (vendo o alvo,
    /// procurando há < 20 s ou com pegada fresca) ela vale ZERO e a estagnação não cobra: com a presa à vista ou o
    /// rastro dela no chão, nada compete (o FOCO da v5). Ver e seguir pegada não pagam nada: só levam à captura.
    /// O ponto paga pelo ORÇAMENTO do mapa, não por valor fixo: trocar o tamanho da célula do FreeMap (mais ou
    /// menos pontos) não muda o total, e o peso da porta frente aos pontos fica o mesmo.
    /// </summary>
    public class FreeRewardSystem : MonoBehaviour
    {
        [Header("-----Exploração-----")]
        // Soma paga por ver TODOS os pontos do mapa (cada ponto = isto / nº de pontos).
        [SerializeField, Min(0f)] private float _allPointsReward = 8f;

        // 1ª travessia de cada porta. 0.15 = ~17 pontos no V8: porta nova vale mais que ver um pedaço de sala, e
        // menos que uma sala inteira (sala média ~35 pontos = ~0.3), para a porta ser caminho e não fim.
        [SerializeField, Min(0f)] private float _newDoorReward = 0.15f;

        // Viu coverage_target dos pontos. Encerra o episódio: o mapa inteiro vale mais que parar nos 80% fáceis.
        [SerializeField, Min(0f)] private float _coverageReward = 5f;

        [Header("-----Caça-----")]
        // 1ª vista do hider no episódio (GraphHiderPerception já só marca a primeira).
        [SerializeField, Min(0f)] private float _hiderSpottedReward = 2f;

        // Por metro, só batendo o RECORDE de proximidade do episódio enquanto vê (deixar escapar e voltar não paga).
        [SerializeField, Min(0f)] private float _hiderApproachReward = 1f;

        // Pegar encerra o episódio: tem que valer mais que o resto do mapa que ele deixa de ver, e pegar cedo mais
        // que tarde (o bônus x fração restante).
        [SerializeField, Min(0f)] private float _hiderCaughtReward = 20f;
        [SerializeField, Min(0f)] private float _hiderCaughtEarlyBonus = 25f;

        [Header("-----Parede-----")]
        // Por STEP encostado (não por evento: o Unity re-dispara a colisão ao deslizar). 0.00034 = o da v5.1
        // (teto -12 com 35000 steps). Ao mudar o episódio, reescale pelo teto.
        [SerializeField, Min(0f)] private float _wallContactPenalty = 0.00034f;

        // Por BATIDA (início de contato, GraphBodyTracker) x batidas nos últimos 5 s, até _wallHitEscalationCap.
        [SerializeField, Min(0f)] private float _wallHitPenalty = 0.06f;
        [SerializeField, Min(1)] private int _wallHitEscalationCap = 5;

        // Por step PRESO: encostado, pedindo para andar e quase parado há mais de 0.5 s (FreeStuckTracker). Quase o
        // dobro do contato, somado a ele: raspar a parede andando é tolerável, empacar contra ela (quina, batente)
        // não. ~-0.03/s preso, além do contato.
        [SerializeField, Min(0f)] private float _wallStuckPenalty = 0.0006f;

        [Header("-----Outras penalidades-----")]
        // Diluída por step (custo total = este valor por episódio): pressa sem punir ação nenhuma.
        [SerializeField, Min(0f)] private float _existentialPenalty = 2f;

        // Por step de física depois de _stagnationSteps sem ponto nem porta novos. 1500 = 30 s: atravessar duas salas
        // já vistas para chegar ao que falta não paga; ficar rodando num lugar visto, sim. Teto -3.5 (35000 steps).
        [SerializeField, Min(0f)] private float _stagnationPenalty = 0.0001f;
        [SerializeField, Min(1)] private int _stagnationSteps = 1500;

        // Por |mudança de ação|² a cada decisão, contra o giro de "beyblade" (os valores da v5). Ir reto custa zero.
        [SerializeField, Min(0f)] private float _moveChangePenalty = 0.0002f;
        [SerializeField, Min(0f)] private float _lookChangePenalty = 0.0001f;

        /// <summary>Steps sem progresso a partir dos quais a estagnação cobra (normaliza a observação).</summary>
        public int StagnationSteps => _stagnationSteps;

        public float EvaluateStep(in FreeStepContext context)
        {
            float reward = 0f;

            if (context.MaxEpisodeSteps > 0)
                reward -= _existentialPenalty / context.MaxEpisodeSteps;

            // Parede.
            if (context.IsTouchingWall)
                reward -= _wallContactPenalty;
            if (context.WallHits > 0)
                reward -= _wallHitPenalty * context.WallHits * Mathf.Min(context.RecentWallHits, _wallHitEscalationCap);
            if (context.IsStuck)
                reward -= _wallStuckPenalty;

            if (!context.Hunting && context.StepsSinceProgress > _stagnationSteps)
                reward -= _stagnationPenalty;

            reward -= _moveChangePenalty * context.MoveChangeSq;
            reward -= _lookChangePenalty * context.LookChangeSq;

            // Exploração.
            float discovery = context.Hunting ? 0f : context.DiscoveryRewardScale;
            if (context.NewPoints > 0 && context.PointCount > 0)
                reward += _allPointsReward * context.NewPoints / context.PointCount * discovery;
            reward += _newDoorReward * context.NewDoors * discovery;

            // Caça.
            if (context.HiderSpotted)
                reward += _hiderSpottedReward;
            reward += _hiderApproachReward * context.HiderApproachDelta;

            // Fim: a captura tem prioridade.
            if (context.HiderCaught)
            {
                float remaining = 1f - (float)context.ElapsedSteps / Mathf.Max(1, context.MaxEpisodeSteps);
                reward += _hiderCaughtReward + _hiderCaughtEarlyBonus * Mathf.Clamp01(remaining);
            }
            else if (context.CoverageReached)
            {
                reward += _coverageReward;
            }

            return reward;
        }
    }
}
