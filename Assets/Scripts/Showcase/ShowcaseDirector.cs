using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Assets.Scripts.Showcase
{
    /// <summary>
    /// Câmera e legenda da vitrine (uma arena por versão do monstro, todas rodando o .onnx em inferência), para gravar.
    /// Montada pelo menu "PROJECT-IA → Vitrine: montar cena com todos os modelos" (ShowcaseSceneSetup).
    /// Teclas: ←/→ arena, 1–9/0 direto, Tab câmera (mapa / seguir / de cima), G todas, M marcadores, H legenda,
    /// [ ] velocidade, F1 ajuda.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class ShowcaseDirector : MonoBehaviour
    {
        public enum View
        {
            Map,
            Follow,
            Top,
            All,
        }

        [Serializable]
        public class Entry
        {
            public string version;
            public string title;
            [TextArea] public string detail;
            public Transform arena;
            public Transform agent;
            public Transform target;

            // Medidos na montagem: a arena fica salva desligada (sem collider para medir no Play).
            public Bounds bounds;

            // Foto de cima da arena, no lugar dela enquanto está desligada (vista Todas).
            public GameObject photo;
        }

        [Header("-----Arenas-----")]
        [SerializeField] private Camera _camera;
        [SerializeField] private List<Entry> _entries = new();
        [SerializeField] private LayerMask _wallLayer;

        [Tooltip("Só a arena da câmera roda; as outras ficam desligadas (na vista Todas, pela foto). Religar começa um episódio novo.")]
        [SerializeField] private bool _onlyCurrentArena = true;

        [Tooltip("Tempo mínimo (s reais) que a próxima arena roda por trás antes de a câmera ir para ela.")]
        [SerializeField, Min(0f)] private float _loadSeconds = 0.3f;

        [Tooltip("No Play, liga cada arena uma vez antes de liberar as teclas. Sem ele, o 1º mapa de cada arena é gerado " +
                 "com o NavMesh da atual também carregado.")]
        [SerializeField] private bool _prewarm = true;

        [Header("-----Ciclo automático (estande)-----")]
        [Tooltip("Troca de arena sozinha, no tempo de cada versão (TourSeconds). As teclas continuam valendo.")]
        [SerializeField] private bool _autoTour = true;
        [Tooltip("Segundos reais sem tecla para o ciclo voltar depois que alguém mexeu.")]
        [SerializeField, Min(0f)] private float _tourResumeSeconds = 20f;

        [Header("-----Câmera-----")]
        [SerializeField] private View _view = View.Map;
        [SerializeField, Min(1f)] private float _mapPadding = 1.08f;
        [SerializeField, Min(1f)] private float _topSize = 14f;
        [SerializeField, Min(0f)] private float _followDistance = 9f;
        [SerializeField, Min(0f)] private float _followHeight = 9f;
        [SerializeField, Min(0.1f)] private float _followSharpness = 4f;

        [Header("-----Marcadores-----")]
        [SerializeField] private bool _showMarkers = true;
        [Tooltip("Diâmetro (m) do marcador nas vistas seguir e de cima.")]
        [SerializeField, Min(0.1f)] private float _closeMarkerSize = 1.4f;
        [Tooltip("Folga (m) entre o topo do corpo e o marcador nas vistas seguir e de cima.")]
        [SerializeField, Min(0f)] private float _markerClearance = 0.8f;
        [SerializeField] private Color _agentColor = new(1f, 0.15f, 0.1f);
        [SerializeField] private Color _targetColor = new(0.2f, 0.75f, 1f);
        [Tooltip("Diâmetro do marcador no mapa, como fração do tamanho do mapa na tela.")]
        [SerializeField, Range(0.005f, 0.1f)] private float _markerScale = 0.035f;

        [Header("-----Tempo-----")]
        [Tooltip("Velocidade no início do Play (vai para a mais próxima de 0.25, 0.5, 1, 2, 3, 4, 5x).")]
        [SerializeField, Range(0.25f, 5f)] private float _startTimeScale = 1f;

        [Header("-----Legenda-----")]
        [SerializeField] private bool _showLabel = true;

        private static readonly float[] TimeScales = { 0.25f, 0.5f, 1f, 2f, 3f, 4f, 5f };

        private static readonly FieldInfo AgentSensors = typeof(Agent).GetField("sensors", BindingFlags.Instance | BindingFlags.NonPublic);

        private Bounds[] _bounds;
        private bool[] _brainChecked;
        private string[] _brainStatus;
        private Transform[] _agentMarkers;
        private Transform[] _targetMarkers;
        private int _current;
        private int _target;
        private bool _currentOn;
        private int _staged = -1;
        private float _stagedSince;
        private int _stagedFixedSteps;
        private bool _warming;
        private int _warmIndex;
        private float _shownSince;
        private float[] _tourSeconds;
        private bool _showTimes;
        private Vector2 _timesScroll;
        private GUIStyle _buttonStyle;
        private GUIStyle _rowStyle;

        private const string TimePrefsKey = "Vitrine.Tempo.";
        private const float TimeStep = 0.5f, MinTourSeconds = 1f, MaxTourSeconds = 120f;
        private float _lastInput = float.NegativeInfinity;
        private int _timeScaleIndex = 2;
        private float _measuredScale = 1f;
        private bool _showHelp;
        private bool _snap = true;
        private Vector3 _followLook;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _centerStyle;
        private Texture2D _panel;

        public IReadOnlyList<Entry> Entries => _entries;

        public Camera ViewCamera => _camera;

        public int Current => _current;

        public View CurrentView => _view;

        /// <summary>O ciclo automático (o teste do Editor desliga para escolher as arenas ele mesmo).</summary>
        public bool AutoTour
        {
            get => _autoTour;
            set => _autoTour = value;
        }

        /// <summary>Tempo de tela por geração: v1–v4 curtas, v5–v6 (grafo) 6 s, v7+ (livre, ronda mais longa) 8 s.</summary>
        public static float TourSeconds(string version)
        {
            int digits = 1;
            while (digits < version.Length && char.IsDigit(version[digits]))
                digits++;
            int major = digits > 1 ? int.Parse(version.Substring(1, digits - 1)) : 0;
            return major >= 7 ? 8f : major >= 5 ? 6f : 3.5f;
        }

        /// <summary>Resultado da conferência .onnx x sensores ("ok", o problema, ou null se ainda não ligou).</summary>
        public string BrainStatus(int index) => _brainStatus != null ? _brainStatus[index] : null;

        private static readonly string[] HiderTypes = { "FreeHider", "GraphHider", "HiderAgent" };

        // A montagem antiga pegava nas Legacy o 1º componente com "Hider" no nome, e o GraphHiderPerception (a visão, no
        // corpo do monstro) também casa: a presa marcada era o próprio monstro. Aqui vale só o tipo exato, fora do monstro.
        private static Transform ResolveTarget(Entry entry)
        {
            bool InsideAgent(Transform t) =>
                entry.agent != null && (t.IsChildOf(entry.agent) || entry.agent.IsChildOf(t));

            if (entry.target != null && !InsideAgent(entry.target) && HasHider(entry.target))
                return entry.target;
            if (entry.arena == null)
                return entry.target;

            foreach (string type in HiderTypes)
            {
                foreach (MonoBehaviour component in entry.arena.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component != null && component.GetType().Name == type && !InsideAgent(component.transform))
                        return component.transform;
                }
            }

            return null;
        }

        private static bool HasHider(Transform t)
        {
            foreach (MonoBehaviour component in t.GetComponents<MonoBehaviour>())
            {
                if (component != null && Array.IndexOf(HiderTypes, component.GetType().Name) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>Aquecimento terminado, nada carregando e a câmera na arena pedida.</summary>
        public bool IsReady => !_warming && (!_onlyCurrentArena || (_currentOn && _staged < 0 && _target == _current));

        private void Start()
        {
            if (_camera == null)
                _camera = Camera.main;

            // Estande: tela cheia no monitor todo (o Unity lembra o último modo, e uma janela não maximiza) e sem cursor.
            if (!Application.isEditor)
            {
                SetFullscreen(true);
                Cursor.visible = false;
            }

            _bounds = new Bounds[_entries.Count];
            LoadTourTimes();
            _brainChecked = new bool[_entries.Count];
            _brainStatus = new string[_entries.Count];
            _agentMarkers = new Transform[_entries.Count];
            _targetMarkers = new Transform[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].target = ResolveTarget(_entries[i]);
                _bounds[i] = _entries[i].bounds.size != Vector3.zero ? _entries[i].bounds : MeasureArena(_entries[i].arena);
                _agentMarkers[i] = CreateMarker("Marcador do monstro", _agentColor);
                _targetMarkers[i] = CreateMarker("Marcador da presa", _targetColor);
            }

            for (int i = 0; i < TimeScales.Length; i++)
            {
                if (Mathf.Abs(TimeScales[i] - _startTimeScale) < Mathf.Abs(TimeScales[_timeScaleIndex] - _startTimeScale))
                    _timeScaleIndex = i;
            }

            Time.timeScale = TimeScales[_timeScaleIndex];
            if (!_onlyCurrentArena)
            {
                for (int i = 0; i < _entries.Count; i++)
                    SetArena(i, true);
            }
            else if (_prewarm)
            {
                StartCoroutine(Prewarm());
            }
        }

        // Na 1ª vez que a arena liga, o OnEnable do Agent roda Initialize + OnEpisodeBegin antes do Awake/Start do resto
        // dela, e o modelo ficava parado até sair e voltar. Aqui cada uma liga uma vez, sozinha: toda visita vira "volta".
        private IEnumerator Prewarm()
        {
            _warming = true;
            for (int i = 0; i < _entries.Count; i++)
                SetArena(i, false);

            for (int i = 0; i < _entries.Count; i++)
            {
                _warmIndex = i;
                SetArena(i, true);
                yield return new WaitForFixedUpdate();
                yield return null;
                yield return null;
                SetArena(i, false);
                yield return null;
            }

            _current = _target;
            _warming = false;
        }

        // No máximo duas ligadas: a da câmera e a próxima, que carrega por trás e só ganha a câmera depois de rodar (a
        // seta segurada ligava e desligava várias no mesmo instante e derrubava o Unity, 22/10; as 18 juntas estouram o
        // PhysX, 21/10). Pedido que muda no meio espera a carga acabar. A desligada aparece pela foto.
        private void SyncArenas()
        {
            if (!_onlyCurrentArena || _warming)
                return;

            if (!_currentOn)
            {
                SetArena(_current, true);
                _currentOn = true;
                _shownSince = Time.unscaledTime;
                return;
            }

            if (_staged < 0)
            {
                if (_target != _current)
                {
                    _staged = _target;
                    _stagedSince = Time.unscaledTime;
                    _stagedFixedSteps = 0;
                    SetArena(_staged, true);
                }

                return;
            }

            // Dois steps de física: o Start de tudo já rodou e o agente já decidiu.
            if (_stagedFixedSteps < 2 || Time.unscaledTime - _stagedSince < _loadSeconds)
                return;

            if (_staged == _target)
            {
                SetArena(_current, false);
                _current = _staged;
                _snap = true;
                _shownSince = Time.unscaledTime;
            }
            else
            {
                SetArena(_staged, false);
            }

            _staged = -1;
        }

        private void FixedUpdate()
        {
            if (_staged >= 0)
                _stagedFixedSteps++;
        }

        private void SetArena(int index, bool active)
        {
            Entry entry = _entries[index];
            if (entry.arena != null && entry.arena.gameObject.activeSelf != active)
                entry.arena.gameObject.SetActive(active);
            if (entry.photo != null)
                entry.photo.SetActive(!active);
            if (active)
                GuardBrain(index);
        }

        // O Inference Engine escreve a observação no tensor sem checar limite: sensor maior que a entrada do .onnx escreve
        // por cima de outra memória e o Unity cai depois, em qualquer alocação (crashes de 22/10, planta na v5.0). Confere
        // logo depois de ligar (o OnEnable do Agent já criou os sensores), antes da 1ª decisão; incompatível fica sem cérebro.
        private void GuardBrain(int index)
        {
            if (_brainChecked == null || _brainChecked[index])
                return;
            _brainChecked[index] = true;

            Agent agent = _entries[index].agent != null ? _entries[index].agent.GetComponent<Agent>() : null;
            BehaviorParameters behavior = agent != null ? agent.GetComponent<BehaviorParameters>() : null;
            if (behavior == null || behavior.Model == null || behavior.BehaviorType == BehaviorType.HeuristicOnly || !agent.isActiveAndEnabled)
                return;

            if (AgentSensors == null || AgentSensors.GetValue(agent) is not List<ISensor> sensors)
            {
                Debug.LogWarning($"[Vitrine] {_entries[index].version}: não consegui ler os sensores do Agent para conferir o .onnx.", agent);
                return;
            }

            string problem = BrainMismatch(ModelLoader.Load(behavior.Model), sensors);
            _brainStatus[index] = problem ?? "ok";
            if (problem == null)
                return;

            Debug.LogError($"[Vitrine] {_entries[index].version}: {behavior.Model.name} não bate com os sensores ({problem}). " +
                           "Rodando sem cérebro para não corromper a memória.", agent);
            behavior.BehaviorType = BehaviorType.HeuristicOnly;
        }

        // Como o ML-Agents liga: sensores em ordem de nome, o i-ésimo vai para a entrada obs_i.
        private static string BrainMismatch(Model model, List<ISensor> sensors)
        {
            var problems = new List<string>();
            int observations = 0;
            foreach (Model.Input input in model.inputs)
            {
                if (!input.name.StartsWith("obs_") || !int.TryParse(input.name.Substring(4), out int i))
                    continue;

                observations++;
                if (i >= sensors.Count)
                {
                    problems.Add($"{input.name} sem sensor");
                    continue;
                }

                int expected = 1;
                for (int axis = 1; axis < input.shape.rank; axis++)
                    expected *= Mathf.Max(1, input.shape.Get(axis));

                ObservationSpec spec = sensors[i].GetObservationSpec();
                int emitted = 1;
                for (int axis = 0; axis < spec.Shape.Length; axis++)
                    emitted *= spec.Shape[axis];

                if (emitted != expected)
                    problems.Add($"{input.name} espera {expected}, {sensors[i].GetName()} emite {emitted}");
            }

            if (observations > 0 && sensors.Count > observations)
                problems.Add($"{sensors.Count} sensores para {observations} entradas");
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
        }

        public void Select(int index, View view)
        {
            if (_entries.Count == 0)
                return;

            _target = (index % _entries.Count + _entries.Count) % _entries.Count;
            _view = view;
            if (!_onlyCurrentArena && _current != _target)
            {
                _current = _target;
                _shownSince = Time.unscaledTime;
            }
            if (_target == _current)
                _snap = true;
        }

        private void Update()
        {
            if (_entries.Count == 0)
                return;

            SyncArenas();

            Keyboard keyboard = Keyboard.current;
            if (_warming)
                return;

            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame)
                _lastInput = Time.unscaledTime;
            if (keyboard != null && (keyboard.tKey.wasPressedThisFrame || (_showTimes && keyboard.escapeKey.wasPressedThisFrame)))
                ShowTimes(!_showTimes);
            if (keyboard != null && keyboard.f11Key.wasPressedThisFrame)
                SetFullscreen(Screen.fullScreenMode == FullScreenMode.Windowed);

            // Com a janela de tempos aberta o ciclo fica parado e as teclas da vitrine não valem.
            if (_showTimes)
            {
                _lastInput = Time.unscaledTime;
                return;
            }

            AdvanceTour();
            if (keyboard == null)
                return;

            // Da arena da câmera, não do pedido: espamar a seta durante a carga continua indo só para a vizinha.
            if (keyboard.rightArrowKey.wasPressedThisFrame)
                Select(_current + 1, _view == View.All ? View.Map : _view);
            if (keyboard.leftArrowKey.wasPressedThisFrame)
                Select(_current - 1, _view == View.All ? View.Map : _view);

            // Digit1..Digit9 e depois Digit0 no enum: o índice i é a tecla Digit1 + i.
            for (int i = 0; i < Mathf.Min(10, _entries.Count); i++)
            {
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
                    Select(i, _view == View.All ? View.Map : _view);
            }

            if (keyboard.tabKey.wasPressedThisFrame)
                Select(_target, _view switch { View.Map => View.Follow, View.Follow => View.Top, _ => View.Map });
            if (keyboard.gKey.wasPressedThisFrame)
                Select(_target, _view == View.All ? View.Map : View.All);
            if (keyboard.mKey.wasPressedThisFrame)
                _showMarkers = !_showMarkers;
            if (keyboard.hKey.wasPressedThisFrame)
                _showLabel = !_showLabel;
            if (keyboard.f1Key.wasPressedThisFrame)
                _showHelp = !_showHelp;
            if (keyboard.leftBracketKey.wasPressedThisFrame || keyboard.rightBracketKey.wasPressedThisFrame)
            {
                _timeScaleIndex = Mathf.Clamp(_timeScaleIndex + (keyboard.rightBracketKey.wasPressedThisFrame ? 1 : -1),
                    0, TimeScales.Length - 1);
                Time.timeScale = TimeScales[_timeScaleIndex];
            }
        }

        // Estande: passa para a próxima depois do tempo da versão; alguém mexendo, espera _tourResumeSeconds sem tecla.
        private void AdvanceTour()
        {
            bool settled = _target == _current && _staged < 0;
            if (!_autoTour || !settled || Time.unscaledTime - _lastInput < _tourResumeSeconds
                || Time.unscaledTime - _shownSince < _tourSeconds[_current])
                return;

            Select(_current + 1, _view == View.All ? View.Map : _view);
        }

        // Tempo de cada arena: o padrão da geração (TourSeconds) até alguém ajustar na janela (T); fica salvo no PC.
        private void LoadTourTimes()
        {
            _tourSeconds = new float[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
                _tourSeconds[i] = PlayerPrefs.GetFloat(TimePrefsKey + _entries[i].version, TourSeconds(_entries[i].version));
        }

        private void SetTourTime(int index, float seconds)
        {
            _tourSeconds[index] = Mathf.Clamp(Mathf.Round(seconds / TimeStep) * TimeStep, MinTourSeconds, MaxTourSeconds);
            PlayerPrefs.SetFloat(TimePrefsKey + _entries[index].version, _tourSeconds[index]);
            PlayerPrefs.Save();
        }

        private void ResetTourTimes()
        {
            for (int i = 0; i < _entries.Count; i++)
                PlayerPrefs.DeleteKey(TimePrefsKey + _entries[i].version);
            PlayerPrefs.Save();
            LoadTourTimes();
        }

        private static void SetFullscreen(bool fullscreen)
        {
            if (fullscreen)
                Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow);
            else
                Screen.SetResolution(Display.main.systemWidth * 2 / 3, Display.main.systemHeight * 2 / 3, FullScreenMode.Windowed);
        }

        private void ShowTimes(bool show)
        {
            _showTimes = show;
            Cursor.visible = show || Application.isEditor;
        }

        private void LateUpdate()
        {
            if (Time.unscaledDeltaTime > 0f)
                _measuredScale = Mathf.Lerp(_measuredScale, Time.deltaTime / Time.unscaledDeltaTime, 0.05f);
            ApplyView();
        }

        /// <summary>Põe a câmera e os marcadores na vista atual agora (o teste do Editor chama antes de fotografar).</summary>
        public void ApplyView()
        {
            if (_camera == null || _entries.Count == 0 || _bounds == null)
                return;

            Entry entry = _entries[_current];
            switch (_view)
            {
                case View.Map:
                    PlaceOrthographic(_bounds[_current]);
                    break;
                case View.All:
                    Bounds all = _bounds[0];
                    foreach (Bounds b in _bounds)
                        all.Encapsulate(b);
                    PlaceOrthographic(all);
                    break;
                case View.Top:
                    PlaceTop(entry);
                    break;
                default:
                    PlaceFollow(entry);
                    break;
            }

            _snap = false;
            UpdateMarkers();
        }

        private void PlaceOrthographic(Bounds bounds)
        {
            _camera.orthographic = true;
            _camera.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / Mathf.Max(0.1f, _camera.aspect)) * _mapPadding;
            _camera.transform.SetPositionAndRotation(
                new Vector3(bounds.center.x, bounds.max.y + 50f, bounds.center.z), Quaternion.Euler(90f, 0f, 0f));
        }

        private void PlaceTop(Entry entry)
        {
            Vector3 focus = entry.agent != null ? entry.agent.position : _bounds[_current].center;
            _camera.orthographic = true;
            _camera.orthographicSize = _topSize;
            Vector3 goal = new(focus.x, _bounds[_current].max.y + 50f, focus.z);
            _camera.transform.SetPositionAndRotation(_snap ? goal : Smooth(_camera.transform.position, goal),
                Quaternion.Euler(90f, 0f, 0f));
        }

        private void PlaceFollow(Entry entry)
        {
            if (entry.agent == null)
            {
                PlaceOrthographic(_bounds[_current]);
                return;
            }

            // Atrás e acima do corpo: alto o bastante para olhar por cima das paredes das salas.
            Vector3 forward = Vector3.ProjectOnPlane(entry.agent.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();

            Vector3 look = entry.agent.position + Vector3.up * 1.5f;
            Vector3 goal = look - forward * _followDistance + Vector3.up * _followHeight;
            _camera.orthographic = false;
            Vector3 position = _snap ? goal : Smooth(_camera.transform.position, goal);
            _followLook = _snap ? look : Smooth(_followLook, look);
            _camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(_followLook - position));
        }

        private Vector3 Smooth(Vector3 from, Vector3 to) =>
            Vector3.Lerp(from, to, 1f - Mathf.Exp(-_followSharpness * Time.unscaledDeltaTime));

        // No mapa os marcadores ficam acima das paredes; seguindo/de cima, flutuam sobre a cabeça (a presa escondida
        // atrás de móvel ou parede continua marcada).
        private void UpdateMarkers()
        {
            bool map = _view == View.Map || _view == View.All;
            float size = map ? (_camera.orthographic ? _camera.orthographicSize * 2f : 20f) * _markerScale : _closeMarkerSize;
            for (int i = 0; i < _entries.Count; i++)
            {
                bool visible = _showMarkers && (_view == View.All || i == _current);
                Place(_agentMarkers[i], _entries[i].agent, visible, size, map ? _bounds[i].max.y + 1f : -1f);
                Place(_targetMarkers[i], _entries[i].target, visible, size * 0.8f, map ? _bounds[i].max.y + 0.5f : -1f);
            }
        }

        // height < 0: acima do topo do corpo desenhado (os monstros vão de 2 a 4 m; altura fixa cortava o pescoço).
        private void Place(Transform marker, Transform follow, bool visible, float size, float height)
        {
            bool show = visible && follow != null && follow.gameObject.activeInHierarchy;
            if (marker.gameObject.activeSelf != show)
                marker.gameObject.SetActive(show);
            if (!show)
                return;

            if (height < 0f)
                height = BodyTop(follow) + _markerClearance;
            marker.position = new Vector3(follow.position.x, height, follow.position.z);
            marker.localScale = new Vector3(size, 0.05f, size);
        }

        private readonly Dictionary<Transform, Renderer[]> _bodies = new();

        private float BodyTop(Transform body)
        {
            if (!_bodies.TryGetValue(body, out Renderer[] renderers))
            {
                renderers = body.GetComponentsInChildren<Renderer>(true);
                _bodies[body] = renderers;
            }

            float top = float.MinValue;
            foreach (Renderer r in renderers)
            {
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                    top = Mathf.Max(top, r.bounds.max.y);
            }

            return top > float.MinValue ? top : body.position.y + 2f;
        }

        private Transform CreateMarker(string markerName, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = markerName;
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(transform, false);
            var renderer = marker.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit != null)
            {
                var material = new Material(unlit);
                material.SetColor("_BaseColor", color);
                renderer.sharedMaterial = material;
            }
            else
            {
                renderer.material.color = color;
            }

            marker.SetActive(false);
            return marker.transform;
        }

        // A extensão do mapa pelas paredes (layer Wall): renderers incluem chão e planos de fora, bem maiores que a arena.
        private Bounds MeasureArena(Transform arena)
        {
            if (arena == null)
                return new Bounds(transform.position, Vector3.one * 20f);

            bool found = false;
            Bounds bounds = default;
            foreach (Collider collider in arena.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || (_wallLayer.value & (1 << collider.gameObject.layer)) == 0)
                    continue;
                if (!found)
                    bounds = collider.bounds;
                else
                    bounds.Encapsulate(collider.bounds);
                found = true;
            }

            if (found)
                return bounds;

            foreach (Renderer renderer in arena.GetComponentsInChildren<Renderer>())
            {
                if (!found)
                    bounds = renderer.bounds;
                else
                    bounds.Encapsulate(renderer.bounds);
                found = true;
            }

            return found ? bounds : new Bounds(arena.position, Vector3.one * 20f);
        }

        private void OnGUI()
        {
            if (_entries.Count == 0)
                return;

            EnsureStyles();
            if (_warming)
            {
                DrawLoading();
                return;
            }

            if (_showTimes)
            {
                DrawTimes();
                return;
            }

            if (!_showLabel && !_showHelp)
                return;

            float unit = Screen.height / 1080f;
            float margin = 32f * unit;

            if (_showLabel)
            {
                Entry entry = _entries[_current];
                string title = _view == View.All ? "Todas as versões" : $"{entry.version} — {entry.title}";
                string body = _view == View.All ? $"{_entries.Count} arenas, cada uma com o próprio cérebro" : entry.detail;
                if (TimeScales[_timeScaleIndex] != 1f)
                {
                    body += $"\nvelocidade {TimeScales[_timeScaleIndex]}x";
                    // Com 11 arenas a física pode não dar conta: mostra a velocidade que o jogo está conseguindo.
                    if (_measuredScale < TimeScales[_timeScaleIndex] * 0.9f)
                        body += $" (real ~{_measuredScale:0.0}x)";
                }

                if (_view != View.All && (entry.target == null || !entry.target.gameObject.activeInHierarchy))
                    body += "\nsem presa nesta arena";
                if (_target != _current)
                    body += $"\n→ {_entries[_target].version} carregando…";

                float width = Mathf.Min(Screen.width - 2f * margin, 980f * unit);
                float titleHeight = _titleStyle.CalcHeight(new GUIContent(title), width - 40f * unit);
                float bodyHeight = string.IsNullOrEmpty(body) ? 0f : _bodyStyle.CalcHeight(new GUIContent(body), width - 40f * unit);
                float height = titleHeight + bodyHeight + 36f * unit;
                var box = new Rect(margin, Screen.height - margin - height, width, height);
                GUI.DrawTexture(box, _panel);
                GUI.Label(new Rect(box.x + 20f * unit, box.y + 14f * unit, box.width - 40f * unit, titleHeight), title, _titleStyle);
                if (bodyHeight > 0f)
                    GUI.Label(new Rect(box.x + 20f * unit, box.y + 18f * unit + titleHeight, box.width - 40f * unit, bodyHeight), body, _bodyStyle);
            }

            if (_showHelp)
            {
                const string help = "←/→ arena   1–9, 0 direto   Tab câmera (mapa / seguir / de cima)\n" +
                                    "G todas   M marcadores   H legenda   [ ] velocidade   F1 fecha\n" +
                                    "Sozinho, troca de arena no tempo de cada versão; uma tecla pausa o ciclo.   T tempo por arena   F11 tela cheia";
                float width = Mathf.Min(Screen.width - 2f * margin, 900f * unit);
                float height = _bodyStyle.CalcHeight(new GUIContent(help), width - 40f * unit) + 24f * unit;
                var box = new Rect(margin, margin, width, height);
                GUI.DrawTexture(box, _panel);
                GUI.Label(new Rect(box.x + 20f * unit, box.y + 12f * unit, box.width - 40f * unit, height), help, _bodyStyle);
            }
        }

        // Janela do estande (T): uma linha por arena com − / +, o ciclo liga e desliga, e os tempos voltam ao padrão.
        private void DrawTimes()
        {
            float unit = Screen.height / 1080f;
            float row = 40f * unit, pad = 20f * unit, button = 48f * unit;
            float width = Mathf.Min(Screen.width - 2f * pad, 820f * unit);
            float height = Mathf.Min(Screen.height - 2f * pad, _entries.Count * row + 200f * unit);
            var box = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            // Duas camadas do painel: mais opaco que a legenda, a vitrine continua rodando atrás.
            GUI.DrawTexture(box, _panel);
            GUI.DrawTexture(box, _panel);

            GUI.Label(new Rect(box.x + pad, box.y + pad * 0.6f, box.width - 2f * pad, 48f * unit), "Tempo em cada arena", _titleStyle);
            GUI.Label(new Rect(box.x + pad, box.y + 58f * unit, box.width - 2f * pad, 30f * unit),
                "Segundos na tela antes de trocar sozinho. Fica salvo neste PC.   T ou Esc fecha", _bodyStyle);

            var list = new Rect(box.x + pad, box.y + 96f * unit, box.width - 2f * pad, box.height - 176f * unit);
            var content = new Rect(0f, 0f, list.width - 24f * unit, _entries.Count * row);
            _timesScroll = GUI.BeginScrollView(list, _timesScroll, content);
            for (int i = 0; i < _entries.Count; i++)
            {
                float y = i * row;
                string name = $"{_entries[i].version} — {_entries[i].title}";
                GUI.Label(new Rect(0f, y, content.width - 2f * button - 126f * unit, row), name, _rowStyle);
                float x = content.width - 2f * button - 110f * unit;
                if (GUI.Button(new Rect(x, y + 4f * unit, button, row - 8f * unit), "−", _buttonStyle))
                    SetTourTime(i, _tourSeconds[i] - TimeStep);
                GUI.Label(new Rect(x + button, y, 110f * unit, row), $"{_tourSeconds[i]:0.0} s", _centerStyle);
                if (GUI.Button(new Rect(x + button + 110f * unit, y + 4f * unit, button, row - 8f * unit), "+", _buttonStyle))
                    SetTourTime(i, _tourSeconds[i] + TimeStep);
            }

            GUI.EndScrollView();

            float footY = box.yMax - 64f * unit, footW = (box.width - 4f * pad) / 3f, footH = 44f * unit;
            if (GUI.Button(new Rect(box.x + pad, footY, footW, footH), _autoTour ? "Ciclo automático: ligado" : "Ciclo automático: desligado", _buttonStyle))
                _autoTour = !_autoTour;
            if (GUI.Button(new Rect(box.x + 2f * pad + footW, footY, footW, footH), "Voltar ao padrão", _buttonStyle))
                ResetTourTimes();
            if (GUI.Button(new Rect(box.x + 3f * pad + 2f * footW, footY, footW, footH), "Fechar", _buttonStyle))
                ShowTimes(false);
        }

        // Tela cheia por cima do aquecimento (a câmera mostraria as arenas piscando): só barra e porcentagem.
        private void DrawLoading()
        {
            float progress = Mathf.Clamp01((float)_warmIndex / _entries.Count);
            Color previous = GUI.color;
            GUI.color = new Color(0.04f, 0.04f, 0.05f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);

            float width = Screen.width * 0.5f;
            float height = Mathf.Max(6f, Screen.height * 0.018f);
            var bar = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.5f, width, height);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * progress, bar.height), Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(bar.x, bar.y - _centerStyle.fontSize * 2.2f, bar.width, _centerStyle.fontSize * 1.8f),
                $"{Mathf.RoundToInt(progress * 100f)}%", _centerStyle);
        }

        private void EnsureStyles()
        {
            int titleSize = Mathf.RoundToInt(Screen.height / 26f);
            if (_titleStyle != null && _titleStyle.fontSize == titleSize)
                return;

            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
                _panel.Apply();
            }

            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = titleSize, fontStyle = FontStyle.Bold, wordWrap = true };
            _titleStyle.normal.textColor = Color.white;
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height / 42f), wordWrap = true };
            _bodyStyle.normal.textColor = new Color(0.86f, 0.86f, 0.86f);
            _centerStyle = new GUIStyle(_bodyStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _centerStyle.normal.textColor = Color.white;
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = _bodyStyle.fontSize };
            _rowStyle = new GUIStyle(_bodyStyle) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        }
    }
}
