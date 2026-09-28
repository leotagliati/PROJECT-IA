using Assets.Scripts.Seeker;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Efeito de tela quando o seeker vê o jogador, feito com o pós-processamento do URP em vez de
/// uma imagem por cima da tela: grão, aberração cromática, distorção de lente e vinheta
/// deformam a própria imagem. Duas camadas, cada uma num Volume global com peso próprio:
///
///  - susto: pico forte que decai rápido, disparado pelo <see cref="ChaseMusic.JumpscareTriggered"/>
///    — mesmo frame e mesmo cooldown do som;
///  - perseguição: nível baixo que segue o <see cref="SeekerChaseState.Blend"/>.
///
/// A câmera do jogador vem com pós-processamento DESLIGADO, e a cena tem um Volume global
/// (SampleSceneProfile: bloom, vinheta, tonemapping) que hoje não aparece por isso. Ligar o
/// pós-processamento sozinho faria esse perfil entrar e mudar o visual do jogo inteiro; por isso
/// a câmera passa a enxergar só a layer SeenFX, onde ficam os Volumes deste componente. Os
/// valores originais da câmera voltam no OnDestroy.
/// </summary>
public class SeenPostFX : MonoBehaviour
{
    [Tooltip("Vazio = Camera.main (a câmera do PlayerDummy é a MainCamera).")]
    [SerializeField] private Camera _camera;

    [Tooltip("Vazio = procura na cena. Sem ChaseMusic, fica só a camada de perseguição.")]
    [SerializeField] private ChaseMusic _chaseMusic;

    [Tooltip("Vazio = procura todos os SeekerChaseState da cena no Start.")]
    [SerializeField] private SeekerChaseState[] _seekers;

    [Tooltip("Layer exclusiva dos Volumes deste efeito (ProjectSettings → Tags and Layers).")]
    [SerializeField] private string _volumeLayer = "SeenFX";

    [Header("-----Susto-----")]
    [SerializeField, Min(0.01f)] private float _burstDuration = 0.9f;

    [Tooltip("Variação aleatória do peso por frame durante o susto (fração). Dá o tremor de sinal ruim.")]
    [SerializeField, Range(0f, 1f)] private float _burstFlicker = 0.35f;

    [SerializeField, Range(0f, 1f)] private float _burstGrain = 1f;
    [SerializeField, Range(0f, 1f)] private float _burstChromatic = 1f;
    [SerializeField, Range(-1f, 1f)] private float _burstDistortion = -0.4f;
    [SerializeField, Range(0f, 1f)] private float _burstVignette = 0.5f;

    [Header("-----Perseguição-----")]
    [SerializeField, Range(0f, 1f)] private float _chaseGrain = 0.55f;
    [SerializeField, Range(0f, 1f)] private float _chaseChromatic = 0.3f;
    [SerializeField, Range(0f, 1f)] private float _chaseVignette = 0.35f;

    [SerializeField, Min(0.01f)] private float _fadeOutOnGameOver = 0.25f;

    private Volume _burstVolume;
    private Volume _chaseVolume;
    private readonly System.Collections.Generic.List<VolumeProfile> _profiles = new();

    private UniversalAdditionalCameraData _cameraData;
    private bool _originalPostProcessing;
    private LayerMask _originalVolumeMask;

    private float _burstStart = float.NegativeInfinity;
    private float _masterGain = 1f;
    private bool _gameOver;

    private void Awake()
    {
        int layer = LayerMask.NameToLayer(_volumeLayer);
        if (layer < 0)
        {
            Debug.LogWarning($"{name}: layer '{_volumeLayer}' não existe — efeito de tela desligado.", this);
            enabled = false;
            return;
        }

        if (_camera == null)
            _camera = Camera.main;

        if (_camera == null)
        {
            Debug.LogWarning($"{name}: nenhuma câmera (nem MainCamera) — efeito de tela desligado.", this);
            enabled = false;
            return;
        }

        _cameraData = _camera.GetUniversalAdditionalCameraData();
        _originalPostProcessing = _cameraData.renderPostProcessing;
        _originalVolumeMask = _cameraData.volumeLayerMask;

        _cameraData.renderPostProcessing = true;
        _cameraData.volumeLayerMask = 1 << layer;

        // Os dois perfis mexem nos mesmos overrides. O URP mistura por prioridade crescente,
        // cada Volume puxando o valor anterior em direção ao seu pelo próprio peso: o susto
        // tem que vir depois, senão a perseguição com peso 1 apagaria o pico por cima dela.
        _chaseVolume = CreateVolume("SeenFX_Chase", layer, 100f, BuildChaseProfile());
        _burstVolume = CreateVolume("SeenFX_Burst", layer, 101f, BuildBurstProfile());
    }

    private void Start()
    {
        if (_chaseMusic == null)
            _chaseMusic = FindFirstObjectByType<ChaseMusic>();

        if (_chaseMusic != null)
            _chaseMusic.JumpscareTriggered += Burst;

        if (_seekers == null || _seekers.Length == 0)
            _seekers = FindObjectsByType<SeekerChaseState>(FindObjectsSortMode.None);
    }

    // Volumes são objetos à parte: desligar o script não apaga o efeito sozinho.
    private void OnDisable()
    {
        if (_burstVolume != null) _burstVolume.weight = 0f;
        if (_chaseVolume != null) _chaseVolume.weight = 0f;
    }

    private void OnDestroy()
    {
        if (_chaseMusic != null)
            _chaseMusic.JumpscareTriggered -= Burst;

        // A câmera pode ter sido destruída antes (troca de cena).
        if (_cameraData != null)
        {
            _cameraData.renderPostProcessing = _originalPostProcessing;
            _cameraData.volumeLayerMask = _originalVolumeMask;
        }

        foreach (VolumeProfile profile in _profiles)
            Destroy(profile);
    }

    private void Burst() => _burstStart = Time.time;

    private void Update()
    {
        if (GameManager.Current != null && GameManager.Current.IsOver)
            _gameOver = true;

        if (_gameOver)
        {
            _masterGain = Mathf.MoveTowards(_masterGain, 0f, Time.deltaTime / _fadeOutOnGameOver);
            if (_masterGain <= 0f)
            {
                enabled = false;   // o OnDisable zera os pesos
                return;
            }
        }

        float t = (Time.time - _burstStart) / _burstDuration;
        // Decaimento quadrático: o grosso passa logo, a cauda segura o susto um pouco mais.
        float burst = t < 1f ? (1f - t) * (1f - t) : 0f;
        burst *= 1f - Random.value * _burstFlicker;

        _burstVolume.weight = burst * _masterGain;
        _chaseVolume.weight = MaxBlend() * _masterGain;
    }

    private float MaxBlend()
    {
        float blend = 0f;

        if (_seekers == null)
            return blend;

        foreach (SeekerChaseState seeker in _seekers)
        {
            if (seeker != null && seeker.isActiveAndEnabled)
                blend = Mathf.Max(blend, seeker.Blend);
        }

        return blend;
    }

    private Volume CreateVolume(string objectName, int layer, float priority, VolumeProfile profile)
    {
        var host = new GameObject(objectName) { layer = layer };
        host.transform.SetParent(transform, false);

        Volume volume = host.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = priority;
        volume.weight = 0f;
        // profile (e não sharedProfile) seria uma cópia instanciada; o perfil já é só deste objeto.
        volume.sharedProfile = profile;
        return volume;
    }

    private VolumeProfile BuildBurstProfile()
    {
        VolumeProfile profile = NewProfile("SeenFX_Burst");

        FilmGrain grain = profile.Add<FilmGrain>(true);
        grain.type.Override(FilmGrainLookup.Large02);
        grain.intensity.Override(_burstGrain);
        grain.response.Override(0.2f);   // grão também nas áreas claras, não só nas sombras

        profile.Add<ChromaticAberration>(true).intensity.Override(_burstChromatic);

        LensDistortion distortion = profile.Add<LensDistortion>(true);
        distortion.intensity.Override(_burstDistortion);
        distortion.scale.Override(1.1f);   // compensa a borda que a distortion negativa puxa pra dentro

        Vignette vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(_burstVignette);
        vignette.smoothness.Override(0.5f);

        return profile;
    }

    private VolumeProfile BuildChaseProfile()
    {
        VolumeProfile profile = NewProfile("SeenFX_Chase");

        FilmGrain grain = profile.Add<FilmGrain>(true);
        grain.type.Override(FilmGrainLookup.Medium3);
        grain.intensity.Override(_chaseGrain);
        grain.response.Override(0.5f);

        profile.Add<ChromaticAberration>(true).intensity.Override(_chaseChromatic);

        Vignette vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(_chaseVignette);
        vignette.smoothness.Override(0.45f);

        return profile;
    }

    private VolumeProfile NewProfile(string profileName)
    {
        VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = profileName;
        _profiles.Add(profile);
        return profile;
    }
}
