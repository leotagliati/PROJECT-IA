using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Porta de dobradiça arrastada em tempo real (estilo Amnesia): segurando o botão, empurrar
/// o mouse para frente empurra a porta, puxar para trás puxa. Ao soltar ela segue com a
/// velocidade que tinha e para por atrito — dá para bater a porta.
///
/// Vai num objeto vazio posicionado NA DOBRADIÇA, com a malha/colisão da porta como filha. A
/// pose da cena é o ângulo 0 (fechada) e o giro é em torno de hingeLocalAxis (Y por padrão). Rigidbody cinemático:
/// a porta empurra o jogador em vez de ser empurrada, e o ângulo é sempre o que o script diz
/// (nada de HingeJoint tremendo ou porta sendo arrastada pelo próprio jogador ao andar).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class HingedDoor : MonoBehaviour, IInteractable, IDraggable
{
    [Header("Dobradiça")]
    [Tooltip("Eixo de giro no espaço local da dobradiça. Modelo importado do Blender costuma " +
             "chegar com a raiz deitada (rotação de 90° + escala 100/220), e aí o 'para cima' " +
             "do mundo é o X ou Z local, não o Y — a porta abriria girando como alçapão.")]
    [SerializeField] private Vector3 hingeLocalAxis = Vector3.up;

    [Tooltip("Limites em graus em torno do eixo da dobradiça, a partir da pose da cena. Negativo abre para o outro lado.")]
    [SerializeField] private float minAngle = 0f;

    [SerializeField] private float maxAngle = 100f;

    [SerializeField] private float startAngle = 0f;

    [Header("Arrasto")]
    [Tooltip("1 = o ponto agarrado acompanha a mão. Maior deixa a porta mais leve.")]
    [SerializeField] private float dragGain = 1f;

    [Tooltip("Teto em graus/s, segurando ou não. Segura o flick de mouse de 3000 dpi.")]
    [SerializeField] private float maxAngularSpeed = 400f;

    [Header("Inércia")]
    [Tooltip("Decaimento exponencial da velocidade depois de soltar (1/s).")]
    [SerializeField] private float friction = 3f;

    [Tooltip("Fração da velocidade devolvida ao bater no limite.")]
    [Range(0f, 1f)]
    [SerializeField] private float limitBounce = 0.15f;

    [Header("Controle (botão abre/fecha)")]
    [Tooltip("Velocidade de cruzeiro (graus/s) quando a porta se move sozinha pelo botão do controle.")]
    [SerializeField] private float autoSpeed = 110f;

    [SerializeField] private float autoAcceleration = 360f;

    [Tooltip("Velocidade com que a porta automática chega no limite. Fica entre latchMinSpeed e " +
             "slamSpeed de propósito: fechar soa o clique do trinco, abrir não bate na parede.")]
    [SerializeField] private float autoArriveSpeed = 30f;

    [Header("Trancas")]
    [Tooltip("Cadeados desta porta. Vazio = porta destrancada.")]
    [SerializeField] private List<DoorLock> locks = new List<DoorLock>();

    [Tooltip("Mensagem ao tentar mover com cadeado. {0} = cadeados restantes.")]
    [SerializeField] private string lockedMessageFormat = "Ainda há {0} cadeado(s)";

    [Header("Prompt")]
    [SerializeField] private string prompt = "Segurar para mover";

    [Header("Áudio (vazio = sem som)")]
    [SerializeField] private string lockedSoundId = "lockedDoor";

    [Tooltip("Rangido contínuo: o volume segue a velocidade da porta. Porta parada = mudo.")]
    [SerializeField] private string creakSoundId = "";

    [Tooltip("Abaixo disto (graus/s) a porta não range: segurar parada ou tremer a mão não soa.")]
    [SerializeField] private float creakMinSpeed = 8f;

    [Tooltip("A partir disto (graus/s) o rangido está no volume cheio.")]
    [SerializeField] private float creakFullSpeed = 120f;

    [Tooltip("Pitch do rangido devagar → rápido. Porta empurrada com força range mais agudo.")]
    [SerializeField] private Vector2 creakPitchRange = new Vector2(0.85f, 1.15f);

    [Tooltip("Tempo de resposta do volume (s). Curto demais estala; longo demais fica atrasado.")]
    [SerializeField] private float creakResponse = 0.08f;

    // O batente é o limite mais perto do ângulo 0 (a pose da cena é a porta fechada); o
    // outro limite é a porta escancarada contra a parede/batedor. Não precisa de collider no
    // trinco: a porta é cinemática e o ângulo é exato, enquanto um trigger daria velocidade
    // de impacto imprecisa e dependeria de a física gerar o evento.
    [Header("Áudio — fechando no batente")]
    [Tooltip("Porta chegando no batente a partir de slamSpeed: batida.")]
    [SerializeField] private string slamSoundId = "";

    [SerializeField] private float slamSpeed = 150f;

    [Tooltip("Porta chegando no batente devagar (entre latchMinSpeed e slamSpeed): clique do trinco encaixando.")]
    [SerializeField] private string latchSoundId = "";

    [Tooltip("Abaixo disto a porta só encosta, sem som nenhum.")]
    [SerializeField] private float latchMinSpeed = 5f;

    [Header("Áudio — saindo do batente")]
    [Tooltip("Porta desencaixando do batente (trinco soltando, vedação de geladeira descolando). " +
             "Toca uma vez por abertura. Vazio = sem som.")]
    [SerializeField] private string openSoundId = "";

    [Header("Áudio — abrindo até o fim")]
    [Tooltip("Porta escancarada batendo no limite de abertura. Vazio = sem som.")]
    [SerializeField] private string openStopSoundId = "";

    [SerializeField] private float openStopMinSpeed = 100f;

    [Header("Áudio — volume do impacto")]
    [Tooltip("Volume da batida no limiar → na velocidade máxima. Bater de leve não soa igual a bater com força.")]
    [SerializeField] private Vector2 impactVolumeRange = new Vector2(0.5f, 1f);

    [Tooltip("Graus que a porta precisa se afastar de um limite para um novo contato contar. " +
             "Sem isso, empurrar a porta encostada no batente repetiria o som a cada passo.")]
    [SerializeField] private float contactRearmAngle = 2f;

    private Rigidbody body;
    private Quaternion closedLocalRotation;
    private Vector3 localGrabPoint;
    private float angle;
    private float angularVelocity;
    private float pendingAngle;
    private bool grabbed;
    private bool autoMoving;
    private float autoTarget;
    private bool restingOnLow;
    private bool restingOnHigh;
    private AudioSource creakSource;
    private float creakBaseVolume;
    private float creakGain;
    private float creakGainVelocity;

    public float Angle => angle;

    public bool IsClosed => Mathf.Abs(angle) < 1f;

    public bool IsUnlocked => locks.Count == 0;

    // Sem prompt durante o arrasto: o texto embaixo da mira só atrapalharia a porta.
    public string Prompt => grabbed ? null : prompt;

    public string ErrorMessage => IsUnlocked ? null : string.Format(lockedMessageFormat, locks.Count);

    public Vector3 GrabPoint => transform.TransformPoint(localGrabPoint);

    // A pose da cena (0) é a porta fechada: o batente é o limite mais perto dela.
    private float ClosedAngle => Mathf.Abs(minAngle) <= Mathf.Abs(maxAngle) ? minAngle : maxAngle;

    private float OpenAngle => Mathf.Abs(minAngle) <= Mathf.Abs(maxAngle) ? maxAngle : minAngle;

    // Tem que ser o MESMO eixo que o FixedUpdate gira (pai * pose fechada * eixo local). Antes
    // era o up do pai, que só coincide quando a pose fechada é giro puro em Y: com a raiz
    // deitada o arrasto media o mouse num eixo e a porta girava em outro.
    private Vector3 HingeAxis
    {
        get
        {
            Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
            return parentRotation * closedLocalRotation * LocalAxis;
        }
    }

    private Vector3 LocalAxis => hingeLocalAxis.sqrMagnitude > 1e-6f ? hingeLocalAxis.normalized : Vector3.up;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;

        closedLocalRotation = transform.localRotation;
        angle = Mathf.Clamp(startAngle, Mathf.Min(minAngle, maxAngle), Mathf.Max(minAngle, maxAngle));
        transform.localRotation = closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis);

        // Porta que nasce encostada num limite já está "em repouso" nele: forçá-la contra o
        // batente no primeiro agarrão não pode soar como se tivesse acabado de fechar.
        restingOnLow = angle <= Mathf.Min(minAngle, maxAngle) + contactRearmAngle;
        restingOnHigh = angle >= Mathf.Max(minAngle, maxAngle) - contactRearmAngle;

        locks.RemoveAll(l => l == null || l.IsUnlocked);

        CreateCreakSource();
    }

    // Fonte própria, e não PlayLoop do pool: o volume precisa mudar a cada frame com a
    // velocidade, e o pool só sabe tocar e parar. Mesmo molde do PlayerBreathing.
    private void CreateCreakSource()
    {
        if (string.IsNullOrEmpty(creakSoundId) || AudioProvider.IsMuted)
            return;

        AudioLibrary library = AudioProvider.Library;
        if (library == null)
            return;

        if (!library.TryGet(creakSoundId, out AudioLibrary.SoundEntry entry) || entry.Clips.Length == 0)
        {
            Debug.LogWarning($"{name}: id '{creakSoundId}' não está na AudioLibrary — porta sem rangido.", this);
            return;
        }

        creakSource = gameObject.AddComponent<AudioSource>();
        creakSource.playOnAwake = false;
        creakSource.loop = true;
        creakSource.spatialBlend = 1f;
        creakSource.rolloffMode = library.Rolloff;
        creakSource.minDistance = library.MinDistance;
        creakSource.maxDistance = entry.MaxDistance > 0f ? entry.MaxDistance : library.MaxDistance;
        creakSource.outputAudioMixerGroup = library.MixerGroup;
        creakSource.clip = entry.Clips[Random.Range(0, entry.Clips.Length)];
        creakSource.volume = 0f;
        creakBaseVolume = entry.Volume;

        // Começa num ponto aleatório: portas iguais lado a lado não rangem em fase.
        creakSource.time = Random.Range(0f, creakSource.clip.length);
    }

    private void OnEnable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked += HandleLockUnlocked;
    }

    private void OnDisable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked -= HandleLockUnlocked;

        grabbed = false;
        autoMoving = false;
        creakGain = 0f;

        if (creakSource != null)
            creakSource.Pause();
    }

    /// <summary>
    /// Caminho do controle (o InteractionController só chama isto quando o aperto veio de um
    /// gamepad; no mouse o aperto vira arrasto). Alterna: porta fechada abre, qualquer outra
    /// coisa fecha — porta entreaberta pelo mouse fecha, que é o que se espera de "usar".
    /// Apertar de novo no meio do movimento inverte.
    /// </summary>
    public InteractionResult Interact(InteractionController interactor)
    {
        if (RefuseIfLocked(out InteractionResult refused))
            return refused;

        bool goingOpen = autoMoving
            ? Mathf.Approximately(autoTarget, ClosedAngle)
            : Mathf.Abs(angle - ClosedAngle) <= contactRearmAngle;

        autoTarget = goingOpen ? OpenAngle : ClosedAngle;
        autoMoving = true;
        return InteractionResult.Success;
    }

    private bool RefuseIfLocked(out InteractionResult result)
    {
        result = InteractionResult.Success;

        if (IsUnlocked)
            return false;

        if (!string.IsNullOrEmpty(lockedSoundId))
            AudioProvider.PlayAt(lockedSoundId, transform.position);

        result = InteractionResult.Fail(ErrorMessage);
        return true;
    }

    public InteractionResult BeginDrag(InteractionController interactor, Vector3 grabPoint)
    {
        if (RefuseIfLocked(out InteractionResult refused))
            return refused;

        // Agarrar no meio do movimento automático assume o controle na hora.
        autoMoving = false;

        // Em espaço local: o ponto agarrado gira junto com a porta, e o braço de alavanca
        // continua certo no meio do arrasto.
        localGrabPoint = transform.InverseTransformPoint(grabPoint);
        pendingAngle = 0f;
        grabbed = true;
        return InteractionResult.Success;
    }

    public void Drag(Vector3 handDelta)
    {
        if (!grabbed)
            return;

        Vector3 axis = HingeAxis;
        Vector3 arm = Vector3.ProjectOnPlane(GrabPoint - transform.position, axis);

        // Agarrar colado na dobradiça daria braço ~0 e ganho infinito.
        float armSqr = Mathf.Max(arm.sqrMagnitude, 0.2f * 0.2f);

        // Cross(eixo, braço) é a direção em que o ponto agarrado anda quando o ângulo cresce,
        // com módulo |braço|; dividir por |braço|² dá radianos. Só a componente tangente da mão
        // conta: puxar na direção da dobradiça não gira nada.
        Vector3 tangent = Vector3.Cross(axis, arm);
        pendingAngle += Vector3.Dot(handDelta, tangent) / armSqr * Mathf.Rad2Deg * dragGain;
    }

    public void EndDrag()
    {
        grabbed = false;
    }

    private void HandleLockUnlocked(DoorLock doorLock)
    {
        doorLock.Unlocked -= HandleLockUnlocked;
        locks.Remove(doorLock);
    }

    // Input chega por frame (Update) e é consumido aqui: o Rigidbody cinemático só empurra o
    // jogador direito se a rotação for aplicada no passo de física, via MoveRotation.
    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float maxStep = maxAngularSpeed * dt;
        float step;

        if (grabbed)
        {
            step = Mathf.Clamp(pendingAngle, -maxStep, maxStep);

            // O excesso é descartado, não enfileirado: senão a porta continuaria andando
            // depois que o mouse parou.
            pendingAngle = 0f;

            // Velocidade suavizada só para ter o que herdar ao soltar. Com FPS abaixo da taxa
            // de física há passos sem input, e a média evita soltar com velocidade zero.
            angularVelocity = Mathf.Lerp(angularVelocity, step / dt, 0.35f);
        }
        else if (autoMoving)
        {
            // Perfil de "rampa": acelera até autoSpeed e freia o bastante para chegar no
            // limite a autoArriveSpeed. Passa pelo mesmo angularVelocity/ApplyLimits do
            // arrasto, então rangido, clique do trinco e batida saem de graça.
            float remaining = Mathf.Abs(autoTarget - angle);
            float brakingSpeed = Mathf.Sqrt(2f * autoAcceleration * remaining);
            float speed = Mathf.Min(autoSpeed, Mathf.Max(autoArriveSpeed, brakingSpeed));

            angularVelocity = Mathf.MoveTowards(angularVelocity, Mathf.Sign(autoTarget - angle) * speed, autoAcceleration * dt);
            step = Mathf.Clamp(angularVelocity * dt, -maxStep, maxStep);
        }
        else
        {
            angularVelocity *= Mathf.Exp(-friction * dt);
            step = Mathf.Clamp(angularVelocity * dt, -maxStep, maxStep);
        }

        angle += step;
        ApplyLimits(Mathf.Abs(step) / dt);

        // Os alvos são sempre os limites, então chegar = ApplyLimits ter prendido a porta lá.
        // O quique que ele aplicou fica: a porta assenta com o atrito normal.
        if (autoMoving && Mathf.Approximately(angle, autoTarget))
            autoMoving = false;

        Quaternion parentRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
        body.MoveRotation(parentRotation * closedLocalRotation * Quaternion.AngleAxis(angle, LocalAxis));
    }

    /// <param name="impactSpeed">
    /// Velocidade real deste passo, antes do clamp. Segurando, o angularVelocity é uma média
    /// que atrasa: um flick do mouse contra o batente bateria fraco demais se lesse ele.
    /// </param>
    private void ApplyLimits(float impactSpeed)
    {
        float low = Mathf.Min(minAngle, maxAngle);
        float high = Mathf.Max(minAngle, maxAngle);

        bool closedIsLow = Mathf.Approximately(ClosedAngle, low);
        bool wasRestingClosed = closedIsLow ? restingOnLow : restingOnHigh;

        // Rearma só com folga: o quique e o tremor da mão encostada no batente não podem
        // contar como chegadas novas.
        if (angle > low + contactRearmAngle) restingOnLow = false;
        if (angle < high - contactRearmAngle) restingOnHigh = false;

        // Mesma folga vale para a saída: o som de abrir só toca quando a porta de fato largou
        // o batente, não a cada tremida da mão segurando ela fechada.
        bool restingClosed = closedIsLow ? restingOnLow : restingOnHigh;
        if (wasRestingClosed && !restingClosed && !string.IsNullOrEmpty(openSoundId))
            AudioProvider.PlayAt(openSoundId, GrabPoint);

        if (angle >= low && angle <= high)
            return;

        bool hitHigh = angle > high;
        angle = Mathf.Clamp(angle, low, high);

        bool intoLimit = hitHigh ? angularVelocity > 0f : angularVelocity < 0f;
        bool alreadyResting = hitHigh ? restingOnHigh : restingOnLow;

        if (hitHigh) restingOnHigh = true;
        else restingOnLow = true;

        if (!alreadyResting)
            PlayContact(closing: Mathf.Approximately(angle, ClosedAngle), impactSpeed);

        if (intoLimit)
            angularVelocity = -angularVelocity * limitBounce;
    }

    private void PlayContact(bool closing, float speed)
    {
        string id;
        float threshold;

        if (!closing)
        {
            id = openStopSoundId;
            threshold = openStopMinSpeed;
        }
        else if (speed >= slamSpeed)
        {
            id = slamSoundId;
            threshold = slamSpeed;
        }
        else
        {
            // Encostar de leve é um clique só, sem escala de volume: o trinco soa igual
            // não importa o quão devagar a porta chegou.
            if (speed >= latchMinSpeed && !string.IsNullOrEmpty(latchSoundId))
                AudioProvider.PlayAt(latchSoundId, GrabPoint);
            return;
        }

        if (speed < threshold || string.IsNullOrEmpty(id))
            return;

        float force = Mathf.InverseLerp(threshold, maxAngularSpeed, speed);
        AudioProvider.PlayAt(id, GrabPoint, Mathf.Lerp(impactVolumeRange.x, impactVolumeRange.y, force));
    }

    // No Update, e não no FixedUpdate: volume mexido a 50 Hz dá degrau audível. A velocidade
    // lida aqui já é a suavizada, então o rangido acompanha o empurrão e morre junto com a
    // inércia depois de soltar.
    private void Update()
    {
        if (creakSource == null)
            return;

        float speed = Mathf.Abs(angularVelocity);
        float target = Mathf.InverseLerp(creakMinSpeed, creakFullSpeed, speed);
        creakGain = Mathf.SmoothDamp(creakGain, target, ref creakGainVelocity, creakResponse);

        if (creakGain < 0.01f)
        {
            // Pause, e não Stop: o próximo empurrão continua o rangido de onde parou em vez de
            // recomeçar o clipe do início toda vez.
            if (creakSource.isPlaying)
                creakSource.Pause();
            return;
        }

        if (!creakSource.isPlaying)
            creakSource.UnPause();
        if (!creakSource.isPlaying)
            creakSource.Play();

        creakSource.volume = creakBaseVolume * creakGain;
        creakSource.pitch = Mathf.Lerp(creakPitchRange.x, creakPitchRange.y, target);
    }
}
