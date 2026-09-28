using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Sorteia <see cref="keysToSpawn"/> pontos distintos entre os filhos marcados e instancia uma
/// chave em cada. Os pontos são Transforms na cena (e não coordenadas no código) para que
/// mover um ponto, ou reaproveitar o spawner em outro mapa, não exija recompilar — e para que
/// dê para ver onde as chaves podem nascer sem dar Play (ver gizmos).
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

    private void Start()
    {
        SpawnKeys();
    }

    /// <summary>Remove as chaves que ainda estão no mapa e sorteia de novo. Para restart de partida.</summary>
    public void Respawn()
    {
        foreach (GameObject key in spawnedKeys)
        {
            // Chave já coletada se destruiu sozinha (KeyItem.Interact); a referência vira null.
            if (key != null)
                Destroy(key);
        }

        spawnedKeys.Clear();
        SpawnKeys();
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

            // A rotação do ponto é somada à do prefab, não a substitui: a correção de eixo do
            // FBX (-90 em X) mora no prefab, e o ponto só diz para onde a chave aponta.
            Transform point = points[i];
            Quaternion rotation = point.rotation * keyPrefab.transform.rotation;
            GameObject key = Instantiate(keyPrefab, point.position, rotation, transform);
            key.name = $"{keyPrefab.name} ({point.name})";
            spawnedKeys.Add(key);
        }
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
            foreach (Transform child in transform)
                points.Add(child);
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
