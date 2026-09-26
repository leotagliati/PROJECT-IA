using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Recompensa de um step, termo a termo. Só telemetria (SeekerDebugOverlay): o que o agente
    /// recebe é a soma, e ela não muda por existir isto.
    /// </summary>
    public struct SeekerRewardBreakdown
    {
        public float Existential;
        public float WallProximity;
        public float WallContact;
        public float NewCell;
        public float Sight;
        public float Approach;
        public float BlockedHeading;
        public float Stuck;
        public float FrontierApproach;

        public readonly float Total => Existential + WallProximity + WallContact + NewCell + Sight + Approach + BlockedHeading + Stuck + FrontierApproach;

        public void Add(in SeekerRewardBreakdown other)
        {
            Existential += other.Existential;
            WallProximity += other.WallProximity;
            WallContact += other.WallContact;
            NewCell += other.NewCell;
            Sight += other.Sight;
            Approach += other.Approach;
            BlockedHeading += other.BlockedHeading;
            Stuck += other.Stuck;
            FrontierApproach += other.FrontierApproach;
        }
    }

    /// <summary>
    /// Calculadora pura de recompensa. Recebe um SeekerStepContext e devolve o delta do step;
    /// não consulta outros sistemas, não decide fim de episódio e não assina eventos de física.
    /// Todo o tuning do agente mora aqui.
    /// </summary>
    public class SeekerRewardSystem : MonoBehaviour
    {
        [Header("-----Penalidades-----")]
        [SerializeField] private float _existentialPenalty = 2f;
        [SerializeField] private float _wallProximityPenalty = 0.01f;

        // Zona morta: só pune a partir dessa proximidade. Desacopla o alcance da PUNICAO do
        // alcance da OBSERVACAO — sem isso, aumentar _detectionRange para enxergar melhor
        // aumenta junto o raio em que o agente e taxado, e navegar corredor vira prejuizo.
        // Com _detectionRange = 5, 0.6 equivale a comecar a punir a 2m da parede.
        [SerializeField, Range(0f, 1f)] private float _wallDangerThreshold = 0.6f;
        // Cobrada por STEP em contato, não por evento de colisão. Com 0.005: raspar de leve
        // numa quina (uns 5 steps) custa -0.025, irrelevante; ficar 500 steps preso contra a
        // parede custa -2.5, significativo. A magnitude passa a depender do tempo grudado,
        // e não de quantas vezes a física resolveu redisparar o contato.
        // Mantida na mesma ordem de grandeza da pressao existencial (-2/episodio): raspar
        // parede o episodio inteiro custa o mesmo que desperdicar o episodio inteiro. Acima
        // disso ela domina a decisao e o agente aprende a nao entrar em corredor nenhum.
        [SerializeField] private float _wallContactPenalty = 0.002f;

        // Cobrada quando a ação aponta para uma célula que a janela de observação mostra como
        // bloqueada. Diferente do contato, é sobre a INTENÇÃO, então a rede consegue associar
        // a punição a algo que ela vê antes de bater. Mesma ordem de grandeza do contato:
        // encostar E insistir custa o dobro de só encostar.
        [SerializeField] private float _blockedHeadingPenalty = 0.002f;

        // Ficar parado (ou oscilar no lugar) não custava nada além do existencial, que é o
        // mesmo andando ou não — não havia gradiente contra travar. Aqui: se em _stuckWindowSteps
        // steps o deslocamento LÍQUIDO ficou abaixo de _stuckDistance, paga por step. Com 50
        // steps (1 s) e 1 m, um agente a 16 u/s tem folga de sobra; só trava de verdade paga.
        // 0.004 × 500 steps preso = -2, a mesma ordem do existencial do episódio inteiro —
        // acima disso ele domina e o agente aprende a correr em círculos para não ser cobrado.
        [SerializeField] private float _stuckPenalty = 0.004f;
        [SerializeField, Min(1)] private int _stuckWindowSteps = 50;
        [SerializeField, Min(0f)] private float _stuckDistance = 1f;

        [Header("-----Recompensas-----")]
        [SerializeField] private float _hiderSightReward = 0.1f;      // bônus único ao avistar
        [SerializeField] private float _hiderApproachReward = 0.3f;   // por aproximar da última posição
        // Precisa ser claramente maior que o custo de explorar. Com 1, um episodio inteiro
        // procurando ja custava -2 de existencial, entao achar o hider mal compensava o risco
        // e a politica racional era ficar parada.
        [SerializeField] private float _hiderFoundReward = 5f;

        [SerializeField] private float _newCellReward = 0.02f;

        // Por célula de caminho (na grade, respeitando paredes) ganha rumo à fronteira mais
        // próxima. É shaping baseado em potencial — diferença de distâncias ao mesmo alvo —
        // então não muda qual política é ótima, só dá gradiente onde a janela é toda 1.
        // Pequeno de propósito: uma fronteira a 10 células rende +0.05 no caminho todo, da
        // ordem de dois ou três _newCellReward. 0 desliga.
        [SerializeField] private float _frontierApproachReward = 0.005f;

        // Estado de reward shaping: detecta a borda de subida do avistamento. É por episódio e
        // por agente — por isso o sistema é MonoBehaviour, e não um ScriptableObject compartilhado.
        private bool _wasSeeingHider;

        // Âncora do termo de fronteira: só mede progresso enquanto o alvo é o mesmo.
        private int _previousFrontierIndex = -1;
        private int _previousFrontierDistance;

        // ------------------------------------------------------------------ telemetria
        // Só leitura, para o SeekerDebugOverlay. Nada daqui entra na recompensa.

        private const int HistorySize = 256;
        private readonly float[] _history = new float[HistorySize];
        private readonly bool[] _historyTouching = new bool[HistorySize];
        private SeekerRewardBreakdown _whileTouching;
        private int _stepsTouching;
        private int _historyCount;
        private int _historyNext;
        private SeekerRewardBreakdown _episodeTotal;

        public float HiderFoundReward => _hiderFoundReward;

        public float WallDangerThreshold => _wallDangerThreshold;

        /// <summary>Janela do teste de travado. O SeekerManager dimensiona o histórico de posições por aqui.</summary>
        public int StuckWindowSteps => _stuckWindowSteps;

        public float StuckDistance => _stuckDistance;

        /// <summary>Último step avaliado, termo a termo.</summary>
        public SeekerRewardBreakdown LastStep { get; private set; }

        /// <summary>Soma de todos os steps do episódio atual, termo a termo (sem o +5 de captura, que é evento).</summary>
        public SeekerRewardBreakdown EpisodeTotal => _episodeTotal;

        public int StepsRecorded => _historyCount;

        /// <summary>Steps do episódio em que o agente estava encostado em parede.</summary>
        public int StepsTouchingWall => _stepsTouching;

        /// <summary>
        /// Soma dos termos SÓ nos steps encostado. Se o total aqui é positivo, encostar está sendo
        /// pago (aproximação ou célula nova cobrindo o contato) — e o agente está certo em bater.
        /// </summary>
        public SeekerRewardBreakdown WhileTouchingWall => _whileTouching;

        /// <summary>Step de <paramref name="stepsAgo"/> atrás (1 = último). Falso se não há registro.</summary>
        public bool TryGetHistory(int stepsAgo, out float total, out bool touching)
        {
            total = 0f;
            touching = false;

            if (stepsAgo < 1 || stepsAgo > _historyCount)
                return false;

            int index = (_historyNext - stepsAgo + HistorySize) % HistorySize;
            total = _history[index];
            touching = _historyTouching[index];
            return true;
        }

        /// <summary>Média da recompensa por step nos últimos <paramref name="steps"/> (até 256) do episódio.</summary>
        public float RecentAverage(int steps)
        {
            int count = Mathf.Min(steps, _historyCount);
            if (count <= 0)
                return 0f;

            float sum = 0f;
            for (int i = 1; i <= count; i++)
                sum += _history[(_historyNext - i + HistorySize) % HistorySize];

            return sum / count;
        }

        private void Record(in SeekerRewardBreakdown step, bool touchingWall)
        {
            LastStep = step;
            _episodeTotal.Add(step);

            if (touchingWall)
            {
                _whileTouching.Add(step);
                _stepsTouching++;
            }

            _history[_historyNext] = step.Total;
            _historyTouching[_historyNext] = touchingWall;
            _historyNext = (_historyNext + 1) % HistorySize;
            _historyCount = Mathf.Min(_historyCount + 1, HistorySize);
        }

        public void ResetEpisode()
        {
            _wasSeeingHider = false;
            _previousFrontierIndex = -1;

            _episodeTotal = default;
            LastStep = default;
            _historyCount = 0;
            _historyNext = 0;
            _whileTouching = default;
            _stepsTouching = 0;
        }

        public float EvaluateStep(in SeekerStepContext context)
        {
            SeekerRewardBreakdown step = default;

            if (context.MaxEpisodeSteps > 0)
                step.Existential = -_existentialPenalty / context.MaxEpisodeSteps;

            // Escalado pela lição: num labirinto estar perto de parede é a condição normal de
            // um corredor, então esse termo vira zero e só a colisão continua punida.
            float wallDanger = Mathf.InverseLerp(_wallDangerThreshold, 1f, context.ClosestWallProximity);
            step.WallProximity = -_wallProximityPenalty * wallDanger * context.WallProximityScale;

            // Não escalada por lição: encostar é ruim nos dois cenários, e agora que é
            // proporcional ao tempo ela se auto-regula em vez de explodir no labirinto.
            if (context.IsTouchingWall)
                step.WallContact = -_wallContactPenalty;

            if (context.HeadingIntoBlockedCell)
                step.BlockedHeading = -_blockedHeadingPenalty;

            // Só com a janela cheia: nos primeiros steps do episódio não há como ter andado.
            if (context.RecentWindowFilled && context.RecentNetDisplacement < _stuckDistance)
                step.Stuck = -_stuckPenalty;

            // O que importa aqui e o TOTAL: (celulas alcancaveis) x _newCellReward tem que
            // ficar bem abaixo do +5 de achar o hider, senao cobrir o mapa vira um objetivo
            // em si. O numero de celulas depende de _arenaSize e _cellSize da memoria de
            // exploracao, entao este peso E ESPECIFICO DO MAPA e precisa ser refeito sempre
            // que a grade mudar:
            //   arena antiga: ~120 celulas x 0.02   = ~+2.4
            //   Map_8:       ~1450 celulas x 0.002  = ~+2.9
            // (O existencial nao entra na conta: ele e cobrado por tempo, o agente parado ou
            // andando paga o mesmo. Explorar e sempre bonus liquido — a pergunta e so se e
            // bonus demais.)
            if (context.EnteredNewCell)
                step.NewCell = _newCellReward;

            // Mesmo alvo nos dois steps: a diferença é progresso real no caminho. Alvo trocado
            // (chegou na fronteira, ou outra ficou mais perto) só reancora.
            if (context.FrontierIndex >= 0 && context.FrontierIndex == _previousFrontierIndex)
                step.FrontierApproach = _frontierApproachReward * (_previousFrontierDistance - context.FrontierDistanceCells);

            _previousFrontierIndex = context.FrontierIndex;
            _previousFrontierDistance = context.FrontierDistanceCells;

            // Bônus único no step em que passa a enxergar o hider (borda de subida).
            if (context.IsSeeingHider && !_wasSeeingHider)
                step.Sight = _hiderSightReward;

            _wasSeeingHider = context.IsSeeingHider;

            if (context.HasSeenHider)
            {
                // Progresso do PRÓPRIO seeker rumo ao alvo atual: as duas distâncias usam a mesma
                // posição conhecida, então um salto do alvo (novo avistamento) não gera falso ganho.
                float previousDistance = Vector3.Distance(context.PreviousStepPosition, context.LastKnownHiderPosition);
                float currentDistance = Vector3.Distance(context.CurrentPosition, context.LastKnownHiderPosition);

                // A distância é euclidiana, então só equivale a "progresso" em espaço aberto.
                // No labirinto o peso cai, senão esse termo pune o contorno de uma parede.
                step.Approach = _hiderApproachReward * (previousDistance - currentDistance) * context.ApproachRewardScale;
            }

            Record(step, context.IsTouchingWall);
            return step.Total;
        }
    }
}
