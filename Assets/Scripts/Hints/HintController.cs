using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Identifica uma dica mostrada, para escondê-la depois. default = nenhuma.</summary>
public readonly struct HintHandle
{
    internal readonly int Id;

    internal HintHandle(int id) => Id = id;

    public bool IsValid => Id != 0;
}

/// <summary>
/// Ponto único para mostrar dicas. Quem quer uma dica (<see cref="HintDirector"/>,
/// <see cref="HintTrigger"/>, qualquer script) chama <see cref="Show"/> com o texto cru e
/// guarda o handle; o controller traduz a marcação e entrega pronta para a
/// <see cref="HintView"/>.
///
/// Marcação do texto:
///   {Action}  teclas/botões do binding atual daquela action do mapa Player ({Move} → W A S D).
///   [Texto]   uma tecla literal ([Mouse], [Esc]).
///   *texto*   destaque na cor de highlight.
/// Rich text do TMP passa direto.
///
/// Ícones: cada <see cref="IconScheme"/> liga um control scheme do .inputactions a um TMP
/// Sprite Asset (gerado em Tools > Hints). O sprite é procurado pelo nome do controle
/// ("buttonSouth", "leftStick", "w"); sem sprite, a tecla é desenhada como caixa com o nome
/// legível. O esquema ativo segue o último dispositivo usado: pegou o controle, as dicas
/// viram botões de controle.
///
/// Várias dicas podem estar ativas ao mesmo tempo, e a mais recente é a que aparece. Quando
/// ela sai, a anterior volta: um trigger de corredor que mostra algo por 4s não apaga a
/// instrução do director que ainda espera o jogador agachar.
/// </summary>
public class HintController : MonoBehaviour
{
    [Serializable]
    private struct IconScheme
    {
        [Tooltip("Nome do control scheme no .inputactions.")]
        public string bindingGroup;

        [Tooltip("Vazio = todas as teclas deste esquema viram caixa desenhada.")]
        public TMP_SpriteAsset icons;
    }

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

    [Header("Ícones")]
    [Tooltip("O primeiro é o inicial. Troca para o esquema que aceitar o último dispositivo usado.")]
    [SerializeField] private IconScheme[] schemes =
    {
        new() { bindingGroup = "Keyboard&Mouse" },
        new() { bindingGroup = "Gamepad" },
    };

    [Tooltip("Trava num esquema (pelo Binding Group), ignorando o dispositivo. Para ver os ícones " +
             "de controle sem ter um conectado. Vazio = automático.")]
    [SerializeField] private string forceScheme;

    // O padding do <mark> só desenha, não ocupa espaço no layout: padding lateral faria uma
    // tecla invadir a vizinha. A largura vem dos espaços inquebráveis dentro da caixa, que o
    // TMP conta como caractere; o padding fica só no vertical (em % do tamanho da fonte).
    [Tooltip("Rich text de uma tecla sem ícone; {0} = nome da tecla.")]
    [SerializeField] private string keyFormat = "<mark=#EDE6D6F0 padding=\"0,0,12,12\"><color=#141414><b> {0} </b></color></mark>";

    [Tooltip("Entre duas teclas da mesma action (W A S D).")]
    [SerializeField] private string keySeparator = "<space=0.15em>";

    [Tooltip("Nome legível da tecla sem ícone para um control path, no lugar do display string do " +
             "Input System (que escreve 'Delta' para o mouse e 'Left Shift' para o shift).")]
    [SerializeField] private KeyLabel[] keyLabels =
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

    // Ordem de leitura do WASD. No .inputactions o composite vem up/down/left/right, que
    // escreveria "W S A D".
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
    private int currentScheme;

    /// <summary>
    /// Sprite asset do esquema ativo. Quem formata texto com <see cref="Format"/> num TMP
    /// próprio (o prompt de interação) precisa pôr este asset no label, senão os
    /// &lt;sprite&gt; saem vazios.
    /// </summary>
    public TMP_SpriteAsset Icons => schemes.Length > 0 ? schemes[currentScheme].icons : null;

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

        if (schemes.Length == 0)
            schemes = new[] { new IconScheme { bindingGroup = "Keyboard&Mouse" } };

        highlightHex = ColorUtility.ToHtmlStringRGBA(highlightColor);
        SetScheme(0);
    }

    private void OnEnable()
    {
        // Rebind entre um enable e outro troca a tecla: o cache formatado não vale mais.
        formatted.Clear();
        InputSystem.onActionChange += HandleActionChange;
    }

    private void OnDisable()
    {
        InputSystem.onActionChange -= HandleActionChange;
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

        if (!string.IsNullOrEmpty(forceScheme))
        {
            int forced = Array.FindIndex(schemes, scheme => scheme.bindingGroup == forceScheme);
            if (forced >= 0)
                SetScheme(forced);
        }

        // Time.time para na pausa: dica com duração não "gasta" tempo atrás do menu.
        float now = Time.time;
        active.RemoveAll(entry => entry.expiresAt <= now);

        bool hidden = PauseControler.IsPaused || active.Count == 0;
        view.SetText(hidden ? null : Format(active[active.Count - 1].text));
    }

    // Ouve o resultado das actions, não o dispositivo cru: o controle encostado na mesa com o
    // analógico com drift não rouba os ícones de quem está no teclado, porque o drift fica
    // abaixo do deadzone e nunca vira action.
    private void HandleActionChange(object actionOrMap, InputActionChange change)
    {
        if (change != InputActionChange.ActionPerformed || !string.IsNullOrEmpty(forceScheme))
            return;

        if (actionOrMap is not InputAction action || action.activeControl == null)
            return;

        InputDevice device = action.activeControl.device;
        if (Supports(currentScheme, device))
            return;

        for (int i = 0; i < schemes.Length; i++)
        {
            if (Supports(i, device))
            {
                SetScheme(i);
                return;
            }
        }
    }

    private bool Supports(int schemeIndex, InputDevice device)
    {
        InputControlScheme? scheme = PlayerInputProvider.Actions.asset.FindControlScheme(schemes[schemeIndex].bindingGroup);
        return scheme.HasValue && scheme.Value.SupportsDevice(device);
    }

    private void SetScheme(int index)
    {
        if (index == currentScheme && view.Label.spriteAsset == schemes[index].icons)
            return;

        currentScheme = index;
        view.Label.spriteAsset = schemes[index].icons;
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
        TMP_SpriteAsset icons = schemes[currentScheme].icons;
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

        InputBinding mask = InputBinding.MaskByGroup(schemes[currentScheme].bindingGroup);
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
            builder.Append(Key(IconName(action.bindings[index].effectivePath), LabelFor(action, index)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Nome do sprite para um control path: o controle sem o dispositivo, com '/' virando '_'
    /// ("&lt;Gamepad&gt;/dpad/up" → "dpad_up"). O dispositivo já está implícito no esquema, e
    /// assim o mesmo asset de PS4 serve para qualquer binding de &lt;Gamepad&gt;.
    /// </summary>
    private static string IconName(string controlPath)
    {
        int slash = controlPath.IndexOf('/');
        string control = slash >= 0 ? controlPath.Substring(slash + 1) : controlPath;
        return control.Replace('/', '_');
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
