using UnityEngine;

[RequireComponent(typeof(SwingDoor))]
public class SwingDoorAudio : MonoBehaviour
{
    [SerializeField] private SwingDoorSoundProfile profile;

    private SwingDoor door;
    private AudioSource creakSource;
    private float creakBaseVolume;
    private float creakGain;
    private float creakGainVelocity;

    private void Awake()
    {
        door = GetComponent<SwingDoor>();

        if (profile == null)
        {
            Debug.LogWarning($"{name}: {nameof(SwingDoorAudio)} sem perfil de som.", this);
            enabled = false;
            return;
        }

        CreateCreakSource();
    }

    private void OnEnable()
    {
        door.LeftClosed += HandleLeftClosed;
        door.LimitReached += HandleLimitReached;
    }

    private void OnDisable()
    {
        door.LeftClosed -= HandleLeftClosed;
        door.LimitReached -= HandleLimitReached;

        creakGain = 0f;
        if (creakSource != null)
            creakSource.Pause();
    }

    // Fonte própria em vez do pool: o volume muda todo frame com a velocidade.
    private void CreateCreakSource()
    {
        if (string.IsNullOrEmpty(profile.creakSoundId) || AudioProvider.IsMuted)
            return;

        AudioLibrary library = AudioProvider.Library;
        if (library == null)
            return;

        if (!library.TryGet(profile.creakSoundId, out AudioLibrary.SoundEntry entry) || entry.Clips.Length == 0)
        {
            Debug.LogWarning($"{name}: id '{profile.creakSoundId}' não está na AudioLibrary — porta sem rangido.", this);
            return;
        }

        creakSource = gameObject.AddComponent<AudioSource>();
        creakSource.playOnAwake = false;
        creakSource.loop = true;
        creakSource.spatialBlend = 1f;
        creakSource.rolloffMode = library.Rolloff;
        creakSource.minDistance = library.MinDistance;
        creakSource.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : library.MaxDistance;
        creakSource.outputAudioMixerGroup = library.MixerGroup;
        creakSource.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        creakSource.volume = 0f;
        creakSource.time = Random.Range(0f, creakSource.clip.length);
        creakBaseVolume = entry.Volume;
    }

    private void HandleLeftClosed()
    {
        if (!string.IsNullOrEmpty(profile.openSoundId))
            AudioProvider.PlayAt(profile.openSoundId, door.GrabPoint);
    }

    private void HandleLimitReached(bool closing, float speed)
    {
        string id;
        float threshold;

        if (!closing)
        {
            id = profile.openStopSoundId;
            threshold = profile.openStopMinSpeed;
        }
        else if (speed >= profile.slamSpeed)
        {
            id = profile.slamSoundId;
            threshold = profile.slamSpeed;
        }
        else
        {
            if (speed >= profile.latchMinSpeed && !string.IsNullOrEmpty(profile.latchSoundId))
                AudioProvider.PlayAt(profile.latchSoundId, door.GrabPoint);
            return;
        }

        if (speed < threshold || string.IsNullOrEmpty(id))
            return;

        float force = Mathf.InverseLerp(threshold, door.MaxAngularSpeed, speed);
        AudioProvider.PlayAt(id, door.GrabPoint, Mathf.Lerp(profile.impactVolumeRange.x, profile.impactVolumeRange.y, force));
    }

    // No Update: volume mexido a 50 Hz no FixedUpdate dá degrau audível.
    private void Update()
    {
        if (creakSource == null)
            return;

        float target = Mathf.InverseLerp(profile.creakMinSpeed, profile.creakFullSpeed, door.AngularSpeed);
        creakGain = Mathf.SmoothDamp(creakGain, target, ref creakGainVelocity, profile.creakResponse);

        if (creakGain < 0.01f)
        {
            // Pause, e não Stop: o próximo empurrão continua o rangido de onde parou.
            if (creakSource.isPlaying)
                creakSource.Pause();
            return;
        }

        if (!creakSource.isPlaying)
            creakSource.UnPause();
        if (!creakSource.isPlaying)
            creakSource.Play();

        creakSource.volume = creakBaseVolume * creakGain;
        creakSource.pitch = Mathf.Lerp(profile.creakPitchRange.x, profile.creakPitchRange.y, target);
    }
}
