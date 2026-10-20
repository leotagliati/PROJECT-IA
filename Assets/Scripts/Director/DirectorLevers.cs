using System;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Director
{
    /// <summary>
    /// O que o diretor MEXE, um por diretor, sempre pelas facades e com padrão 1: som plantado (NoiseEvents), destaque
    /// de chave, e escalas que mudam aos poucos dentro da faixa segura: velocidade e visão do monstro, fôlego do jogador
    /// (gasta e enche) e o raio em que o monstro ouve o passo dele. Orçamento e espera por partida; gastou, acabou.
    /// </summary>
    [Serializable]
    public class DirectorLevers
    {
        [Header("Faixa segura (testada em 17/10)")]
        [SerializeField] private Vector2 _speedRange = new Vector2(0.85f, 1.2f);
        [SerializeField] private Vector2 _viewRange = new Vector2(0.85f, 1.15f);
        [SerializeField] private Vector2 _staminaRange = new Vector2(0.7f, 1.3f);
        [SerializeField] private Vector2 _footstepRange = new Vector2(0.6f, 1.5f);

        [Tooltip("Quanto a escala anda por segundo até o alvo: nunca de uma vez.")]
        [SerializeField, Min(0.001f)] private float _scaleRate = 0.03f;

        [Header("Orçamento por partida")]
        [SerializeField, Min(0)] private int _attractBudget = 6;
        [SerializeField, Min(0)] private int _decoyBudget = 6;
        [SerializeField, Min(0)] private int _highlightBudget = 3;

        [Tooltip("Espera (s) entre dois sons plantados, de qualquer tipo.")]
        [SerializeField, Min(0f)] private float _noiseCooldown = 20f;
        [SerializeField, Min(0f)] private float _highlightCooldown = 45f;

        [Header("Som plantado")]
        [Tooltip("Som de atrair cai a esta distância (m) do jogador: perto, não em cima.")]
        [SerializeField] private Vector2 _attractOffset = new Vector2(3f, 8f);

        [Tooltip("Distração: longe do jogador pelo menos isto (m), e ao alcance do monstro.")]
        [SerializeField, Min(0f)] private float _decoyMinFromPrey = 25f;
        [SerializeField, Min(1f)] private float _maxPlantRadius = 60f;
        [SerializeField, Min(0f)] private float _plantMargin = 5f;
        [SerializeField, Min(1)] private int _decoySamples = 16;

        [Tooltip("Som que o jogador ouve no ponto plantado (id do AudioLibrary). Vazio = só a IA ouve (treino).")]
        [SerializeField] private string _plantSoundId = "";

        [Header("Destaque de chave")]
        [Tooltip("Som na chave destacada (id do AudioLibrary). Vazio = só o treino (a presa passa a conhecê-la).")]
        [SerializeField] private string _highlightSoundId = "";

        private GraphLocomotion _locomotion;
        private GraphHiderPerception _perception;
        private FreeHider _hider;
        private FreeFootprintTrail _trail;
        private NavMeshPath _path;

        public float SpeedScale { get; private set; } = 1f;
        public float ViewScale { get; private set; } = 1f;
        public float SpeedTarget { get; private set; } = 1f;
        public float ViewTarget { get; private set; } = 1f;
        public int AttractsLeft { get; private set; }
        public int DecoysLeft { get; private set; }
        public int HighlightsLeft { get; private set; }
        public float NoiseCooldownLeft { get; private set; }
        public float HighlightCooldownLeft { get; private set; }
        public float StaminaScale { get; private set; } = 1f;
        public float FootstepScale { get; private set; } = 1f;
        public float StaminaTarget { get; private set; } = 1f;
        public float FootstepTarget { get; private set; } = 1f;

        public float AttractBudgetFraction => _attractBudget > 0 ? (float)AttractsLeft / _attractBudget : 0f;
        public float DecoyBudgetFraction => _decoyBudget > 0 ? (float)DecoysLeft / _decoyBudget : 0f;
        public float HighlightBudgetFraction => _highlightBudget > 0 ? (float)HighlightsLeft / _highlightBudget : 0f;
        public float SpeedNormalized => Mathf.InverseLerp(_speedRange.x, _speedRange.y, SpeedScale);
        public float ViewNormalized => Mathf.InverseLerp(_viewRange.x, _viewRange.y, ViewScale);
        public float SpeedOffset => Mathf.Abs(SpeedScale - 1f);
        public float ViewOffset => Mathf.Abs(ViewScale - 1f);
        public float StaminaNormalized => Mathf.InverseLerp(_staminaRange.x, _staminaRange.y, StaminaScale);
        public float FootstepNormalized => Mathf.InverseLerp(_footstepRange.x, _footstepRange.y, FootstepScale);
        public float StaminaOffset => Mathf.Abs(StaminaScale - 1f);
        public float FootstepOffset => Mathf.Abs(FootstepScale - 1f);

        public bool CanPlant => NoiseCooldownLeft <= 0f;
        public bool CanAttract => CanPlant && AttractsLeft > 0;
        public bool CanDecoy => CanPlant && DecoysLeft > 0;
        public bool CanHighlight => HighlightCooldownLeft <= 0f && HighlightsLeft > 0;

        public void Configure(GraphLocomotion locomotion, GraphHiderPerception perception)
        {
            _locomotion = locomotion;
            _perception = perception;
        }

        /// <summary>O jogador do treino (a presa) e o rastro dele; no jogo, as mesmas alavancas no jogador.</summary>
        public void ConfigurePlayer(FreeHider hider, FreeFootprintTrail trail)
        {
            _hider = hider;
            _trail = trail;
        }

        public void Reset()
        {
            SpeedScale = SpeedTarget = 1f;
            ViewScale = ViewTarget = 1f;
            StaminaScale = StaminaTarget = 1f;
            FootstepScale = FootstepTarget = 1f;
            AttractsLeft = _attractBudget;
            DecoysLeft = _decoyBudget;
            HighlightsLeft = _highlightBudget;
            NoiseCooldownLeft = 0f;
            HighlightCooldownLeft = 0f;
            Apply();
        }

        /// <summary>-1 = menos, 0 = normal, +1 = mais (velocidade, visão, fôlego, barulho do passo).</summary>
        public void SetTargets(int speed, int view, int stamina, int footstep)
        {
            SpeedTarget = Pick(_speedRange, speed);
            ViewTarget = Pick(_viewRange, view);
            StaminaTarget = Pick(_staminaRange, stamina);
            FootstepTarget = Pick(_footstepRange, footstep);
        }

        private static float Pick(Vector2 range, int direction) => direction < 0 ? range.x : direction > 0 ? range.y : 1f;

        /// <summary>Sem poder ajudar a IA (alívio, perseguição), o que estava ajudando volta ao normal.</summary>
        public void ClampHelpToAI()
        {
            SpeedTarget = Mathf.Min(SpeedTarget, 1f);
            ViewTarget = Mathf.Min(ViewTarget, 1f);
            StaminaTarget = Mathf.Max(StaminaTarget, 1f);
            FootstepTarget = Mathf.Min(FootstepTarget, 1f);
        }

        public void Tick(float dt)
        {
            NoiseCooldownLeft = Mathf.Max(0f, NoiseCooldownLeft - dt);
            HighlightCooldownLeft = Mathf.Max(0f, HighlightCooldownLeft - dt);
            SpeedScale = Mathf.MoveTowards(SpeedScale, SpeedTarget, _scaleRate * dt);
            ViewScale = Mathf.MoveTowards(ViewScale, ViewTarget, _scaleRate * dt);
            StaminaScale = Mathf.MoveTowards(StaminaScale, StaminaTarget, _scaleRate * dt);
            FootstepScale = Mathf.MoveTowards(FootstepScale, FootstepTarget, _scaleRate * dt);
            Apply();
        }

        private void Apply()
        {
            if (_locomotion != null)
                _locomotion.SpeedScale = SpeedScale;
            if (_perception != null)
                _perception.ViewScale = ViewScale;
            if (_hider != null)
                _hider.SetStaminaRate(StaminaScale);
            if (_trail != null)
                _trail.NoiseScale = FootstepScale;
        }

        /// <summary>Barulho perto do jogador, com alcance para o monstro ouvir de onde está.</summary>
        public bool TryAttract(Vector3 monster, Vector3 prey)
        {
            if (!CanAttract)
                return false;

            Vector2 circle = UnityEngine.Random.insideUnitCircle.normalized
                             * UnityEngine.Random.Range(_attractOffset.x, _attractOffset.y);
            if (!DirectorNav.TryGround(prey + new Vector3(circle.x, 0f, circle.y), out Vector3 point, _attractOffset.y)
                && !DirectorNav.TryGround(prey, out point))
                return false;

            if (!Plant(monster, point))
                return false;

            AttractsLeft--;
            return true;
        }

        /// <summary>Barulho LONGE do jogador, do lado oposto, que o monstro ouve: tira ele de perto.</summary>
        public bool TryDecoy(Vector3 monster, Vector3 prey, FreeMap map)
        {
            if (!CanDecoy || map == null || map.PointCount == 0)
                return false;

            EnsurePath();
            float bestScore = float.MinValue;
            Vector3 best = Vector3.zero;
            for (int i = 0; i < _decoySamples; i++)
            {
                Vector3 point = map.PointGround(UnityEngine.Random.Range(0, map.PointCount));
                float fromPrey = DirectorNav.PathLength(prey, point, _path);
                float fromMonster = DirectorNav.PathLength(monster, point, _path);
                if (fromPrey < _decoyMinFromPrey || fromMonster < 0f || fromMonster + _plantMargin > _maxPlantRadius)
                    continue;

                float score = fromPrey - 0.5f * fromMonster;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = point;
                }
            }

            if (bestScore == float.MinValue || !Plant(monster, best))
                return false;

            DecoysLeft--;
            return true;
        }

        /// <summary>Destaca a chave mais perto que o jogador ainda não achou (no jogo: brilho e som nela).</summary>
        public bool TryHighlight(FreeHider hider)
        {
            if (!CanHighlight || hider == null || !hider.RevealNearestKey())
                return false;

            if (!string.IsNullOrEmpty(_highlightSoundId))
                AudioProvider.PlayAt(_highlightSoundId, hider.transform.position);
            HighlightsLeft--;
            HighlightCooldownLeft = _highlightCooldown;
            return true;
        }

        private bool Plant(Vector3 monster, Vector3 point)
        {
            EnsurePath();
            float reach = DirectorNav.PathLength(monster, point, _path);
            if (reach < 0f)
                return false;

            NoiseEvents.Emit(point, Mathf.Min(_maxPlantRadius, reach + _plantMargin));
            if (!string.IsNullOrEmpty(_plantSoundId))
                AudioProvider.PlayAt(_plantSoundId, point);
            NoiseCooldownLeft = _noiseCooldown;
            return true;
        }

        private void EnsurePath()
        {
            if (_path == null)
                _path = new NavMeshPath();
        }
    }
}
