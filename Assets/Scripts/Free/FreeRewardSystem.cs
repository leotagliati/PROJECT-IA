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
    /// gerador dá ~925 pontos e 35 portas por arena; no Map - V8 da v8.2, 927 pontos, 23 salas e 29 portas (o Console
    /// do FreeMap diz o número certo):
    ///   Termo                                         Teto / total
    ///   ver pontos (8 / nº de pontos, por ponto)      +8 vendo o mapa inteiro (~0.009 por ponto), x escala
    ///     x (1 + 1.5 x raridade do ponto)             até +20 se nada do mapa fosse visto antes; ~+3 a +4 a mais típico
    ///   porta nova (0.15)                             +5.25 (35 portas; 4.35 com as 29 da v8.2), x escala; uma porta = ~17 pontos
    ///   cobertura (5, encerra; só sem hider)          +5
    ///   avistar o hider (2, só a 1ª vista)            +2
    ///   aproximar vendo (1.0/m, só batendo o recorde) ~+20 (da 1ª vista, até 22 m, à captura)
    ///   captura (20 + 25 x fração restante)           20 a 45 (encerra)
    ///   existencial (2 / steps do episódio)           -2.0
    ///   contato com parede (0.00034/step)             -12 encostado o tempo todo (o valor da v5.1)
    ///   batida (0.1 x batidas recentes, até x10)      -3 (30 isoladas) a -30 (rajada); a 10ª em 5 s custa 1.0
    ///   PRESA na parede (0.0012/step, após 0.5 s)     -42 presa o tempo todo; ~-0.06 por segundo preso
    ///   estagnação (0.0001/step após 1500 sem nada)   -3.5 parado o episódio todo; ~0 explorando
    ///   suavidade (0.0002 andar, 0.0001 olhar, x|Δ|²) -0.3 típico; patológico ~-8
    ///   CHAVES (só nas lições finais, com o FreeKeyField):
    ///   ronda (0.1 por chave conferida parada > 60 s)  ~+1 a +3 (3 chaves, uma volta por minuto)
    ///   chave levada (-1 quando o monstro fica sabendo) -3 (as 3)
    ///   presa escapou (-5, levou todas; encerra)       -5, e perde a captura (20-45)
    ///   campar (0.0005/step perto da mesma chave > 15 s) ~-0.025 por segundo campando
    ///   CAMPAR NA SALA (v8.2, 0.0005/step na mesma sala além de 15 s + 0.25 s/ponto, sem alvo à vista nem pista)
    ///                                                 ~-0.025 por segundo; ~-17 parado numa sala o episódio todo
    /// Na caça a exploração vale x 0.5 (discovery_reward_scale; 0.3 até o v8.2_mapa_04), x2 no 1º minuto caindo a x1
    /// (_earlyExplorationBoost): ~6 no mapa todo, bem abaixo da captura (20-45),
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

        // COMEÇO DO EPISÓDIO = EXPLORAR (v8.2, 14/10): ponto e porta novos valem x (1 + isto) no step 0, caindo em linha
        // reta até x1 em _earlyExplorationSteps (steps de física; 3000 = 60 s). Ele saía direto na ronda decorada; com o
        // começo pagando o dobro, varrer salas novas cedo compensa. Teto: ver o mapa inteiro já no 1º minuto daria
        // ~2 x 6 = 12 (impossível; meio mapa ~+3), longe da captura (20-45). Caçando continua zero.
        [SerializeField, Min(0f)] private float _earlyExplorationBoost = 1f;
        [SerializeField, Min(1)] private int _earlyExplorationSteps = 3000;

        // CONTRA A RONDA FIXA (v8.2_mapa_06): ponto novo paga x (1 + isto x raridade), raridade = quão pouco esta arena viu
        // o ponto nos últimos ~20 episódios, relativa ao mais visto (FreeArenaController.SeenRarity; 0 = o mais visto,
        // 1 = nunca visto). No v8.2_mapa_05 ele fazia a mesma volta todo episódio e 9 das 23 salas ficavam em < 6%;
        // a presa já nasce nelas, mas só pegar pagava ir lá. Bônus no raro, e não custo no comum: custar ver o comum
        // faria olhar menos (ou ficar parado) valer a pena, e as salas comuns são caminho e às vezes têm a presa. Se
        // equilibra sozinho: ele passa a ir lá, o ponto deixa de ser raro e o valor migra para o próximo esquecido.
        // Paga evento que ele causa (ver), sem alvo escolhido por código. Teto com 1.5: o mapa inteiro nunca visto
        // valeria 8 x 2.5 = 20 (x escala 0.5 = 10, mais as portas ~2.2); na prática ~1/3 do mapa é raro (~+3 a +4),
        // abaixo da captura (20-45).
        [SerializeField, Min(0f)] private float _rarityGain = 1.5f;

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

        // Por BATIDA (início de contato, GraphBodyTracker) x batidas nos últimos 5 s, até _wallHitEscalationCap. 0.1 e
        // até x10 (v8.2, 14/10; eram 0.06 e x5): bater em SEGUIDA no mesmo canto cresce rápido (a 5ª batida em 5 s custa
        // 0.5, a 10ª 1.0) e passa a valer dar a volta; batida isolada continua barata (0.1).
        [SerializeField, Min(0f)] private float _wallHitPenalty = 0.1f;
        [SerializeField, Min(1)] private int _wallHitEscalationCap = 10;

        // Por step PRESO: encostado, pedindo para andar e quase parado há mais de 0.5 s (FreeStuckTracker). Quase o
        // dobro do contato, somado a ele: raspar a parede andando é tolerável, empacar contra ela (quina, batente)
        // não. 0.0012 (v8.2, 14/10; era 0.0006, e o tempo preso subiu de 0.6% para 2% no v8.2_mapa_04): ~-0.06/s preso,
        // além do contato.
        [SerializeField, Min(0f)] private float _wallStuckPenalty = 0.0012f;

        [Header("-----Chaves (lições finais)-----")]
        // Conferir (ver) uma chave que ficou mais de 60 s sem ser vista (FreeKeyField): a GUARDA é passar por elas de
        // tempos em tempos, não ficar em cima. Pequeno: ronda é caminho, a captura continua mandando.
        [SerializeField, Min(0f)] private float _keyCheckReward = 0.1f;

        // Quando o monstro fica sabendo (com atraso) que uma chave foi levada: não guardou.
        [SerializeField, Min(0f)] private float _keyLostPenalty = 1f;

        // A presa levou todas (escapou, encerra): o pior fim depois de não achar ninguém.
        [SerializeField, Min(0f)] private float _escapePenalty = 5f;

        // Por step de física CAMPANDO (perto da mesma chave há mais de 15 s): sentar na chave não é guardar. Contra
        // ele está a captura: com a presa longe, campar não pega ninguém.
        [SerializeField, Min(0f)] private float _campPenalty = 0.0005f;

        [Header("-----Campar numa sala (v8.2)-----")]
        // Por step de física na MESMA sala além do limite dela (15 s + 0.25 s por ponto da sala), sem o alvo à vista
        // nem pista fresca (PROCURANDO conta: esperar no lugar em que o perdeu de vista era tocaia de graça):
        // o monstro tem que rondar, não esperar de tocaia. O mesmo valor do campar na chave (~-0.025 por segundo);
        // somado à estagnação (que já cobra 0.0001 parado num lugar visto) passa a valer sair da sala.
        [SerializeField, Min(0f)] private float _roomCampPenalty = 0.0005f;

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
            float early = 1f + _earlyExplorationBoost * Mathf.Clamp01(1f - (float)context.ElapsedSteps / _earlyExplorationSteps);
            float discovery = context.Hunting ? 0f : context.DiscoveryRewardScale * early;
            if (context.NewPoints > 0 && context.PointCount > 0)
                reward += _allPointsReward * (context.NewPoints + _rarityGain * context.NewPointsRarity) / context.PointCount * discovery;
            reward += _newDoorReward * context.NewDoors * discovery;

            // Caça.
            if (context.HiderSpotted)
                reward += _hiderSpottedReward;
            reward += _hiderApproachReward * context.HiderApproachDelta;

            // Chaves.
            reward += _keyCheckReward * context.KeyChecks;
            reward -= _keyLostPenalty * context.KeysLost;
            if (context.Camping)
                reward -= _campPenalty;
            if (context.RoomCamping)
                reward -= _roomCampPenalty;

            // Fim: a captura tem prioridade; depois a fuga da presa.
            if (context.HiderCaught)
            {
                float remaining = 1f - (float)context.ElapsedSteps / Mathf.Max(1, context.MaxEpisodeSteps);
                reward += _hiderCaughtReward + _hiderCaughtEarlyBonus * Mathf.Clamp01(remaining);
            }
            else if (context.HiderEscaped)
            {
                reward -= _escapePenalty;
            }
            else if (context.CoverageReached)
            {
                reward += _coverageReward;
            }

            return reward;
        }
    }
}
