using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Cena de créditos: segura a máquina de escrever até o <see cref="LoadingWarmup"/> da cena terminar (senão as
/// primeiras letras sairiam atrás da tela preta) e volta ao menu com Esc/Start ou sozinha depois do fim.
/// </summary>
public class CreditsScreen : MonoBehaviour
{
    [SerializeField] private Typewriter typewriter;

    [Tooltip("Opcional. Vazio = a máquina começa na hora.")]
    [SerializeField] private LoadingWarmup warmup;

    [Tooltip("Arraste a cena do menu. Precisa estar ligada no Build Settings.")]
    [SerializeField] private SceneReference menuScene;

    [Tooltip("Espera extra antes de voltar ao menu, contada depois que a máquina termina — e ela só termina " +
             "depois do tempo de leitura da última página (ou de alguém apertar \"voltar ao menu\"). 0 = na hora.")]
    [SerializeField, Min(0f)] private float exitDelayAfterEnd;

    [SerializeField, Min(0.01f)] private float fadeTime = 0.8f;
    [SerializeField] private Color fadeColor = Color.black;

    [Header("Música")]
    [Tooltip("Id da AudioLibrary (o volume vem da entrada). Vazio = sem música.")]
    [SerializeField] private string musicId = "credits_music";

    [SerializeField, Min(0.01f)] private float musicFadeIn = 1.5f;

    [SerializeField, Min(0.01f)] private float musicFadeOut = 1.5f;

    private float _endTimer;
    private bool _leaving;
    private AudioSource _music;
    private float _musicVolume;

    private void Awake()
    {
        if (typewriter != null)
            typewriter.enabled = false;

        SetupMusic();
    }

    // AudioSource próprio pelo mesmo motivo do ThemeMusic: trilha é 2D e não pode ter a voz roubada pelo pool.
    private void SetupMusic()
    {
        AudioLibrary library = AudioProvider.Library;
        if (string.IsNullOrEmpty(musicId) || AudioProvider.IsMuted || library == null)
            return;

        if (!library.TryGet(musicId, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning($"{name}: id '{musicId}' não está na AudioLibrary — créditos sem música.", this);
            return;
        }

        _music = gameObject.AddComponent<AudioSource>();
        _music.playOnAwake = false;
        _music.loop = true;
        _music.spatialBlend = 0f;
        _music.clip = entry.Clips[0];
        _music.outputAudioMixerGroup = library.MixerGroup;
        _music.volume = 0f;
        _musicVolume = entry.Volume;
        _music.Play();
        StartCoroutine(FadeMusicIn());
    }

    private IEnumerator FadeMusicIn()
    {
        for (float t = 0f; t < musicFadeIn && !_leaving; t += Time.unscaledDeltaTime)
        {
            _music.volume = _musicVolume * (t / musicFadeIn);
            yield return null;
        }

        if (!_leaving)
            _music.volume = _musicVolume;
    }

    private IEnumerator Start()
    {
        // Desligado = a cena já foi aquecida nesta sessão. O Progress chega a 1 antes do fade de saída da tela,
        // e a máquina ainda tem o autoStartDelay: a primeira letra cai com a cena já visível.
        while (warmup != null && warmup.enabled && warmup.Progress < 1f)
            yield return null;

        if (typewriter != null)
            typewriter.enabled = true;
    }

    private void Update()
    {
        if (_leaving)
            return;

        bool back = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                    (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        if (typewriter != null && typewriter.IsFinished)
            _endTimer += Time.deltaTime;

        if (back || _endTimer >= exitDelayAfterEnd)
            StartCoroutine(Leave());
    }

    private IEnumerator Leave()
    {
        _leaving = true;

        if (typewriter != null)
            typewriter.enabled = false;

        AsyncOperation load = menuScene.LoadAsync();
        if (load == null)
            yield break;

        load.allowSceneActivation = false;

        Image fade = CreateFadeOverlay();

        // A música sai do volume em que estiver: pular durante o fade-in não dá um pulo para cima.
        float musicFrom = _music != null ? _music.volume : 0f;

        // A tela fica preta no fadeTime e espera nela até a música terminar de sair: trocar de cena antes cortaria o som.
        float total = Mathf.Max(fadeTime, _music != null ? musicFadeOut : 0f);

        for (float t = 0f; t < total; t += Time.unscaledDeltaTime)
        {
            SetAlpha(fade, Mathf.Clamp01(t / fadeTime));
            if (_music != null)
                _music.volume = musicFrom * (1f - Mathf.Clamp01(t / musicFadeOut));
            yield return null;
        }

        SetAlpha(fade, 1f);
        if (_music != null)
            _music.Stop();
        load.allowSceneActivation = true;
    }

    private Image CreateFadeOverlay()
    {
        var go = new GameObject("CreditsFade", typeof(Canvas), typeof(Image));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        image.rectTransform.anchorMin = Vector2.zero;
        image.rectTransform.anchorMax = Vector2.one;
        image.rectTransform.sizeDelta = Vector2.zero;
        SetAlpha(image, 0f);
        return image;
    }

    private void SetAlpha(Image image, float alpha)
    {
        Color c = fadeColor;
        c.a = alpha;
        image.color = c;
    }
}
