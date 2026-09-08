using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Lanterna de mão em primeira pessoa. A luz não é filha da câmera: ela persegue a
/// rotação da câmera com atraso e sobrepõe um sway derivado da velocidade angular
/// (ou seja, do quanto o mouse mexeu no frame). O resultado é um feixe que chicoteia
/// quando você vira rápido e assenta com mola quando você para.
///
/// Ordem de execução alta de propósito: precisa rodar depois do CameraJuice,
/// que ainda está ajustando rotação e posição da câmera no LateUpdate.
/// </summary>
[DefaultExecutionOrder(100)]
public class Flashlight : MonoBehaviour
{
    [Header("Referências")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private PlayerMovement movement;

    [Tooltip("Deixe vazio para a lanterna criar a própria Light na inicialização.")]
    [SerializeField] private Light spotLight;

    [Header("Luz")]
    [SerializeField] private float range = 28f;
    [SerializeField] private float innerAngle = 22f;
    [SerializeField] private float outerAngle = 58f;
    [SerializeField] private float intensity = 4.5f;
    [SerializeField] private Color color = new Color(1f, 0.957f, 0.855f);
    [SerializeField] private bool castShadows = true;

    [Tooltip("Gera uma textura de cookie em runtime: borda macia e imperfeições do refletor. " +
             "Sem isso o cone fica com cara de holofote perfeito.")]
    [SerializeField] private bool useProceduralCookie = true;

    [Header("Posição na mão")]
    [Tooltip("Deslocamento em relação à câmera (x = direita, y = cima, z = frente).")]
    [SerializeField] private Vector3 handOffset = new Vector3(0.3f, -0.25f, 0.2f);

    [SerializeField] private float positionSharpness = 20f;

    [Header("Inércia (atraso do feixe)")]
    [Tooltip("Quanto maior, mais rápido a lanterna alcança a câmera. Baixo = braço pesado.")]
    [SerializeField] private float followSharpness = 9f;

    [Tooltip("Atraso máximo em graus. Impede o feixe de sair da tela num giro de 180 graus.")]
    [SerializeField] private float maxLagAngle = 14f;

    [Header("Sway do mouse")]
    [Tooltip("Graus de desvio para cada 100 graus/s de giro da câmera.")]
    [SerializeField] private float swayAmount = 4f;

    [SerializeField] private float maxSway = 7f;

    [Tooltip("Tempo de retorno da mola. Menor = mais nervoso.")]
    [SerializeField] private float swayReturn = 0.14f;

    [Header("Balanço da caminhada")]
    [SerializeField] private float walkSwayAngle = 0.9f;
    [SerializeField] private float runSwayAngle = 1.8f;
    [SerializeField] private float walkSwayFrequency = 8f;
    [SerializeField] private float runSwayFrequency = 12f;
    [SerializeField] private float swaySmoothing = 6f;

    [Tooltip("Inclinação lateral quando o player anda de lado.")]
    [SerializeField] private float strafeRoll = 3f;

    [Header("Flicker (mau contato)")]
    [Tooltip("A luz não respira: ela corta seco e volta. O que varia é o ritmo.")]
    [SerializeField] private bool enableFlicker = true;

    [Tooltip("Segundos de luz estável entre uma falha e outra. x = mínimo, y = máximo.")]
    [SerializeField] private Vector2 flickerInterval = new Vector2(2f, 9f);

    [Tooltip("Quanto tempo dura cada rajada de falha. x = mínimo, y = máximo.")]
    [SerializeField] private Vector2 flickerBurstDuration = new Vector2(0.05f, 0.35f);

    [Tooltip("Duração de cada piscada dentro da rajada. Valores baixos = falha nervosa.")]
    [SerializeField] private Vector2 flickerBlinkDuration = new Vector2(0.02f, 0.07f);

    [Tooltip("Quanto de luz sobra no corte. 0 = apaga de vez.")]
    [SerializeField, Range(0f, 1f)] private float flickerOffLevel = 0f;

    [Header("Liga/desliga")]
    [SerializeField] private bool startsOn = true;
    [SerializeField] private float turnOnSpeed = 14f;
    [SerializeField] private float turnOffSpeed = 22f;

    private Transform cameraTransform;
    private Transform lightTransform;

    private Quaternion smoothedRotation;
    private Quaternion previousCameraRotation;

    private float swayPitch, swayYaw;
    private float swayPitchVelocity, swayYawVelocity;

    private float bobTimer;
    private float currentBobAngle;
    private float currentBobFrequency;
    private float currentRoll;

    private bool isOn;
    private float currentIntensity;

    private float flickerTimer;
    private float burstTimer;
    private float blinkTimer;
    private bool inBurst;
    private bool blinkOn = true;
    private float flickerLevel = 1f;

    private Texture2D generatedCookie;
    private GameObject generatedLightObject;

    /// <summary>Estado atual da lanterna. Áudio e IA podem olhar isso.</summary>
    public bool IsOn => isOn;

    private void Awake()
    {
        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();

        flickerTimer = Random.Range(flickerInterval.x, flickerInterval.y);
        currentBobFrequency = walkSwayFrequency;
    }

    private void Start()
    {
        if (targetCamera == null)
        {
            Debug.LogError($"{nameof(Flashlight)}: nenhuma câmera encontrada.", this);
            enabled = false;
            return;
        }

        cameraTransform = targetCamera.transform;

        EnsureLight();

        lightTransform = spotLight.transform;
        smoothedRotation = cameraTransform.rotation;
        previousCameraRotation = cameraTransform.rotation;

        isOn = startsOn;
        currentIntensity = isOn ? intensity : 0f;
        spotLight.intensity = currentIntensity;
        spotLight.enabled = isOn;

        SnapToCamera();
    }

    /// <summary>
    /// Usa a Light do inspector se existir; senão monta uma spot solta na cena.
    /// Solta de propósito: ela não pode herdar a rotação da câmera, o atraso é o efeito.
    /// </summary>
    private void EnsureLight()
    {
        if (spotLight == null)
        {
            generatedLightObject = new GameObject("Flashlight Beam");
            spotLight = generatedLightObject.AddComponent<Light>();
        }

        ApplyLightSettings();
    }

    /// <summary>
    /// Empurra os valores do inspector para a Light. Separado do EnsureLight porque o
    /// OnValidate também chama isso: sem essa reaplicação, range/ângulo/cor/cookie só
    /// valeriam no Start e você não conseguiria tunar com o jogo rodando.
    /// </summary>
    private void ApplyLightSettings()
    {
        spotLight.type = LightType.Spot;
        spotLight.range = range;
        spotLight.spotAngle = outerAngle;
        spotLight.innerSpotAngle = innerAngle;
        spotLight.color = color;
        spotLight.shadows = castShadows ? LightShadows.Soft : LightShadows.None;
        spotLight.shadowStrength = 0.85f;
        spotLight.renderMode = LightRenderMode.ForcePixel;

        if (!useProceduralCookie)
        {
            // Só tira o cookie se ele for nosso. Um cookie que você arrastou no
            // inspector é seu, não da gente.
            if (generatedCookie != null && spotLight.cookie == generatedCookie)
                spotLight.cookie = null;

            return;
        }

        if (spotLight.cookie == null)
        {
            if (generatedCookie == null)
                generatedCookie = BuildCookie(256);

            spotLight.cookie = generatedCookie;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Chamado quando você mexe em qualquer campo no inspector, inclusive em play mode.
    /// É o que faz os parâmetros da luz valerem na hora.
    /// </summary>
    private void OnValidate()
    {
        outerAngle = Mathf.Clamp(outerAngle, 2f, 179f);
        innerAngle = Mathf.Clamp(innerAngle, 1f, outerAngle - 1f);
        range = Mathf.Max(0.1f, range);
        intensity = Mathf.Max(0f, intensity);

        // Os ranges do flicker viram Random.Range(x, y): y invertido dá resultado bobo.
        flickerInterval.y = Mathf.Max(flickerInterval.x, flickerInterval.y);
        flickerBurstDuration.y = Mathf.Max(flickerBurstDuration.x, flickerBurstDuration.y);
        flickerBlinkDuration.y = Mathf.Max(flickerBlinkDuration.x, flickerBlinkDuration.y);

        // Fora do play mode a luz nem existe ainda (ela é criada no Start).
        if (!Application.isPlaying || spotLight == null)
            return;

        ApplyLightSettings();
    }
#endif

    private void OnDestroy()
    {
        // A luz vive solta na cena, então ela não some junto com o player sozinha.
        if (generatedLightObject != null)
            Destroy(generatedLightObject);

        if (generatedCookie != null)
            Destroy(generatedCookie);
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
    }

    private void OnDisable()
    {
        PlayerInputProvider.Release();
    }

    private void Update()
    {
        if (PlayerInputProvider.Player.Flashlight.WasPressedThisFrame())
            Toggle();
    }

    public void Toggle() => SetOn(!isOn);

    public void SetOn(bool on)
    {
        isOn = on;

        if (isOn && spotLight != null)
            spotLight.enabled = true;
    }

    private void LateUpdate()
    {
        if (cameraTransform == null)
            return;

        UpdateSway();
        UpdateBob();
        UpdateTransform();
        UpdateFlicker();
        UpdateIntensity();

        previousCameraRotation = cameraTransform.rotation;
    }

    /// <summary>
    /// Extrai a velocidade angular da câmera neste frame e converte em desvio do feixe.
    /// Vem da rotação real da câmera, não do input cru, então funciona com qualquer
    /// sensibilidade e continua certo se outra coisa (recoil, cutscene) girar a câmera.
    /// </summary>
    private void UpdateSway()
    {
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);

        Quaternion delta = cameraTransform.rotation * Quaternion.Inverse(previousCameraRotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);

        if (float.IsNaN(axis.x) || float.IsInfinity(axis.x))
            angle = 0f;

        if (angle > 180f)
            angle -= 360f;

        // Velocidade angular em graus/s, trazida para o espaço da câmera:
        // x = velocidade de pitch, y = velocidade de yaw.
        Vector3 angularVelocity = cameraTransform.InverseTransformDirection(axis.normalized * angle) / dt;

        float scale = swayAmount / 100f;
        float targetPitch = Mathf.Clamp(-angularVelocity.x * scale, -maxSway, maxSway);
        float targetYaw = Mathf.Clamp(-angularVelocity.y * scale, -maxSway, maxSway);

        swayPitch = Mathf.SmoothDamp(swayPitch, targetPitch, ref swayPitchVelocity, swayReturn);
        swayYaw = Mathf.SmoothDamp(swayYaw, targetYaw, ref swayYawVelocity, swayReturn);
    }

    private void UpdateBob()
    {
        float targetAngle = 0f;
        float targetFrequency = currentBobFrequency;

        if (movement != null)
        {
            switch (movement.CurrentState)
            {
                case PlayerState.Walking:
                    targetAngle = walkSwayAngle;
                    targetFrequency = walkSwayFrequency;
                    break;

                // Agachado balança menos e mais devagar que andando de pé.
                case PlayerState.CrouchWalking:
                    targetAngle = walkSwayAngle * 0.5f;
                    targetFrequency = walkSwayFrequency * 0.7f;
                    break;

                case PlayerState.Running:
                    targetAngle = runSwayAngle;
                    targetFrequency = runSwayFrequency;
                    break;
            }
        }

        currentBobAngle = Mathf.Lerp(currentBobAngle, targetAngle, Time.deltaTime * swaySmoothing);
        currentBobFrequency = Mathf.Lerp(currentBobFrequency, targetFrequency, Time.deltaTime * swaySmoothing);

        bobTimer += Time.deltaTime * currentBobFrequency;

        if (bobTimer > Mathf.PI * 2f)
            bobTimer -= Mathf.PI * 2f;

        float targetRoll = movement != null ? -movement.MoveInput.x * strafeRoll : 0f;
        currentRoll = Mathf.Lerp(currentRoll, targetRoll, Time.deltaTime * swaySmoothing);
    }

    private void UpdateTransform()
    {
        float dt = Time.deltaTime;

        // Perseguição amortecida, independente de framerate.
        float followT = 1f - Mathf.Exp(-followSharpness * dt);
        smoothedRotation = Quaternion.Slerp(smoothedRotation, cameraTransform.rotation, followT);

        // Trava o atraso máximo para o feixe nunca sair da tela num giro brusco.
        float lag = Quaternion.Angle(smoothedRotation, cameraTransform.rotation);
        if (lag > maxLagAngle)
            smoothedRotation = Quaternion.Slerp(cameraTransform.rotation, smoothedRotation, maxLagAngle / lag);

        float bobPitch = Mathf.Sin(bobTimer) * currentBobAngle;
        float bobYaw = Mathf.Cos(bobTimer * 0.5f) * currentBobAngle * 0.7f;

        lightTransform.rotation = smoothedRotation *
            Quaternion.Euler(swayPitch + bobPitch, swayYaw + bobYaw, currentRoll);

        Vector3 targetPosition = cameraTransform.TransformPoint(handOffset);
        float positionT = 1f - Mathf.Exp(-positionSharpness * dt);
        lightTransform.position = Vector3.Lerp(lightTransform.position, targetPosition, positionT);
    }

    /// <summary>
    /// Flicker de mau contato: períodos longos de luz estável, cortados por rajadas
    /// curtas onde a luz alterna entre aceso e apagado. O valor é binário de propósito —
    /// nada de interpolação, senão vira lâmpada respirando em vez de contato falhando.
    /// </summary>
    private void UpdateFlicker()
    {
        if (!enableFlicker || !isOn)
        {
            ResetFlicker();
            return;
        }

        float dt = Time.deltaTime;

        if (!inBurst)
        {
            flickerTimer -= dt;

            if (flickerTimer > 0f)
                return;

            inBurst = true;
            burstTimer = Random.Range(flickerBurstDuration.x, flickerBurstDuration.y);
            blinkTimer = 0f;
        }

        burstTimer -= dt;

        if (burstTimer <= 0f)
        {
            ResetFlicker();
            flickerTimer = Random.Range(flickerInterval.x, flickerInterval.y);
            return;
        }

        blinkTimer -= dt;

        if (blinkTimer <= 0f)
        {
            blinkOn = !blinkOn;
            flickerLevel = blinkOn ? 1f : flickerOffLevel;
            blinkTimer = Random.Range(flickerBlinkDuration.x, flickerBlinkDuration.y);
        }
    }

    private void ResetFlicker()
    {
        inBurst = false;
        blinkOn = true;
        flickerLevel = 1f;
    }

    /// <summary>
    /// Força uma rajada de falha agora. Útil pra gameplay: susto, seeker chegando perto,
    /// bateria acabando.
    /// </summary>
    public void TriggerFlicker()
    {
        flickerTimer = 0f;
    }

    private void UpdateIntensity()
    {
        float target = isOn ? intensity : 0f;
        float speed = isOn ? turnOnSpeed : turnOffSpeed;

        // O fade suave é só do liga/desliga. O flicker entra depois, cru, como
        // multiplicador — se passasse por este Lerp o corte seco viraria mingau.
        currentIntensity = Mathf.Lerp(currentIntensity, target, 1f - Mathf.Exp(-speed * Time.deltaTime));
        spotLight.intensity = currentIntensity * flickerLevel;

        // Desliga o componente de vez quando apaga: uma spot com sombra custa caro
        // mesmo com intensidade zero. Olha a intensidade base, não a com flicker,
        // senão uma piscada mataria a luz no meio da rajada.
        if (!isOn && currentIntensity < 0.01f && spotLight.enabled)
            spotLight.enabled = false;
    }

    private void SnapToCamera()
    {
        lightTransform.position = cameraTransform.TransformPoint(handOffset);
        lightTransform.rotation = cameraTransform.rotation;
    }

    /// <summary>
    /// Cookie procedural: queda suave na borda, hot spot central e anéis fracos
    /// imitando as imperfeições de um refletor barato.
    /// </summary>
    private static Texture2D BuildCookie(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "FlashlightCookie",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };

        var pixels = new Color32[size * size];
        float center = (size - 1) * 0.5f;
        float seed = Random.value * 50f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center) / center;
                float dy = (y - center) / center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                // Borda: preto absoluto fora do círculo, senão o Clamp vaza um quadrado.
                float falloff = 1f - Mathf.SmoothStep(0.55f, 1f, distance);

                // Hot spot central.
                float hotspot = 1f + 0.35f * (1f - Mathf.SmoothStep(0f, 0.45f, distance));

                // Anéis fracos do refletor.
                float rings = 1f + 0.05f * Mathf.Sin(distance * 26f);

                // Sujeira na lente.
                float grain = 1f + 0.06f * (Mathf.PerlinNoise(seed + dx * 4f, seed + dy * 4f) - 0.5f);

                byte value = (byte)(Mathf.Clamp01(falloff * hotspot * rings * grain) * 255f);
                pixels[y * size + x] = new Color32(value, value, value, value);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(true, false);

        return texture;
    }
}
