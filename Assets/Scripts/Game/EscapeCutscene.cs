using System.Collections;
using Assets.Scripts.Seeker;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// Final do jogo no trigger da saída: uma cutscene em tempo real que vira uma perseguição jogável.
///
/// Cutscene (controles fora: mapa Player desligado, pause travado por <see cref="GameManager.PauseLocked"/> do trigger
/// até ser pego ou vencer; o
/// <see cref="PlayerMovement"/> recebe input roteirizado, então animação, CameraJuice, SpineLook e passos seguem os
/// mesmos): a câmera mira o checkpoint → caminha até ele (por distância: entrando de lado ou colado na parede, termina
/// sempre no centro do corredor, já com a rotação do checkpoint), com tremor e rumble um pouco antes de chegar → para
/// → alguns segundos imóvel → gira 180° e vê o Seeker sair da porta, andar até o <see cref="respawn"/> e virar para o
/// corredor.
///
/// Perseguição: no fim do giro do Seeker os efeitos de perseguição ligam (os do monstro de IA), o controle volta e
/// aparece "Fuja!". Se o Seeker encostar, não há game over: tela preta rápida, o Seeker volta desativado para onde
/// estava na cena, o player volta ao respawn (antes deste trigger) com o controle, e o trigger rearma — passando por
/// ele de novo, a cutscene inteira se repete. A vitória é de um <see cref="EscapeTrigger"/> no fim do corredor. Tudo
/// reposicionando, sem recarregar a cena: a porta fica como estiver e o estado segue Escaping.
///
/// O forward (eixo azul) do checkpoint e o do respawn apontam para o fim do corredor. O yaw é o do root do player
/// (câmera e lanterna vão junto); o pitch do <see cref="PlayerCamera"/> fica travado em <see cref="cameraPitch"/>
/// enquanto o código dirige. Pitch 0 não é horizontal: o SpineLook tira a rotação da câmera do pescoço da animação,
/// que fica um pouco levantado em relação à pose do prefab.
/// </summary>
[RequireComponent(typeof(Collider))]
public class EscapeCutscene : MonoBehaviour
{
    // Chegou: a centímetros, a direção até o ponto fica instável e o yaw já é o do checkpoint de qualquer forma.
    private const float ArriveTolerance = 0.05f;

    // Piso do input na freada: acima da zona morta de 0,1 do PlayerMovement.UpdateState, senão ele vira Idle e para
    // alguns centímetros antes do ponto.
    private const float MinInput = 0.15f;

    // Acerto final do yaw ao parar: só o resíduo do SmoothDamp, um ou dois graus.
    private const float SettleDuration = 0.2f;

    [Header("References")]
    [SerializeField] private PlayerMovement player;
    [SerializeField] private CameraShake cameraShake;
    [SerializeField] private RumbleSoundEffect rumble;
    [SerializeField] private SeekerCutscenePuppet seeker;

    // Posição = destino da caminhada; forward (no plano) = para onde o player olha ao parar.
    [SerializeField] private Transform checkpoint;

    // Marca do Seeker (anda até aqui e vira para o forward) e ponto de volta do player quando é pego.
    [SerializeField] private Transform respawn;

    // HUD que some na cutscene e volta junto com o controle: crosshair (com o prompt de interação), barra de stamina.
    // Desativa o GameObject, não o alpha: o StaminaBarUI reescreve o próprio alpha todo frame. A tela de vitória, o
    // CameraView e as dicas ficam de fora (o "Fuja!" sai pelas dicas).
    [Header("HUD")]
    [SerializeField] private GameObject[] hideOnStart;

    [Header("Walk")]
    [SerializeField, Min(0.01f)] private float alignDuration = 0.4f;
    [SerializeField, Min(0.01f)] private float turnSmoothing = 0.3f;
    [SerializeField, Min(0f)] private float faceCheckpointDistance = 2f;
    [SerializeField, Min(0.01f)] private float brakeDistance = 0.8f;
    [SerializeField, Min(0f)] private float shakeDistance = 1.2f;
    [SerializeField, Range(-30f, 30f)] private float cameraPitch = 6f;
    [SerializeField, Min(0.1f)] private float maxWalkTime = 8f;

    [Header("Look Back")]
    [FormerlySerializedAs("stillAfterShake")]
    [SerializeField, Min(0f)] private float stillAtCheckpoint = 4f;
    [SerializeField, Min(1f)] private float turnBackSpeed = 130f;
    [SerializeField] private bool turnBackToRight = true;

    [Header("Escape")]
    [SerializeField] private string escapeHintText = "*Fuja!*";
    [SerializeField, Min(0f)] private float escapeHintDuration = 2.5f;

    // Vazio = um overlay preto montado em código, por cima de tudo.
    [Header("Restart (s)")]
    [SerializeField] private CanvasGroup blackFade;
    [SerializeField, Min(0.01f)] private float blackFadeOut = 0.4f;
    [SerializeField, Min(0f)] private float blackHold = 0.2f;
    [SerializeField, Min(0.01f)] private float blackFadeIn = 0.4f;

    private bool fired;
    private bool tookInput;
    private bool restarting;
    private bool fadingAudio;
    private PlayerCamera playerCamera;
    private ChaseMusic chaseMusic;
    private HintController hints;
    private HintHandle hintHandle;
    private float yawVelocity;

    // Pose de onde o Seeker foi posto na cena: é para lá que ele volta a cada reinício.
    private Vector3 seekerStartPosition;
    private Quaternion seekerStartRotation;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    // Aqui, e não no Awake do Seeker: ele começa desativado e o Awake dele só roda quando a cutscene o liga. Esquecido
    // ativo na cena, ficaria na porta a partida inteira, com a estática tocando; desligado aqui, antes de qualquer
    // Start, o Start dele (que liga áudio e visual) só roda ao ativar.
    private void Awake()
    {
        GameManager.StateChanged += HandleGameState;
        WinScreenUI.CutToWhite += HandleCutToWhite;

        if (seeker == null)
            return;

        seekerStartPosition = seeker.transform.position;
        seekerStartRotation = seeker.transform.rotation;

        // Uma vez só, aqui: a cutscene pode rodar várias vezes (uma por captura) e o Begin inscreveria de novo.
        seeker.ChaseStarted += HandleChaseStarted;
        seeker.Caught += HandleCaught;

        if (seeker.gameObject.activeSelf)
        {
            Debug.LogWarning($"{name}: o Seeker da cutscene estava ativo na cena — desativado até a cutscene.", seeker);
            seeker.gameObject.SetActive(false);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (fired)
            return;

        PlayerMovement entered = other.GetComponentInParent<PlayerMovement>();
        if (entered == null)
            return;

        if (player == null)
            player = entered;

        Begin();
    }

#if UNITY_EDITOR
    [ContextMenu("Testar cutscene")]
    private void TestCutscene()
    {
        if (!Application.isPlaying || fired)
            return;

        if (player == null)
            player = FindFirstObjectByType<PlayerMovement>();

        if (player != null)
            Begin();
    }
#endif

    private void Begin()
    {
        fired = true;

        // Sem destino não há caminhada, e sem respawn o Seeker não tem para onde ir; melhor não começar do que tirar
        // o controle e deixar o player parado.
        if (checkpoint == null || respawn == null)
        {
            Debug.LogError($"{name}: checkpoint ou respawn não atribuído — a cutscene final não roda.", this);
            return;
        }

        playerCamera = player.GetComponentInChildren<PlayerCamera>();
        hints = FindFirstObjectByType<HintController>();
        chaseMusic = FindFirstObjectByType<ChaseMusic>();

        if (cameraShake == null)
            cameraShake = FindFirstObjectByType<CameraShake>();
        if (rumble == null)
            rumble = FindFirstObjectByType<RumbleSoundEffect>();

        if (seeker == null)
            Debug.LogWarning($"{name}: Seeker da cutscene não atribuído — o player olha para trás e não vê ninguém.", this);

        // Na segunda passada (depois de uma captura) o jogo já está em Escaping e o BeginEscape não faz nada. O pause
        // trava daqui até ser pego (o Restart solta) ou vencer.
        if (GameManager.Current != null)
        {
            GameManager.Current.BeginEscape();
            GameManager.Current.PauseLocked = true;
        }
        else
            Debug.LogError($"{name}: sem GameManager na cena — a cutscene roda, mas não termina o jogo.", this);

        TakeControl();
        SetHud(false);

        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        Transform body = player.transform;
        float checkpointYaw = YawOf(checkpoint.forward);

        // 1-2. Caminhada até o checkpoint, com o tremor perto da chegada.
        yield return WalkToCheckpoint(body, checkpointYaw);

        // 3. Parado e reto: fecha o resíduo do SmoothDamp para olhar exatamente o forward do checkpoint.
        float residual = Mathf.DeltaAngle(body.eulerAngles.y, checkpointYaw);
        if (Mathf.Abs(residual) > 0.1f)
            yield return Turn(body, residual, SettleDuration);

        // Um bom tempo imóvel: o estrondo assenta antes de ele ousar olhar para trás.
        yield return Wait(stillAtCheckpoint);

        // 4. O Seeker aparece parado no início do giro, ainda fora do campo de visão: quando o olhar chega, ele já
        // está lá, e não brota na tela.
        if (seeker != null)
            seeker.Appear();

        // 5. Meia-volta exata. Delta assinado: com 180° o LerpAngle escolheria o lado sozinho.
        float side = turnBackToRight ? 1f : -1f;
        yield return Turn(body, 180f * side, 180f / turnBackSpeed);

        // Só agora, com o jogador olhando, ele anda até o respawn e vira: andando durante o giro, a caminhada
        // acontecia fora de quadro e o que se via era só ele girando.
        if (seeker != null)
            seeker.Begin(respawn, body);

        // 6. Parado, olhando, até o Seeker chegar na marca e virar: o HandleChaseStarted devolve o controle. O pitch
        // segue travado até lá. Sem Seeker, devolve já.
        if (seeker == null)
        {
            ReturnControl(showHint: true);
            yield break;
        }

        while (tookInput)
        {
            LockPitch();
            yield return null;
        }
    }

    /// <summary>
    /// Anda até o checkpoint como um jogador andaria: o input aponta para o ponto no espaço do player (se o olhar
    /// ainda não chegou lá, ele anda na diagonal, como quem segura W+A) e o yaw vai corrigindo aos poucos. Longe,
    /// ele olha para o ponto; perto, passa a olhar como o checkpoint olha, e chega já reto.
    /// </summary>
    private IEnumerator WalkToCheckpoint(Transform body, float checkpointYaw)
    {
        float startYaw = body.eulerAngles.y;
        float startPitch = playerCamera != null ? playerCamera.Pitch : cameraPitch;
        bool shaken = false;
        yawVelocity = 0f;

        for (float t = 0f; t < maxWalkTime; t += Time.deltaTime)
        {
            Vector3 toPoint = Flat(checkpoint.position - body.position);
            float distance = toPoint.magnitude;
            if (distance <= ArriveTolerance)
                break;

            // Ainda andando, um pouco antes de chegar: o estrondo pega ele no passo e ele para logo depois.
            if (!shaken && distance <= shakeDistance)
            {
                shaken = true;
                PlayShake();
            }

            float k = faceCheckpointDistance > 0f
                ? Mathf.SmoothStep(0f, 1f, 1f - distance / faceCheckpointDistance)
                : 1f;
            float targetYaw = Mathf.LerpAngle(YawOf(toPoint), checkpointYaw, k);

            float yaw;
            if (t < alignDuration)
            {
                // Alinhamento da entrada: acerta o olhar e mira o ponto, com começo e fim suaves. O alvo pode mudar
                // enquanto ele anda, por isso o lerp é até o alvo DESTE frame.
                float a = Mathf.SmoothStep(0f, 1f, t / alignDuration);
                yaw = Mathf.LerpAngle(startYaw, targetYaw, a);

                if (playerCamera != null)
                    playerCamera.Pitch = Mathf.Lerp(startPitch, cameraPitch, a);
            }
            else
            {
                yaw = Mathf.SmoothDampAngle(body.eulerAngles.y, targetYaw, ref yawVelocity, turnSmoothing);
                LockPitch();
            }

            body.rotation = Quaternion.Euler(0f, yaw, 0f);

            // Freada pela distância: a velocidade é input × speed, então ele desacelera chegando, e o Animator
            // (com damp) passa de Walk a Idle sozinho.
            Vector3 local = body.InverseTransformDirection(toPoint / distance);
            float magnitude = Mathf.Clamp(distance / brakeDistance, MinInput, 1f);
            player.SetScriptedInput(new Vector2(local.x, local.z) * magnitude, false);

            yield return null;
        }

        player.SetScriptedInput(Vector2.zero, false);
        LockPitch();

        // Caminho bloqueado (saiu pelo maxWalkTime longe do ponto): o tremor não pode ficar de fora.
        if (!shaken)
            PlayShake();
    }

    // ------------------------------------------------------------------ perseguição

    private void HandleChaseStarted()
    {
        if (tookInput)
            ReturnControl(showHint: true);
    }

    /// <summary>
    /// Passou pelo EscapeTrigger do fim do corredor: em vez de parar seco, os dois seguem durante o clarão — o player
    /// andando (ou correndo, se já corria) para a luz e o Seeker vindo atrás, com o som descendo junto com a imagem.
    /// Só quando a tela fica toda branca (<see cref="WinScreenUI.CutToWhite"/>) o Seeker congela; o player, a
    /// PlayerEscapedSequence desliga.
    /// </summary>
    private void HandleGameState(GameState state)
    {
        if (state != GameState.Won || !fired || restarting || player == null)
            return;

        // Corre para a luz se já vinha correndo; parado ou andando, anda. O yaw e o olhar ficam onde ele deixou.
        bool running = player.CurrentState == PlayerState.Running;
        TakeControl();
        player.SetScriptedInput(Vector2.up, running);

        // Sem tela de vitória ninguém dispara o CutToWhite: congela já, para o monstro não andar para sempre.
        if (FindFirstObjectByType<WinScreenUI>() == null && seeker != null)
            seeker.Freeze();
    }

    private void HandleCutToWhite()
    {
        if (seeker != null)
            seeker.Freeze();
    }

    private void HandleCaught()
    {
        if (restarting || (GameManager.Current != null && GameManager.Current.IsOver))
            return;

        StartCoroutine(Restart());
    }

    /// <summary>
    /// Pego na fuga: sem game over, só uma tela preta rápida e tudo de volta a antes do final. O Seeker volta
    /// desativado para a pose da cena, o player volta ao respawn (antes do trigger) olhando para o corredor e já com o
    /// controle, e o trigger rearma: passando por ele de novo, a cutscene inteira se repete (checkpoint, tremor,
    /// giro, o Seeker saindo da porta).
    /// </summary>
    private IEnumerator Restart()
    {
        restarting = true;

        TakeControl();
        if (hints != null)
            hints.Hide(hintHandle);

        // O som do jogo inteiro desce junto com a imagem (pelo Fade do AudioProvider, sem tocar no volume do jogador).
        CanvasGroup fade = BlackFade();
        fadingAudio = true;
        yield return FadeBlack(fade, 0f, 1f, blackFadeOut);

        // No preto, os resíduos morrem de vez: o tema de perseguição levaria segundos para sumir sozinho e vazaria
        // para o respawn, e o pool ainda pode ter passos ou o estrondo tocando. A estática do Seeker para com ele
        // desativado logo abaixo. O que voltar com o volume já é o silêncio do corredor.
        if (chaseMusic != null)
            chaseMusic.Cut();
        AudioProvider.StopAll();

        if (seeker != null)
            seeker.ResetTo(seekerStartPosition, seekerStartRotation);

        // Mesmo padrão do GameManager.SkipTutorial: o CharacterController desfaz um teleporte feito com ele ligado.
        CharacterController controller = player.GetComponent<CharacterController>();
        if (controller != null)
            controller.enabled = false;

        player.transform.SetPositionAndRotation(respawn.position, Quaternion.Euler(0f, YawOf(respawn.forward), 0f));

        if (controller != null)
            controller.enabled = true;

        LockPitch();

        yield return Wait(blackHold);
        yield return FadeBlack(fade, 1f, 0f, blackFadeIn);
        fadingAudio = false;

        // Sem "Fuja!": ainda não há ninguém atrás dele. O Seeker fica desativado até a próxima passada pelo trigger.
        restarting = false;
        ReturnControl(showHint: false);
        fired = false;

        // Antes do trigger de novo: pode pausar (configurações etc.) até passar por ele.
        if (GameManager.Current != null)
            GameManager.Current.PauseLocked = false;
    }

    // O mapa UI fica ligado no lugar do Player; o Esc dele cai no PauseControler, que recusa com o PauseLocked.
    private void TakeControl()
    {
        PlayerInputProvider.SetMode(InputMode.UI);
        player.SetScriptedInput(Vector2.zero, false);
        tookInput = true;
    }

    private void ReturnControl(bool showHint)
    {
        player.ClearScriptedInput();
        PlayerInputProvider.SetMode(InputMode.Player);
        tookInput = false;

        // A barra de stamina importa na fuga.
        SetHud(true);

        if (showHint && hints != null && !string.IsNullOrEmpty(escapeHintText))
            hintHandle = hints.Show(escapeHintText, escapeHintDuration);
    }

    private void SetHud(bool visible)
    {
        if (hideOnStart == null)
            return;

        foreach (GameObject hud in hideOnStart)
        {
            if (hud != null)
                hud.SetActive(visible);
        }
    }

    // Imagem e som na mesma curva: tela toda preta = jogo todo mudo.
    private IEnumerator FadeBlack(CanvasGroup fade, float from, float to, float duration)
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            fade.alpha = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / duration));
            AudioProvider.Fade = 1f - fade.alpha;
            yield return null;
        }

        fade.alpha = to;
        AudioProvider.Fade = 1f - to;
    }

    // Overlay próprio quando nenhum foi arrastado: Canvas por cima de tudo (inclusive do CameraView, que mostra a
    // RenderTexture do player), sem bloquear clique.
    private CanvasGroup BlackFade()
    {
        if (blackFade != null)
            return blackFade;

        var canvasObject = new GameObject("EscapeBlackFade", typeof(Canvas), typeof(CanvasGroup));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        blackFade = canvasObject.GetComponent<CanvasGroup>();
        blackFade.alpha = 0f;
        blackFade.interactable = false;
        blackFade.blocksRaycasts = false;

        var imageObject = new GameObject("Black", typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(canvasObject.transform, false);

        var rect = (RectTransform)imageObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = imageObject.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;

        return blackFade;
    }

    // ------------------------------------------------------------------ utilidades

    // Escrito todo frame enquanto o código dirige: a mesma inclinação do começo ao fim, aconteça o que acontecer.
    private void LockPitch()
    {
        if (playerCamera != null)
            playerCamera.Pitch = cameraPitch;
    }

    private void PlayShake()
    {
        if (cameraShake != null)
            cameraShake.Play();
        if (rumble != null)
            rumble.Play();
    }

    // Giro com começo e fim suaves (SmoothStep): é um olhar, não uma torre girando a velocidade constante. Delta
    // assinado em graus (+ = direita), para o lado ser sempre o pedido. Quem chama converte a velocidade do
    // Inspector em duração (graus / velocidade): a média é a pedida, o pico no meio fica ~1,5× ela.
    private IEnumerator Turn(Transform body, float deltaDegrees, float duration)
    {
        float startYaw = body.eulerAngles.y;
        float endYaw = startYaw + deltaDegrees;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / duration);
            body.rotation = Quaternion.Euler(0f, Mathf.Lerp(startYaw, endYaw, k), 0f);
            LockPitch();
            yield return null;
        }

        body.rotation = Quaternion.Euler(0f, endYaw, 0f);
    }

    private IEnumerator Wait(float seconds)
    {
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            LockPitch();
            yield return null;
        }
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

    private void OnDestroy()
    {
        GameManager.StateChanged -= HandleGameState;
        WinScreenUI.CutToWhite -= HandleCutToWhite;

        if (seeker != null)
        {
            seeker.ChaseStarted -= HandleChaseStarted;
            seeker.Caught -= HandleCaught;
        }

        // O modo de input é estático e sobrevive ao load: sem isto o menu (e o jogo seguinte) abriria com o mapa
        // Player desligado. O PauseControler só restaura o modo se a cena descarregar pausada.
        if (tookInput)
            PlayerInputProvider.SetMode(InputMode.Player);

        // Mesma razão para o som: cena trocada no meio da tela preta não pode abrir a próxima muda.
        if (fadingAudio)
            AudioProvider.Fade = 1f;
    }
}
