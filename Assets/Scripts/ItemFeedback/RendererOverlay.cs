using UnityEngine;

/// <summary>
/// Anexa um material EXTRA a um conjunto de renderers e o remove depois. Materiais além do
/// número de submeshes redesenham o último submesh — é o overlay clássico do Unity, sem
/// objeto filho nem cópia de mesh. O MaterialPropertyBlock vai só para o índice do
/// overlay, então o material original não é tocado (URP/Lit também usa _BaseColor; sem o
/// índice, o bloco sobrescreveria a cor do item).
///
/// Sem overlay anexado, o item custa exatamente o que custava antes. É classe pura, não
/// MonoBehaviour: quem decide QUANDO anexar são os efeitos (ItemBlink, ItemGlint).
/// </summary>
public sealed class RendererOverlay
{
    private readonly Renderer[] renderers;
    private readonly Material[][] baseMaterials;
    private readonly Material[][] overlaidMaterials;

    public bool Attached { get; private set; }

    public RendererOverlay(Renderer[] renderers, Material overlay)
    {
        this.renderers = renderers;
        baseMaterials = new Material[renderers.Length][];
        overlaidMaterials = new Material[renderers.Length][];

        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] source = renderers[i].sharedMaterials;
            var overlaid = new Material[source.Length + 1];
            source.CopyTo(overlaid, 0);
            overlaid[source.Length] = overlay;

            baseMaterials[i] = source;
            overlaidMaterials[i] = overlaid;
        }
    }

    public void Attach()
    {
        if (Attached)
            return;

        Attached = true;
        Apply(overlaidMaterials);
    }

    public void Detach()
    {
        if (!Attached)
            return;

        Attached = false;
        Apply(baseMaterials);
    }

    /// <summary>Aplica o bloco só no material de overlay de cada renderer.</summary>
    public void SetPropertyBlock(MaterialPropertyBlock block)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].SetPropertyBlock(block, overlaidMaterials[i].Length - 1);
        }
    }

    private void Apply(Material[][] set)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].sharedMaterials = set[i];
        }
    }
}
