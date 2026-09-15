using UnityEngine;

public class LightFlicker : MonoBehaviour
{
    [Tooltip("Vazio: usa a Light neste GameObject.")]
    [SerializeField] private Light targetLight;

    [Header("Tremor")]
    [Tooltip("Fração da intensidade base que o ruído tira e devolve. 0 = sem tremor.")]
    [SerializeField, Range(0f, 1f)] private float noiseAmount = 0.15f;

    [Tooltip("Velocidade do ruído. Vela ~ 3; fluorescente velha ~ 12.")]
    [SerializeField] private float noiseSpeed = 10f;

    [Header("Apagão")]
    [Tooltip("Intervalo (s) entre apagões, sorteado uniforme. x=mín, y=máx.")]
    [SerializeField] private Vector2 dropoutInterval = new Vector2(1.5f, 6f);

    [Tooltip("Duração (s) de cada apagão. x=mín, y=máx.")]
    [SerializeField] private Vector2 dropoutDuration = new Vector2(0.03f, 0.15f);

    [Tooltip("Fração da intensidade base durante o apagão. 0 = apaga de vez.")]
    [SerializeField, Range(0f, 1f)] private float dropoutIntensity = 0f;

    [Tooltip("Chance de um apagão emendar em outro logo em seguida (rajada apaga-acende-apaga).")]
    [SerializeField, Range(0f, 1f)] private float burstChance = 0.4f;

    [Tooltip("Pausa (s) entre apagões de uma mesma rajada. x=mín, y=máx.")]
    [SerializeField] private Vector2 burstGap = new Vector2(0.03f, 0.12f);

    [Header("Emissiva (opcional)")]
    [Tooltip("Mesh da luminária cuja emissão acompanha a luz. Vazio: só a Light pisca.")]
    [SerializeField] private Renderer emissiveRenderer;

    [SerializeField] private int emissiveMaterialIndex = 0;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private float baseIntensity;
    private float noiseSeed;

    private float nextDropoutAt;
    private float dropoutEndsAt;
    private bool inDropout;

    // Instância própria do material: mexer no sharedMaterial suja o asset do projeto.
    private Material emissiveMaterial;
    private Color baseEmission;
    private bool hasEmissive;

    private void Awake()
    {
        if (targetLight == null)
            targetLight = GetComponent<Light>();

        if (targetLight == null)
        {
            Debug.LogWarning($"[LightFlicker] Nenhuma Light em '{name}'. Desabilitando.", this);
            enabled = false;
            return;
        }

        baseIntensity = targetLight.intensity;
        noiseSeed = Random.Range(0f, 1000f);

        if (emissiveRenderer != null)
        {
            var mats = emissiveRenderer.materials; // .materials instancia; .sharedMaterials não
            if (emissiveMaterialIndex >= 0 && emissiveMaterialIndex < mats.Length
                && mats[emissiveMaterialIndex].HasProperty(EmissionColorId))
            {
                emissiveMaterial = mats[emissiveMaterialIndex];
                baseEmission = emissiveMaterial.GetColor(EmissionColorId);
                hasEmissive = true;
            }
        }
    }

    private void OnEnable()
    {
        inDropout = false;
        ScheduleNextDropout(Time.time);
    }

    private void OnDisable()
    {
        // Deixa a luz como estava na cena, para ligar/desligar o efeito não mudar o nível.
        if (targetLight != null)
            targetLight.intensity = baseIntensity;

        if (hasEmissive)
            emissiveMaterial.SetColor(EmissionColorId, baseEmission);
    }

    private void OnDestroy()
    {
        if (emissiveMaterial != null)
            Destroy(emissiveMaterial);
    }

    private void Update()
    {
        float now = Time.time;
        UpdateDropout(now);

        float factor;
        if (inDropout)
        {
            factor = dropoutIntensity;
        }
        else
        {
            float noise = Mathf.PerlinNoise(noiseSeed, now * noiseSpeed) * 2f - 1f;
            factor = 1f + noise * noiseAmount;
        }

        targetLight.intensity = baseIntensity * factor;

        if (hasEmissive)
            emissiveMaterial.SetColor(EmissionColorId, baseEmission * factor);
    }

    private void UpdateDropout(float now)
    {
        if (inDropout)
        {
            if (now < dropoutEndsAt)
                return;

            inDropout = false;

            // Rajada: emenda outro apagão depois de uma pausa curta em vez de esperar o
            // intervalo cheio. Sem isso cada apagão é um evento isolado e previsível.
            if (Random.value < burstChance)
                nextDropoutAt = now + Random.Range(burstGap.x, burstGap.y);
            else
                ScheduleNextDropout(now);

            return;
        }

        if (now >= nextDropoutAt)
        {
            inDropout = true;
            dropoutEndsAt = now + Random.Range(dropoutDuration.x, dropoutDuration.y);
        }
    }

    private void ScheduleNextDropout(float now)
    {
        nextDropoutAt = now + Random.Range(dropoutInterval.x, dropoutInterval.y);
    }
}
