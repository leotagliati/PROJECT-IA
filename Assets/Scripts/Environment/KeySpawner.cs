using System.Collections.Generic;
using UnityEngine;

public class KeySpawner : MonoBehaviour
{
    [SerializeField] private GameObject keyPrefab;
    [SerializeField] private int keysToSpawn = 3;

    private struct SpawnPoint
    {
        public Vector3 position;
        public Quaternion rotation;

        public SpawnPoint(Vector3 pos, Vector3 rotEuler)
        {
            position = pos;
            rotation = Quaternion.Euler(rotEuler);
        }
    }

    // Lista fixa (não serializada e não exibida no Inspector) com os transforms das imagens
    private static readonly List<SpawnPoint> spawnPoints = new List<SpawnPoint>()
    {
        new SpawnPoint(new Vector3(-36.279f, -0.366f, 37.396f), new Vector3(-90f, 0f, 155.465f)),
        new SpawnPoint(new Vector3(-48.112f, 2.277f, 45.157f), new Vector3(-90f, 0f, 115.043f)),
        new SpawnPoint(new Vector3(-53.069f, -0.112f, 39.183f), new Vector3(-90f, 0f, 231.725f)),
        new SpawnPoint(new Vector3(9.253f, -0.381f, 45.664f), new Vector3(-90f, 0f, -180.459f)),
        new SpawnPoint(new Vector3(5.719593f, 0.739f, 34.37406f), new Vector3(-90f, 0f, -32.92f)),
        new SpawnPoint(new Vector3(19.906f, -0.239f, 49.179f), new Vector3(-90f, 0f, -156.585f)),
        new SpawnPoint(new Vector3(19.689f, 0.793f, 8.05f), new Vector3(-90f, 0f, -302.018f)),
        new SpawnPoint(new Vector3(14.18f, 1.09f, -19.361f), new Vector3(-89.98f, 0f, -113.807f)),
        new SpawnPoint(new Vector3(-18.757f, -0.341f, -41.858f), new Vector3(-90f, 0f, 92.346f)),
        new SpawnPoint(new Vector3(1.685f, 1.989f, -33.183f), new Vector3(-90f, 0f, 179.86f))
    };

    private void Start()
    {
        SpawnKeys();
    }

    private void SpawnKeys()
    {
        if (keyPrefab == null)
        {
            Debug.LogError("KeySpawner: O prefab da chave não foi atribuído no Inspector!", this);
            return;
        }

        int count = Mathf.Clamp(keysToSpawn, 0, spawnPoints.Count);
        List<SpawnPoint> availablePoints = new List<SpawnPoint>(spawnPoints);

        for (int i = 0; i < count; i++)
        {
            int randomIndex = Random.Range(0, availablePoints.Count);
            SpawnPoint selectedPoint = availablePoints[randomIndex];

            Instantiate(keyPrefab, selectedPoint.position, selectedPoint.rotation);
            availablePoints.RemoveAt(randomIndex);
        }
    }
}