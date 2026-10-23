using System.Collections;
using Assets.Scripts.Seeker;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Final do jogo. Trigger alguns metros corredor adentro depois da porta de saída:
/// estrondo → a porta aparece caída e o Seeker parado nela (fora de quadro: o player está de costas) → o player vira
/// → plano da câmera do Seeker olhando o player → volta à primeira pessoa com o controle → o Seeker persegue em linha
/// reta. Quem termina é o <see cref="EscapeTrigger"/> no fim do corredor.
///
/// Pego, é a morte normal (PlayerCaughtSequence + game over). Daí em diante a sessão lembra o checkpoint: o reload
/// põe o player no <see cref="checkpoint"/>, diante da porta de saída já destrancada, e a cutscene roda de novo.
/// </summary>
[RequireComponent(typeof(Collider))]
public class EscapeCutscene : MonoBehaviour
{
    [Header("Referências")]
    [Tooltip("Vazio: o PlayerMovement da cena.")]
    [SerializeField] private PlayerMovement player;

    [Tooltip("Desativado na cena, parado na porta.")]
    [SerializeField] private SeekerChasePuppet seeker;

    [Tooltip("Câmera filha da cabeça do Seeker. O código a desliga e só a liga durante o plano.")]
    [SerializeField] private Camera seekerCamera;

    [Tooltip("Para onde a câmera do player vira quando é pego (o DeathLookTarget do Seeker do final).")]
    [SerializeField] private Transform deathLookTarget;

    [Tooltip("Onde o player renasce depois de morrer no final. O forward (eixo azul) aponta para a porta.")]
    [SerializeField] private Transform checkpoint;

    [Header("Porta")]
    [Tooltip("A porta de saída (com o Lockable). No checkpoint ela já começa destrancada.")]
    [SerializeField] private GameObject standingDoor;
    [SerializeField] private GameObject fallenDoor;

    [Header("Estrondo")]
    // Juntos, na porta: madeira quebrando + o vidro da janelinha. Vem de trás do player, por isso não segue a câmera.
    [Tooltip("Entradas da AudioLibrary tocadas juntas na posição da porta.")]
    [SerializeField] private string[] boomSoundIds = { "door_break", "throwable_smash" };
    [SerializeField, Min(0f)] private float shakeDuration = 1.2f;
    [SerializeField, Min(0f)] private float shakeMagnitude = 0.08f;

    [Header("Tempos (s)")]
    [SerializeField, Min(0f)] private float beforeTurn = 1f;
    [SerializeField, Min(0.01f)] private float turnDuration = 0.8f;
    [Tooltip("Pitch ao terminar de virar. Quem entrou olhando o chão precisa levantar o olhar para ver o Seeker.")]
    [SerializeField, Range(-30f, 30f)] private float turnPitch = 0f;
    [SerializeField, Min(0f)] private float lookAtSeeker = 1f;
    [SerializeField, Min(0f)] private float seekerShot = 2f;

    [Header("Cinema")]
    [Tooltip("Altura de cada barra preta, em fração da tela.")]
    [SerializeField, Range(0f, 0.3f)] private float barHeight = 0.12f;
    [SerializeField, Min(0.01f)] private float barSlide = 0.4f;

    // Desativa o GameObject em vez de zerar alpha: o StaminaBarUI reescreve o próprio alpha todo frame. As dicas não
    // entram aqui: o HintController se cala sozinho enquanto o GameManager.PauseLocked está ligado.
    [Tooltip("UI escondida enquanto o jogo tira o controle (crosshair, stamina, chaves).")]
    [SerializeField] private GameObject[] hideDuringCutscene = { };

    [Header("Fuga")]
    [SerializeField] private string escapeHint = "*Fuja!*";
    [SerializeField, Min(0f)] private float escapeHintDuration = 2.5f;

    // Sobrevive ao reload do game over (GameManager.Restart); só o fim da sessão apaga.
    private static bool reachedCheckpoint;

    private bool fired;
    private bool tookControl;
    private PlayerCamera playerLook;
    private PlayerEscapedSequence escapedSequence;
    private RectTransform topBar;
    private RectTransform bottomBar;
    private Coroutine barsRoutine;
    private float barsAmount;
    private bool[] hudWasActive;

    /// <summary>Esquece o checkpoint: o próximo jogo pega as chaves de novo.</summary>
    public static void ForgetCheckpoint() => reachedCheckpoint = false;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void Awake()
    {
        if (seekerCamera != null)
            seekerCamera.enabled = false;
        if (fallenDoor != null)
            fallenDoor.SetActive(false);

        if (seeker != null)
        {
            seeker.Caught += HandleCaught;
            seeker.gameObject.SetActive(false);
        }
    }

    // No Start, e não no Awake: o Lockable limpa a própria lista no Awake dele, e o GameManager já pôs o player no
    // respawn do tutorial (o Awake dele roda antes de todos).
    private void Start()
    {
        if (player == null)
            player = FindFirstObjectByType<PlayerMovement>();

        if (reachedCheckpoint)
            RestoreCheckpoint();
    }

    private void RestoreCheckpoint()
    {
        if (standingDoor != null && standingDoor.TryGetComponent(out Lockable lockable))
            lockable.UnlockSilently();

        if (checkpoint == null || player == null)
        {
            Debug.LogWarning($"{name}: sem checkpoint ou player — renasce no respawn normal, com a porta destrancada.", this);
            return;
        }

        // O CharacterController desfaz um teleporte feito com ele ligado (mesmo padrão do GameManager.SkipTutorial).
        CharacterController controller = player.GetComponent<CharacterController>();
        if (controller != null)
            controller.enabled = false;
        player.transform.SetPositionAndRotation(checkpoint.position, Quaternion.Euler(0f, checkpoint.eulerAngles.y, 0f));
        if (controller != null)
            controller.enabled = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (fired)
            return;

        PlayerMovement entered = other.GetComponentInParent<PlayerMovement>();
        if (entered == null)
            return;

        player = entered;
        Begin();
    }

#if UNITY_EDITOR
    [ContextMenu("Testar cutscene")]
    private void TestCutscene()
    {
        if (Application.isPlaying && !fired && player != null)
            Begin();
    }
#endif

    private void Begin()
    {
        fired = true;

        if (seeker == null)
        {
            Debug.LogError($"{name}: seeker não atribuído — o final não roda.", this);
            return;
        }

        reachedCheckpoint = true;
        playerLook = player.GetComponentInChildren<PlayerCamera>();

        escapedSequence = player.GetComponentInParent<PlayerEscapedSequence>();
        if (escapedSequence == null)
            escapedSequence = player.GetComponentInChildren<PlayerEscapedSequence>();
        if (escapedSequence != null)
            escapedSequence.CutToWhite += HandleCutToWhite;

        if (GameManager.Current != null)
            GameManager.Current.BeginEscape();

        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        TakeControl();
        SetCinematic(true);

        CameraShake shake = player.GetComponentInChildren<CameraShake>();
        if (shake != null)
            shake.Impulse(shakeDuration, shakeMagnitude);
        Vector3 doorPosition = standingDoor != null ? standingDoor.transform.position : seeker.transform.position;
        foreach (string id in boomSoundIds)
        {
            if (!string.IsNullOrEmpty(id))
                AudioProvider.PlayAt(id, doorPosition);
        }

        if (standingDoor != null)
            standingDoor.SetActive(false);
        if (fallenDoor != null)
            fallenDoor.SetActive(true);

        seeker.Appear(player.transform);

        yield return new WaitForSeconds(beforeTurn);
        yield return TurnTowards(seeker.transform.position);
        yield return new WaitForSeconds(lookAtSeeker);
        yield return SeekerShot();

        SetCinematic(false);
        ReturnControl();

        HintController hints = FindFirstObjectByType<HintController>();
        if (hints != null && !string.IsNullOrEmpty(escapeHint))
            hints.Show(escapeHint, escapeHintDuration);

        seeker.Chase();
    }

    private IEnumerator TurnTowards(Vector3 point)
    {
        Transform body = player.transform;
        Vector3 toPoint = point - body.position;
        float startYaw = body.eulerAngles.y;
        float delta = Mathf.DeltaAngle(startYaw, Mathf.Atan2(toPoint.x, toPoint.z) * Mathf.Rad2Deg);
        float startPitch = playerLook != null ? playerLook.Pitch : turnPitch;

        for (float t = 0f; t < turnDuration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / turnDuration);
            body.rotation = Quaternion.Euler(0f, startYaw + delta * k, 0f);
            if (playerLook != null)
                playerLook.Pitch = Mathf.Lerp(startPitch, turnPitch, k);
            yield return null;
        }

        body.rotation = Quaternion.Euler(0f, startYaw + delta, 0f);
        if (playerLook != null)
            playerLook.Pitch = turnPitch;
    }

    // A câmera do Seeker pega emprestada a RenderTexture do player, então o RetroScreen vale igual no plano.
    private IEnumerator SeekerShot()
    {
        Camera playerCamera = playerLook != null ? playerLook.playerCamera : null;
        if (seekerCamera == null || playerCamera == null)
            yield break;

        // De fora, o player sem cabeça (ShadowsOnly em primeira pessoa) aparece decapitado: mostra só para esta câmera.
        ShowInReflections head = player.GetComponentInParent<ShowInReflections>();
        if (head == null)
            head = player.GetComponentInChildren<ShowInReflections>();
        if (head != null)
            head.ExtraCamera = seekerCamera;

        seekerCamera.transform.LookAt(playerCamera.transform.position);
        seekerCamera.targetTexture = playerCamera.targetTexture;
        playerCamera.enabled = false;
        seekerCamera.enabled = true;

        yield return new WaitForSeconds(seekerShot);

        seekerCamera.enabled = false;
        playerCamera.enabled = true;
        if (head != null)
            head.ExtraCamera = null;
    }

    // Congela quando a tela já está toda branca; até lá ele segue vindo atrás do player durante o clarão.
    private void HandleCutToWhite() => seeker.Freeze();

    // A morte de sempre: o lookTarget do player aponta para o monstro de IA, que não é quem pegou.
    private void HandleCaught()
    {
        if (GameManager.Current == null)
            return;

        PlayerCaughtSequence caught = player.GetComponentInParent<PlayerCaughtSequence>();
        if (caught == null)
            caught = player.GetComponentInChildren<PlayerCaughtSequence>();
        if (caught != null && deathLookTarget != null)
            caught.LookTarget = deathLookTarget;

        GameManager.Current.PlayerCaught();
    }

    // O mapa UI fica no lugar do Player: o Esc cai no PauseControler, que recusa pelo PauseLocked.
    private void TakeControl()
    {
        PlayerInputProvider.SetMode(InputMode.UI);
        tookControl = true;
        if (GameManager.Current != null)
            GameManager.Current.PauseLocked = true;
    }

    private void ReturnControl()
    {
        PlayerInputProvider.SetMode(InputMode.Player);
        tookControl = false;
        if (GameManager.Current != null)
            GameManager.Current.PauseLocked = false;
    }

    private void SetCinematic(bool on)
    {
        SetHudVisible(!on);

        if (barsRoutine != null)
            StopCoroutine(barsRoutine);
        barsRoutine = StartCoroutine(SlideBars(on ? 1f : 0f));
    }

    // Guarda o estado de cada um ao esconder: o que já estava desligado fica desligado.
    private void SetHudVisible(bool visible)
    {
        if (!visible)
        {
            hudWasActive = new bool[hideDuringCutscene.Length];
            for (int i = 0; i < hideDuringCutscene.Length; i++)
            {
                if (hideDuringCutscene[i] == null)
                    continue;

                hudWasActive[i] = hideDuringCutscene[i].activeSelf;
                hideDuringCutscene[i].SetActive(false);
            }
        }
        else if (hudWasActive != null)
        {
            for (int i = 0; i < hideDuringCutscene.Length; i++)
            {
                if (hideDuringCutscene[i] != null)
                    hideDuringCutscene[i].SetActive(hudWasActive[i]);
            }

            hudWasActive = null;
        }
    }

    // Sai do valor atual, não do extremo: uma volta no meio da entrada não dá salto.
    private IEnumerator SlideBars(float target)
    {
        if (topBar == null)
        {
            topBar = CreateOverlay("CinematicBars", 500, Color.black).rectTransform;
            bottomBar = Instantiate(topBar.gameObject, topBar.parent).GetComponent<RectTransform>();
        }

        float from = barsAmount;
        for (float t = 0f; t < barSlide; t += Time.deltaTime)
        {
            ApplyBars(Mathf.Lerp(from, target, Mathf.SmoothStep(0f, 1f, t / barSlide)));
            yield return null;
        }

        ApplyBars(target);
        barsRoutine = null;
    }

    private void ApplyBars(float amount)
    {
        barsAmount = amount;
        float height = barHeight * amount;

        topBar.anchorMin = new Vector2(0f, 1f - height);
        topBar.anchorMax = Vector2.one;
        bottomBar.anchorMin = Vector2.zero;
        bottomBar.anchorMax = new Vector2(1f, height);
    }

    // Image em tela cheia num Canvas próprio, por cima do CameraView que mostra a RenderTexture.
    private Image CreateOverlay(string overlayName, int sortingOrder, Color color)
    {
        var root = new GameObject(overlayName, typeof(Canvas));
        root.transform.SetParent(transform, false);

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        var imageObject = new GameObject("Image", typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(root.transform, false);

        var rect = (RectTransform)imageObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private void OnDestroy()
    {
        if (seeker != null)
            seeker.Caught -= HandleCaught;
        if (escapedSequence != null)
            escapedSequence.CutToWhite -= HandleCutToWhite;

        // O modo de input é estático e sobrevive ao load: cena trocada no meio da cutscene abriria sem controle.
        if (tookControl)
            PlayerInputProvider.SetMode(InputMode.Player);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => reachedCheckpoint = false;
}
