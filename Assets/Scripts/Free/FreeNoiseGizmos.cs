using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// DEBUG (v8.2): mostra os barulhos (<see cref="NoiseEvents"/>) na cena enquanto o Play roda no Editor. Nasce
    /// sozinho no Play (nada para pôr na cena) e só desenha gizmo: aparece na aba Scene e, com o botão Gizmos ligado,
    /// na Game.
    ///   ONDA (lilás)        o barulho: uma onda no chão que cresce até o raio dele e some em _showSeconds. O raio é
    ///                       PELO NAVMESH, então o círculo é o teto: atrás de parede o caminho pela porta é mais longo.
    ///   LINHA (vermelho)    um monstro OUVIU: do monstro ao barulho, com a distância pelo caminho. Sem linha = ninguém
    ///                       ouviu (longe demais pelo caminho, ou o monstro estava vendo o alvo e ignorou).
    /// Fora do Editor não faz nada.
    /// </summary>
    public class FreeNoiseGizmos : MonoBehaviour
    {
        private struct Noise
        {
            public Vector3 Position;
            public float Radius;
            public float Time;
        }

        private struct Heard
        {
            public Vector3 Listener;
            public Vector3 Source;
            public float PathLength;
            public float Time;
        }

        // Quanto tempo (s) cada barulho e cada "ouviu" ficam desenhados.
        private const float ShowSeconds = 2.5f;

        // A onda chega ao raio neste tempo (s) e fica lá até sumir.
        private const float GrowSeconds = 0.4f;

        private static FreeNoiseGizmos _instance;

        private readonly List<Noise> _noises = new List<Noise>();
        private readonly List<Heard> _heard = new List<Heard>();

        /// <summary>Chamado pelo FreeTrackSense quando um monstro ouve um barulho (só desenha no Editor).</summary>
        public static void ReportHeard(Vector3 listener, Vector3 source, float pathLength)
        {
            if (_instance != null)
                _instance._heard.Add(new Heard { Listener = listener, Source = source, PathLength = pathLength, Time = Time.time });
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateInPlay()
        {
            if (_instance != null)
                return;

            var host = new GameObject("[Debug] Barulhos (FreeNoiseGizmos)");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<FreeNoiseGizmos>();
        }

        private void OnEnable() => NoiseEvents.Emitted += OnNoise;

        private void OnDisable() => NoiseEvents.Emitted -= OnNoise;

        private void OnNoise(Vector3 position, float radius) =>
            _noises.Add(new Noise { Position = position, Radius = radius, Time = Time.time });

        private void Update()
        {
            float now = Time.time;
            _noises.RemoveAll(n => now - n.Time > ShowSeconds);
            _heard.RemoveAll(h => now - h.Time > ShowSeconds);
        }

        private void OnDrawGizmos()
        {
            float now = Time.time;
            foreach (Noise noise in _noises)
            {
                float age = now - noise.Time;
                float fade = 1f - age / ShowSeconds;
                float radius = noise.Radius * Mathf.Clamp01(age / GrowSeconds);
                var color = new Color(0.65f, 0.45f, 1f, fade);

                UnityEditor.Handles.color = color;
                UnityEditor.Handles.DrawWireDisc(noise.Position + Vector3.up * 0.05f, Vector3.up, radius, 3f);
                Gizmos.color = color;
                Gizmos.DrawSphere(noise.Position + Vector3.up * 0.3f, 0.3f);
                UnityEditor.Handles.Label(noise.Position + Vector3.up * 1.5f, $"barulho {noise.Radius:0} m");
            }

            foreach (Heard heard in _heard)
            {
                float fade = 1f - (now - heard.Time) / ShowSeconds;
                UnityEditor.Handles.color = new Color(1f, 0.3f, 0.3f, fade);
                UnityEditor.Handles.DrawLine(heard.Listener + Vector3.up * 2f, heard.Source + Vector3.up * 0.3f, 3f);
                UnityEditor.Handles.Label(heard.Listener + Vector3.up * 3.2f, $"OUVIU ({heard.PathLength:0} m pelo caminho)");
            }
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// PRÉVIA do raio de um barulho, SEM Play (o gizmo de quem faz barulho chama isto quando selecionado): o círculo é o
    /// raio em linha reta (o máximo); os PONTOS são onde o monstro ouviria de verdade, com o caminho pelo NavMesh até a
    /// fonte cabendo no raio (lilás). Cinza = dentro do círculo mas longe demais pelo caminho (atrás de parede).
    /// Precisa do NavMesh carregado (cena aberta com o NavMeshSurface ligado, ou o Play). Recalcula só quando a fonte
    /// anda mais de meio metro, e no máximo a cada 0.3 s (arrastar o objeto não trava o editor).
    /// </summary>
    public class NoiseRadiusPreview
    {
        private const float CellSize = 2f;
        private const float RecomputeMove = 0.5f;
        private const double RecomputeInterval = 0.3;

        private readonly List<Vector3> _heard = new List<Vector3>();
        private readonly List<Vector3> _blocked = new List<Vector3>();
        private readonly UnityEngine.AI.NavMeshPath _path = new UnityEngine.AI.NavMeshPath();
        private Vector3 _lastSource = new Vector3(float.NaN, 0f, 0f);
        private float _lastRadius = -1f;
        private double _lastTime;

        public void Draw(Vector3 source, float radius, Color color, string label)
        {
            if (radius <= 0f)
                return;

            Refresh(source, radius);
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.DrawWireDisc(source + Vector3.up * 0.05f, Vector3.up, radius, 2f);
            UnityEditor.Handles.Label(source + Vector3.up * 2f + Vector3.forward * radius, $"{label} {radius:0} m");

            Gizmos.color = color;
            foreach (Vector3 point in _heard)
                Gizmos.DrawCube(point + Vector3.up * 0.1f, new Vector3(0.5f, 0.05f, 0.5f));
            Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            foreach (Vector3 point in _blocked)
                Gizmos.DrawCube(point + Vector3.up * 0.1f, new Vector3(0.3f, 0.05f, 0.3f));
        }

        private void Refresh(Vector3 source, float radius)
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            bool moved = float.IsNaN(_lastSource.x) || (source - _lastSource).sqrMagnitude > RecomputeMove * RecomputeMove;
            if ((!moved && Mathf.Approximately(radius, _lastRadius)) || now - _lastTime < RecomputeInterval)
                return;

            _lastSource = source;
            _lastRadius = radius;
            _lastTime = now;
            _heard.Clear();
            _blocked.Clear();
            if (!UnityEngine.AI.NavMesh.SamplePosition(source, out UnityEngine.AI.NavMeshHit from, 3f, UnityEngine.AI.NavMesh.AllAreas))
                return;

            int steps = Mathf.CeilToInt(radius / CellSize);
            for (int x = -steps; x <= steps; x++)
            {
                for (int z = -steps; z <= steps; z++)
                {
                    var offset = new Vector3(x * CellSize, 0f, z * CellSize);
                    if (offset.magnitude > radius)
                        continue;

                    if (!UnityEngine.AI.NavMesh.SamplePosition(from.position + offset, out UnityEngine.AI.NavMeshHit cell, CellSize * 0.5f, UnityEngine.AI.NavMesh.AllAreas)
                        || Mathf.Abs(cell.position.y - from.position.y) > 1.5f)
                        continue;

                    bool heard = UnityEngine.AI.NavMesh.CalculatePath(from.position, cell.position, UnityEngine.AI.NavMesh.AllAreas, _path)
                                 && _path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete && PathLength(_path) <= radius;
                    (heard ? _heard : _blocked).Add(cell.position);
                }
            }
        }

        private static float PathLength(UnityEngine.AI.NavMeshPath path)
        {
            Vector3[] corners = path.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }
    }
#endif
}
