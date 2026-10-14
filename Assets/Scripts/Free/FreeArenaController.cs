using Assets.Scripts.Graph;
using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>O que vale num episódio da v8 (lido do currículo no começo de cada episódio).</summary>
    public struct FreeEpisodeSettings
    {
        // Fração dos PONTOS vistos que encerra o episódio com o bônus de cobertura. > 1 = sem fim por cobertura.
        public float CoverageTarget;

        // Multiplica pontos e portas (1 = exploração pura; 0.3 na caça, para pegar mandar em tudo).
        public float DiscoveryRewardScale;

        // Presa (hider_mode): 0 nenhuma, 1 parada, 2 anda, 3 foge. O enum é o do GraphHider (os .yaml já usam). Os
        // modos 4 (Escondida), 5 (Misto) e 6 (Rápida) do currículo chegam aqui como Foge + HiderHides / HiderFast.
        public GraphHider.Mode HiderMode;

        // A presa fica ESCONDIDA (FreeHider): esconderijo atrás de esconderijo, parada muito tempo, e só foge achada.
        public bool HiderHides;

        // A presa corre MAIS que o monstro (_fastHiderSpeed): na reta ela abre distância; pega-se cortando caminho ou
        // quando o fôlego acaba.
        public bool HiderFast;

        // Há CHAVES neste episódio (key_chance): a presa vai buscá-las e escapa se levar todas (FreeKeyField).
        public bool HasKeys;

        // m/s da presa CORRENDO e segundos de corrida (hider_speed / hider_stamina); 0 = os do FreeHider.
        public float HiderSpeed;
        public float HiderStamina;

        public bool HasHider => HiderMode != GraphHider.Mode.None;
    }

    /// <summary>
    /// Dono do AMBIENTE da v8, um por arena (no objeto raiz da arena), SÓ COM NAVMESH: o <see cref="FreeMap"/>, o
    /// currículo, o spawn, a presa (<see cref="FreeHider"/>) e o alvo do monstro. Não há NavGraph nem nó na arena.
    /// Parâmetro novo de currículo = campo no FreeEpisodeSettings + _defaultX + uma linha no ApplyCurriculum.
    ///
    /// SPAWN: sala sorteada com peso _rareSpawnFloor + (1 - taxa com que ela foi vista nos últimos episódios desta
    /// arena), depois um ponto qualquer dela, virado para qualquer lado. É a correção que levou a v5.1 ao mapa inteiro
    /// (nascer longe das salas que ele pula), ligada desde o step 0. A taxa começa em 1 (peso mínimo para todas).
    ///
    /// MODO DE JOGO (_gameMode): sem currículo (valem a última lição e os padrões abaixo), a presa sai de cena e o
    /// alvo passa a ser o jogador, achado pela tag; o FreeExplorerManager cuida da inferência e do fim.
    /// Nunca guarde estado estático (há várias arenas).
    /// </summary>
    public class FreeArenaController : MonoBehaviour
    {
        [Header("-----Currículo (padrões = sem trainer)-----")]
        // 0.95: o mapa quase inteiro; os 5% deixam folga para ponto em canto que a visão não alcança de lugar nenhum
        // (atrás de armário alto encostado). Meta parcial (0.8) deixou a v4.1 pular sempre as mesmas salas.
        [SerializeField] private float _defaultCoverageTarget = 0.95f;
        [SerializeField] private float _defaultDiscoveryRewardScale = 1f;

        // 0 nenhuma, 1 parada, 2 anda, 3 foge, 4 escondida, 5 misto (sorteia escondida, rápida ou foge por episódio),
        // 6 rápida. 5 = a última lição: Play sem trainer roda o .onnx no ambiente em que ele terminou.
        [SerializeField, Range(0, 6)] private int _defaultHiderMode = 5;

        // m/s da presa RÁPIDA correndo (hider_mode 6, e parte do 5). 12.5 = 1 m/s acima da perseguição do monstro
        // (11.5): com os mesmos 10 s de fôlego ela abre ~10 m na reta, e andando (0.588 x 12.5 = 7.4) é alcançada.
        [SerializeField, Min(0f)] private float _fastHiderSpeed = 12.5f;

        // Lição Misto: chance de cada episódio ter a presa escondida, e a rápida; o resto foge normal.
        [SerializeField, Range(0f, 1f)] private float _mixedHiddenChance = 0.4f;
        [SerializeField, Range(0f, 1f)] private float _mixedFastChance = 0.3f;

        // Chance de um episódio ter chaves (key_chance; 0 = nunca). Sem trainer vale isto: 0.5, a última lição.
        [SerializeField, Range(0f, 1f)] private float _defaultKeyChance = 0.5f;
        [SerializeField, Min(0f)] private float _defaultHiderSpeed = 0f;
        [SerializeField, Min(0f)] private float _defaultHiderStamina = 0f;

        [Header("-----Mundo-----")]
        // Parede para a visão do alvo, a captura e a presa (a mesma máscara da FreeExplorationMemory). Vazia = Reset.
        [SerializeField] private LayerMask _wallLayer;

        // Fallback: GetComponentInChildren (a presa e o chão são filhos da arena).
        [SerializeField] private FreeHider _hider;
        [SerializeField] private Renderer _floorRenderer;

        [Header("-----Modo de jogo-----")]
        // Ligado: jogo de verdade (inferência, sem currículo, sem presa, caça o jogador, sem timeout).
        [SerializeField] private bool _gameMode = false;

        // Tag do jogador. "Goal" é a convenção do projeto (o PlayerDummy já vem com ela).
        [SerializeField] private string _playerTag = "Goal";

        [Header("-----Spawn-----")]
        [SerializeField] private bool _spawnFavorsRareRooms = true;

        // Peso mínimo de uma sala sempre vista: nenhuma sala deixa de ser sorteada.
        [SerializeField, Min(0f)] private float _rareSpawnFloor = 0.2f;

        // Peso do episódio novo na média móvel da taxa de cada sala (~1/20: os últimos ~20 episódios desta arena).
        [SerializeField, Range(0.01f, 1f)] private float _roomRateSmoothing = 0.05f;

        // Modo de jogo: pontos sorteados para nascer longe do jogador (FreeExplorerManager._gameModeSpawnDistance).
        [SerializeField, Min(1)] private int _avoidSpawnTries = 12;

        private FreeMap _map;
        private FreeKeyField _keys;
        private FreePlayerTarget _playerTarget;
        private float[] _roomSeenRate;
        private float[] _spawnWeights;
        private Color _initialFloorColor;
        private bool _floorColorSaved;

        public FreeEpisodeSettings Settings { get; private set; }

        public bool GameMode => _gameMode;

        // Máscara vazia = visão do alvo atravessando parede, e o treino não acusaria nada: cai no padrão do Reset.
        public LayerMask WallLayer => _wallLayer.value != 0 ? _wallLayer : (LayerMask)LayerMask.GetMask("Wall", "Obstacle", "Door");

        /// <summary>Resolvido sob demanda: o agente lê isto no Initialize, que pode vir antes do Awake da arena.</summary>
        public FreeMap Map
        {
            get
            {
                if (_map == null)
                    _map = GetComponentInChildren<FreeMap>();
                return _map;
            }
        }

        /// <summary>As chaves desta arena (criado se faltar; vazio sem chaves no episódio).</summary>
        public FreeKeyField Keys
        {
            get
            {
                if (_keys == null)
                {
                    _keys = GetComponent<FreeKeyField>();
                    if (_keys == null)
                        _keys = gameObject.AddComponent<FreeKeyField>();
                }

                return _keys;
            }
        }

        public FreeHider Hider
        {
            get
            {
                if (_hider == null)
                    _hider = GetComponentInChildren<FreeHider>(true);
                return _hider;
            }
        }

        /// <summary>O alvo do monstro: a presa no treino, o jogador no modo de jogo. Null se não há.</summary>
        public IGraphTarget Target => _gameMode ? PlayerTarget : Hider;

        /// <summary>
        /// O jogador (modo de jogo), achado pela tag entre os objetos ATIVOS; ganha um FreePlayerTarget se não tiver.
        /// </summary>
        public FreePlayerTarget PlayerTarget
        {
            get
            {
                if (_playerTarget != null)
                    return _playerTarget;

                GameObject player = GameObject.FindWithTag(_playerTag);
                if (player == null)
                    return null;

                _playerTarget = player.GetComponent<FreePlayerTarget>();
                if (_playerTarget == null)
                    _playerTarget = player.AddComponent<FreePlayerTarget>();
                return _playerTarget;
            }
        }

#if UNITY_EDITOR
        private void Reset() => _wallLayer = LayerMask.GetMask("Wall", "Obstacle", "Door");

        private NoiseRadiusPreview _keyNoisePreview;

        // Com a arena selecionada, SEM Play: o barulho de pegar a chave no ponto de chave (KeySpots) mais perto do
        // centro da aba Scene (são ~200 pontos; todos juntos virariam borrão). Ande com a câmera para ver outro.
        private void OnDrawGizmosSelected()
        {
            Transform spots = transform.Find("KeySpots");
            UnityEditor.SceneView view = UnityEditor.SceneView.lastActiveSceneView;
            if (spots == null || spots.childCount == 0 || view == null)
                return;

            Transform nearest = null;
            float best = float.MaxValue;
            foreach (Transform spot in spots)
            {
                float distance = (spot.position - view.pivot).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    nearest = spot;
                }
            }

            FreeKeyField keys = GetComponent<FreeKeyField>();
            float radius = keys != null ? keys.PickupNoiseRadius : FreeKeyField.DefaultPickupNoiseRadius;
            _keyNoisePreview ??= new NoiseRadiusPreview();
            _keyNoisePreview.Draw(nearest.position, radius, new Color(0.65f, 0.45f, 1f, 0.9f), $"chave ({nearest.name})");
        }
#endif

        private void Awake() => SaveFloorColor();

        // Antes do primeiro ResetEpisode, que pode chegar antes do Awake.
        private void SaveFloorColor()
        {
            if (_floorColorSaved)
                return;

            _floorColorSaved = true;
            if (_floorRenderer != null)
                _initialFloorColor = _floorRenderer.material.color;
        }

        public void ResetEpisode()
        {
            SaveFloorColor();
            Settings = ApplyCurriculum();
            if (_floorRenderer != null)
                _floorRenderer.material.color = _initialFloorColor;
        }

        /// <summary>Posiciona (ou desliga) a presa. Chamar DEPOIS do spawn do monstro: ela nasce longe dele.</summary>
        public void ResetHider(Transform seeker)
        {
            // Chaves antes da presa: ela sai para buscá-las. No jogo, as do KeySpawner da cena (se houver).
            Keys.Configure(Map, WallLayer, _gameMode);
            Keys.ResetEpisode(Settings.HasKeys);

            FreeHider hider = Hider;
            if (hider == null)
                return;

            if (_gameMode)
            {
                hider.Deactivate();
                return;
            }

            hider.Configure(Map, WallLayer, Keys);
            hider.ResetEpisode(Settings, seeker);
        }

        public void ShowOutcome(bool success)
        {
            if (_floorRenderer != null)
                _floorRenderer.material.color = success ? Color.green : Color.red;
        }

        /// <summary>
        /// Ponto de spawn no chão e uma rotação aleatória; false se o mapa está vazio. Com <paramref name="avoid"/> (o
        /// jogador, no modo de jogo), sorteia até _avoidSpawnTries pontos e fica com o primeiro a pelo menos
        /// <paramref name="minDistance"/> m dele, ou com o mais longe.
        /// </summary>
        public bool TryGetSpawn(out Vector3 ground, out Quaternion rotation, Vector3? avoid = null, float minDistance = 0f)
        {
            ground = Vector3.zero;
            rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            FreeMap map = Map;
            if (map == null || map.RoomCount == 0)
                return false;

            EnsureRoomStats();
            float total = 0f;
            for (int r = 0; r < _spawnWeights.Length; r++)
            {
                _spawnWeights[r] = _spawnFavorsRareRooms ? _rareSpawnFloor + (1f - _roomSeenRate[r]) : 1f;
                total += _spawnWeights[r];
            }

            if (!avoid.HasValue)
                return TryPickSpawnPoint(map, total, out ground);

            float best = -1f;
            for (int attempt = 0; attempt < _avoidSpawnTries; attempt++)
            {
                if (!TryPickSpawnPoint(map, total, out Vector3 candidate))
                    continue;

                float distance = new Vector2(candidate.x - avoid.Value.x, candidate.z - avoid.Value.z).magnitude;
                if (distance > best)
                {
                    best = distance;
                    ground = candidate;
                }

                if (distance >= minDistance)
                    break;
            }

            return best >= 0f;
        }

        // Sala pelo peso do spawn, depois um ponto qualquer dela.
        private bool TryPickSpawnPoint(FreeMap map, float total, out Vector3 ground)
        {
            ground = Vector3.zero;
            int room = 0;
            float pick = Random.value * total;
            for (int r = 0; r < _spawnWeights.Length; r++)
            {
                pick -= _spawnWeights[r];
                if (pick <= 0f)
                {
                    room = r;
                    break;
                }
            }

            var points = map.RoomPoints(room);
            if (points.Count == 0)
                return false;

            ground = map.PointGround(points[Random.Range(0, points.Count)]);
            return true;
        }

        /// <summary>Fim de episódio: soma na média móvel quais salas foram vistas (o peso do spawn).</summary>
        public void RecordRoomOutcome(FreeExplorationMemory memory)
        {
            EnsureRoomStats();
            for (int r = 0; r < _roomSeenRate.Length; r++)
                _roomSeenRate[r] = Mathf.Lerp(_roomSeenRate[r], memory.IsRoomSeen(r) ? 1f : 0f, _roomRateSmoothing);
        }

        private void EnsureRoomStats()
        {
            int rooms = Map.RoomCount;
            if (_roomSeenRate != null && _roomSeenRate.Length == rooms)
                return;

            // Começa em 1 ("sempre vista" = peso mínimo), não em 0.5: a taxa vive só na memória do build, e com 0.5
            // todo --resume puxava o spawn para salas aleatórias nos primeiros episódios.
            _roomSeenRate = new float[rooms];
            _spawnWeights = new float[rooms];
            for (int r = 0; r < rooms; r++)
                _roomSeenRate[r] = 1f;
        }

        private FreeEpisodeSettings ApplyCurriculum()
        {
            // Modo de jogo não lê o trainer. A cobertura nunca encerra e há sempre "presa" (o jogador, que foge): vale
            // a última lição (HiderFoge, meta 1.1). A meta entra na observação [13] (pontos vistos / meta): com o
            // padrão 0.95 a rede veria outra escala.
            EnvironmentParameters parameters = Academy.IsInitialized && !_gameMode ? Academy.Instance.EnvironmentParameters : null;
            float Get(string key, float fallback) => parameters != null ? parameters.GetWithDefault(key, fallback) : fallback;
            float coverage = Mathf.Max(0.05f, Get("coverage_target", _defaultCoverageTarget));
            int lesson = Mathf.Clamp(Mathf.RoundToInt(Get("hider_mode", _defaultHiderMode)), 0, 6);

            // 4, 5 e 6 são a presa que foge: escondida (4), rápida (6) ou sorteada por episódio (5, a última lição): no
            // jogo o jogador às vezes corre, às vezes fica agachado num canto, e o monstro tem de saber caçar os dois.
            float roll = Random.value;
            bool hides = lesson == 4 || (lesson == 5 && roll < _mixedHiddenChance);
            bool fast = lesson == 6 || (lesson == 5 && !hides && roll < _mixedHiddenChance + _mixedFastChance);
            var mode = lesson >= 4 ? GraphHider.Mode.Flee : (GraphHider.Mode)lesson;
            float speed = Mathf.Max(0f, Get("hider_speed", _defaultHiderSpeed));

            // Treino sem key_chance no .yaml (os da v8.0) = sem chaves; o padrão do Inspector vale só sem trainer.
            float keyFallback = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn ? 0f : _defaultKeyChance;

            return new FreeEpisodeSettings
            {
                CoverageTarget = _gameMode ? Mathf.Max(coverage, 1.1f) : coverage,
                DiscoveryRewardScale = Mathf.Max(0f, Get("discovery_reward_scale", _defaultDiscoveryRewardScale)),
                HiderMode = _gameMode ? GraphHider.Mode.Flee : mode,
                HiderHides = !_gameMode && hides,
                HiderFast = !_gameMode && fast,
                HasKeys = !_gameMode && mode != GraphHider.Mode.None && Random.value < Mathf.Clamp01(Get("key_chance", keyFallback)),
                HiderSpeed = !_gameMode && fast ? _fastHiderSpeed : speed,
                HiderStamina = Mathf.Max(0f, Get("hider_stamina", _defaultHiderStamina)),
            };
        }
    }
}
