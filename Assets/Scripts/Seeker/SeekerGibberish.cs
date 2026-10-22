using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// "Fala" do seeker: cada frase é montada na hora com fatias aleatórias dos clipes da entrada, com pitch, reverso,
    /// gaguejo e pausa sorteados por fatia, então nenhuma frase se repete. Mais frequente e mais alta em perseguição.
    /// Os clipes precisam de Load Type Decompress On Load: a montagem lê as amostras com GetData.
    /// </summary>
    public class SeekerGibberish : MonoBehaviour
    {
        [SerializeField] private string _soundId = "seeker_gibberish";
        [SerializeField, Min(0f)] private float _mouthHeight = 1.7f;

        [Header("-----Frase-----")]
        [SerializeField] private Vector2 _phraseSeconds = new Vector2(0.8f, 2.2f);
        [SerializeField] private Vector2 _sliceSeconds = new Vector2(0.04f, 0.3f);

        [Tooltip("Pitch por fatia (1 = original).")]
        [SerializeField] private Vector2 _slicePitch = new Vector2(0.7f, 1.3f);

        [SerializeField, Range(0f, 1f)] private float _reverseChance = 0.25f;

        [Tooltip("Chance de repetir a fatia 2 a 4 vezes seguidas.")]
        [SerializeField, Range(0f, 1f)] private float _stutterChance = 0.2f;

        [SerializeField, Range(0f, 1f)] private float _gapChance = 0.15f;
        [SerializeField] private Vector2 _gapSeconds = new Vector2(0.03f, 0.15f);

        [Tooltip("Rampa nas bordas de cada fatia: sem ela, cada corte dá um clique.")]
        [SerializeField, Min(0.001f)] private float _fadeSeconds = 0.006f;

        [Tooltip("Volume (RMS) que toda fatia busca: a estática e os gritos vêm em volumes muito diferentes.")]
        [SerializeField, Range(0.01f, 1f)] private float _sliceLoudness = 0.25f;

        [Header("-----Quando fala (s entre frases)-----")]
        [SerializeField] private Vector2 _patrolInterval = new Vector2(1.5f, 4f);
        [SerializeField] private Vector2 _chaseInterval = new Vector2(0.3f, 1.2f);

        [Tooltip("Atraso da fala ao começar a perseguição.")]
        [SerializeField] private Vector2 _spottedDelay = new Vector2(0.2f, 0.8f);

        [SerializeField, Range(0f, 2f)] private float _patrolVolume = 0.6f;
        [SerializeField, Range(0f, 2f)] private float _chaseVolume = 1f;

        private struct Samples
        {
            public float[] Data;
            public int Rate;
        }

        private readonly List<Samples> _sources = new List<Samples>();
        private AudioSource _voice;
        private AudioClip _phrase;
        private float _entryVolume;
        private float _nextTime;
        private bool _wasChasing;

        private SeekerChaseState _chaseState;
        private SeekerAudioSystem _audioSystem;
        private SeekerLostRoar _lostRoar;

        private void Start()
        {
            if (AudioProvider.IsMuted || (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn))
            {
                enabled = false;
                return;
            }

            AudioLibrary library = AudioProvider.Library;
            if (library == null || !library.TryGet(_soundId, out AudioLibrary.SoundEntry entry))
            {
                Debug.LogWarning($"{name}: id '{_soundId}' não está na AudioLibrary — seeker sem fala.", this);
                enabled = false;
                return;
            }

            foreach (AudioClip clip in entry.Clips)
                Load(clip);

            if (_sources.Count == 0)
            {
                enabled = false;
                return;
            }

            _chaseState = GetComponentInChildren<SeekerChaseState>();
            _audioSystem = GetComponentInChildren<SeekerAudioSystem>();
            _entryVolume = entry.Volume;

            var host = new GameObject("Voice_" + _soundId);
            host.transform.SetParent(transform, false);
            host.transform.localPosition = Vector3.up * _mouthHeight;

            // Mesma configuração 3D das vozes do AudioPool.
            _voice = host.AddComponent<AudioSource>();
            _voice.playOnAwake = false;
            _voice.spatialBlend = 1f;
            _voice.dopplerLevel = 0f;
            _voice.rolloffMode = library.Rolloff;
            _voice.minDistance = entry.MinDistance > 0f ? entry.MinDistance : library.MinDistance;
            _voice.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : library.MaxDistance;
            _voice.outputAudioMixerGroup = library.MixerGroup;

            Schedule(0f);
        }

        private void OnDestroy()
        {
            if (_phrase != null)
                Destroy(_phrase);
        }

        private void Update()
        {
            if (_audioSystem != null && _audioSystem.IsSilenced)
            {
                _voice.Stop();
                return;
            }

            // Adicionado depois deste Start pelo FreeExplorerManager, por isso a busca tardia.
            if (_lostRoar == null)
                _lostRoar = GetComponentInChildren<SeekerLostRoar>();

            float chase = _chaseState != null ? _chaseState.Blend : 0f;
            bool chasing = _chaseState != null && _chaseState.IsChasing;
            if (chasing && !_wasChasing)
                _nextTime = Mathf.Min(_nextTime, Time.time + Random.Range(_spottedDelay.x, _spottedDelay.y));
            _wasChasing = chasing;

            bool canSpeak = (GameManager.Current == null || GameManager.Current.IsPlaying)
                && (_lostRoar == null || !_lostRoar.IsRoaring);

            if (!canSpeak || _voice.isPlaying || Time.time < _nextTime)
                return;

            Speak(_entryVolume * Mathf.Lerp(_patrolVolume, _chaseVolume, chase));
            Schedule(chase);
        }

        public void Speak(float volume)
        {
            _voice.Stop();
            if (_phrase != null)
                Destroy(_phrase);

            int rate = AudioSettings.outputSampleRate;
            float[] buffer = BuildPhrase(rate);

            _phrase = AudioClip.Create("Gibberish", buffer.Length, 1, rate, false);
            _phrase.SetData(buffer, 0);

            _voice.clip = _phrase;
            _voice.volume = volume;
            _voice.Play();
        }

        private void Schedule(float chase)
        {
            float min = Mathf.Lerp(_patrolInterval.x, _chaseInterval.x, chase);
            float max = Mathf.Lerp(_patrolInterval.y, _chaseInterval.y, chase);
            _nextTime = Time.time + Random.Range(min, max);
        }

        private void Load(AudioClip clip)
        {
            if (clip == null)
                return;

            if (clip.loadType != AudioClipLoadType.DecompressOnLoad)
            {
                Debug.LogWarning($"{name}: '{clip.name}' precisa de Load Type Decompress On Load para a fala do seeker.", clip);
                return;
            }

            if (clip.loadState != AudioDataLoadState.Loaded)
                clip.LoadAudioData();

            var raw = new float[clip.samples * clip.channels];
            if (!clip.GetData(raw, 0))
                return;

            // Mono: a voz é 3D, os canais se perderiam de qualquer jeito.
            var mono = new float[clip.samples];
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0f;
                for (int c = 0; c < clip.channels; c++)
                    sum += raw[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }

            _sources.Add(new Samples { Data = mono, Rate = clip.frequency });
        }

        private float[] BuildPhrase(int rate)
        {
            var buffer = new float[Mathf.CeilToInt(Random.Range(_phraseSeconds.x, _phraseSeconds.y) * rate)];
            int write = 0;

            while (write < buffer.Length)
            {
                if (write > 0 && Random.value < _gapChance)
                {
                    write += Mathf.CeilToInt(Random.Range(_gapSeconds.x, _gapSeconds.y) * rate);
                    continue;
                }

                Samples source = _sources[Random.Range(0, _sources.Count)];
                double step = (double)source.Rate / rate * Random.Range(_slicePitch.x, _slicePitch.y);
                int length = Mathf.CeilToInt(Random.Range(_sliceSeconds.x, _sliceSeconds.y) * rate);
                length = Mathf.Min(length, (int)((source.Data.Length - 2) / step));
                if (length <= 0)
                    continue;

                double span = length * step;
                double start = Random.Range(0f, (float)(source.Data.Length - 1 - span));
                bool reverse = Random.value < _reverseChance;
                float gain = SliceGain(source.Data, start, span);

                int repeats = Random.value < _stutterChance ? Random.Range(2, 5) : 1;
                for (int r = 0; r < repeats && write < buffer.Length; r++)
                    write = WriteSlice(buffer, write, source.Data, start, step, length, reverse, gain, rate);
            }

            float peak = 0f;
            foreach (float s in buffer)
                peak = Mathf.Max(peak, Mathf.Abs(s));
            if (peak > 0.95f)
            {
                float scale = 0.95f / peak;
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] *= scale;
            }

            return buffer;
        }

        private float SliceGain(float[] data, double start, double span)
        {
            int from = (int)start;
            int to = Mathf.Min(data.Length, (int)(start + span));
            double sum = 0;
            for (int i = from; i < to; i++)
                sum += data[i] * data[i];

            float rms = Mathf.Sqrt((float)(sum / Mathf.Max(1, to - from)));
            // Teto no ganho: fatia quase muda (silêncio entre gritos) viraria chiado estourado.
            return rms > 0.0001f ? Mathf.Min(_sliceLoudness / rms, 4f) : 0f;
        }

        private int WriteSlice(float[] buffer, int write, float[] data, double start, double step, int length,
            bool reverse, float gain, int rate)
        {
            int fade = Mathf.Max(1, Mathf.Min(Mathf.CeilToInt(_fadeSeconds * rate), length / 2));
            int count = Mathf.Min(length, buffer.Length - write);

            for (int i = 0; i < count; i++)
            {
                double position = start + (reverse ? length - 1 - i : i) * step;
                int index = (int)position;
                float t = (float)(position - index);
                float sample = Mathf.Lerp(data[index], data[index + 1], t);

                float edge = Mathf.Min(1f, Mathf.Min(i, length - 1 - i) / (float)fade);
                buffer[write + i] = sample * gain * edge;
            }

            return write + count;
        }
    }
}
