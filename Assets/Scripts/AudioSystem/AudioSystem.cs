using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Pool fixo de AudioSources 3D. Um único objeto na cena; a API é estática, então qualquer
/// script dispara som sem precisar de referência:
///
///     AudioSystem.Play("footstep", transform.position.x, transform.position.z);
///     AudioSystem.PlayAt("door", posicao);
///     var loop = AudioSystem.PlayLoop("radio", radioTransform);  // guarde para chamar Stop(loop)
///
/// Os clipes são cadastrados no Inspector por id (string), com variação de pitch embutida —
/// assim o mesmo passo repetido 200x não vira metralhadora.
/// </summary>
[DefaultExecutionOrder(-100)]
public class AudioSystem : MonoBehaviour
{
    [System.Serializable]
    public class SoundEntry
    {
        public string Id;

        // Mais de um clipe = sorteio a cada disparo. Um clipe só também funciona.
        public AudioClip[] Clips;

        [Range(0f, 1f)] public float Volume = 1f;

        // Variação aleatória de pitch, em fração (0.1 = ±10%).
        [Range(0f, 0.5f)] public float PitchJitter = 0.1f;

        // 0 = usa o alcance padrão do sistema.
        public float MaxDistance = 0f;
    }

    [Header("-----Pool-----")]
    [SerializeField] private int _poolSize = 16;

    // Altura em que o som é colocado quando só se informa X/Z. Vale a altura do ouvido, não a do chão.
    [SerializeField] private float _defaultHeight = 1f;

    [Header("-----3D-----")]
    [SerializeField] private float _minDistance = 1f;
    [SerializeField] private float _maxDistance = 25f;
    [SerializeField] private AudioRolloffMode _rolloff = AudioRolloffMode.Linear;
    [SerializeField] private AudioMixerGroup _mixerGroup;

    [Header("-----Sounds-----")]
    [SerializeField] private SoundEntry[] _sounds;

    [Header("-----Debug-----")]
    // Treino headless com N arenas clonadas não tem por que gastar voz nenhuma.
    [SerializeField] private bool _muted;
    [SerializeField] private bool _drawGizmos = true;

    private static AudioSystem _instance;

    private AudioSource[] _sources;
    private Transform[] _followTargets;   // slot que persegue um objeto em movimento (passos, inimigo)
    private float[] _startTimes;          // pra roubar sempre a voz mais antiga
    private int _cursor;

    private readonly Dictionary<string, SoundEntry> _byId = new Dictionary<string, SoundEntry>();

    private void Awake()
    {
        // Checagem explícita, e não ??=: o operador de null-coalescing ignora o "fake null" do Unity.
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;

        foreach (SoundEntry entry in _sounds)
        {
            if (string.IsNullOrEmpty(entry.Id) || entry.Clips == null || entry.Clips.Length == 0)
                continue;

            _byId[entry.Id] = entry;
        }

        BuildPool();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    private void BuildPool()
    {
        _sources = new AudioSource[_poolSize];
        _followTargets = new Transform[_poolSize];
        _startTimes = new float[_poolSize];

        for (int i = 0; i < _poolSize; i++)
        {
            var voice = new GameObject("Voice_" + i.ToString("00"));
            voice.transform.SetParent(transform, false);

            AudioSource source = voice.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;      // isto — e só isto — é o que torna o som 3D
            source.dopplerLevel = 0f;      // sem isso, fonte em movimento desafina
            source.rolloffMode = _rolloff;
            source.minDistance = _minDistance;
            source.maxDistance = _maxDistance;
            source.outputAudioMixerGroup = _mixerGroup;

            _sources[i] = source;
        }
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

    // ------------------------------------------------------------------ API estática

    public static void Play(string id, float x, float z, float volumeScale = 1f)
    {
        if (_instance == null)
            return;

        _instance.PlayInternal(id, new Vector3(x, _instance._defaultHeight, z), null, false, volumeScale);
    }

    public static void PlayAt(string id, Vector3 position, float volumeScale = 1f)
    {
        if (_instance == null)
            return;

        _instance.PlayInternal(id, position, null, false, volumeScale);
    }

    /// <summary>Som que segue um objeto enquanto toca (passos, motor, alguém arrastando caixa).</summary>
    public static void PlayFollowing(string id, Transform target, float volumeScale = 1f)
    {
        if (_instance == null || target == null)
            return;

        _instance.PlayInternal(id, target.position, target, false, volumeScale);
    }

    /// <summary>
    /// Loop ocupa o slot até alguém chamar Stop — vale guardar o retorno. Devolve null se o id
    /// não existe ou o sistema está mudo.
    /// </summary>
    public static AudioSource PlayLoop(string id, Transform target, float volumeScale = 1f)
    {
        if (_instance == null || target == null)
            return null;

        return _instance.PlayInternal(id, target.position, target, true, volumeScale);
    }

    public static void Stop(AudioSource handle)
    {
        if (handle == null)
            return;

        handle.Stop();
        handle.loop = false;
    }

    public static void StopAll()
    {
        if (_instance == null)
            return;

        for (int i = 0; i < _instance._sources.Length; i++)
        {
            _instance._sources[i].Stop();
            _instance._sources[i].loop = false;
            _instance._followTargets[i] = null;
        }
    }

    public static void SetMuted(bool muted)
    {
        if (_instance == null)
            return;

        _instance._muted = muted;

        if (muted)
            StopAll();
    }

    // ------------------------------------------------------------------ interno

    private AudioSource PlayInternal(string id, Vector3 position, Transform follow, bool loop, float volumeScale)
    {
        if (_muted)
            return null;

        if (!_byId.TryGetValue(id, out SoundEntry entry))
        {
            Debug.LogWarning("[AudioSystem] id desconhecido: " + id, this);
            return null;
        }

        int slot = TakeSlot();
        AudioSource source = _sources[slot];

        source.Stop();
        source.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        source.volume = entry.Volume * volumeScale;
        source.pitch = 1f + Random.Range(-entry.PitchJitter, entry.PitchJitter);
        source.minDistance = _minDistance;
        source.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : _maxDistance;
        source.loop = loop;
        source.transform.position = position;

        _followTargets[slot] = follow;
        _startTimes[slot] = Time.unscaledTime;

        source.Play();
        return source;
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
