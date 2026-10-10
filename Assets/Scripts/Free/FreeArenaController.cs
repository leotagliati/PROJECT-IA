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

        // Há presa neste episódio (hider_mode > 0, lido pelo GraphArenaController da arena).
        public bool HasHider;
    }

    /// <summary>
    /// Dono do AMBIENTE da v8, um por arena (no objeto raiz da arena): o <see cref="FreeMap"/>, o currículo e o
    /// spawn. Parâmetro novo de currículo = campo no FreeEpisodeSettings + _defaultX + uma linha no ApplyCurriculum.
    ///
    /// PRESA: quem cuida do hider é o <see cref="GraphArenaController"/> que já está na arena (o da v5), sem mudança:
    /// ele lê hider_mode / hider_speed / hider_stamina do mesmo currículo, posiciona o GraphHider longe do monstro e o
    /// faz fugir. Esta arena só repassa (ResetEpisode, ResetHider, ShowOutcome).
    ///
    /// SPAWN: sala sorteada com peso _rareSpawnFloor + (1 - taxa com que ela foi vista nos últimos episódios desta
    /// arena), depois um ponto qualquer dela, virado para qualquer lado. É a correção que levou a v5.1 ao mapa inteiro
    /// (nascer longe das salas que ele pula), ligada desde o step 0. A taxa começa em 1 (peso mínimo para todas).
    /// </summary>
    public class FreeArenaController : MonoBehaviour
    {
        [Header("-----Currículo (padrões = sem trainer)-----")]
        // 0.95: o mapa quase inteiro; os 5% deixam folga para ponto em canto que a visão não alcança de lugar nenhum
        // (atrás de armário alto encostado). Meta parcial (0.8) deixou a v4.1 pular sempre as mesmas salas.
        [SerializeField] private float _defaultCoverageTarget = 0.95f;
        [SerializeField] private float _defaultDiscoveryRewardScale = 1f;

        [Header("-----Spawn-----")]
        [SerializeField] private bool _spawnFavorsRareRooms = true;

        // Peso mínimo de uma sala sempre vista: nenhuma sala deixa de ser sorteada.
        [SerializeField, Min(0f)] private float _rareSpawnFloor = 0.2f;

        // Peso do episódio novo na média móvel da taxa de cada sala (~1/20: os últimos ~20 episódios desta arena).
        [SerializeField, Range(0.01f, 1f)] private float _roomRateSmoothing = 0.05f;

        private FreeMap _map;
        private GraphArenaController _graphArena;
        private float[] _roomSeenRate;
        private float[] _spawnWeights;

        public FreeEpisodeSettings Settings { get; private set; }

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

        /// <summary>O controlador da v5 na arena: presa, NavGraph (a visão do alvo usa a máscara de parede dele).</summary>
        public GraphArenaController GraphArena
        {
            get
            {
                if (_graphArena == null)
                    _graphArena = GetComponentInChildren<GraphArenaController>(true);
                return _graphArena;
            }
        }

        public void ResetEpisode()
        {
            if (GraphArena != null)
                GraphArena.ResetEpisode();

            Settings = ApplyCurriculum();
        }

        /// <summary>Posiciona (ou desliga) a presa. Chamar DEPOIS do spawn do monstro: ela nasce longe dele.</summary>
        public void ResetHider(Transform seeker)
        {
            if (GraphArena != null)
                GraphArena.ResetHider(seeker);
        }

        public void ShowOutcome(bool success)
        {
            if (GraphArena != null)
                GraphArena.ShowOutcome(success);
        }

        /// <summary>Ponto de spawn no chão e uma rotação aleatória; false se o mapa está vazio.</summary>
        public bool TryGetSpawn(out Vector3 ground, out Quaternion rotation)
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
            EnvironmentParameters parameters = Academy.IsInitialized ? Academy.Instance.EnvironmentParameters : null;
            float Get(string key, float fallback) => parameters != null ? parameters.GetWithDefault(key, fallback) : fallback;

            return new FreeEpisodeSettings
            {
                CoverageTarget = Mathf.Max(0.05f, Get("coverage_target", _defaultCoverageTarget)),
                DiscoveryRewardScale = Mathf.Max(0f, Get("discovery_reward_scale", _defaultDiscoveryRewardScale)),

                // hider_mode é lido pelo GraphArenaController (o dono do hider); aqui só se há presa.
                HasHider = GraphArena != null && GraphArena.Settings.HasHider,
            };
        }

#if UNITY_EDITOR
        // Monta a arena para a v8 (Prefab Mode da cópia do prefab de treino, com Undo): põe o FreeMap aqui e troca o
        // monstro da v5 por uma CÓPIA dele com o cérebro da v8 (o original fica desligado). A presa fica como está.
        // Rodar de novo não duplica: se já há um FreeExplorerManager, só confere o resto.
        [ContextMenu("v8: montar arena livre")]
        private void SetUpArena()
        {
            UnityEditor.Undo.SetCurrentGroupName("Montar arena v8");
            int undoGroup = UnityEditor.Undo.GetCurrentGroup();
            string result = SetUpForV8();
            UnityEditor.Undo.CollapseUndoOperations(undoGroup);
            Debug.Log($"{name}: {result} Confira o mapa no FreeMap ⋮ \"Gerar e relatar (v8)\" e salve o prefab.", this);
        }

        /// <summary>A montagem em si (também usada pelo FreeV8Setup em batchmode). Devolve um resumo para o Console.</summary>
        public string SetUpForV8()
        {
            if (GetComponentInChildren<FreeMap>(true) == null)
                UnityEditor.Undo.AddComponent<FreeMap>(gameObject);

            FreeExplorerManager agent = GetComponentInChildren<FreeExplorerManager>(true);
            if (agent != null)
                return $"arena v8 já montada (agente '{agent.name}').";

            GraphExplorerManager original = null;
            foreach (GraphExplorerManager candidate in GetComponentsInChildren<GraphExplorerManager>(true))
            {
                if (original == null || candidate.gameObject.activeSelf)
                    original = candidate;
            }

            if (original == null)
                return "ERRO: nenhum monstro da v5 (GraphExplorerManager) na arena para copiar.";

            LayerMask walls = GraphArena != null && GraphArena.Graph != null
                ? GraphArena.Graph.WallLayer
                : (LayerMask)LayerMask.GetMask("Wall", "Obstacle", "Door");
            agent = FreeExplorerManager.ConvertFromV5(original, walls);
            return $"arena v8 montada: agente '{agent.name}' (cópia de '{original.name}', que ficou desligado).";
        }
#endif
    }
}
