using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public readonly struct HintHandle
{
    internal readonly int Id;
    internal HintHandle(int id) => Id = id;
    public bool IsValid => Id != 0;
}

public class HintController : MonoBehaviour
{
    [Serializable]
    private struct KeyLabel
    {
        public string path;
        public string label;
    }

    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private HintView view;

    [Header("Destaque")]
    [SerializeField] private Color highlightColor = new(1f, 0.85f, 0.55f, 1f);

    [Header("Ícones (tabela em Resources/InputIconLibrary)")]
    [Tooltip("Trava num esquema (pelo Binding Group), ignorando o dispositivo. Para ver os ícones " +
             "de controle sem ter um conectado. Vazio = automático.")]
    [SerializeField] private string forceScheme;

    [Tooltip("Rich text de uma tecla sem ícone; {0} = nome da tecla.")]
    [SerializeField] private string keyFormat = "<mark=#EDE6D6F0 padding=\"0,0,12,12\"><color=#141414><b> {0} </b></color></mark>";

    [Tooltip("Entre duas teclas da mesma action (W A S D).")]
    [SerializeField] private string keySeparator = "<space=0.15em>";

    [Tooltip("Nome legível da tecla sem ícone para um control path, no lugar do display string do " +
             "Input System (que escreve 'Delta' para o mouse e 'Left Shift' para o shift).")]
    [SerializeField]
    private KeyLabel[] keyLabels =
    {
        new() { path = "<Mouse>/delta", label = "Mouse" },
        new() { path = "<Pointer>/delta", label = "Mouse" },
        new() { path = "<Mouse>/leftButton", label = "Clique" },
        new() { path = "<Keyboard>/leftShift", label = "Shift" },
        new() { path = "<Keyboard>/leftCtrl", label = "Ctrl" },
        new() { path = "<Keyboard>/space", label = "Espaço" },
    };

    private static readonly Regex ActionToken = new(@"\{(\w+)\}");
    private static readonly Regex LiteralKey = new(@"\[([^\]]+)\]");
    private static readonly Regex Highlight = new(@"\*([^*]+)\*");
    private static readonly string[] CompositeOrder = { "up", "left", "down", "right" };

    private sealed class Entry
    {
        public int id;
        public string text;
        public float expiresAt;
    }

    private readonly List<Entry> active = new();
    private readonly Dictionary<string, string> formatted = new();
    private int nextId = 1;
    private string highlightHex;

    /// <summary>
    /// Sprite asset do esquema ativo. Quem formata texto com <see cref="Format"/> num TMP
    /// próprio precisa pôr este asset no label, senão os &lt;sprite&gt; saem vazios.
    /// </summary>
    public TMP_SpriteAsset Icons => InputIcons.Icons;

    /// <summary>Trocou teclado ↔ controle: texto já formatado ficou com os ícones errados.</summary>
    public event Action SchemeChanged;

    private void Awake()
    {
        if (view == null)
            view = FindFirstObjectByType<HintView>();

        if (view == null || view.Label == null)
        {
            Debug.LogError($"{name}: sem {nameof(HintView)} na cena — as dicas não têm onde aparecer.", this);
            enabled = false;
            return;
        }

        highlightHex = ColorUtility.ToHtmlStringRGBA(highlightColor);

        if (!string.IsNullOrEmpty(forceScheme))
            InputIcons.ForceScheme(forceScheme);
    }

    private void OnEnable()
    {
        // Rebind entre um enable e outro troca a tecla: o cache formatado não vale mais.
        formatted.Clear();
        view.Label.spriteAsset = InputIcons.Icons;
        InputIcons.SchemeChanged += HandleSchemeChanged;
    }

    private void OnDisable()
    {
        InputIcons.SchemeChanged -= HandleSchemeChanged;
    }

    /// <summary>
    /// Mostra uma dica por cima das que já estão ativas.
    /// </summary>
    /// <param name="duration">Segundos de jogo (param na pausa). 0 = até alguém chamar <see cref="Hide"/>.</param>
    public HintHandle Show(string text, float duration = 0f)
    {
        if (string.IsNullOrEmpty(text))
            return default;

        // Guarda o texto cru: trocar de teclado para controle reformata as dicas já ativas.
        var entry = new Entry
        {
            id = nextId++,
            text = text,
            expiresAt = duration > 0f ? Time.time + duration : float.PositiveInfinity,
        };

        active.Add(entry);
        return new HintHandle(entry.id);
    }

    /// <summary>Tira a dica. Handle já expirado ou inválido é no-op.</summary>
    public void Hide(HintHandle handle)
    {
        if (handle.IsValid)
            active.RemoveAll(entry => entry.id == handle.Id);
    }

    public bool IsActive(HintHandle handle) =>
        handle.IsValid && active.Exists(entry => entry.id == handle.Id);

    public void Clear() => active.Clear();

    private void Update()
    {
        GameManager game = GameManager.Current;
        if (game != null && game.IsOver)
            active.Clear();

        // Time.time para na pausa: dica com duração não "gasta" tempo atrás do menu.
        float now = Time.time;
        active.RemoveAll(entry => entry.expiresAt <= now);

        bool hidden = PauseControler.IsPaused || active.Count == 0;
        view.SetText(hidden ? null : Format(active[active.Count - 1].text));
    }

    private void HandleSchemeChanged()
    {
        view.Label.spriteAsset = InputIcons.Icons;
        formatted.Clear();
        SchemeChanged?.Invoke();
    }

    /// <summary>
    /// Traduz a marcação ({Action}, [Tecla], *destaque*) para rich text no esquema ativo, sem
    /// mostrar nada. Para UI que não é a linha de dica mas quer as mesmas teclas/ícones.
    /// </summary>
    public string Format(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (formatted.TryGetValue(text, out string result))
            return result;

        // Destaque primeiro: as teclas geradas abaixo não têm asterisco, mas o texto do
        // usuário pode ter colchete dentro de um trecho destacado.
        // Lazy: Format pode ser chamado por outro objeto antes do Awake deste.
        highlightHex ??= ColorUtility.ToHtmlStringRGBA(highlightColor);
        result = Highlight.Replace(text, match => $"<color=#{highlightHex}>{match.Groups[1].Value}</color>");
        result = LiteralKey.Replace(result, match => Key(match.Groups[1].Value, match.Groups[1].Value));
        result = ActionToken.Replace(result, match => KeysFor(match.Groups[1].Value) ?? match.Value);

        formatted.Add(text, result);
        return result;
    }

    private string Key(string iconName, string fallbackLabel)
    {
        TMP_SpriteAsset icons = InputIcons.Icons;
        if (icons != null && icons.GetSpriteIndexFromName(iconName) >= 0)
            return $"<sprite name=\"{iconName}\">";

        return string.Format(keyFormat, fallbackLabel);
    }

    private string KeysFor(string actionName)
    {
        InputAction action = PlayerInputProvider.Player.Get().FindAction(actionName);
        if (action == null)
        {
            Debug.LogWarning($"{name}: dica cita {{{actionName}}}, que não é action do mapa Player.", this);
            return null;
        }

        InputBinding mask = InputBinding.MaskByGroup(InputIcons.ActiveBindingGroup);
        var keys = new List<(int order, int binding)>();
        var seenParts = new HashSet<string>();
        var bindings = action.bindings;

        for (int i = 0; i < bindings.Count; i++)
        {
            InputBinding binding = bindings[i];

            // O composite em si não tem grupo; só as partes têm.
            if (binding.isComposite || !mask.Matches(binding))
                continue;

            if (binding.isPartOfComposite)
            {
                // Move tem WASD e setas na mesma parte: só a primeira de cada.
                if (!seenParts.Add(binding.name))
                    continue;

                int order = Array.IndexOf(CompositeOrder, binding.name.ToLowerInvariant());
                keys.Add((order < 0 ? CompositeOrder.Length + i : order, i));
            }
            else
            {
                // Binding simples: o primeiro do grupo é o principal. Look tem Pointer e
                // Mouse, que dariam a mesma tecla duas vezes.
                keys.Add((i, i));
                break;
            }
        }

        if (keys.Count == 0)
            return null;

        keys.Sort((a, b) => a.order.CompareTo(b.order));

        var builder = new StringBuilder();
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
                builder.Append(keySeparator);

            int index = keys[i].binding;
            builder.Append(Key(InputIcons.IconName(action.bindings[index].effectivePath), LabelFor(action, index)));
        }

        return builder.ToString();
    }

    private string LabelFor(InputAction action, int bindingIndex)
    {
        string path = action.bindings[bindingIndex].effectivePath;

        foreach (KeyLabel entry in keyLabels)
            if (string.Equals(entry.path, path, StringComparison.OrdinalIgnoreCase))
                return entry.label;

        return action.GetBindingDisplayString(bindingIndex);
    }
}
