using UnityEngine;

/// <summary>
/// [Efeito 3 — luz de vela] Uma point light fraca e quente que flutua com ruído Perlin em
/// vez de pulsar. Banha o chão e a parede ao redor, então o jogador percebe o item PELA
/// SALA, antes de ver o objeto — o único efeito daqui que funciona com o item fora do
/// feixe da lanterna ou atrás de um móvel.
///
/// Custo: uma luz real por item. Com Forward+ e poucos itens, ok; com vinte chaves num
/// labirinto, não. Cria a própria Light se nenhuma for arrastada.
/// </summary>
public class ItemGlowLight : MonoBehaviour
{
    [Tooltip("Vazio: cria uma point light filha na inicialização.")]
    [SerializeField] private Light glowLight;

    [Header("Luz")]
    [SerializeField] private Color color = new Color(1f, 0.85f, 0.6f);
    [SerializeField] private float intensity = 1.2f;
    [SerializeField] private float range = 2.5f;

    [Tooltip("Sobe a luz do centro do item, para não nascer dentro do mesh e ser cortada.")]
    [SerializeField] private float heightOffset = 0.15f;

    [Header("Flicker")]
    [Tooltip("Fração da intensidade que o ruído tira e devolve. 0 = luz parada.")]
    [SerializeField, Range(0f, 1f)] private float flickerAmount = 0.35f;

    [Tooltip("Velocidade do ruído. Vela ~ 3; lâmpada com mau contato ~ 12.")]
    [SerializeField] private float flickerSpeed = 3f;

    [SerializeField] private bool pauseWhileHighlighted;

    private HighlightTarget highlight;
    private float noiseSeed;
    private bool ownsLight;

    private void Awake()
    {
        highlight = GetComponent<HighlightTarget>();
        noiseSeed = Random.Range(0f, 1000f);

        if (glowLight == null)
        {
            var holder = new GameObject("GlowLight");
            holder.transform.SetParent(transform, false);
            holder.transform.localPosition = Vector3.up * heightOffset;

            glowLight = holder.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.shadows = LightShadows.None;
            ownsLight = true;
        }

        glowLight.color = color;
        glowLight.range = range;
        glowLight.intensity = intensity;
    }

    private void OnEnable()
    {
        if (glowLight != null)
            glowLight.enabled = true;
    }

    private void OnDisable()
    {
        if (glowLight != null)
            glowLight.enabled = false;
    }

    private void OnDestroy()
    {
        if (ownsLight && glowLight != null)
            Destroy(glowLight.gameObject);
    }

    private void Update()
    {
        if (pauseWhileHighlighted && highlight != null && highlight.IsHighlighted)
        {
            glowLight.enabled = false;
            return;
        }

        glowLight.enabled = true;

        // Perlin em vez de seno: nunca repete, e a vela não "bate" num ritmo perceptível.
        float noise = Mathf.PerlinNoise(noiseSeed, Time.time * flickerSpeed) * 2f - 1f;
        glowLight.intensity = intensity * (1f + noise * flickerAmount);
    }
}
