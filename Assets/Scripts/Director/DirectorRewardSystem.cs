using Assets.Scripts.Free;
using UnityEngine;

namespace Assets.Scripts.Director
{
    /// <summary>
    /// Recompensa do diretor: função pura do <see cref="DirectorStepContext"/>. Paga a EXPERIÊNCIA do jogador, não quem
    /// ganha, no NÍVEL DELE. Ordem para qualquer jogador (v5.1): escapar com sustos > derrota dramática > vitória sem
    /// graça > derrota no meio > derrota cedo. O custo por segundo é pequeno e a tensão na faixa paga: acabar a partida
    /// cedo nunca pode render mais (no v5 rendia: castigo por segundo empurrava o diretor a deixar pegar o jogador bom).
    /// Contas no cabeçalho do config/diretor_v5.1.yaml.
    /// </summary>
    public class DirectorRewardSystem : MonoBehaviour
    {
        [Header("-----Close calls-----")]
        [SerializeField, Min(0)] private int _closeCallMin = 3;
        [SerializeField, Min(1)] private int _closeCallMax = 6;
        [SerializeField] private float _closeCallReward = 1f;

        // Close call colado no anterior vale pouco: é um susto em cima do outro.
        [SerializeField, Min(0f)] private float _closeCallSpacingSeconds = 90f;
        [SerializeField] private float _crowdedCloseCallReward = 0.25f;
        [SerializeField] private float _excessCloseCallPenalty = 0.5f;

        [Header("-----Fim da partida (por habilidade: ruim -> bom)-----")]
        // Escapou com a faixa mínima de close calls: a partida boa, para qualquer jogador.
        [SerializeField] private float _survivedReward = 2f;
        // Escapou sem susto: base + fração dos close calls - até _tooEasyPenalty no bom; fica sempre acima de perder.
        [SerializeField] private float _boringSurvivalBase = 0.5f;
        [SerializeField] private float _boringSurvivalReward = 0.5f;
        [SerializeField] private float _tooEasyPenalty = 1.5f;

        // Pego antes disto (s): o jogador nem jogou. O mesmo para todos: perder cedo nunca vale a pena.
        [SerializeField, Min(0f)] private float _earlyCatchSeconds = 180f;
        [SerializeField] private float _earlyCatchPenalty = 2f;
        [SerializeField] private float _catchPenalty = 0.75f;

        // Pego tarde e depois da faixa mínima de close calls: derrota dramática, aceitável.
        [SerializeField, Min(0f)] private float _dramaticCatchSeconds = 300f;
        [SerializeField] private float _dramaticCatchReward = 0.5f;

        [Header("-----Ritmo e custo-----")]
        // Tensão baixa por mais que isto (s) começa a custar, por segundo (x0.5 no ruim, x1.5 no bom).
        [SerializeField, Min(0f)] private float _boredGraceSeconds = 60f;
        [SerializeField, Min(0f)] private float _boredPenaltyPerSecond = 0.001f;

        // Por segundo com a tensão nesta faixa: a partida longa E tensa rende mais (teto ~+0.7 em 700 s).
        [SerializeField] private Vector2 _goodTension = new Vector2(0.3f, 0.85f);
        [SerializeField, Min(0f)] private float _tensionRewardPerSecond = 0.001f;
        // v6: baratos. No v5.1 o custo passava o efeito e ele aprendeu a não fazer nada; o orçamento já segura o abuso.
        [SerializeField, Min(0f)] private float _interventionCost = 0.01f;

        // Distrair custa um pouco mais: no v3 virou a única ação (4.4 por partida), que só afasta o monstro.
        [SerializeField, Min(0f)] private float _decoyCost = 0.03f;

        // Por segundo, por 0.1 de escala fora do normal (velocidade, visão, fôlego e barulho do passo).
        [SerializeField, Min(0f)] private float _leverCostPerSecond = 0.0005f;

        public int CloseCallMin => _closeCallMin;
        public int CloseCallMax => _closeCallMax;

        public float EvaluateStep(in DirectorStepContext context)
        {
            float reward = 0f;
            float skill = Mathf.Clamp01(context.PlayerSkill);

            if (context.CloseCall)
            {
                if (context.CloseCallCount > _closeCallMax)
                    reward -= _excessCloseCallPenalty;
                else
                    reward += context.SecondsSincePreviousCloseCall >= _closeCallSpacingSeconds
                        ? _closeCallReward
                        : _crowdedCloseCallReward;
            }

            if (context.BoredSeconds > _boredGraceSeconds)
                reward -= _boredPenaltyPerSecond * (0.5f + skill) * context.DeltaSeconds;
            if (context.Tension >= _goodTension.x && context.Tension <= _goodTension.y)
                reward += _tensionRewardPerSecond * context.DeltaSeconds;

            reward -= _interventionCost * ((context.Attracted ? 1 : 0) + (context.Highlighted ? 1 : 0));
            if (context.Decoyed)
                reward -= _decoyCost;
            float offsets = context.SpeedOffset + context.ViewOffset + context.StaminaOffset + context.FootstepOffset;
            reward -= _leverCostPerSecond * 10f * offsets * context.DeltaSeconds;

            if (context.Ended)
                reward += EndReward(context, skill);

            return reward;
        }

        private float EndReward(in DirectorStepContext context, float skill)
        {
            int calls = Mathf.Min(context.CloseCallCount, _closeCallMax);
            switch (context.Outcome)
            {
                case FreeEpisodeOutcome.Caught:
                    if (context.ElapsedSeconds < _earlyCatchSeconds)
                        return -_earlyCatchPenalty;
                    if (context.ElapsedSeconds >= _dramaticCatchSeconds && calls >= _closeCallMin)
                        return _dramaticCatchReward;
                    return -_catchPenalty;

                case FreeEpisodeOutcome.Escaped:
                case FreeEpisodeOutcome.TimedOut:
                    return calls >= _closeCallMin
                        ? _survivedReward
                        : _boringSurvivalBase + _boringSurvivalReward * calls / Mathf.Max(1, _closeCallMin) - _tooEasyPenalty * skill;

                default:
                    return 0f;
            }
        }
    }
}
