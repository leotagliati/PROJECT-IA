using Unity.MLAgents.Sensors;
using UnityEngine;

public class SeekerPerceptionSystem : MonoBehaviour
{
    [SerializeField] private float _detectionRange = 2f;
    [SerializeField] private LayerMask _wallLayer;
    [SerializeField] private float _originHeightOffset = 0.1f;


    private static readonly Vector3[] Directions =
    {
        Vector3.forward, // frente
        Vector3.back,    // trás
        Vector3.left,    // esquerda
        Vector3.right,   // direita
    };

    // 0 = livre, ~1 = parede perto
    public float GetWallProximity(Vector3 direction)
    {
        Vector3 origin = transform.position + Vector3.up * _originHeightOffset;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, _detectionRange, _wallLayer))
            return 1f - (hit.distance / _detectionRange);

        return 0f;
    }

    // Preenche os 4 valores na ordem de Directions
    public void GetWallProximities(float[] results)
    {
        for (int i = 0; i < Directions.Length; i++)
            results[i] = GetWallProximity(Directions[i]);
    }

    public int DirectionCount => Directions.Length;

    private void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position + Vector3.up * _originHeightOffset;
        foreach (var dir in Directions)
        {
            float prox = Application.isPlaying ? GetWallProximity(dir) : 0f;
            Gizmos.color = Color.Lerp(Color.green, Color.red, prox);
            Gizmos.DrawLine(origin, origin + dir * _detectionRange);
        }
    }
}