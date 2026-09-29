using UnityEngine;

public enum InputMode
{
    Player,
    UI,
}

public static class PlayerInputProvider
{
    private static PlayerInputActions actions;
    private static int users;
    private static InputMode mode;

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
    public static PlayerInputActions.UIActions UI => Actions.UI;

    public static InputMode Mode => mode;

    public static void Acquire()
    {
        users++;

        if (users == 1)
            SetMapEnabled(mode, true);
    }

    public static void Release()
    {
        if (users == 0)
            return;

        users--;

        if (users == 0 && actions != null)
            SetMapEnabled(mode, false);
    }

    // Só um mapa fica ligado por vez: Esc do Player e Cancel do UI compartilham a tecla.
    public static void SetMode(InputMode next)
    {
        if (next == mode)
            return;

        if (users > 0)
        {
            SetMapEnabled(mode, false);
            SetMapEnabled(next, true);
        }

        mode = next;
    }

    private static void SetMapEnabled(InputMode map, bool enabled)
    {
        if (map == InputMode.Player)
        {
            if (enabled) Actions.Player.Enable();
            else Actions.Player.Disable();
        }
        else
        {
            if (enabled) Actions.UI.Enable();
            else Actions.UI.Disable();
        }
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
        mode = InputMode.Player;
    }
}
