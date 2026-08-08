using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Dono do ambiente, não do agente: spawn do hider e do seeker, feedback visual do resultado
    /// e leitura do currículo. Existe um por arena — quando as arenas forem duplicadas para treinar
    /// em paralelo, cada cópia carrega o próprio estado.
    /// </summary>
    public class SeekerArenaController : MonoBehaviour
    {
        [Header("-----Referências-----")]
        [SerializeField] private HiderAgent _hider;
        [SerializeField] private Renderer _floorRenderer;
        [SerializeField] private Transform[] _seekerSpawnPoints;

        [Header("-----Currículo-----")]
        [SerializeField] private GameObject _maze;
        [SerializeField] private string _mazeParameterName = "maze_enabled";

        [Header("-----Feedback-----")]
        // Só para debug visual: durante o delay o agente segue recebendo decisões que não viram
        // experiência útil. Mantenha em 0 para treinar.
        [SerializeField] private float _episodeEndDelay = 0f;

        private Color _initialFloorColor;

        public float EpisodeEndDelay => _episodeEndDelay;

        private void Awake()
        {
            if (_floorRenderer != null)
                _initialFloorColor = _floorRenderer.material.color;
        }

        public void ResetEpisode()
        {
            ApplyCurriculum();

            if (_floorRenderer != null)
                _floorRenderer.material.color = _initialFloorColor;

            if (_hider != null)
                _hider.Spawn();
        }

        public void ShowOutcome(bool won)
        {
            if (_floorRenderer != null)
                _floorRenderer.material.color = won ? Color.green : Color.red;
        }

        /// <summary>
        /// Devolve um spawn aleatório para o seeker. Retorna false quando nenhum ponto foi
        /// configurado — nesse caso o agente volta para a pose inicial que ele mesmo guardou.
        /// </summary>
        public bool TryGetSeekerSpawn(out Vector3 position, out Quaternion rotation)
        {
            if (_seekerSpawnPoints == null || _seekerSpawnPoints.Length == 0)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            Transform point = _seekerSpawnPoints[Random.Range(0, _seekerSpawnPoints.Length)];
            position = point.position;
            rotation = point.rotation;
            return true;
        }

        // Liga/desliga o labirinto conforme a lição atual do curriculum (config/seeker_curriculum.yaml).
        private void ApplyCurriculum()
        {
            if (_maze == null)
                return;

            float mazeEnabled = Academy.Instance.EnvironmentParameters.GetWithDefault(_mazeParameterName, 0f);
            _maze.SetActive(mazeEnabled > 0.5f);
        }
    }
}
