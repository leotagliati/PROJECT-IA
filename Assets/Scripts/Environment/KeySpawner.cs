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

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.1f);

        foreach (Transform point in CollectPoints())
        {
            Gizmos.DrawWireSphere(point.position, 0.25f);
            Gizmos.DrawLine(transform.position, point.position);
            Gizmos.DrawRay(point.position, point.forward * 0.5f);
        }
    }

#if UNITY_EDITOR
    // Migração única das coordenadas que antes ficavam fixas no código. Clique com o botão
    // direito no componente → "Criar pontos legados", salve a cena e apague este bloco.
    [ContextMenu("Criar pontos legados")]
    private void CreateLegacyPoints()
    {
        (Vector3 pos, Vector3 euler)[] legacy =
        {
            (new Vector3(-36.279f, -0.366f, 37.396f), new Vector3(-90f, 0f, 155.465f)),
            (new Vector3(-48.112f, 2.277f, 45.157f), new Vector3(-90f, 0f, 115.043f)),
            (new Vector3(-53.069f, -0.112f, 39.183f), new Vector3(-90f, 0f, 231.725f)),
            (new Vector3(9.253f, -0.381f, 45.664f), new Vector3(-90f, 0f, -180.459f)),
            (new Vector3(5.719593f, 0.739f, 34.37406f), new Vector3(-90f, 0f, -32.92f)),
            (new Vector3(19.906f, -0.239f, 49.179f), new Vector3(-90f, 0f, -156.585f)),
            (new Vector3(19.689f, 0.793f, 8.05f), new Vector3(-90f, 0f, -302.018f)),
            (new Vector3(14.18f, 1.09f, -19.361f), new Vector3(-89.98f, 0f, -113.807f)),
            (new Vector3(-18.757f, -0.341f, -41.858f), new Vector3(-90f, 0f, 92.346f)),
            (new Vector3(1.685f, 1.989f, -33.183f), new Vector3(-90f, 0f, 179.86f)),
        };

        if (keyPrefab == null)
        {
            Debug.LogError("[KeySpawner] Atribua o prefab antes: a rotação dos pontos é derivada dele.", this);
            return;
        }

        // As rotações legadas já incluíam a do prefab. Tirar ela aqui faz o spawn (ponto * prefab)
        // cair exatamente na mesma orientação de antes; com o Key3 atual sobra só um giro em Y.
        Quaternion prefabInverse = Quaternion.Inverse(keyPrefab.transform.rotation);

        for (int i = 0; i < legacy.Length; i++)
        {
            GameObject point = new GameObject($"KeyPoint_{i:00}");
            UnityEditor.Undo.RegisterCreatedObjectUndo(point, "Criar pontos legados");
            point.transform.SetPositionAndRotation(
                legacy[i].pos, Quaternion.Euler(legacy[i].euler) * prefabInverse);
            point.transform.SetParent(transform, worldPositionStays: true);
        }
    }
#endif
}
