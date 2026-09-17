using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Tabela de sons e configuração do pool, como asset. É o equivalente do .inputactions:
/// uma fonte só, compartilhada por todas as cenas, em vez de um objeto por cena com a
/// tabela copiada. O <see cref="AudioProvider"/> carrega de Resources/AudioLibrary no
/// primeiro Play.
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Audio/Library")]
public class AudioLibrary : ScriptableObject
{
    /// <summary>Caminho dentro de Resources/. O asset tem que se chamar assim.</summary>
    public const string ResourcePath = "AudioLibrary";

    [System.Serializable]
    public class SoundEntry
    {
        public string Id;

        // Mais de um clipe = sorteio a cada disparo. Um clipe só também funciona.
        public AudioClip[] Clips;

        [Range(0f, 1f)] public float Volume = 1f;

        // Variação aleatória de pitch, em fração (0.1 = ±10%).
        [Range(0f, 0.5f)] public float PitchJitter = 0.1f;

        // 0 = usa o alcance padrão da library.
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

    private Dictionary<string, SoundEntry> _byId;

    public int PoolSize => _poolSize;
    public float DefaultHeight => _defaultHeight;
    public float MinDistance => _minDistance;
    public float MaxDistance => _maxDistance;
    public AudioRolloffMode Rolloff => _rolloff;
    public AudioMixerGroup MixerGroup => _mixerGroup;

    public bool TryGet(string id, out SoundEntry entry)
    {
        if (_byId == null)
            BuildIndex();

        return _byId.TryGetValue(id, out entry);
    }

    private void BuildIndex()
    {
        _byId = new Dictionary<string, SoundEntry>();

        if (_sounds == null)
            return;

        foreach (SoundEntry entry in _sounds)
        {
            if (string.IsNullOrEmpty(entry.Id) || entry.Clips == null || entry.Clips.Length == 0)
                continue;

            _byId[entry.Id] = entry;
        }
    }

    private void OnValidate()
    {
        // Editar a tabela no Inspector durante o Play invalida o índice; ele volta no próximo TryGet.
        _byId = null;
    }
}
