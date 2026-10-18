using System;
using UnityEngine;

/// <summary>
/// O monstro gritou de raiva por ter perdido o jogador (<see cref="Assets.Scripts.Seeker.SeekerLostRoar"/>). Quem grita
/// não conhece câmera nem tela: o lado do jogador (<see cref="RoarFeedback"/>) escuta e decide pela distância o que
/// sente. Mesmo molde do <see cref="NoiseEvents"/>.
/// </summary>
public static class SeekerRoarEvents
{
    /// <summary>Posição da cabeça do monstro no grito.</summary>
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
