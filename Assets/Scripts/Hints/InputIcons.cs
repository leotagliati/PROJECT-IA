using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TextCore;

public static class InputIcons
{
    private static InputIconLibrary library;
    private static bool initialized;
    private static int current;
    private static string forced;
    private static readonly Dictionary<(TMP_SpriteAsset, string), Sprite> sprites = new();

    public static event Action SchemeChanged;

    public static TMP_SpriteAsset Icons
    {
        get
        {
            InputIconLibrary.Scheme? scheme = Active;
            return scheme?.icons;
        }
    }

    public static string ActiveBindingGroup => Active?.bindingGroup ?? "Keyboard&Mouse";

    private static InputIconLibrary.Scheme? Active
    {
        get
        {
            Initialize();
            return library != null && library.schemes.Length > 0 ? library.schemes[current] : null;
        }
    }

    /// <summary>Trava num esquema (pelo Binding Group). Vazio = automático.</summary>
    public static void ForceScheme(string bindingGroup)
    {
        Initialize();
        forced = string.IsNullOrEmpty(bindingGroup) ? null : bindingGroup;

        if (forced == null || library == null)
            return;

        int index = Array.FindIndex(library.schemes, s => s.bindingGroup == forced);
        if (index >= 0)
            SetScheme(index);
    }

    /// <summary>Nome do sprite para um control path: "&lt;Gamepad&gt;/dpad/up" → "dpad_up".</summary>
    public static string IconName(string controlPath)
    {
        int slash = controlPath.IndexOf('/');
        string control = slash >= 0 ? controlPath.Substring(slash + 1) : controlPath;
        return control.Replace('/', '_');
    }

    /// <summary>Ícone da tecla principal da action no esquema ativo, como Sprite de UI. Null se não houver.</summary>
    public static Sprite GetActionIcon(string actionName)
    {
        TMP_SpriteAsset icons = Icons;
        InputAction action = PlayerInputProvider.Player.Get().FindAction(actionName);
        if (icons == null || action == null || !(icons.spriteSheet is Texture2D sheet))
            return null;

        InputBinding mask = InputBinding.MaskByGroup(ActiveBindingGroup);
        foreach (InputBinding binding in action.bindings)
        {
            if (binding.isComposite || binding.isPartOfComposite || !mask.Matches(binding))
                continue;

            string name = IconName(binding.effectivePath);
            if (sprites.TryGetValue((icons, name), out Sprite cached))
                return cached;

            int index = icons.GetSpriteIndexFromName(name);
            if (index < 0)
                return null;

            GlyphRect r = icons.spriteCharacterTable[index].glyph.glyphRect;
            Sprite sprite = Sprite.Create(sheet, new Rect(r.x, r.y, r.width, r.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name;
            sprites.Add((icons, name), sprite);
            return sprite;
        }

        return null;
    }

    private static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        library = Resources.Load<InputIconLibrary>(InputIconLibrary.ResourcePath);

        if (library == null)
            Debug.LogError($"[InputIcons] Resources/{InputIconLibrary.ResourcePath}.asset não encontrado.");

        InputSystem.onActionChange += HandleActionChange;
    }

    // Ouve actions, não o dispositivo cru: drift de analógico abaixo do deadzone nunca vira
    // action, então o controle parado na mesa não rouba os ícones do teclado.
    private static void HandleActionChange(object actionOrMap, InputActionChange change)
    {
        if (change != InputActionChange.ActionPerformed || forced != null || library == null)
            return;

        if (actionOrMap is not InputAction action || action.activeControl == null)
            return;

        InputDevice device = action.activeControl.device;
        if (Supports(current, device))
            return;

        for (int i = 0; i < library.schemes.Length; i++)
        {
            if (Supports(i, device))
            {
                SetScheme(i);
                return;
            }
        }
    }

    private static bool Supports(int index, InputDevice device)
    {
        InputControlScheme? scheme = PlayerInputProvider.Actions.asset.FindControlScheme(library.schemes[index].bindingGroup);
        return scheme.HasValue && scheme.Value.SupportsDevice(device);
    }

    private static void SetScheme(int index)
    {
        if (index == current)
            return;

        current = index;
        SchemeChanged?.Invoke();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        if (initialized)
            InputSystem.onActionChange -= HandleActionChange;

        library = null;
        initialized = false;
        current = 0;
        forced = null;
        sprites.Clear();
        SchemeChanged = null;
    }
}
