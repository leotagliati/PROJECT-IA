using System;
using UnityEngine;

/// <summary>
/// Barulho que a IA pode ouvir (item arremessado batendo, por exemplo). Quem faz barulho não conhece o
/// monstro; cada GraphPingSystem escuta e decide pelo próprio grafo se ouviu.
/// </summary>
public static class NoiseEvents
{
    /// <summary>Posição do barulho e alcance em metros PELO GRAFO (o caminho pelas portas).</summary>
    public static event Action<Vector3, float> Emitted;

    public static void Emit(Vector3 position, float hearingMeters)
    {
        Emitted?.Invoke(position, hearingMeters);
    }

    // Sem domain reload, o evento estático manteria assinantes da sessão de Play anterior.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Emitted = null;
    }
}
