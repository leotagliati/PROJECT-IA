using System.Collections.Generic;
using UnityEngine;

public class KeySpawner : MonoBehaviour
{
    [SerializeField] private GameObject keyPrefab;

    [Tooltip("Vazio: usa os filhos diretos como pontos.")]
    [SerializeField] private List<Transform> spawnPoints = new();

    [SerializeField, Min(0)] private int keysToSpawn = 3;

    [Tooltip("Distância mínima (m) entre duas chaves.")]
    [SerializeField, Min(0f)] private float minKeyDistance = 18f;

    [Tooltip("0 = aleatório a cada Play.")]
    [SerializeField] private int seed = 0;

    private readonly List<GameObject> spawnedKeys = new();
    private bool hasSpawned;

    public IReadOnlyList<GameObject> SpawnedKeys => spawnedKeys;

    private void OnEnable()
    {
        GameManager.StateChanged += OnGameStateChanged;

        if (GameManager.Current != null && GameManager.Current.State == GameState.Playing)
            SpawnKeys();
    }

    private void OnDisable() => GameManager.StateChanged -= OnGameStateChanged;

    private void OnGameStateChanged(GameState newState)
    {
        if (newState == GameState.Playing)
            SpawnKeys();
    }

    public void Respawn()
    {
        hasSpawned = false;
        SpawnKeys();
    }

    [ContextMenu("Spawnar chave em todos os pontos")]
    private void SpawnAtAllPoints()
    {
        if (!Application.isPlaying || !HasPrefab())
            return;

        ClearKeys();
        foreach (Transform point in CollectPoints())
            SpawnAt(point);
    }

    private void SpawnKeys()
    {
        if (hasSpawned || !HasPrefab())
            return;

        hasSpawned = true;
        ClearKeys();

        List<Transform> points = CollectPoints();
        int count = Mathf.Min(keysToSpawn, points.Count);

        if (count < keysToSpawn)
            Debug.LogWarning($"[KeySpawner] Pediu {keysToSpawn} chaves mas só há {points.Count} pontos.", this);

        WarnIfNotEnoughKeys(count);
        Shuffle(points);

        foreach (Transform point in PickSpreadPoints(points, count))
            SpawnAt(point);
    }

    private List<Transform> PickSpreadPoints(List<Transform> points, int count)
    {
        List<Transform> picked = new(count);
        List<Transform> rest = new();
        float minSqr = minKeyDistance * minKeyDistance;

        foreach (Transform point in points)
        {
            if (picked.Count < count && SqrDistanceToNearest(point, picked) >= minSqr)
                picked.Add(point);
            else
                rest.Add(point);
        }

        if (picked.Count < count)
            Debug.LogWarning($"[KeySpawner] Só {picked.Count} de {count} pontos a {minKeyDistance} m entre si.", this);

        while (picked.Count < count)
        {
            Transform farthest = rest[0];
            foreach (Transform point in rest)
            {
                if (SqrDistanceToNearest(point, picked) > SqrDistanceToNearest(farthest, picked))
                    farthest = point;
            }

            picked.Add(farthest);
            rest.Remove(farthest);
        }

        return picked;
    }

    private static float SqrDistanceToNearest(Transform point, List<Transform> others)
    {
        float min = float.PositiveInfinity;
        foreach (Transform other in others)
            min = Mathf.Min(min, (point.position - other.position).sqrMagnitude);

        return min;
    }

    private void Shuffle(List<Transform> points)
    {
        System.Random rng = seed != 0 ? new System.Random(seed) : new System.Random();
        for (int i = points.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (points[i], points[j]) = (points[j], points[i]);
        }
    }

    private void SpawnAt(Transform point)
    {
        Quaternion rotation = point.rotation * keyPrefab.transform.rotation;
        GameObject key = Instantiate(keyPrefab, point.position, rotation, transform);
        key.name = $"{keyPrefab.name} ({point.name})";
        spawnedKeys.Add(key);
    }

    private void ClearKeys()
    {
        foreach (KeyItem key in GetComponentsInChildren<KeyItem>(true))
            Destroy(key.gameObject);

        spawnedKeys.Clear();
    }

    private List<Transform> CollectPoints()
    {
        List<Transform> points = new();

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
            foreach (Transform child in transform)
            {
                if (child.GetComponentInChildren<KeyItem>(true) == null)
                    points.Add(child);
            }
        }

        return points;
    }

    private bool HasPrefab()
    {
        if (keyPrefab == null)
            Debug.LogError("[KeySpawner] O prefab da chave não foi atribuído no Inspector!", this);

        return keyPrefab != null;
    }

    private void WarnIfNotEnoughKeys(int count)
    {
        int lockedCount = 0;
        foreach (DoorLock doorLock in FindObjectsByType<DoorLock>(FindObjectsSortMode.None))
        {
            if (!doorLock.IsUnlocked)
                lockedCount++;
        }

        if (count < lockedCount)
            Debug.LogWarning($"[KeySpawner] {count} chave(s) para {lockedCount} cadeado(s): a partida não fecha.", this);
    }
}
