using UnityEngine;

/// <summary>
/// [Efeito 2 — glint] Uma faixa de luz que atravessa o item periodicamente, como brilho
/// correndo numa lâmina. Chama o olho por MOVIMENTO, não por contraste — funciona mesmo
/// com o item pequeno na tela e na pixelização.
///
/// A animação mora no shader (ItemFeedback/Glint, via ItemGlint.mat), então este componente
/// só anexa o overlay e sorteia a fase de cada item. Custo: um draw extra permanente por
/// item enquanto ativo.
/// </summary>
public class ItemGlint : MonoBehaviour
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int PhaseId = Shader.PropertyToID("_Phase");

    [Tooltip("Assets/Materials/ItemGlint.mat. Velocidade, largura e direção da faixa ficam no material.")]
    [SerializeField] private Material glintMaterial;

    [SerializeField] private Color color = Color.white;

    [Tooltip("Intensidade da faixa (alpha do aditivo).")]
    [SerializeField, Range(0f, 1f)] private float strength = 0.8f;

    [Tooltip("Vários itens na mesma sala não recebem a faixa ao mesmo tempo.")]
    [SerializeField] private bool randomizePhase = true;

    [SerializeField] private bool pauseWhileHighlighted = true;

    private RendererOverlay overlay;
    private MaterialPropertyBlock block;
    private HighlightTarget highlight;

    private void Awake()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        highlight = GetComponent<HighlightTarget>();

        if (glintMaterial == null || renderers.Length == 0)
        {
            Debug.LogWarning($"{nameof(ItemGlint)}: sem material ou sem renderer.", this);
            enabled = false;
            return;
        }

        overlay = new RendererOverlay(renderers, glintMaterial);

        Color c = color;
        c.a = strength;

        block = new MaterialPropertyBlock();
        block.SetColor(BaseColorId, c);
        block.SetFloat(PhaseId, randomizePhase ? Random.value : 0f);
    }

    private void OnEnable()
    {
        if (overlay == null)
            return;

        overlay.Attach();
        overlay.SetPropertyBlock(block);
    }

    private void OnDisable()
    {
        overlay?.Detach();
    }

    private void Update()
    {
        if (!pauseWhileHighlighted || highlight == null)
            return;

        // Sob a mira o outline assume; fora dela a faixa volta.
        if (highlight.IsHighlighted)
            overlay.Detach();
        else if (!overlay.Attached)
        {
            overlay.Attach();
            overlay.SetPropertyBlock(block);
        }
    }
}
