using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Comportamento do bilhete de tutorial: fila de pedidos, quando ele pode abrir, a pausa e o input no mapa UI
/// enquanto está na tela, e o que já foi visto. O que aparece na tela é do <see cref="TutorialNoteView"/>.
///
/// Um por cena, fora do root da view. Não conhece gameplay nem dicas: quem pede é o
/// <see cref="TutorialNoteTrigger"/>, e quem precisa saber que há bilhete na tela (pausa, dicas) lê
/// <see cref="IsOpen"/>.
/// </summary>
public class TutorialNoteController : MonoBehaviour
{
    [Tooltip("Não pode ficar dentro do root da view: desligar o bilhete desligaria o controller junto.")]
    [SerializeField] private TutorialNoteView view;

    [Tooltip("Segundos de jogo entre o pedido e o bilhete abrir: dá tempo de ver o item chegar na mão.")]
    [SerializeField, Min(0f)] private float openDelay = 0.4f;

    [Tooltip("Segundos reais antes de aceitar o fechar. O clique que pega o item é o mesmo botão do avançar, e " +
             "sem isso o bilhete fecharia sem ser lido.")]
    [SerializeField, Min(0f)] private float minVisibleTime = 0.8f;

    [Tooltip("Id da AudioLibrary tocado ao abrir. Vazio = sem som.")]
    [SerializeField] private string openSound;

    [Tooltip("O GameManager recarrega a cena a cada morte. Ligado, bilhete visto não volta no retry.")]
    [SerializeField] private bool rememberAcrossReloads = true;

    private struct Request
    {
        public TutorialNote note;
        public Action closed;
        public bool once;
    }

    // Estático sobrevive ao reload de cena, que é o objetivo (mesmo molde do HintDirector).
    private static readonly HashSet<string> seen = new();
    private static int closedFrame = -1;

    private readonly Queue<Request> pending = new();
    private Request current;
    private Coroutine running;
    private float openedAt;
    private bool promptShown;
    private bool open;

    private float previousTimeScale;
    private InputMode previousMode;

    public static TutorialNoteController Current { get; private set; }

    /// <summary>Há bilhete na tela. Vale também no frame em que fechou, como no diálogo.</summary>
    public static bool IsOpen => (Current != null && Current.open) || closedFrame == Time.frameCount;

    private void Awake()
    {
        if (!rememberAcrossReloads)
            seen.Clear();
    }

    private void OnEnable()
    {
        if (view == null || transform.IsChildOf(view.Root.transform))
        {
            Debug.LogError("[TutorialNoteController] Precisa de uma TutorialNoteView, e fora do root dela.", this);
            enabled = false;
            return;
        }

        Current = this;
        view.AdvancePressed += OnAdvance;
    }

    private void OnDisable()
    {
        // Troca de cena com o bilhete aberto não pode deixar o jogo pausado nem o input preso no mapa UI.
        if (open)
            Close(notify: false);

        pending.Clear();
        running = null;

        if (view != null)
            view.AdvancePressed -= OnAdvance;

        if (Current == this)
            Current = null;
    }

    public bool HasSeen(TutorialNote note) => note != null && seen.Contains(note.name);

    /// <summary>
    /// Pede o bilhete. Abre assim que nada mais estiver na tela (pausa, diálogo, outro bilhete); se vários
    /// chegarem juntos, entram em fila.
    /// </summary>
    /// <param name="once">Bilhete já visto não abre de novo. Conferido na hora de abrir, não no pedido: duas
    /// garrafas pegas antes do primeiro abrir pediriam o mesmo bilhete duas vezes.</param>
    public void Show(TutorialNote note, Action closed = null, bool once = true)
    {
        if (note == null)
            return;

        pending.Enqueue(new Request { note = note, closed = closed, once = once });
        running ??= StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        while (pending.Count > 0)
        {
            // Tempo escalado: a pausa congela a espera junto.
            if (openDelay > 0f)
                yield return new WaitForSeconds(openDelay);

            yield return new WaitUntil(CanOpen);

            Request next = pending.Dequeue();
            if (next.once && HasSeen(next.note))
                continue;

            Open(next);
            yield return new WaitUntil(() => !open);
        }

        running = null;
    }

    private static bool CanOpen()
    {
        GameManager game = GameManager.Current;
        return !PauseControler.IsPaused && !DialogueController.IsOpen && (game == null || !game.IsOver);
    }

    private void Open(Request request)
    {
        current = request;
        open = true;
        openedAt = Time.unscaledTime;

        seen.Add(request.note.name);

        previousTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        // Acquire: garante o mapa ligado mesmo numa cena sem nenhum outro usuário de input.
        previousMode = PlayerInputProvider.Mode;
        PlayerInputProvider.Acquire();
        PlayerInputProvider.SetMode(InputMode.UI);

        view.Show(request.note);
        promptShown = minVisibleTime <= 0f;
        view.SetPromptVisible(promptShown);

        if (!string.IsNullOrEmpty(openSound) && Camera.main != null)
            AudioProvider.PlayFollowing(openSound, Camera.main.transform);
    }

    private void Update()
    {
        if (open && !promptShown && ReadyToClose())
        {
            promptShown = true;
            view.SetPromptVisible(true);
        }
    }

    private bool ReadyToClose() => Time.unscaledTime - openedAt >= minVisibleTime;

    private void OnAdvance()
    {
        if (open && ReadyToClose())
            Close(notify: true);
    }

    private void Close(bool notify)
    {
        open = false;
        closedFrame = Time.frameCount;

        // Na troca de cena a view pode ter sido destruída antes deste OnDisable.
        if (view != null)
            view.Hide();

        PlayerInputProvider.SetMode(previousMode);
        PlayerInputProvider.Release();
        Time.timeScale = previousTimeScale;

        Action closed = current.closed;
        current = default;

        if (notify)
            closed?.Invoke();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Current = null;
        closedFrame = -1;
        seen.Clear();
    }
}
