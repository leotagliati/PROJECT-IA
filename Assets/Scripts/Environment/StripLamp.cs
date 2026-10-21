using System.Collections;
using UnityEngine;

// Vai na raiz do prefab Strip. Cada instância sorteia no Awake se fica apagada, acesa ou
// piscando, então a cena muda a cada partida sem ninguém escolher lâmpada à mão.
// Só as acesas fazem alguma coisa depois disso: as ~120 apagadas não rodam nada.
public class StripLamp : MonoBehaviour
{
    public enum Mode { Random, Off, On, Flicker }

    [Tooltip("Random sorteia pelas chances abaixo; os outros fixam esta instância.")]
    [SerializeField] private Mode mode = Mode.Random;

    [SerializeField, Range(0f, 1f)] private float onChance = 0.04f;
    [SerializeField, Range(0f, 1f)] private float flickerChance = 0.03f;

    [Tooltip("Luz da lâmpada, já posicionada no prefab. Apagada, ela é desligada.")]
    [SerializeField] private Light lampLight;

    [Tooltip("Multiplicador HDR da emissão da malha quando acesa. A cor vem da Light.")]
    [SerializeField] private float emissionIntensity = 3f;

    [Tooltip("Além disso a Light desliga (a malha continua brilhando). 0 = nunca.")]
    [SerializeField] private float lightCullDistance = 30f;

    [Header("Som")]
    [SerializeField] private string buzzSoundId = "lamp_buzz";
    [SerializeField] private string flickerSoundId = "lamp_flicker";

    [Tooltip("O loop só ocupa voz do pool com o player dentro desta distância. Acompanhe o MaxDistance da entrada.")]
    [SerializeField] private float soundDistance = 10f;

    [Tooltip("Volume do loop da que pisca durante o apagão (fração). Corta junto com a luz.")]
    [SerializeField, Range(0f, 1f)] private float flickerDropoutVolume = 0.3f;

    [Header("Estouro")]
    [Tooltip("De quanto em quanto tempo (s) a lâmpada tenta estourar sozinha.")]
    [SerializeField] private float burstCheckInterval = 10f;

    [SerializeField, Range(0f, 1f)] private float burstChance = 0.01f;

    [Tooltip("O sorteio periódico só vale com o player dentro disto. Longe, ninguém vê nem ouve: o " +
             "estouro só gastaria uma das raras acesas e o cooldown, e puxaria o seeker para longe dele.")]
    [SerializeField] private float burstMaxDistance = 20f;

    [Tooltip("Player entrando neste raio sorteia uma vez com a chance de perto.")]
    [SerializeField] private float nearRadius = 4f;

    [SerializeField, Range(0f, 1f)] private float nearBurstChance = 0.15f;

    [Tooltip("Atraso (s) entre o player entrar no raio e o estouro, para pegar ele embaixo.")]
    [SerializeField] private Vector2 nearBurstDelay = new Vector2(0.2f, 1f);

    [Tooltip("Multiplica as duas chances nas lâmpadas que piscam: já estão falhando.")]
    [SerializeField] private float flickerBurstMultiplier = 2f;

    [Tooltip("Intervalo mínimo (s) entre estouros de QUALQUER lâmpada da cena.")]
    [SerializeField] private float globalBurstCooldown = 20f;

    [SerializeField] private string burstSoundId = "lamp_burst";

    [Tooltip("Alcance do barulho para a IA, em metros pelo grafo (garrafa quebrando = 55).")]
    [SerializeField] private float hearingMeters = 35f;

    [Tooltip("Faíscas: filho DESLIGADO com GlassShatter e o ParticleSystem. Vazio: procura nos filhos.")]
    [SerializeField] private GlassShatter sparks;

    [SerializeField] private float flashMultiplier = 4f;
    [SerializeField] private float flashDuration = 0.06f;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    // Compartilhados por todas as lâmpadas: um Find só na cena, e o cooldown é da cena toda.
    private static Transform player;
    private static float lastBurstTime = float.NegativeInfinity;

    private Material[] materials;
    private Color emission;
    private float baseIntensity;
    private LightFlicker flicker;
    private bool flickering;
    private bool broken;
    private AudioHandle loop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        player = null;
        lastBurstTime = float.NegativeInfinity;
    }

    private void Awake()
    {
        if (lampLight == null)
        {
            Debug.LogWarning($"[StripLamp] '{name}' sem Light atribuída.", this);
            enabled = false;
            return;
        }

        Mode chosen = mode == Mode.Random ? Roll() : mode;
        if (chosen == Mode.Off)
        {
            lampLight.enabled = false;
            // Um LightFlicker do prefab seguiria com Update nas ~120 apagadas.
            if (lampLight.TryGetComponent(out LightFlicker offFlicker))
                offFlicker.enabled = false;
            enabled = false;
            return;
        }

        lampLight.enabled = true;
        baseIntensity = lampLight.intensity;
        emission = lampLight.color * emissionIntensity;

        var renderers = GetComponentsInChildren<MeshRenderer>();
        materials = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            // .material instancia: o Focus_01_Lit é compartilhado pelas lâmpadas apagadas.
            materials[i] = renderers[i].material;
            materials[i].EnableKeyword("_EMISSION");
            materials[i].SetColor(EmissionColorId, emission);
        }

        // Um LightFlicker já no prefab serve para ajustar o piscar; aqui só decide se roda.
        flickering = chosen == Mode.Flicker;
        bool hasFlicker = lampLight.TryGetComponent(out flicker);
        if (flickering && !hasFlicker)
            flicker = lampLight.gameObject.AddComponent<LightFlicker>();
        else if (!flickering && hasFlicker)
            flicker.enabled = false;

        if (sparks == null)
            sparks = GetComponentInChildren<GlassShatter>(true);
    }

    private void Start()
    {
        // Só roda nas acesas: o Awake desliga o componente nas apagadas, e aí o Start não vem.
        StartCoroutine(Watch());
    }

    private Mode Roll()
    {
        float r = Random.value;
        if (r < flickerChance) return Mode.Flicker;
        if (r < flickerChance + onChance) return Mode.On;
        return Mode.Off;
    }

    // Um tique por segundo só mede distância. O sorteio longe sai a cada burstCheckInterval
    // tiques, com fase aleatória para as lâmpadas não sortearem juntas; o de perto, uma vez
    // por entrada no raio, para ficar parado embaixo não virar estouro garantido.
    private IEnumerator Watch()
    {
        var tick = new WaitForSeconds(1f);
        int ticksPerCheck = Mathf.Max(1, Mathf.RoundToInt(burstCheckInterval));
        int ticks = Random.Range(0, ticksPerCheck);
        bool wasNear = false;
        float multiplier = flickering ? flickerBurstMultiplier : 1f;

        while (!broken)
        {
            yield return tick;

            if (player == null)
            {
                var movement = FindFirstObjectByType<PlayerMovement>();
                if (movement != null) player = movement.transform;
            }

            float sqrDist = player != null
                ? (player.position - lampLight.transform.position).sqrMagnitude
                : float.PositiveInfinity;

            if (lightCullDistance > 0f)
                lampLight.enabled = sqrDist < lightCullDistance * lightCullDistance;

            // O pool tem 16 vozes: loop de lâmpada longe roubaria voz de quem importa.
            bool audible = sqrDist < soundDistance * soundDistance;
            bool playing = AudioProvider.IsPlaying(loop);
            if (audible && !playing)
                loop = AudioProvider.PlayLoop(flickering ? flickerSoundId : buzzSoundId, lampLight.transform);
            else if (!audible && playing)
                StopLoop();

            bool near = sqrDist < nearRadius * nearRadius;
            if (near && !wasNear && Random.value < nearBurstChance * multiplier)
            {
                yield return new WaitForSeconds(Random.Range(nearBurstDelay.x, nearBurstDelay.y));
                if (TryClaimBurst())
                {
                    yield return Burst();
                    yield break;
                }
            }
            wasNear = near;

            if (++ticks >= ticksPerCheck)
            {
                ticks = 0;
                if (sqrDist < burstMaxDistance * burstMaxDistance
                    && Random.value < burstChance * multiplier && TryClaimBurst())
                {
                    yield return Burst();
                    yield break;
                }
            }
        }
    }

    private bool TryClaimBurst()
    {
        if (Time.time - lastBurstTime < globalBurstCooldown)
            return false;
        lastBurstTime = Time.time;
        return true;
    }

    private IEnumerator Burst()
    {
        broken = true;
        if (flicker != null) flicker.enabled = false;
        StopLoop();

        Vector3 point = lampLight.transform.position;

        lampLight.enabled = true;
        lampLight.intensity = baseIntensity * flashMultiplier;
        SetEmission(emission * flashMultiplier);

        AudioProvider.PlayAt(burstSoundId, point);
        NoiseEvents.Emit(point, hearingMeters);
        if (sparks != null)
            sparks.Shatter(point, Vector3.down);

        yield return new WaitForSeconds(flashDuration);

        lampLight.enabled = false;
        SetEmission(Color.black);
    }

    // O LightFlicker só mexe na Light; a malha acompanha pela razão da intensidade.
    private void LateUpdate()
    {
        if (!flickering || broken) return;
        float factor = baseIntensity > 0f ? lampLight.intensity / baseIntensity : 0f;
        SetEmission(emission * factor);
        AudioProvider.SetVolume(loop, Mathf.Lerp(flickerDropoutVolume, 1f, Mathf.Clamp01(factor)));
    }

    private void StopLoop()
    {
        AudioProvider.Stop(loop);
        loop = AudioHandle.None;
    }

    private void OnDisable()
    {
        StopLoop();
    }

    private void SetEmission(Color color)
    {
        foreach (var m in materials)
            m.SetColor(EmissionColorId, color);
    }

    private void OnDestroy()
    {
        if (materials == null) return;
        foreach (var m in materials)
            if (m != null) Destroy(m);
    }
}
