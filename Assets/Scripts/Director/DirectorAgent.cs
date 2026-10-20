using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Assets.Scripts.Director
{
    /// <summary>
    /// DIRETOR DE JOGO por reforço: ajusta o ritmo para cada jogador ficar no nível certo de dificuldade. Um por arena,
    /// segue o episódio do monstro (que fica CONGELADO, em inferência) e só lê o jogo e age pelas alavancas:
    ///   DirectorSenses        tensão, close calls, distância pelo caminho, tempo sem ser visto
    ///   DirectorLevers        som plantado (atrair / distrair), destaque de chave, velocidade e visão do monstro,
    ///                         fôlego e barulho do passo do jogador
    ///   DirectorRewardSystem  recompensa (função pura do DirectorStepContext)
    /// Os LIMITES são máscara de ação, não recompensa: no alívio (depois de um close call), na perseguição, depois do
    /// máximo de close calls ou do tempo previsto, nada que ajude a IA fica disponível. Nunca pega, nunca mexe em chave.
    /// Decide a cada _decisionSeconds (pedido daqui, sem DecisionRequester).
    /// </summary>
    public class DirectorAgent : Agent
    {
        public const string BehaviorNameDirector = "Director";
        public const int ObservationSize = 29;

        // Evento (nada, atrair, distrair, destacar chave); PRESSÃO DO MONSTRO (velocidade e visão juntas) e PRESSÃO NO
        // JOGADOR (fôlego menor e passo mais alto juntos), cada uma normal / menos / mais. Eram 5 ramos no v5: a
        // entropia ainda estava alta no fim de 1.2M.
        public static readonly int[] Branches = { 4, 3, 3 };

        private const int EventNone = 0, EventAttract = 1, EventDecoy = 2, EventHighlight = 3;
        private const int LeverNormal = 0, LeverLess = 1, LeverMore = 2;

        [Header("-----Sistemas (vazio = acha na arena)-----")]
        [SerializeField] private FreeExplorerManager _monster;
        [SerializeField] private DirectorRewardSystem _rewardSystem;

        [Header("-----Ritmo-----")]
        // 5 s (era 3): fica mais claro qual decisão causou o quê.
        [SerializeField, Min(0.5f)] private float _decisionSeconds = 5f;

        [Tooltip("Alívio obrigatório (s) depois de um close call: nada que ajude a IA.")]
        [SerializeField, Min(0f)] private float _reliefSeconds = 60f;

        [Tooltip("Tempo previsto da partida (s). Passou, só ajuda o jogador a terminar.")]
        [SerializeField, Min(1f)] private float _expectedSeconds = 600f;

        [SerializeField] private DirectorSenses _senses = new DirectorSenses();
        [SerializeField] private DirectorLevers _levers = new DirectorLevers();

        [Header("-----Debug-----")]
        [SerializeField] private bool _debugHud = false;

        private bool _subscribed;
        private int _stepsSinceDecision;
        private float _reliefLeft;
        private bool _attracted, _decoyed, _highlighted;
        private int _attracts, _decoys, _highlights;
        private float _speedScaleSum, _viewScaleSum, _staminaScaleSum, _footstepScaleSum, _activeSeconds;

        private bool Ready => _monster != null && _monster.IsReady;
        private float Elapsed => _monster != null ? _monster.ElapsedSeconds : 0f;
        private bool Chasing => _monster.Perception.IsSeeing || _monster.Locomotion.State == GraphLocomotion.Awareness.Chase;
        private bool Overtime => Elapsed >= _expectedSeconds;
        private bool CanHelpAI => _reliefLeft <= 0f && !Overtime && !Chasing && _senses.CloseCalls < _rewardSystem.CloseCallMax;

        private IGraphTarget Prey => _monster.Arena != null ? _monster.Arena.Target : null;
        private bool PreyLive => _monster.Settings.HasHider && GraphTarget.IsLive(Prey);
        private FreeHider PreyHider => Prey as FreeHider;

        protected override void Awake()
        {
            if (_monster == null)
            {
                FreeArenaController arena = GetComponentInParent<FreeArenaController>();
                if (arena != null)
                    _monster = arena.GetComponentInChildren<FreeExplorerManager>(true);
            }

            if (_rewardSystem == null)
                _rewardSystem = GetComponent<DirectorRewardSystem>();
            if (_rewardSystem == null)
                _rewardSystem = gameObject.AddComponent<DirectorRewardSystem>();

            EnforceBrainShape(GetComponent<BehaviorParameters>());
            base.Awake();
        }

        private void EnforceBrainShape(BehaviorParameters behavior)
        {
            if (behavior == null)
                return;

            if (behavior.BrainParameters.VectorObservationSize != ObservationSize)
                Debug.LogWarning($"{name}: VectorObservationSize ajustado para {ObservationSize}. Salve o prefab.", this);
            behavior.BrainParameters.VectorObservationSize = ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(Branches);
        }

        // Start, não OnEnable: os sistemas do monstro só existem depois do Awake dele.
        private void Start()
        {
            if (_monster == null)
            {
                Debug.LogError($"{name}: diretor sem monstro (FreeExplorerManager na arena).", this);
                return;
            }

            _monster.EpisodeBegan += ResetState;
            _monster.EpisodeFinished += HandleMonsterFinished;
            if (_monster.Locomotion != null)
                _monster.Locomotion.GaveUp += _senses.NotifyGaveUp;
            _levers.Configure(_monster.Locomotion, _monster.Perception);
            FreeHider hider = _monster.Arena != null ? _monster.Arena.Hider : null;
            _levers.ConfigurePlayer(hider, hider != null ? hider.GetComponent<FreeFootprintTrail>() : null);
            _subscribed = true;
            ResetState();
        }

        private void OnDestroy()
        {
            if (!_subscribed || _monster == null)
                return;

            _monster.EpisodeBegan -= ResetState;
            _monster.EpisodeFinished -= HandleMonsterFinished;
            if (_monster.Locomotion != null)
                _monster.Locomotion.GaveUp -= _senses.NotifyGaveUp;
        }

        public override void OnEpisodeBegin() => ResetState();

        private void ResetState()
        {
            _senses.Reset();
            _levers.Reset();
            _stepsSinceDecision = 0;
            _reliefLeft = 0f;
            _attracted = _decoyed = _highlighted = false;
            _attracts = _decoys = _highlights = 0;
            _speedScaleSum = _viewScaleSum = _staminaScaleSum = _footstepScaleSum = _activeSeconds = 0f;
        }

        private void FixedUpdate()
        {
            if (!Ready || !PreyLive)
                return;

            float dt = Time.fixedDeltaTime;
            Vector3 monster = _monster.transform.position;
            _senses.Tick(dt, monster, Prey.Position, _monster.Perception.IsSeeing, _monster.Locomotion.State);
            if (_senses.CloseCallThisStep)
                _reliefLeft = _reliefSeconds;
            else
                _reliefLeft = Mathf.Max(0f, _reliefLeft - dt);

            if (!CanHelpAI)
                _levers.ClampHelpToAI();
            _levers.Tick(dt);

            AddReward(_rewardSystem.EvaluateStep(BuildContext(dt, ended: false, default)));
            _attracted = _decoyed = _highlighted = false;

            _speedScaleSum += _levers.SpeedScale * dt;
            _viewScaleSum += _levers.ViewScale * dt;
            _staminaScaleSum += _levers.StaminaScale * dt;
            _footstepScaleSum += _levers.FootstepScale * dt;
            _activeSeconds += dt;

            if (++_stepsSinceDecision >= Mathf.Max(1, Mathf.RoundToInt(_decisionSeconds / dt)))
            {
                _stepsSinceDecision = 0;
                _senses.UpdatePathDistance(monster, Prey.Position);
                RequestDecision();
            }
        }

        private DirectorStepContext BuildContext(float dt, bool ended, FreeEpisodeOutcome outcome) => new DirectorStepContext
        {
            DeltaSeconds = dt,
            CloseCall = !ended && _senses.CloseCallThisStep,
            CloseCallCount = _senses.CloseCalls,
            SecondsSincePreviousCloseCall = _senses.SecondsSincePreviousCloseCall,
            BoredSeconds = _senses.BoredSeconds,
            Tension = _senses.Tension,
            Attracted = _attracted,
            Decoyed = _decoyed,
            Highlighted = _highlighted,
            SpeedOffset = _levers.SpeedOffset,
            ViewOffset = _levers.ViewOffset,
            StaminaOffset = _levers.StaminaOffset,
            FootstepOffset = _levers.FootstepOffset,
            PlayerSkill = PreyHider != null && PreyHider.Profile.Active ? PreyHider.Profile.Skill : 0.5f,
            Ended = ended,
            Outcome = outcome,
            ElapsedSeconds = Elapsed,
        };

        // Antes do EndEpisode do monstro: o fim da partida é o fim do episódio do diretor.
        private void HandleMonsterFinished(FreeEpisodeOutcome outcome)
        {
            // Monstro fora do mapa ou episódio sem presa: não diz nada sobre o diretor.
            if (outcome == FreeEpisodeOutcome.OutOfMap || (!PreyLive && outcome == FreeEpisodeOutcome.Covered))
            {
                EpisodeInterrupted();
                return;
            }

            AddReward(_rewardSystem.EvaluateStep(BuildContext(0f, ended: true, outcome)));
            RecordStats(outcome);
            EndEpisode();
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!Ready || !PreyLive)
            {
                for (int i = 0; i < ObservationSize; i++)
                    sensor.AddObservation(0f);
                return;
            }

            GraphLocomotion.Awareness state = _monster.Locomotion.State;
            FreeHider hider = PreyHider;
            FreeKeyField keys = _monster.Keys;
            bool hasKeys = keys != null && keys.Count > 0;
            int max = Mathf.Max(1, _rewardSystem.CloseCallMax);

            sensor.AddObservation(Mathf.Clamp01(Elapsed / (_expectedSeconds * 1.5f)));
            sensor.AddObservation(state == GraphLocomotion.Awareness.Patrol ? 1f : 0f);
            sensor.AddObservation(state == GraphLocomotion.Awareness.Alert ? 1f : 0f);
            sensor.AddObservation(state == GraphLocomotion.Awareness.Chase ? 1f : 0f);
            sensor.AddObservation(_monster.Perception.IsSeeing ? 1f : 0f);
            sensor.AddObservation(_monster.Perception.IsSearchingLastKnown ? 1f : 0f);
            sensor.AddObservation(_senses.PathDistance < 0f ? 1f : Mathf.Clamp01(_senses.PathDistance / 60f));
            sensor.AddObservation(_senses.HasSeen ? Mathf.Clamp01(_senses.SecondsSinceSeen / 120f) : 1f);
            sensor.AddObservation(_senses.Tension);
            sensor.AddObservation(Mathf.Clamp01((float)_senses.CloseCalls / max));
            sensor.AddObservation(Mathf.Clamp01(_senses.SecondsSinceCloseCall / 180f));
            sensor.AddObservation(_reliefSeconds > 0f ? _reliefLeft / _reliefSeconds : 0f);
            sensor.AddObservation(_levers.AttractBudgetFraction);
            sensor.AddObservation(_levers.DecoyBudgetFraction);
            sensor.AddObservation(_levers.HighlightBudgetFraction);
            sensor.AddObservation(_levers.SpeedNormalized);
            sensor.AddObservation(_levers.ViewNormalized);
            sensor.AddObservation(_levers.StaminaNormalized);
            sensor.AddObservation(_levers.FootstepNormalized);
            sensor.AddObservation(Prey.IsRunning ? 1f : 0f);
            sensor.AddObservation(hider != null && hider.IsCrouching ? 1f : 0f);
            sensor.AddObservation(hider != null ? hider.StaminaFraction : 1f);
            sensor.AddObservation(hasKeys ? 1f : 0f);
            sensor.AddObservation(hasKeys ? (float)(keys.Count - keys.Remaining) / keys.Count : 0f);
            sensor.AddObservation(Overtime ? 1f : 0f);

            // Como o jogador está indo (v4): é o que diz ao diretor se ele é bom, como diria num jogador de verdade.
            float alive = Mathf.Max(1f, Elapsed);
            sensor.AddObservation(Mathf.Clamp01(_senses.Sightings / 5f));
            sensor.AddObservation(_senses.Sightings > 0 ? Mathf.Clamp01((float)_senses.CloseCalls / _senses.Sightings) : 0f);
            sensor.AddObservation(Mathf.Clamp01(_senses.HuntedTime / alive));
            sensor.AddObservation(_senses.MeanPathDistance < 0f ? 1f : Mathf.Clamp01(_senses.MeanPathDistance / 60f));
        }

        // director_off (YAML): linha de base, a partida sem diretor; as métricas saem iguais para comparar.
        private bool Off => Academy.IsInitialized && Academy.Instance.EnvironmentParameters.GetWithDefault("director_off", 0f) > 0.5f;

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (!Ready || !PreyLive || Off)
            {
                for (int branch = 0; branch < Branches.Length; branch++)
                    for (int action = 1; action < Branches[branch]; action++)
                        actionMask.SetActionEnabled(branch, action, false);
                return;
            }

            bool helpAI = CanHelpAI;
            actionMask.SetActionEnabled(0, EventAttract, helpAI && _levers.CanAttract);
            actionMask.SetActionEnabled(0, EventDecoy, _levers.CanDecoy);
            FreeKeyField keys = _monster.Keys;
            bool keysLeft = keys != null && keys.Count > 0 && keys.Remaining > 0;
            actionMask.SetActionEnabled(0, EventHighlight,
                _levers.CanHighlight && keysLeft && PreyHider != null && !PreyHider.KnowsAllRemainingKeys);
            // "Mais" nas duas pressões ajuda a IA.
            actionMask.SetActionEnabled(1, LeverMore, helpAI);
            actionMask.SetActionEnabled(2, LeverMore, helpAI);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!Ready || !PreyLive)
                return;

            ActionSegment<int> discrete = actions.DiscreteActions;
            bool helpAI = CanHelpAI;
            // Pressão do monstro: velocidade e visão juntas. Pressão no jogador: fôlego menor e passo mais alto juntos.
            int monsterPressure = LeverValue(discrete[1], helpAI);
            int playerPressure = LeverValue(discrete[2], helpAI);
            _levers.SetTargets(monsterPressure, monsterPressure, -playerPressure, playerPressure);

            Vector3 monster = _monster.transform.position;
            switch (discrete[0])
            {
                case EventAttract when helpAI:
                    if (_levers.TryAttract(monster, Prey.Position))
                    {
                        _attracted = true;
                        _attracts++;
                    }
                    break;

                case EventDecoy:
                    if (_levers.TryDecoy(monster, Prey.Position, _monster.Arena.Map))
                    {
                        _decoyed = true;
                        _decoys++;
                    }
                    break;

                case EventHighlight:
                    if (_levers.TryHighlight(PreyHider))
                    {
                        _highlighted = true;
                        _highlights++;
                    }
                    break;
            }
        }

        // A máscara já tira o "mais" sem poder ajudar a IA; isto segura a heurística e decisões atrasadas.
        private static int LeverValue(int action, bool helpAI) =>
            action == LeverLess ? -1 : action == LeverMore && helpAI ? 1 : 0;

        /// <summary>
        /// Por partida, no TensorBoard (compare com o Hunt/Caught do monstro, gravado junto):
        ///   Director/CloseCalls, InBand (close calls na faixa), Survived, EarlyCatch, Seconds
        ///   Director/Attracts, Decoys, Highlights, Mean(Speed|View|Stamina|Footstep)Scale, MeanTension, BoredFraction
        ///   Player/Survived|CloseCalls|Seconds por habilidade (Ruim, Medio, Bom) e Survived|CloseCalls por estilo
        /// </summary>
        private void RecordStats(FreeEpisodeOutcome outcome)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            int calls = _senses.CloseCalls;
            bool caught = outcome == FreeEpisodeOutcome.Caught;
            stats.Add("Director/CloseCalls", calls);
            stats.Add("Director/InBand", calls >= _rewardSystem.CloseCallMin && calls <= _rewardSystem.CloseCallMax ? 1f : 0f);
            stats.Add("Director/Survived", caught ? 0f : 1f);
            stats.Add("Director/EarlyCatch", caught && Elapsed < 180f ? 1f : 0f);
            stats.Add("Director/Seconds", Elapsed);
            stats.Add("Director/Attracts", _attracts);
            stats.Add("Director/Decoys", _decoys);
            stats.Add("Director/Highlights", _highlights);
            // Por jogador imitado: o diretor tem que acertar o nível de cada um, não a média.
            FreeHider hider = PreyHider;
            if (hider != null && hider.Profile.Active)
            {
                string skill = hider.Profile.SkillLabel;
                string style = hider.Profile.Style.ToString();
                stats.Add($"Player/Survived{skill}", caught ? 0f : 1f);
                stats.Add($"Player/CloseCalls{skill}", calls);
                stats.Add($"Player/Seconds{skill}", Elapsed);
                stats.Add($"Player/Survived{style}", caught ? 0f : 1f);
                stats.Add($"Player/CloseCalls{style}", calls);
            }

            if (_activeSeconds > 0f)
            {
                stats.Add("Director/MeanSpeedScale", _speedScaleSum / _activeSeconds);
                stats.Add("Director/MeanViewScale", _viewScaleSum / _activeSeconds);
                stats.Add("Director/MeanStaminaScale", _staminaScaleSum / _activeSeconds);
                stats.Add("Director/MeanFootstepScale", _footstepScaleSum / _activeSeconds);
                stats.Add("Director/MeanTension", _senses.TensionSum / _activeSeconds);
                stats.Add("Director/BoredFraction", _senses.BoredTime / _activeSeconds);
            }
        }

        private void OnGUI()
        {
            if (!_debugHud || !Ready)
                return;

            string phase = Chasing ? "PICO" : _reliefLeft > 0f ? $"ALÍVIO ({_reliefLeft:0} s)" : Overtime ? "PASSOU DO TEMPO" : "subida";
            string text =
                $"<b>Diretor</b>  {name}\n" +
                $"Fase: {phase}   Tensão: {_senses.Tension:0.00}   vazio há {_senses.BoredSeconds:0} s\n" +
                $"Close calls: {_senses.CloseCalls} (faixa {_rewardSystem.CloseCallMin}-{_rewardSystem.CloseCallMax})   " +
                $"distância {_senses.PathDistance:0} m\n" +
                $"Monstro: velocidade x{_levers.SpeedScale:0.00} (alvo {_levers.SpeedTarget:0.00})   visão x{_levers.ViewScale:0.00}\n" +
                $"Jogador: fôlego x{_levers.StaminaScale:0.00}   passo x{_levers.FootstepScale:0.00}\n" +
                $"Sobram: atrair {_levers.AttractsLeft}  distrair {_levers.DecoysLeft}  destacar {_levers.HighlightsLeft}\n" +
                $"Reward: {GetCumulativeReward():0.00}";
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };
            GUI.Box(new Rect(10, 250, 560, 120), text, style);
        }
    }
}
