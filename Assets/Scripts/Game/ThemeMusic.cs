using Assets.Scripts.Seeker;
using UnityEngine;

/// <summary>
/// Trilha de fundo em loop que sai de cena quando a tensão sobe. Dois gatilhos, ambos já
/// existentes no projeto — este componente só escuta, ninguém precisa conhecê-lo:
///
///  - perseguição (<see cref="SeekerChaseState.IsChasing"/> de qualquer seeker): a música cai
///    rápido e fica pausada, voltando de onde parou depois que a perseguição acaba;
///  - fim de jogo (<see cref="GameManager.StateChanged"/> com Won/Lost): cai e não volta. As
///    sequências de captura/fuga ficam com o palco sozinhas.
///
/// AudioSource próprio em vez do <see cref="AudioPool"/>: o pool rouba a voz mais antiga quando
/// enche, e um loop iniciado no primeiro frame é exatamente a mais antiga. Além disso as vozes
/// do pool são 3D, e trilha é 2D. O clipe ainda vem da <see cref="AudioLibrary"/>, pelo id.
/// </summary>
public class ThemeMusic : MonoBehaviour
{
    [SerializeField] private string _themeId = "theme";

    [Tooltip("Vazio = procura todos os SeekerChaseState da cena no Start.")]
    [SerializeField] private SeekerChaseState[] _seekers;

    [Header("-----Fades-----")]
    [SerializeField, Min(0.01f)] private float _fadeInOnStart = 2f;

    // Cortar em ~meio segundo: a estática de perseguição do seeker entra em 0.3s e a música
    // não pode disputar com ela.
    [SerializeField, Min(0.01f)] private float _fadeOutOnChase = 0.4f;

    // Voltar devagar, e só depois de um respiro: a música retornando é o sinal de "escapou",
    // e se ela voltasse colada ao fim do hold do SeekerChaseState soaria como bug.
    [SerializeField, Min(0f)] private float _resumeDelay = 3f;
    [SerializeField, Min(0.01f)] private float _fadeInOnResume = 4f;

    [SerializeField, Min(0.01f)] private float _fadeOutOnGameOver = 0.25f;

    private AudioSource _source;
    private float _baseVolume;
    private float _gain;
    private float _calmSince = float.NegativeInfinity;
    private bool _gameOver;
    private bool _startedOnce;

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

        if (!library.TryGet(_themeId, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning($"{name}: id '{_themeId}' não está na AudioLibrary — sem trilha.", this);
            enabled = false;
            return;
        }

        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 0f;
        // Sem PitchJitter da entrada: variar o pitch de trilha desafina a música inteira.
        _source.pitch = 1f;
        _source.clip = entry.Clips[0];
        _source.outputAudioMixerGroup = library.MixerGroup;
        _source.volume = 0f;
        _baseVolume = entry.Volume;
    }

    private void Start()
    {
        if (_seekers == null || _seekers.Length == 0)
            _seekers = FindObjectsByType<SeekerChaseState>(FindObjectsSortMode.None);
    }

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable() => GameManager.StateChanged -= HandleGameState;

    private void HandleGameState(GameState state)
    {
        if (state == GameState.Won || state == GameState.Lost)
            _gameOver = true;
    }

    private void Update()
    {
        bool chasing = AnyChasing();
        if (chasing)
            _calmSince = float.PositiveInfinity;
        else if (float.IsPositiveInfinity(_calmSince))
            _calmSince = Time.time;

        // _calmSince começa em -∞: o primeiro fade-in não espera o respiro, só a volta de uma
        // perseguição espera.
        bool audible = !_gameOver && !chasing && Time.time - _calmSince >= _resumeDelay;

        float fade = audible
            ? (_startedOnce ? _fadeInOnResume : _fadeInOnStart)
            : (_gameOver ? _fadeOutOnGameOver : _fadeOutOnChase);

        _gain = Mathf.MoveTowards(_gain, audible ? 1f : 0f, Time.deltaTime / fade);
        _source.volume = _baseVolume * _gain;

        // Pause e não Stop: a volta continua o trecho em que parou, em vez de reabrir a intro.
        if (audible && !_source.isPlaying)
        {
            if (_startedOnce)
                _source.UnPause();
            else
                _source.Play();

            _startedOnce = true;
        }
        else if (!audible && _gain <= 0f && _source.isPlaying)
        {
            if (_gameOver)
            {
                _source.Stop();
                enabled = false;
            }
            else
            {
                _source.Pause();
            }
        }
    }

    private bool AnyChasing()
    {
        foreach (SeekerChaseState seeker in _seekers)
        {
            if (seeker != null && seeker.isActiveAndEnabled && seeker.IsChasing)
                return true;
        }

        return false;
    }
}
