using UnityEngine;

public static class PlayerInputProvider
{
    private static PlayerInputActions actions;
    private static int users;

    public static PlayerInputActions Actions
    {
        get
        {
            if (actions == null)
                actions = new PlayerInputActions();

            return actions;
        }
    }

    public static PlayerInputActions.PlayerActions Player => Actions.Player;

    public static void Acquire()
    {
        users++;

        if (users == 1)
            Actions.Player.Enable();
    }

    public static void Release()
    {
        if (users == 0)
            return;

        users--;

        if (users == 0 && actions != null)
            actions.Player.Disable();
    }

    // Estáticos sobrevivem ao Stop quando o domain reload está desligado nas opções de
    // Play Mode: sem isso, o segundo Play começaria com a contagem de usuários do primeiro
    // e o mapa nunca mais ligaria.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        if (actions != null)
            actions.Dispose();

        actions = null;
        users = 0;
    }
}
