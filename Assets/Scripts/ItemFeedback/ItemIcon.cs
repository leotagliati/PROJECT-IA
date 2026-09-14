using UnityEngine;

/// <summary>
/// [Efeito 5 — ícone] Um sprite unlit flutuando acima do item, sempre de frente para a
/// câmera, subindo e descendo devagar. O mais legível de todos e o mais barato — e o mais
/// "game-y": quebra a imersão de horror. Fica aqui porque a Puppet Combo usa em alguns
/// títulos e vale ver lado a lado com os outros.
///
/// Sem sprite arrastado, desenha uma estrela de 4 pontas procedural. O material é o
/// ItemFlash (URP/Unlit transparente); a textura entra por MaterialPropertyBlock, então o
/// asset não é alterado.
/// </summary>
[DefaultExecutionOrder(130)] // depois de tudo que mexe na câmera, para o billboard não atrasar um frame
public class ItemIcon : MonoBehaviour
{
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [Tooltip("Assets/Materials/ItemFlash.mat ou qualquer unlit transparente com _BaseMap/_BaseColor.")]
    [SerializeField] private Material iconMaterial;

    [Tooltip("Vazio: estrela de 4 pontas gerada em código.")]
    [SerializeField] private Texture2D sprite;

    [SerializeField] private Color color = Color.white;

    [Header("Posição")]
    [SerializeField] private float height = 0.35f;
    [SerializeField] private float size = 0.18f;

    [Header("Movimento")]
    [SerializeField] private float bobAmplitude = 0.04f;
    [SerializeField] private float bobFrequency = 1.6f;

    [Tooltip("Graus por segundo de giro no próprio plano. 0 = parado.")]
    [SerializeField] private float spinDegreesPerSecond = 40f;

    [SerializeField] private bool pauseWhileHighlighted = true;

    private Transform icon;
    private MeshRenderer iconRenderer;
    private Transform cameraTransform;
    private HighlightTarget highlight;
    private Texture2D generated;
    private float phase;

    private void Awake()
    {
        highlight = GetComponent<HighlightTarget>();
        phase = Random.Range(0f, Mathf.PI * 2f);

        if (iconMaterial == null)
        {
            Debug.LogWarning($"{nameof(ItemIcon)}: sem material.", this);
            enabled = false;
            return;
        }

        if (sprite == null)
        {
            generated = BuildStarTexture(64);
            sprite = generated;
        }

        Build();
    }

    private void Build()
    {
        var holder = new GameObject("Icon");
        holder.transform.SetParent(transform, false);
        holder.transform.localScale = Vector3.one * size;
        icon = holder.transform;

        holder.AddComponent<MeshFilter>().sharedMesh = BuildQuad();
        iconRenderer = holder.AddComponent<MeshRenderer>();
        iconRenderer.sharedMaterial = iconMaterial;
        iconRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        iconRenderer.receiveShadows = false;

        var block = new MaterialPropertyBlock();
        block.SetTexture(BaseMapId, sprite);
        block.SetColor(BaseColorId, color);
        iconRenderer.SetPropertyBlock(block);
    }

    private void OnEnable()
    {
        if (icon != null)
            icon.gameObject.SetActive(true);
    }

    private void OnDisable()
    {
        if (icon != null)
            icon.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (generated != null)
            Destroy(generated);
    }

    private void LateUpdate()
    {
        if (cameraTransform == null)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;

            cameraTransform = cam.transform;
        }

        bool visible = !(pauseWhileHighlighted && highlight != null && highlight.IsHighlighted);

        if (iconRenderer.enabled != visible)
            iconRenderer.enabled = visible;

        if (!visible)
            return;

        float bob = Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f + phase) * bobAmplitude;
        icon.position = transform.position + Vector3.up * (height + bob);

        // O quad tem a face frontal em -Z (mesma convenção do Quad primitivo do Unity), então
        // o forward aponta para LONGE da câmera; ao contrário, o Cull Back esconderia o ícone.
        // Depois gira no próprio plano.
        Quaternion face = Quaternion.LookRotation(icon.position - cameraTransform.position, Vector3.up);
        icon.rotation = face * Quaternion.Euler(0f, 0f, Time.time * spinDegreesPerSecond);
    }

    private static Mesh BuildQuad()
    {
        var mesh = new Mesh { name = "IconQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
        };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        // Horário visto de -Z: a face frontal é -Z, como no Quad primitivo do Unity.
        mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Estrela de 4 pontas com borda suave: |x|^k + |y|^k, k menor que 1, dá o formato de brilho.</summary>
    private static Texture2D BuildStarTexture(int res)
    {
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            name = "ItemIconStar",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var pixels = new Color32[res * res];

        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float u = (x + 0.5f) / res * 2f - 1f;
            float v = (y + 0.5f) / res * 2f - 1f;

            float star = Mathf.Pow(Mathf.Abs(u), 0.5f) + Mathf.Pow(Mathf.Abs(v), 0.5f);
            float a = Mathf.Clamp01((1f - star) * 3f);

            pixels[y * res + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }
}
