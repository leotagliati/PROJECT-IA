using UnityEngine;

public class PlayerBreathing : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;

    [Tooltip("Vazio = procura junto do PlayerMovement. Com stamina, o ofegante segue a barra.")]
    [SerializeField] private PlayerStamina stamina;

    [SerializeField] private string calmId = "breath";
    [SerializeField] private string heavyId = "breath_heavy";

    [Header("-----Fôlego-----")]
    // A barra desce e sobe em linha reta e dá um salto quando o jogador sai de exausto (o
    // cansaço vai de 1 para 1 - recoverThreshold): sem suavizar, o ofegante pula junto.
    [Tooltip("Com stamina: segundos para o ofegante acompanhar uma mudança da barra.")]
    [SerializeField, Min(0.01f)] private float staminaFollowTime = 0.6f;

    [Tooltip("Sem stamina: segundos correndo sem parar até o fôlego acabar por completo.")]
    [SerializeField, Min(0.01f)] private float timeToExhaust = 5f;

    [Tooltip("Sem stamina: segundos parado/andando até recuperar do fôlego zerado.")]
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

    [Header("-----Vapor-----")]
    [Tooltip("Vapor na boca nas expirações. Vazio = procura junto do PlayerMovement; sem " +
             "BreathVapor, nada de vapor nem de detecção.")]
    [SerializeField] private BreathVapor vapor;

    // A detecção não distingue inspirar de expirar: pega todo pico de volume. Se o vapor sair
    // na inspiração, liste aqui só as expirações (o Console mostra o que foi detectado).
    [Tooltip("Segundos das expirações no clipe calmo. Vazio = detecta pelos picos de volume.")]
    [SerializeField] private float[] calmExhaleTimes = new float[0];

    [Tooltip("Segundos das expirações no clipe ofegante. Vazio = detecta pelos picos de volume.")]
    [SerializeField] private float[] heavyExhaleTimes = new float[0];

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
        public float[] Exhales;
        public float LastTime;
    }

    private void Awake()
    {
        if (movement == null)
            movement = GetComponentInParent<PlayerMovement>();

        if (stamina == null && movement != null)
            stamina = movement.GetComponent<PlayerStamina>();

        if (vapor == null && movement != null)
            vapor = movement.GetComponent<BreathVapor>();

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

        _calm = CreateVoice(library, calmId, calmExhaleTimes);
        _heavy = CreateVoice(library, heavyId, heavyExhaleTimes);

        if (_calm == null && _heavy == null)
            enabled = false;
    }

    private Voice CreateVoice(AudioLibrary library, string id, float[] exhaleTimes)
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
            Exhales = vapor != null
                ? (exhaleTimes != null && exhaleTimes.Length > 0 ? exhaleTimes : DetectExhales(source.clip))
                : new float[0],
            LastTime = source.time,
        };
    }

    /// <summary>
    /// Início de cada trecho alto do clipe (envelope RMS em janelas de 50 ms, com histerese
    /// para um sopro não virar vários). Depende do clipe estar em Decompress On Load: comprimido
    /// na memória ou streaming, o GetData falha.
    /// </summary>
    private float[] DetectExhales(AudioClip clip)
    {
        int channels = clip.channels;
        var data = new float[clip.samples * channels];

        if (!clip.LoadAudioData() || !clip.GetData(data, 0))
        {
            Debug.LogWarning($"{name}: não deu para ler '{clip.name}' (precisa de Decompress On Load) — " +
                             "liste as expirações à mão para ter vapor.", this);
            return new float[0];
        }

        int window = Mathf.Max(1, clip.frequency / 20);
        int windows = clip.samples / window;
        var envelope = new float[windows];
        float max = 0f;

        for (int w = 0; w < windows; w++)
        {
            float sum = 0f;
            int start = w * window * channels;
            int end = start + window * channels;

            for (int i = start; i < end; i++)
                sum += data[i] * data[i];

            envelope[w] = Mathf.Sqrt(sum / (window * channels));
            max = Mathf.Max(max, envelope[w]);
        }

        var times = new System.Collections.Generic.List<float>();
        bool loud = false;

        for (int w = 0; w < windows; w++)
        {
            if (!loud && envelope[w] >= max * 0.5f)
            {
                loud = true;
                times.Add(w * window / (float)clip.frequency);
            }
            else if (loud && envelope[w] < max * 0.3f)
            {
                loud = false;
            }
        }

        if (Application.isEditor)
            Debug.Log($"{name}: expirações detectadas em '{clip.name}' (s): {string.Join(", ", times.ConvertAll(t => t.ToString("0.00")))}", this);

        return times.ToArray();
    }

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable()
    {
        GameManager.StateChanged -= HandleGameState;
        Silence();
    }

    private void HandleGameState(GameState state)
    {
        // Só a derrota. Na vitória o fôlego segue com ele correndo para o clarão, que apaga o som do jogo inteiro; o
        // fim de verdade vem quando a PlayerEscapedSequence desliga o PlayerMovement (checado no Update).
        if (state == GameState.Lost)
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
            || (GameManager.Current != null && GameManager.Current.State == GameState.Lost))
        {
            BeginFadeOut();
        }

        UpdateExertion();

        float heavyGain = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(heavyThreshold, 1f, _exertion));
        float calmGain = 1f - heavyGain * calmDuckAtFull;

        if (_gameOver)
            _masterGain = Mathf.MoveTowards(_masterGain, 0f, Time.deltaTime / fadeOutOnGameOver);

        Apply(_calm, calmGain);
        Apply(_heavy, heavyGain);

        // As duas vozes avançam todo frame (senão a que volta a dominar dispara as marcas que
        // passaram enquanto estava por baixo), mas só a dominante solta vapor.
        bool calmExhaled = CrossedExhale(_calm);
        bool heavyExhaled = CrossedExhale(_heavy);

        if (vapor != null && !_gameOver && (heavyGain >= 0.5f ? heavyExhaled : calmExhaled))
            vapor.Exhale(heavyGain);

        // O OnDisable chama o Silence.
        if (_gameOver && _masterGain <= 0f)
            enabled = false;
    }

    private void UpdateExertion()
    {
        if (stamina != null && stamina.isActiveAndEnabled)
        {
            // Uma fonte de verdade para "cansado": o ofegante é o quanto da barra foi gasto, e
            // exausto é sempre o máximo — é o momento em que o jogador tenta correr e não consegue.
            float target = stamina.IsExhausted ? 1f : 1f - stamina.Normalized;
            _exertion = Mathf.MoveTowards(_exertion, target, Time.deltaTime / staminaFollowTime);
            return;
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
    }

    /// <summary>Alguma marca de expiração ficou entre o frame anterior e este, contando a volta do loop.</summary>
    private static bool CrossedExhale(Voice voice)
    {
        if (voice == null || voice.Exhales.Length == 0)
            return false;

        float previous = voice.LastTime;
        float now = voice.Source.time;
        voice.LastTime = now;

        foreach (float t in voice.Exhales)
        {
            bool crossed = now >= previous
                ? t > previous && t <= now
                : t > previous || t <= now;

            if (crossed)
                return true;
        }

        return false;
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
