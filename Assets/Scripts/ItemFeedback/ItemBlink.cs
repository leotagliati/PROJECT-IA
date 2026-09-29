using UnityEngine;

/// <summary>
/// [Efeito 1 — pulso] Overlay unlit translúcido cujo alpha segue uma curva: sobe rápido,
/// desce devagar, nunca chega no chapado. Na linha dos jogos da Puppet Combo, mas com
/// envelope — a troca seca para branco lia como bug de shader.
///
/// O LOOK vem do material do overlay, não daqui:
///   • Assets/Materials/ItemFlash.mat  → o item inteiro clareia (pulso cheio);
///   • Assets/Materials/ItemRim.mat    → só a silhueta acende (fresnel), centro fica intacto.
/// Mesmo componente, mesma curva; troque o material para comparar.
///
/// É unlit de propósito: atravessa escuridão, fog e a pixelização. Enquanto o item está
/// sob a mira o outline do HighlightTarget já diz "é aqui" e o pulso para. Os dois
/// convivem: o outline mora no renderingLayerMask, não nos materiais.
/// </summary>
public class ItemBlink : MonoBehaviour
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [Tooltip("Material unlit translúcido do overlay: ItemFlash (cheio) ou ItemRim (silhueta).")]
    [SerializeField] private Material overlayMaterial;

    [Header("Pulso")]
    [SerializeField] private Color color = Color.white;

    [Tooltip("Alpha no pico da curva. 1 = branco chapado, que é o que parecia bug.")]
    [SerializeField, Range(0f, 1f)] private float peakAlpha = 0.7f;

    [Tooltip("Duração de um pulso. A curva abaixo é avaliada de 0 a 1 nesse tempo.")]
    [SerializeField] private float pulseDuration = 0.5f;

    [Tooltip("Forma do pulso. Padrão: ataque curto, decaimento longo — parece reflexo, não pisca-pisca.")]
    [SerializeField] private AnimationCurve envelope = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 8f),
        new Keyframe(0.15f, 1f, 0f, 0f),
        new Keyframe(1f, 0f, -1f, 0f));

    [Tooltip("Pausa entre o fim de um pulso e o começo do próximo.")]
    [SerializeField] private float interval = 1.6f;

    [Tooltip("Vários itens na mesma sala não pulsam em uníssono.")]
    [SerializeField] private bool randomizePhase = true;

    [Header("Opcional")]
    [Tooltip("Luz que acompanha o envelope. Vazio = não usa.")]
    [SerializeField] private Light pulseLight;

    [SerializeField] private bool pauseWhileHighlighted = true;

    private RendererOverlay overlay;
    private MaterialPropertyBlock block;
    private HighlightTarget highlight;

    private float lightBaseIntensity;
    private float pulseStartTime;
    private float nextPulseTime;

    private void Awake()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        highlight = GetComponent<HighlightTarget>();
        block = new MaterialPropertyBlock();

        if (overlayMaterial == null || renderers.Length == 0)
        {
            Debug.LogWarning($"{nameof(ItemBlink)}: sem material de overlay ou sem renderer.", this);
            enabled = false;
            return;
        }

        overlay = new RendererOverlay(renderers, overlayMaterial);

        if (pulseLight != null)
            lightBaseIntensity = pulseLight.intensity;
    }

    private void OnEnable()
    {
        nextPulseTime = Time.time + (randomizePhase ? Random.Range(0f, interval) : interval);

        if (pulseLight != null)
            pulseLight.enabled = false;
    }

    private void OnDisable()
    {
        EndPulse();
    }

    private void Update()
    {
        if (pauseWhileHighlighted && highlight != null && highlight.IsHighlighted)
        {
            EndPulse();
            return;
        }

        if (!overlay.Attached)
        {
            if (Time.time >= nextPulseTime)
                BeginPulse();

            return;
        }

        float t = (Time.time - pulseStartTime) / pulseDuration;

        if (t >= 1f)
        {
            EndPulse();
            nextPulseTime = Time.time + interval;
            return;
        }

        ApplyLevel(envelope.Evaluate(t));
    }

    private void BeginPulse()
    {
        pulseStartTime = Time.time;
        overlay.Attach();

        if (pulseLight != null)
            pulseLight.enabled = true;

        ApplyLevel(0f);
    }

    private void EndPulse()
    {
        if (overlay == null)
            return;

        overlay.Detach();

        if (pulseLight != null)
            pulseLight.enabled = false;
    }

    /// <summary>Nível 0..1 do envelope aplicado ao alpha do overlay e à luz.</summary>
    private void ApplyLevel(float level)
    {
        Color c = color;
        c.a = peakAlpha * Mathf.Clamp01(level);
        block.SetColor(BaseColorId, c);
        overlay.SetPropertyBlock(block);

        if (pulseLight != null)
            pulseLight.intensity = lightBaseIntensity * level;
    }
}
