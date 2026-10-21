using System;
using UnityEngine;

/// <summary>
/// Comportamento do diálogo: qual fala está na tela, o ritmo da digitação, o que o avançar faz (revela a fala que
/// ainda está sendo digitada; completa, passa para a próxima; depois da última, fecha) e o estado do jogo enquanto
/// a conversa dura — pausa e input no mapa UI, sem o qual o mouse seguiria girando a câmera (Look é delta, não
/// para com timeScale 0). Tudo que aparece na tela, e o input em si, é do <see cref="DialogueView"/>.
///
/// Um por cena. Não conhece interação nem dicas: quem abre é o <see cref="DialogueTrigger"/>, e quem precisa saber
/// que há diálogo na tela (pausa, dicas) lê <see cref="IsOpen"/>.
/// </summary>
public class DialogueController : MonoBehaviour
{
    [Tooltip("Não pode ficar dentro do root da view: desligar a caixa desligaria o controller junto.")]
    [SerializeField] private DialogueView view;

    [Header("Digitação")]
    [SerializeField, Min(1f)] private float charactersPerSecond = 45f;
    [SerializeField, Min(1)] private int soundEveryCharacters = 2;

    [Tooltip("Id da AudioLibrary. Vazio = sem som.")]
    [SerializeField] private string typingSound = "dialogue_type";

    [Header("Jogo")]
    [SerializeField] private bool pauseGame = true;

    private const float MaxTypingStep = 1f / 30f;

    private static int closedFrame = -1;

    private DialogueAsset dialogue;
    private Action onFinished;
    private int line;
    private int visible;
    private int total;
    private float typed;
    private bool typing;
    private bool open;

    private float previousTimeScale;
    private InputMode previousMode;
    private Transform listener;

    public static DialogueController Current { get; private set; }

    /// <summary>
    /// Há diálogo na tela. Vale também no frame em que fechou: o clique que fecha é o mesmo botão do Interact, e
    /// sem isso a mesma batida reabriria a conversa.
    /// </summary>
    public static bool IsOpen => (Current != null && Current.open) || closedFrame == Time.frameCount;

    private void OnEnable()
    {
        if (view == null || transform.IsChildOf(view.Root.transform))
        {
            Debug.LogError("[DialogueController] Precisa de uma DialogueView, e fora do root dela: desligar a caixa " +
                           "desligaria o controller junto.", this);
            enabled = false;
            return;
        }

        Current = this;
        view.AdvancePressed += OnAdvance;
    }

    private void OnDisable()
    {
        // Troca de cena com a conversa aberta não pode deixar o jogo pausado nem o input preso no mapa UI.
        if (open)
            Close(notify: false);

        if (view != null)
            view.AdvancePressed -= OnAdvance;

        if (Current == this)
            Current = null;
    }

    /// <summary>
    /// Abre a conversa. False se já houver uma aberta, o jogo estiver pausado ou não houver falas.
    /// </summary>
    /// <param name="cameraPoint">Opcional: pose (posição e rotação) da câmera durante a conversa. Precisa de um
    /// <see cref="DialogueCamera"/> na câmera do player; sem ele, é ignorado.</param>
    public bool Play(DialogueAsset asset, Action finished = null, Transform cameraPoint = null)
    {
        if (open || asset == null || asset.Lines.Count == 0 || PauseControler.IsPaused)
            return false;

        dialogue = asset;
        onFinished = finished;
        open = true;

        if (pauseGame)
        {
            previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }

        // Acquire: garante o mapa ligado mesmo numa cena sem nenhum outro usuário de input.
        previousMode = PlayerInputProvider.Mode;
        PlayerInputProvider.Acquire();
        PlayerInputProvider.SetMode(InputMode.UI);

        if (cameraPoint != null)
        {
            if (DialogueCamera.Current != null)
                DialogueCamera.Current.Focus(cameraPoint);
            else
                Debug.LogWarning($"[DialogueController] '{asset.name}' pede o ponto de câmera '{cameraPoint.name}', " +
                                 "mas não há DialogueCamera na câmera do player: a câmera fica onde está.", cameraPoint);
        }

        view.Show();
        ShowLine(0);
        return true;
    }

    private void OnAdvance()
    {
        if (!open)
            return;

        if (typing)
        {
            // A primeira fala não se pula: o clique que abre a conversa (Interact divide o botão esquerdo com o
            // avançar) chega como avanço e a revelava inteira na hora. Guardas por frame e por soltura do botão não
            // seguraram — a causa exata no Input System não foi isolada.
            if (line > 0)
                CompleteLine();
        }
        else if (line + 1 < dialogue.Lines.Count)
            ShowLine(line + 1);
        else
            Close(notify: true);
    }

    private void ShowLine(int index)
    {
        line = index;
        DialogueAsset.Line current = dialogue.Lines[index];

        total = view.SetLine(current.speaker, current.text, canSkip: index > 0);
        visible = 0;
        typed = 0f;
        typing = true;

        if (total == 0)
            CompleteLine();
    }

    private void Update()
    {
        if (!typing)
            return;

        // Não escalado (com o jogo pausado o deltaTime é zero), mas limitado: o unscaled não respeita o Maximum Delta
        // Time, e a primeira abertura da caixa trava (shader/atlas do TMP) — o frame seguinte chegava com ~1 s e
        // revelava a primeira fala inteira de uma vez.
        typed += Mathf.Min(Time.unscaledDeltaTime, MaxTypingStep) * charactersPerSecond;
        int target = Mathf.Min((int)typed, total);

        while (visible < target)
        {
            visible++;

            if (visible % soundEveryCharacters == 0)
                PlaySound();
        }

        view.SetVisibleCharacters(visible);

        if (visible >= total)
            CompleteLine();
    }

    private void CompleteLine()
    {
        typing = false;
        visible = total;
        view.SetVisibleCharacters(total);
        view.SetLineComplete(true);
    }

    private void Close(bool notify)
    {
        open = false;
        typing = false;
        closedFrame = Time.frameCount;

        // Na troca de cena a view pode ter sido destruída antes deste OnDisable.
        if (view != null)
            view.Hide();

        if (DialogueCamera.Current != null)
            DialogueCamera.Current.Release();

        PlayerInputProvider.SetMode(previousMode);
        PlayerInputProvider.Release();

        if (pauseGame)
            Time.timeScale = previousTimeScale;

        Action finished = onFinished;
        onFinished = null;
        dialogue = null;

        if (notify)
            finished?.Invoke();
    }

    // O pool de áudio é 3D: seguir o listener deixa o som "dentro da cabeça", como UI deve soar.
    private void PlaySound()
    {
        if (string.IsNullOrEmpty(typingSound))
            return;

        if (listener == null)
        {
            AudioListener found = FindFirstObjectByType<AudioListener>();
            listener = found != null ? found.transform : transform;
        }

        AudioProvider.PlayFollowing(typingSound, listener);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Current = null;
        closedFrame = -1;
    }
}
