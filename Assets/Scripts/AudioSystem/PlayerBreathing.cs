using UnityEngine;

public class PlayerBreathing : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;

    [SerializeField] private string calmId = "breath";
    [SerializeField] private string heavyId = "breath_heavy";

    [Header("-----Fôlego-----")]
    [Tooltip("Segundos correndo sem parar até o fôlego acabar por completo.")]
    [SerializeField, Min(0.01f)] private float timeToExhaust = 5f;

    [Tooltip("Segundos parado/andando até recuperar do fôlego zerado.")]
    [SerializeField, Min(0.01f)] private float timeToRecover = 7f;

    // Abaixo disso o ofegante fica mudo: um sprint curto (desviar de uma quina) não pode
    // disparar respiração pesada.
    [Tooltip("Fração de fôlego gasto a partir da qual o ofegante começa a aparecer.")]
    [SerializeField, Range(0f, 1f)] private float heavyThreshold = 0.3f;

    [Tooltip("Quanto o loop calmo cai quando o ofegante está no máximo (1 = some).")]
    [SerializeField, Range(0f, 1f)] private float calmDuckAtFull = 1f;

    [Header("-----Variação de pitch-----")]
    // Deriva lenta por ruído, e não sorteio por ciclo: salto de pitch no ponto de loop é
    // audível, deriva contínua não. A amplitude é o PitchJitter da entrada na library.
    [Tooltip("Velocidade da deriva de pitch (ciclos de ruído por segundo).")]
    [SerializeField, Min(0f)] private float pitchDriftRate = 0.12f;
    [SerializeField, Min(0.01f)] private float fadeOutOnGameOver = 0.25f;

    private Voice _calm;
    private Voice _heavy;
    private float _exertion;
    private float _masterGain = 1f;
    private bool _gameOver;

    private class Voice
    {
        public AudioSource Source;
        public float BaseVolume;
        public float PitchJitter;
        public float NoiseSeed;
    }

    private void Awake()
    {
        if (movement == null)
            movement = GetComponentInParent<PlayerMovement>();

        if (AudioProvider.IsMuted || movement == null)
        {
            enabled = false;
            return;
        }

        // Library ausente já é logada pelo provider.
        AudioLibrary library = AudioProvider.Library;
        if (library == null)
        {
            enabled = false;
            return;
        }

        _calm = CreateVoice(library, calmId);
        _heavy = CreateVoice(library, heavyId);

        if (_calm == null && _heavy == null)
            enabled = false;
    }

    private Voice CreateVoice(AudioLibrary library, string id)
    {
        if (!library.TryGet(id, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning($"{name}: id '{id}' não está na AudioLibrary — sem essa respiração.", this);
            return null;
        }

        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        // 2D: é a respiração de quem está ouvindo, não um som no mundo.
        source.spatialBlend = 0f;
        source.outputAudioMixerGroup = library.MixerGroup;
        source.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        source.volume = 0f;

        // Começa num ponto aleatório do clipe: os dois loops não ficam em fase, e cada Play
        // não abre sempre na mesma inspiração.
        source.time = Random.Range(0f, source.clip.length);
        source.Play();

        return new Voice
        {
            Source = source,
            BaseVolume = entry.Volume,
            PitchJitter = entry.PitchJitter,
            NoiseSeed = Random.Range(0f, 1000f),
        };
    }

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable()
    {
        GameManager.StateChanged -= HandleGameState;
        Silence();
    }

    private void HandleGameState(GameState state)
    {
        if (state == GameState.Won || state == GameState.Lost)
            BeginFadeOut();
    }

    private void BeginFadeOut() => _gameOver = true;

    /// <summary>Para e desliga as fontes. Volume zero não basta: a fonte continua ativa e tocando.</summary>
    private void Silence()
    {
        foreach (Voice voice in new[] { _calm, _heavy })
        {
            if (voice == null || voice.Source == null)
                continue;

            voice.Source.Stop();
            voice.Source.enabled = false;
        }
    }

    private void Update()
    {
        // Além do evento, consulta por frame: PlayerMovement desligado é o que a captura (e
        // qualquer cutscene) faz com o jogador, venha o fim de onde vier.
        if (!movement.isActiveAndEnabled
            || (GameManager.Current != null && GameManager.Current.IsOver))
        {
            BeginFadeOut();
        }

        // PlayerMovement desligado (captura, cutscene) não zera o CurrentState: ele fica
        // congelado no último valor, e congelado em Running o fôlego seguiria enchendo.
        // Pulo no meio da corrida não é descanso: o estado vira Jumping, mas o sprint segue.
        PlayerState state = movement.CurrentState;
        bool sprinting = movement.isActiveAndEnabled
                      && (state == PlayerState.Running
                          || (state == PlayerState.Jumping && movement.SprintHeld));

        _exertion = sprinting
            ? Mathf.MoveTowards(_exertion, 1f, Time.deltaTime / timeToExhaust)
            : Mathf.MoveTowards(_exertion, 0f, Time.deltaTime / timeToRecover);

        float heavyGain = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(heavyThreshold, 1f, _exertion));
        float calmGain = 1f - heavyGain * calmDuckAtFull;

        if (_gameOver)
            _masterGain = Mathf.MoveTowards(_masterGain, 0f, Time.deltaTime / fadeOutOnGameOver);

        Apply(_calm, calmGain);
        Apply(_heavy, heavyGain);

        // O OnDisable chama o Silence.
        if (_gameOver && _masterGain <= 0f)
            enabled = false;
    }

    private void Apply(Voice voice, float gain)
    {
        if (voice == null)
            return;

        voice.Source.volume = voice.BaseVolume * gain * _masterGain;

        float noise = Mathf.PerlinNoise(voice.NoiseSeed, Time.time * pitchDriftRate) * 2f - 1f;
        voice.Source.pitch = 1f + noise * voice.PitchJitter;
    }
}
