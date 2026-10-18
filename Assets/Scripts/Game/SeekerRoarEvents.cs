using System;
using UnityEngine;

/// <summary>O monstro gritou por ter perdido o jogador; o <see cref="RoarFeedback"/> escuta e decide pela distância.</summary>
public static class SeekerRoarEvents
{
    public static event Action<Vector3> Roared;

    public static void Emit(Vector3 position)
    {
        Roared?.Invoke(position);
    }

    // Sem domain reload, o evento estático manteria assinantes da sessão de Play anterior.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Roared = null;
    }
}
