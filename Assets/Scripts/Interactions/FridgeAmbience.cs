using UnityEngine;

public class FridgeAmbience : MonoBehaviour
{
    [Tooltip("Vazio: procura nos filhos.")]
    [SerializeField] private SwingDoor door;

    [Header("Hum")]
    [SerializeField] private string humSoundId = "fridgeHum";

    [Tooltip("Multiplica o volume da AudioLibrary. x = fechada, y = toda aberta.")]
    [SerializeField] private Vector2 humVolume = new Vector2(0.35f, 1f);

    [SerializeField] private float humResponse = 0.15f;

    [Header("Luz interna")]
    [Tooltip("Vazio: procura nos filhos (inclusive desativados).")]
    [SerializeField] private Light interiorLight;

    [Tooltip("Vazio: procura na luz interna.")]
    [SerializeField] private LightFlicker flicker;

    private AudioSource humSource;
    private float humBaseVolume;
    private float humGain;
    private float humGainVelocity;
    private bool lightOn;

    private void Awake()
    {
        if (door == null)
            door = GetComponentInChildren<SwingDoor>(true);

        if (door == null)
        {
            Debug.LogWarning($"{name}: {nameof(FridgeAmbience)} sem {nameof(SwingDoor)}.", this);
            enabled = false;
            return;
        }

        if (interiorLight == null)
            interiorLight = GetComponentInChildren<Light>(true);
        if (flicker == null && interiorLight != null)
            flicker = interiorLight.GetComponent<LightFlicker>();

        CreateHumSource();
        humGain = Mathf.Lerp(humVolume.x, humVolume.y, door.OpenFraction);
        // Força a primeira aplicação: a luz pode estar ligada na cena com a porta fechada.
        lightOn = door.IsClosed;
        SetLight(!door.IsClosed);
    }

    private void OnEnable()
    {
        if (humSource != null)
            humSource.Play();
    }

    private void OnDisable()
    {
        if (humSource != null)
            humSource.Stop();
    }

    // Fonte própria em vez do PlayLoop do pool: o volume acompanha a porta todo frame.
    private void CreateHumSource()
    {
        if (string.IsNullOrEmpty(humSoundId) || AudioProvider.IsMuted)
            return;

        AudioLibrary library = AudioProvider.Library;
        if (library == null)
            return;

        if (!library.TryGet(humSoundId, out AudioLibrary.SoundEntry entry) || entry.Clips.Length == 0)
        {
            Debug.LogWarning($"{name}: id '{humSoundId}' não está na AudioLibrary — geladeira sem hum.", this);
            return;
        }

        humSource = gameObject.AddComponent<AudioSource>();
        humSource.playOnAwake = false;
        humSource.loop = true;
        humSource.spatialBlend = 1f;
        humSource.rolloffMode = library.Rolloff;
        humSource.minDistance = library.MinDistance;
        humSource.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : library.MaxDistance;
        humSource.outputAudioMixerGroup = library.MixerGroup;
        humSource.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        // Começo aleatório: duas geladeiras no mesmo mapa não zumbem em fase.
        humSource.time = Random.Range(0f, humSource.clip.length);
        humBaseVolume = entry.Volume;
    }

    private void Update()
    {
        SetLight(!door.IsClosed);

        if (humSource == null)
            return;

        float target = Mathf.Lerp(humVolume.x, humVolume.y, door.OpenFraction);
        humGain = Mathf.SmoothDamp(humGain, target, ref humGainVelocity, humResponse);
        humSource.volume = humBaseVolume * humGain;
    }

    private void SetLight(bool on)
    {
        if (on == lightOn)
            return;

        lightOn = on;

        if (interiorLight != null)
            interiorLight.enabled = on;
        if (flicker != null)
            flicker.enabled = on;
    }
}
