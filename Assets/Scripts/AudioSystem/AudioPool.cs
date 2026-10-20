using UnityEngine;

/// <summary>
/// Host do pool de AudioSources 3D. Não é singleton nem vai na cena: o
/// <see cref="AudioProvider"/> instancia um no primeiro Play e o mantém entre cenas. Todo
/// o estado de "quem está tocando onde" mora aqui; a library só descreve os sons.
/// </summary>
public class AudioPool : MonoBehaviour
{
    private AudioLibrary _library;

    private AudioSource[] _sources;
    private Transform[] _followTargets;   // slot que persegue um objeto em movimento (passos, inimigo)
    private float[] _startTimes;          // pra roubar sempre a voz mais antiga
    private int[] _generations;           // incrementa a cada Play no slot; ver AudioHandle
    private int _cursor;
    private AudioSource _uiSource;

    [SerializeField] private bool _drawGizmos = true;

    public bool Muted { get; set; }

    public AudioLibrary Library => _library;

    public void Initialize(AudioLibrary library)
    {
        _library = library;

        int size = Mathf.Max(1, library.PoolSize);

        _sources = new AudioSource[size];
        _followTargets = new Transform[size];
        _startTimes = new float[size];
        _generations = new int[size];

        for (int i = 0; i < size; i++)
        {
            var voice = new GameObject("Voice_" + i.ToString("00"));
            voice.transform.SetParent(transform, false);

            AudioSource source = voice.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;      // isto — e só isto — é o que torna o som 3D
            source.dopplerLevel = 0f;      // sem isso, fonte em movimento desafina
            source.rolloffMode = library.Rolloff;
            source.minDistance = library.MinDistance;
            source.maxDistance = library.MaxDistance;
            source.outputAudioMixerGroup = library.MixerGroup;

            _sources[i] = source;
        }

        var ui = new GameObject("Voice_UI");
        ui.transform.SetParent(transform, false);

        _uiSource = ui.AddComponent<AudioSource>();
        _uiSource.playOnAwake = false;
        _uiSource.spatialBlend = 0f;
        // Os menus vivem no Pause, que pausa o AudioListener (PauseAudio): sem isto o clique não tocaria lá.
        _uiSource.ignoreListenerPause = true;
        _uiSource.outputAudioMixerGroup = library.MixerGroup;
    }

    /// <summary>
    /// Som de interface: 2D, fora do pool (não rouba voz do mundo) e audível com o jogo pausado. OneShot para
    /// cliques rápidos se sobreporem; o pitch é do source, então vale para os que ainda soam.
    /// </summary>
    public void PlayUI(string id, float volumeScale, float pitchScale)
    {
        if (Muted)
            return;

        if (!_library.TryGet(id, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning("[AudioPool] id desconhecido: " + id, this);
            return;
        }

        _uiSource.pitch = (1f + Random.Range(-entry.PitchJitter, entry.PitchJitter)) * pitchScale;
        _uiSource.PlayOneShot(entry.Clips[Random.Range(0, entry.Clips.Length)], entry.Volume * volumeScale);
    }

    /// <summary>
    /// Slots colados num objeto acompanham ele. LateUpdate porque o movimento
    /// (CharacterController/agente) já rodou no Update/FixedUpdate deste frame.
    /// </summary>
    private void LateUpdate()
    {
        for (int i = 0; i < _sources.Length; i++)
        {
            if (_followTargets[i] == null)
                continue;

            if (!_sources[i].isPlaying)
            {
                _followTargets[i] = null;
                continue;
            }

            _sources[i].transform.position = _followTargets[i].position;
        }
    }

    /// <param name="pitchScale">Multiplica o pitch da entrada.</param>
    public AudioHandle Play(string id, Vector3 position, Transform follow, bool loop, float volumeScale, float pitchScale = 1f)
    {
        if (Muted)
            return AudioHandle.None;

        if (!_library.TryGet(id, out AudioLibrary.SoundEntry entry))
        {
            Debug.LogWarning("[AudioPool] id desconhecido: " + id, this);
            return AudioHandle.None;
        }

        int slot = TakeSlot();
        AudioSource source = _sources[slot];

        source.Stop();
        source.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        source.volume = entry.Volume * volumeScale;
        source.pitch = (1f + Random.Range(-entry.PitchJitter, entry.PitchJitter)) * pitchScale;
        source.minDistance = _library.MinDistance;
        source.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : _library.MaxDistance;
        source.loop = loop;
        source.transform.position = position;

        _followTargets[slot] = follow;
        _startTimes[slot] = Time.unscaledTime;
        _generations[slot]++;

        source.Play();
        return new AudioHandle(slot, _generations[slot]);
    }

    public bool IsPlaying(AudioHandle handle)
    {
        return Owns(handle) && _sources[handle.Slot].isPlaying;
    }

    public void Stop(AudioHandle handle)
    {
        if (!Owns(handle))
            return;

        StopSlot(handle.Slot);
    }

    public void StopAll()
    {
        for (int i = 0; i < _sources.Length; i++)
            StopSlot(i);
    }

    /// <summary>Handle ainda aponta para o som que o criou, e não para quem roubou o slot depois.</summary>
    private bool Owns(AudioHandle handle)
    {
        return handle.IsValid
            && handle.Slot >= 0
            && handle.Slot < _sources.Length
            && _generations[handle.Slot] == handle.Generation;
    }

    private void StopSlot(int slot)
    {
        _sources[slot].Stop();
        _sources[slot].loop = false;
        _followTargets[slot] = null;
    }

    /// <summary>
    /// Procura uma voz livre a partir do cursor. Se todas estiverem ocupadas, rouba a mais antiga:
    /// perder o som velho incomoda menos do que engolir o novo.
    /// </summary>
    private int TakeSlot()
    {
        for (int i = 0; i < _sources.Length; i++)
        {
            int index = (_cursor + i) % _sources.Length;

            if (!_sources[index].isPlaying)
            {
                _cursor = (index + 1) % _sources.Length;
                return index;
            }
        }

        int oldest = 0;
        for (int i = 1; i < _sources.Length; i++)
        {
            if (_startTimes[i] < _startTimes[oldest])
                oldest = i;
        }

        _cursor = (oldest + 1) % _sources.Length;
        return oldest;
    }

    private void OnDrawGizmosSelected()
    {
        if (!_drawGizmos || _sources == null)
            return;

        foreach (AudioSource source in _sources)
        {
            if (source == null || !source.isPlaying)
                continue;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(source.transform.position, source.minDistance);
            Gizmos.color = new Color(1f, 1f, 0f, 0.25f);
            Gizmos.DrawWireSphere(source.transform.position, source.maxDistance);
        }
    }
}
