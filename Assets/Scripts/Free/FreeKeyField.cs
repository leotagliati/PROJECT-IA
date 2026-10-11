using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// As CHAVES de uma arena da v8 (lições finais; o jogo pode não ter chaves, e aí isto fica vazio). Um por arena,
    /// criado pelo <see cref="FreeArenaController"/> se faltar.
    ///   TREINO  a cada episódio com chaves, _keyCount pontos em salas diferentes, de preferência colados em parede
    ///           (canto, atrás de móvel). A presa (<see cref="FreeHider"/>) vai buscá-las; pegar todas = ESCAPOU.
    ///   JOGO    as chaves de verdade do <see cref="KeySpawner"/> da cena; pegar = a chave some (KeyItem.Destroy).
    ///   AVISO   o monstro sempre sabe ONDE estão as chaves, mas só fica sabendo que uma foi pega _noticeDelaySeconds
    ///           depois (Known). Aí vem o alarme (observação, Alerta no monstro) e a chave fica marcada como levada.
    ///   RONDA   o monstro "confere" uma chave quando a vê (cone, alcance, linha livre); conferir uma que ficou
    ///           _checkIntervalSeconds sem ser vista é o que a recompensa paga (guarda, FreeRewardSystem).
    ///   CAMPAR  ficar a menos de _campRadius da MESMA chave por mais de _campSeconds seguidos (FreeRewardSystem cobra).
    /// </summary>
    public class FreeKeyField : MonoBehaviour
    {
        public struct Key
        {
            public Vector3 Position;
            public bool Taken;
            public float TakenTime;
            public bool Known;
            public float KnownTime;
            public float LastChecked;
            public GameObject GameKey;
        }

        [Header("-----Treino-----")]
        [SerializeField, Min(1)] private int _keyCount = 3;

        // Pontos sorteados por chave; fica o mais colado em parede (o lugar em que se esconde uma chave).
        [SerializeField, Min(1)] private int _spotCandidates = 6;

        [Header("-----Aviso, ronda e campar-----")]
        // Segundos entre a presa pegar a chave e o monstro saber (alarme): ele chega tarde e tem de seguir a pista.
        [SerializeField, Min(0f)] private float _noticeDelaySeconds = 3f;

        // Segundos que o alarme dura na observação (1 -> 0).
        [SerializeField, Min(0.1f)] private float _alarmSeconds = 15f;

        // Chave sem ser vista há mais disto (s) vale uma "conferida" (a ronda que a recompensa paga).
        [SerializeField, Min(1f)] private float _checkIntervalSeconds = 60f;

        // Até onde (m) o monstro confere uma chave com os olhos (a visão dos pontos da FreeExplorationMemory).
        [SerializeField, Min(1f)] private float _checkDistance = 18f;

        // Campar: a menos disto (m) da mesma chave, por mais disto (s) seguidos.
        [SerializeField, Min(0f)] private float _campRadius = 8f;
        [SerializeField, Min(0f)] private float _campSeconds = 15f;

        private readonly List<Key> _keys = new List<Key>();
        private FreeMap _map;
        private LayerMask _walls;
        private bool _gameMode;
        private KeySpawner _spawner;
        private bool _spawnerSearched;
        private float _alarmTime = float.NegativeInfinity;
        private int _campKey = -1;
        private float _campSince;

        public int Count => _keys.Count;
        public Key Get(int key) => _keys[key];

        public float CheckIntervalSeconds => _checkIntervalSeconds;
        public float AlarmSeconds => _alarmSeconds;

        /// <summary>Chaves ainda no mapa (de verdade, não o que o monstro sabe).</summary>
        public int Remaining
        {
            get
            {
                int left = 0;
                foreach (Key key in _keys)
                {
                    if (!key.Taken)
                        left++;
                }

                return left;
            }
        }

        /// <summary>Chaves que o monstro já sabe que foram levadas.</summary>
        public int KnownTaken
        {
            get
            {
                int known = 0;
                foreach (Key key in _keys)
                {
                    if (key.Known)
                        known++;
                }

                return known;
            }
        }

        /// <summary>A presa levou todas e o monstro já soube da última: escapou (encerra o episódio no treino).</summary>
        public bool Escaped => _keys.Count > 0 && KnownTaken == _keys.Count;

        /// <summary>Alarme na observação: 1 quando o monstro acaba de saber de uma chave levada, 0 depois de _alarmSeconds.</summary>
        public float Alarm => Mathf.Clamp01(1f - (Time.time - _alarmTime) / _alarmSeconds);

        /// <summary>Chaves que o monstro passou a saber levadas desde o último ClearStepFlags.</summary>
        public int NewlyKnownThisStep { get; private set; }

        /// <summary>Conferidas de chave parada há mais de _checkIntervalSeconds desde o último ClearStepFlags.</summary>
        public int ChecksThisStep { get; private set; }

        /// <summary>Campando agora (perto da mesma chave há mais de _campSeconds).</summary>
        public bool IsCamping { get; private set; }

        // Métricas do episódio.
        public int EpisodeChecks { get; private set; }
        public int EpisodeCampSteps { get; private set; }

        public void Configure(FreeMap map, LayerMask walls, bool gameMode)
        {
            _map = map;
            _walls = walls;
            _gameMode = gameMode;
        }

        /// <summary>Novo episódio: <paramref name="enabled"/> = há chaves neste episódio (no jogo, as do KeySpawner).</summary>
        public void ResetEpisode(bool enabled)
        {
            _keys.Clear();
            _alarmTime = float.NegativeInfinity;
            _campKey = -1;
            IsCamping = false;
            EpisodeChecks = 0;
            EpisodeCampSteps = 0;
            ClearStepFlags();

            if (_gameMode)
            {
                BindGameKeys();
                return;
            }

            if (!enabled || _map == null || _map.RoomCount == 0)
                return;

            var usedRooms = new HashSet<int>();
            for (int attempt = 0; attempt < _map.RoomCount * 3 && _keys.Count < _keyCount; attempt++)
            {
                int room = Random.Range(0, _map.RoomCount);
                if (!usedRooms.Add(room) && usedRooms.Count < _map.RoomCount)
                    continue;

                _keys.Add(new Key { Position = KeySpot(room), LastChecked = float.NegativeInfinity });
            }
        }

        public void ClearStepFlags()
        {
            NewlyKnownThisStep = 0;
            ChecksThisStep = 0;
        }

        /// <summary>
        /// Um step de física, com o olho e o olhar do monstro: aviso atrasado, chaves do jogo que sumiram, ronda e
        /// campar.
        /// </summary>
        public void Tick(Vector3 seekerGround, Vector3 eye, Vector3 view, float cosHalfAngle)
        {
            float now = Time.time;

            // No jogo o KeySpawner sorteia as chaves no Start dele, que pode vir depois do começo do episódio.
            if (_gameMode && _keys.Count == 0 && (_spawner != null || !_spawnerSearched))
                BindGameKeys();

            for (int k = 0; k < _keys.Count; k++)
            {
                Key key = _keys[k];

                // No jogo, a chave pega some da cena (KeyItem destrói o objeto).
                if (_gameMode && !key.Taken && key.GameKey == null)
                {
                    key.Taken = true;
                    key.TakenTime = now;
                }

                if (key.Taken && !key.Known && now - key.TakenTime >= _noticeDelaySeconds)
                {
                    key.Known = true;
                    key.KnownTime = now;
                    _alarmTime = now;
                    NewlyKnownThisStep++;
                }

                if (!key.Known && Sees(eye, view, cosHalfAngle, key.Position))
                {
                    if (now - key.LastChecked > _checkIntervalSeconds)
                    {
                        ChecksThisStep++;
                        EpisodeChecks++;
                    }

                    key.LastChecked = now;
                }

                _keys[k] = key;
            }

            UpdateCamping(seekerGround, now);
        }

        /// <summary>A presa pega a chave a até <paramref name="radius"/> m dela (treino). Devolve se pegou alguma.</summary>
        public bool TryTake(Vector3 hiderGround, float radius)
        {
            for (int k = 0; k < _keys.Count; k++)
            {
                Key key = _keys[k];
                if (key.Taken)
                    continue;

                Vector3 delta = key.Position - hiderGround;
                if (new Vector2(delta.x, delta.z).magnitude > radius)
                    continue;

                key.Taken = true;
                key.TakenTime = Time.time;
                _keys[k] = key;
                return true;
            }

            return false;
        }

        /// <summary>A chave ainda no mapa mais perto pelo caminho do NavMesh (a presa vai nela); false se não há.</summary>
        public bool TryNearestRemaining(Vector3 from, NavMeshPath path, out Vector3 position)
        {
            position = from;
            float best = float.MaxValue;
            foreach (Key key in _keys)
            {
                if (key.Taken || !NavMesh.CalculatePath(from, key.Position, NavMesh.AllAreas, path)
                    || path.status != NavMeshPathStatus.PathComplete)
                    continue;

                float length = 0f;
                Vector3[] corners = path.corners;
                for (int i = 1; i < corners.Length; i++)
                    length += Vector3.Distance(corners[i - 1], corners[i]);

                if (length < best)
                {
                    best = length;
                    position = key.Position;
                }
            }

            return best < float.MaxValue;
        }

        // Campar: perto da MESMA chave ainda no mapa por mais de _campSeconds seguidos. Trocar de chave ou se afastar zera.
        private void UpdateCamping(Vector3 seekerGround, float now)
        {
            int near = -1;
            float nearest = _campRadius;
            for (int k = 0; k < _keys.Count; k++)
            {
                if (_keys[k].Taken)
                    continue;

                Vector3 delta = _keys[k].Position - seekerGround;
                float distance = new Vector2(delta.x, delta.z).magnitude;
                if (distance <= nearest)
                {
                    nearest = distance;
                    near = k;
                }
            }

            if (near != _campKey)
            {
                _campKey = near;
                _campSince = now;
            }

            IsCamping = near >= 0 && now - _campSince > _campSeconds;
            if (IsCamping)
                EpisodeCampSteps++;
        }

        private bool Sees(Vector3 eye, Vector3 view, float cosHalfAngle, Vector3 position)
        {
            Vector3 target = position + Vector3.up * 0.5f;
            var planar = new Vector2(target.x - eye.x, target.z - eye.z);
            float distance = planar.magnitude;
            if (distance > _checkDistance)
                return false;

            var forward = new Vector2(view.x, view.z);
            if (distance > 1f && forward.sqrMagnitude > 1e-6f && Vector2.Dot(planar / distance, forward.normalized) < cosHalfAngle)
                return false;

            return !Physics.Linecast(eye, target, _walls, QueryTriggerInteraction.Ignore);
        }

        // Ponto de chave numa sala: entre _spotCandidates pontos do chão dela, o mais colado em parede.
        private Vector3 KeySpot(int room)
        {
            IReadOnlyList<int> points = _map.RoomPoints(room);
            Vector3 best = _map.PointGround(points[Random.Range(0, points.Count)]);
            float bestEdge = float.MaxValue;
            for (int k = 0; k < _spotCandidates; k++)
            {
                Vector3 grid = _map.PointGround(points[Random.Range(0, points.Count)]);
                Vector3 probe = grid + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f));
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                    continue;

                float edge = NavMesh.FindClosestEdge(hit.position, out NavMeshHit edgeHit, NavMesh.AllAreas) ? edgeHit.distance : 5f;
                if (edge < bestEdge)
                {
                    bestEdge = edge;
                    best = hit.position;
                }
            }

            return best;
        }

        // Jogo: as chaves que o KeySpawner da cena instanciou (sem KeySpawner, o jogo não tem chaves).
        private void BindGameKeys()
        {
            if (_spawner == null && !_spawnerSearched)
            {
                _spawnerSearched = true;
                _spawner = FindFirstObjectByType<KeySpawner>();
            }

            if (_spawner == null)
                return;

            foreach (GameObject gameKey in _spawner.SpawnedKeys)
            {
                if (gameKey == null)
                    continue;

                Vector3 ground = NavMesh.SamplePosition(gameKey.transform.position, out NavMeshHit hit, 2f, NavMesh.AllAreas)
                    ? hit.position : gameKey.transform.position;
                _keys.Add(new Key { Position = ground, GameKey = gameKey, LastChecked = float.NegativeInfinity });
            }
        }

        // Gizmo amarelo: chave no mapa (cheia) ou levada (arame; mais escura antes de o monstro saber).
        private void OnDrawGizmos()
        {
            foreach (Key key in _keys)
            {
                Gizmos.color = key.Taken ? (key.Known ? new Color(0.6f, 0.5f, 0.1f, 0.6f) : new Color(0.9f, 0.6f, 0.1f, 0.9f)) : new Color(1f, 0.85f, 0.1f, 1f);
                if (key.Taken)
                    Gizmos.DrawWireSphere(key.Position + Vector3.up * 0.5f, 0.4f);
                else
                    Gizmos.DrawSphere(key.Position + Vector3.up * 0.5f, 0.4f);
            }
        }
    }
}
