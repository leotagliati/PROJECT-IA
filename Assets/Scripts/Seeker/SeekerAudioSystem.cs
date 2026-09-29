using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerAudioSystem : MonoBehaviour
    {
        [Header("-----Estática-----")]
        [SerializeField] private string _staticId = "seeker_static";
        [SerializeField] private string _chaseStaticId = "seeker_static_chase";
        [SerializeField] private Transform _staticOrigin;

        [SerializeField, Range(0.5f, 2f)] private float _staticPitch = 1f;
        [SerializeField, Range(0.5f, 2f)] private float _chaseStaticPitch = 1f;

        [SerializeField] private SeekerChaseState _chaseState;

        [SerializeField, Min(0.01f)] private float _silenceFade = 0.05f;

        [Header("-----Passos-----")]
        [SerializeField] private string _footstepId = "seeker_footsteps";
        [SerializeField, Range(0f, 2f)] private float _footstepVolume = 1f;
        [SerializeField, Min(0f)] private float _footstepMinInterval = 0.15f;

        [Header("-----Treino-----")]
        [SerializeField] private bool _playDuringTraining = false;

        private AudioSource _static;
        private AudioSource _chaseStatic;
        private float _staticVolume;
        private float _chaseStaticVolume;

        private float _gain = 1f;       // 1 = tocando, 0 = silenciado
        private float _lastFootstepTime = float.NegativeInfinity;

        private bool _ready;            // passou pelo gate de treino/mudo: passos podem tocar
        private bool _loopsAlive;       // há estática para atualizar no Update
        private bool _silenced;

        public bool IsSilenced => _silenced;

        public void Initialize()
        {
            if (!_playDuringTraining && Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
                return;

            if (AudioProvider.IsMuted)
                return;

            _ready = true;

            if (_chaseState == null)
                _chaseState = GetComponent<SeekerChaseState>();

            // Library ausente já foi logada pelo provider; aqui só não há o que tocar.
            AudioLibrary library = AudioProvider.Library;
            if (library == null)
                return;

            Transform origin = _staticOrigin != null ? _staticOrigin : transform;
            _static = CreateLoop(library, _staticId, origin, _staticPitch, out _staticVolume);
            _chaseStatic = CreateLoop(library, _chaseStaticId, origin, _chaseStaticPitch, out _chaseStaticVolume);
            _loopsAlive = _static != null || _chaseStatic != null;

            ResetEpisode();
        }

        public void ResetEpisode()
        {
            _lastFootstepTime = float.NegativeInfinity;

            // Desfaz o Silence() do episódio anterior. Só acontece com áudio ligado no treino
            // (no jogo não há reset depois do fim), mas sem isto a primeira captura calaria o
            // seeker para todos os episódios seguintes — o Update já teria dado Stop nos loops.
            _silenced = false;
            _gain = 1f;
            RestartLoop(_static);
            RestartLoop(_chaseStatic);
            _loopsAlive = _static != null || _chaseStatic != null;

            // O SeekerChaseState já zerou o blend no reset dele (roda antes); aqui só reflete.
            ApplyVolumes();
        }

        /// <summary>
        /// Fim de jogo: cala tudo até o próximo ResetEpisode. Dali em diante o único áudio é o da
        /// sequência de captura. O passo obedece no ato — o Animator ainda leva alguns frames de damp para
        /// chegar em Idle, e sem isto o jumpscare abria com o seeker dando um último passo.
        /// </summary>
        public void Silence()
        {
            _silenced = true;
        }

        /// <summary>Entrada do Animation Event, via <see cref="SeekerFootsteps"/> no objeto do Animator.</summary>
        public void PlayFootstep(Vector3 position)
        {
            if (!_ready || _silenced)
                return;

            if (Time.time - _lastFootstepTime < _footstepMinInterval)
                return;

            _lastFootstepTime = Time.time;
            AudioProvider.PlayAt(_footstepId, position, _footstepVolume);
        }

        private void Update()
        {
            if (!_loopsAlive)
                return;

            _gain = Mathf.MoveTowards(_gain, _silenced ? 0f : 1f, Time.deltaTime / _silenceFade);

            ApplyVolumes();

            if (_silenced && _gain <= 0f)
            {
                if (_static != null) _static.Stop();
                if (_chaseStatic != null) _chaseStatic.Stop();
                _loopsAlive = false;
            }
        }

        private static void RestartLoop(AudioSource source)
        {
            if (source != null && !source.isPlaying)
                source.Play();
        }

        private void ApplyVolumes()
        {
            // Sem SeekerChaseState o seeker soa sempre em patrulha — degrada, não quebra.
            float chase = _chaseState != null ? _chaseState.Blend : 0f;

            if (_static != null)
                _static.volume = _staticVolume * (1f - chase) * _gain;

            if (_chaseStatic != null)
                _chaseStatic.volume = _chaseStaticVolume * chase * _gain;
        }

        /// <summary>
        /// Mesma configuração 3D que o pool dá às vozes dele, para a estática soar no mesmo espaço
        /// que os outros sons. Se mudar lá, muda aqui.
        /// </summary>
        private AudioSource CreateLoop(AudioLibrary library, string id, Transform parent, float pitch, out float baseVolume)
        {
            baseVolume = 0f;

            if (!library.TryGet(id, out AudioLibrary.SoundEntry entry) || entry.Clips == null || entry.Clips.Length == 0)
            {
                Debug.LogWarning($"{name}: id '{id}' não está na AudioLibrary — estática desligada.", this);
                return null;
            }

            var host = new GameObject("Static_" + id);
            host.transform.SetParent(parent, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = library.Rolloff;
            source.minDistance = library.MinDistance;
            source.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : library.MaxDistance;
            source.outputAudioMixerGroup = library.MixerGroup;
            source.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
            source.pitch = pitch;
            source.volume = 0f;

            // Começa num ponto aleatório: se os dois ids apontarem para o mesmo clipe (placeholder
            // até existir a estática normal), em fase eles seriam um som só com volume variando.
            source.time = Random.Range(0f, source.clip.length);
            source.Play();

            baseVolume = entry.Volume;
            return source;
        }
    }
}
