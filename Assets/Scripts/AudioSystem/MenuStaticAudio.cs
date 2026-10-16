using UnityEngine;

/// <summary>
/// Chiado de estática em loop, baixo, enquanto a tela de menu está ativa. Vai no objeto do menu
/// (o mesmo da estática visual): o som fica preso à visibilidade da tela, então trocar entre
/// pause e configurações não corta o loop e o MainMenu, sem este componente, fica de fora.
/// </summary>
public class MenuStaticAudio : MonoBehaviour
{
    [SerializeField] private string soundId = "pause_static";

    private AudioSource _source;

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

        if (!library.TryGet(soundId, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning($"{name}: id '{soundId}' não está na AudioLibrary — menu sem chiado.", this);
            enabled = false;
            return;
        }

        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        // 2D: é o chiado da tela, não um som no mundo.
        _source.spatialBlend = 0f;
        _source.outputAudioMixerGroup = library.MixerGroup;
        _source.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        _source.volume = entry.Volume;
        // O PauseAudio pausa o AudioListener inteiro; sem isso o chiado calaria justamente no pause.
        _source.ignoreListenerPause = true;
    }

    // Começa num ponto aleatório: cada abertura do menu não abre sempre no mesmo trecho.
    private void OnEnable()
    {
        if (_source == null)
            return;

        _source.time = Random.Range(0f, _source.clip.length);
        _source.Play();
    }

    private void OnDisable()
    {
        if (_source != null)
            _source.Stop();
    }
}
