using Assets.Scripts.Seeker;
using UnityEngine;

/// <summary>
/// Trilha da perseguição: um jumpscare no instante em que o seeker vê o jogador e um tema em
/// loop enquanto a perseguição durar. É o par do <see cref="ThemeMusic"/>, que sai de cena no
/// mesmo gatilho (<see cref="SeekerChaseState.IsChasing"/>), então os dois não disputam.
///
/// O jumpscare dispara na borda de subida do IsChasing, não a cada frame visto. O próprio
/// SeekerChaseState já segura o IsChasing por alguns segundos depois de perder o jogador de
/// vista, mas quebrar a linha de visão várias vezes ainda geraria bordas seguidas — daí o
/// cooldown, contado a partir do último jumpscare tocado.
///
/// AudioSources próprios, 2D, pelo mesmo motivo do ThemeMusic: o pool rouba a voz mais antiga
/// e não deixa mexer no volume depois do Play. Os clipes vêm da <see cref="AudioLibrary"/>.
/// </summary>
public class ChaseMusic : MonoBehaviour
{
    [SerializeField] private string _jumpscareId = "jumpscare";
    [SerializeField] private string _chaseThemeId = "chase_theme";

    [Tooltip("Vazio = procura todos os SeekerChaseState da cena no Start.")]
    [SerializeField] private SeekerChaseState[] _seekers;

    [Header("-----Jumpscare-----")]
    [Tooltip("Segundos mínimos entre dois jumpscares, contados do último que tocou.")]
    [SerializeField, Min(0f)] private float _jumpscareCooldown = 20f;

    [Header("-----Tema-----")]
    // Entra quase junto com o jumpscare: é a resposta a ele.
    [SerializeField, Min(0.01f)] private float _fadeIn = 0.5f;

    // Sai devagar: o IsChasing cai só depois do hold do SeekerChaseState, e cortar seco ali
    // soaria como bug, não como "escapou".
    [SerializeField, Min(0.01f)] private float _fadeOut = 3f;

    [SerializeField, Min(0.01f)] private float _fadeOutOnGameOver = 0.25f;

    /// <summary>
    /// Disparado junto com o som do susto, já filtrado pelo cooldown. Quem reage ao susto
    /// (efeito de tela) escuta isto em vez de refazer a detecção, para som e imagem baterem juntos.
    /// </summary>
    public event System.Action JumpscareTriggered;

    private AudioSource _jumpscareSource;
    private AudioSource _themeSource;
    private AudioLibrary.SoundEntry _jumpscare;
    private float _themeBaseVolume;
    private float _gain;
    private float _lastJumpscareTime = float.NegativeInfinity;
    private bool _wasChasing;
    private bool _gameOver;

    private void Awake()
    {
        if (AudioProvider.IsMuted)
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

        if (library.TryGet(_jumpscareId, out _jumpscare))
        {
            // PlayOneShot multiplica o volume pedido pelo volume da fonte: a fonte fica em 1 e
            // o volume da entrada vai no próprio PlayOneShot. Com a fonte em 0 o susto sai mudo.
            _jumpscareSource = CreateSource(library, loop: false);
            _jumpscareSource.volume = 1f;
        }
        else
            Debug.LogWarning($"{name}: id '{_jumpscareId}' não está na AudioLibrary — sem jumpscare.", this);

        if (library.TryGet(_chaseThemeId, out AudioLibrary.SoundEntry theme))
        {
            _themeSource = CreateSource(library, loop: true);
            _themeSource.clip = theme.Clips[0];
            _themeBaseVolume = theme.Volume;
        }
        else
        {
            Debug.LogWarning($"{name}: id '{_chaseThemeId}' não está na AudioLibrary — sem tema de perseguição.", this);
        }

        if (_jumpscareSource == null && _themeSource == null)
            enabled = false;
    }

    private AudioSource CreateSource(AudioLibrary library, bool loop)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        // 2D: susto e trilha são do jogador, não um som vindo de um ponto do mundo.
        source.spatialBlend = 0f;
        source.outputAudioMixerGroup = library.MixerGroup;
        source.volume = 0f;
        return source;
    }

    private void Start()
    {
        if (_seekers == null || _seekers.Length == 0)
            _seekers = FindObjectsByType<SeekerChaseState>(FindObjectsSortMode.None);
    }

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    // Os AudioSources são componentes à parte: desligar o script não cala nada sozinho.
    private void OnDisable()
    {
        GameManager.StateChanged -= HandleGameState;
        Silence();
    }

    private void HandleGameState(GameState state)
    {
        if (state == GameState.Won || state == GameState.Lost)
            _gameOver = true;
    }

    private void Update()
    {
        if (GameManager.Current != null && GameManager.Current.IsOver)
            _gameOver = true;

        bool chasing = !_gameOver && AnyChasing();

        if (chasing && !_wasChasing)
            TryJumpscare();

        _wasChasing = chasing;

        UpdateTheme(chasing);

        if (_gameOver && _gain <= 0f)
            enabled = false;   // o OnDisable chama o Silence
    }

    private void TryJumpscare()
    {
        if (_jumpscareSource == null || Time.time - _lastJumpscareTime < _jumpscareCooldown)
            return;

        _lastJumpscareTime = Time.time;

        _jumpscareSource.pitch = 1f + Random.Range(-_jumpscare.PitchJitter, _jumpscare.PitchJitter);
        _jumpscareSource.PlayOneShot(_jumpscare.Clips[Random.Range(0, _jumpscare.Clips.Length)], _jumpscare.Volume);

        JumpscareTriggered?.Invoke();
    }

    private void UpdateTheme(bool chasing)
    {
        if (_themeSource == null)
        {
            _gain = 0f;
            return;
        }

        float fade = chasing ? _fadeIn : (_gameOver ? _fadeOutOnGameOver : _fadeOut);
        _gain = Mathf.MoveTowards(_gain, chasing ? 1f : 0f, Time.deltaTime / fade);
        _themeSource.volume = _themeBaseVolume * _gain;

        // Stop e não Pause: cada perseguição nova abre o tema do começo. Se o jogador for visto
        // de novo no meio do fade-out, o tema só volta a subir de onde está.
        if (chasing && !_themeSource.isPlaying)
            _themeSource.Play();
        else if (!chasing && _gain <= 0f && _themeSource.isPlaying)
            _themeSource.Stop();
    }

    private void Silence()
    {
        foreach (AudioSource source in new[] { _jumpscareSource, _themeSource })
        {
            if (source == null)
                continue;

            source.Stop();
            source.enabled = false;
        }
    }

    private bool AnyChasing()
    {
        if (_seekers == null)
            return false;

        foreach (SeekerChaseState seeker in _seekers)
        {
            if (seeker != null && seeker.isActiveAndEnabled && seeker.IsChasing)
                return true;
        }

        return false;
    }
}
