using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Sorteia <see cref="keysToSpawn"/> pontos distintos entre os filhos marcados e instancia uma
/// chave em cada. Os pontos são Transforms na cena (e não coordenadas no código) para que
/// mover um ponto, ou reaproveitar o spawner em outro mapa, não exija recompilar — e para que
/// dê para ver onde as chaves podem nascer sem dar Play (ver gizmos).
///
/// Só spawna quando o estado do jogo muda para <see cref="GameState.Playing"/> (tutorial
/// concluído), para evitar que chaves apareçam durante o tutorial.
/// </summary>
public class KeySpawner : MonoBehaviour
{
    [SerializeField] private GameObject keyPrefab;

    [Tooltip("Vazio: usa todos os filhos diretos deste GameObject como pontos.")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

    [SerializeField, Min(0)] private int keysToSpawn = 3;

    [Tooltip("0 = aleatório a cada Play. Outro valor fixa o sorteio, para reproduzir um bug de layout.")]
    [SerializeField] private int seed = 0;

    private readonly List<GameObject> spawnedKeys = new List<GameObject>();

    public IReadOnlyList<GameObject> SpawnedKeys => spawnedKeys;

    private void OnEnable()
    {
        GameManager.StateChanged += OnGameStateChanged;

        // Se o tutorial já foi concluído, o estado já está em Playing quando este script habilita.
        if (GameManager.Current != null && GameManager.Current.State == GameState.Playing)
            SpawnKeys();
    }

    private void OnDisable()
    {
        if (GameManager.Current != null)
            GameManager.StateChanged -= OnGameStateChanged;
    }

    private void OnGameStateChanged(GameState newState)
    {
        if (newState == GameState.Playing)
            SpawnKeys();
    }

    /// <summary>Remove as chaves que ainda estão no mapa e sorteia de novo. Para restart de partida.</summary>
    public void Respawn()
    {
        ClearSpawnedKeys();
        SpawnKeys();
    }

    // Debug: uma chave em cada ponto, para conferir no Play se todos são alcançáveis pela mira.
    [ContextMenu("Spawnar chave em todos os pontos")]
    private void SpawnAtAllPoints()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[KeySpawner] Use no Play Mode: a chave precisa do jogador para ser testada.", this);
            return;
        }

        if (keyPrefab == null)
        {
            Debug.LogError("[KeySpawner] O prefab da chave não foi atribuído no Inspector!", this);
            return;
        }

        ClearSpawnedKeys();
        foreach (Transform point in CollectPoints())
            SpawnAt(point);
    }

    private void ClearSpawnedKeys()
    {
        foreach (GameObject key in spawnedKeys)
        {
            // Chave já coletada se destruiu sozinha (KeyItem.Interact); a referência vira null.
            if (key != null)
                Destroy(key);
        }

        spawnedKeys.Clear();
    }

    private void SpawnKeys()
    {
        if (keyPrefab == null)
        {
            Debug.LogError("[KeySpawner] O prefab da chave não foi atribuído no Inspector!", this);
            return;
        }

        List<Transform> points = CollectPoints();

        if (keysToSpawn > points.Count)
        {
            Debug.LogWarning(
                $"[KeySpawner] Pediu {keysToSpawn} chaves mas só há {points.Count} pontos. " +
                $"Spawnando {points.Count}.", this);
        }

        int count = Mathf.Min(keysToSpawn, points.Count);
        WarnIfNotEnoughKeys(count);

        System.Random rng = seed != 0 ? new System.Random(seed) : new System.Random();

        // Fisher-Yates parcial: só embaralha as `count` primeiras posições, sem RemoveAt no meio
        // da lista. Usa System.Random próprio para o seed não mexer no UnityEngine.Random global.
        for (int i = 0; i < count; i++)
        {
            int j = rng.Next(i, points.Count);
            (points[i], points[j]) = (points[j], points[i]);
            SpawnAt(points[i]);
        }
    }

    private void SpawnAt(Transform point)
    {
        // A rotação do ponto é somada à do prefab, não a substitui: a correção de eixo do
        // FBX (-90 em X) mora no prefab, e o ponto só diz para onde a chave aponta.
        Quaternion rotation = point.rotation * keyPrefab.transform.rotation;
        GameObject key = Instantiate(keyPrefab, point.position, rotation, transform);
        key.name = $"{keyPrefab.name} ({point.name})";
        spawnedKeys.Add(key);
    }

    // Cópia: o sorteio embaralha a lista, e não queremos reordenar o que está serializado.
    private List<Transform> CollectPoints()
    {
        List<Transform> points = new List<Transform>();

        if (spawnPoints.Count > 0)
        {
            foreach (Transform point in spawnPoints)
            {
                if (point != null)
                    points.Add(point);
            }
        }
        else
        {
            // As chaves nascem como filhas do spawner, e o Destroy só vale no fim do frame: sem
            // esse filtro, um respawn sorteia as chaves antigas como pontos.
            foreach (Transform child in transform)
            {
                if (child.GetComponentInChildren<KeyItem>(true) == null)
                    points.Add(child);
            }
        }

        return points;
    }

    // Menos chaves que cadeados trava a partida sem erro nenhum: o jogador só descobre depois
    // de rodar o mapa inteiro. Conta cadeados em vez de receber a porta por referência para
    // continuar valendo se aparecerem mais portas na cena.
    private void WarnIfNotEnoughKeys(int count)
    {
        int lockedCount = 0;
        foreach (DoorLock doorLock in FindObjectsByType<DoorLock>(FindObjectsSortMode.None))
        {
            if (!doorLock.IsUnlocked)
                lockedCount++;
        }

        if (count < lockedCount)
        {
            Debug.LogWarning(
                $"[KeySpawner] {count} chave(s) para {lockedCount} cadeado(s): a partida não fecha.", this);
        }
    }
}
