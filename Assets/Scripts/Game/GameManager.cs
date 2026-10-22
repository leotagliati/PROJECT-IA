using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public enum GameState
{
    Preparing,
    Playing,
    // Final (EscapeCutscene): a caça acabou, a vitória ainda não. A IA de verdade só age em Playing.
    Escaping,
    Won,
    Lost,
}

[DefaultExecutionOrder(-100)]
public class GameManager : MonoBehaviour
{
    // Nem a vitória nem a derrota recarregam sozinhas: a WinScreenUI leva aos créditos e a tela de game over espera o
    // jogador escolher um botão.
    //
    // Tutorial já concluído: o spawn fica fora da sala do tutorial e a partida já abre em Playing.
    // A memória é estática porque o reload recria o GameManager; só o menu principal a apaga (ForgetTutorial).
    [Header("Reinício")]
    [Tooltip("Onde o jogador nasce se o tutorial já foi concluído nesta sessão.")]
    [SerializeField] private Transform respawnPoint;

    [Tooltip("GameObject do player (que tem o CharacterController).")]
    [SerializeField] private Transform player;

    [Tooltip("Dispara no reinício quando a partida já começa no game state Playing")]
    [SerializeField] private UnityEvent tutorialSkipped;

    private static bool tutorialCompleted;

    public static GameManager Current { get; private set; }

    public static event Action<GameState> StateChanged;

    public GameState State { get; private set; } = GameState.Preparing;
    public bool IsPlaying => State == GameState.Playing;
    public bool IsOver => State == GameState.Won || State == GameState.Lost;

    /// <summary>Trava do pause enquanto uma sequência tira o controle do jogador. Quem liga é quem desliga.</summary>
    public bool PauseLocked { get; set; }

    public bool AllowsPause => !IsOver && !PauseLocked;

    public float ElapsedTime { get; private set; }

    /// <summary>Esta carga abriu direto em Playing (tutorial já concluído): quem só faz sentido na abertura da caça se cala.</summary>
    public bool SkippedTutorial { get; private set; }

    void Awake()
    {
        if (Current != null && Current != this)
        {
            Debug.LogError($"{name}: já existe um GameManager na cena ({Current.name}).", this);
            enabled = false;
            return;
        }

        Current = this;

        if (tutorialCompleted)
            SkipTutorial();
    }

    void Start()
    {
        if (SkippedTutorial)
            tutorialSkipped.Invoke();

        StateChanged?.Invoke(State);
    }

    void Update()
    {
        switch (State)
        {
            case GameState.Playing:
            case GameState.Escaping:
                ElapsedTime += Time.deltaTime;
                break;
        }
    }

    [ContextMenu("Iniciar partida")]
    public void StartMainLoop()
    {
        if (State != GameState.Preparing)
            return;

        SetState(GameState.Playing);
        tutorialCompleted = true;
    }

    /// <summary>Esquece que o tutorial foi concluído: o próximo load passa por ele de novo.</summary>
    public static void ResetTutorial() => tutorialCompleted = false;

    /// <summary>
    /// Sair para o menu (pause) ou para os créditos (vitória): um jogo novo passa pelo tutorial, dicas e diálogo de novo.
    /// </summary>
    public static void EndSession()
    {
        ResetTutorial();
        HintDirector.ForgetProgress();
        DialogueTrigger.ForgetDialog();
        EscapeCutscene.ForgetCheckpoint();

        // O PlayerCamera trava o cursor e nada no menu destrava; pelo pause ele já vinha solto, pela vitória não.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void PlayerCaught() => Finish(GameState.Lost);

    public void PlayerEscaped() => Finish(GameState.Won);

    public void BeginEscape()
    {
        if (State == GameState.Playing)
            SetState(GameState.Escaping);
    }

    public void Restart()
    {
        var scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
        // Cenas de teste fora do Build Settings têm buildIndex -1; no Editor dá para carregar pelo path.
        if (scene.buildIndex < 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                scene.path, new LoadSceneParameters(LoadSceneMode.Single));
            return;
        }
#endif
        SceneManager.LoadScene(scene.buildIndex);
    }

    // No Awake: os seekers sincronizam com o State no OnEnable e já precisam ver Playing.
    private void SkipTutorial()
    {
        if (respawnPoint == null || player == null)
        {
            Debug.LogWarning($"{name}: sem respawnPoint/player — o tutorial roda de novo.", this);
            return;
        }

        var controller = player.GetComponent<CharacterController>();
        if (controller != null)
            controller.enabled = false;

        player.SetPositionAndRotation(respawnPoint.position, Quaternion.Euler(0f, respawnPoint.eulerAngles.y, 0f));

        if (controller != null)
            controller.enabled = true;

        SkippedTutorial = true;
        State = GameState.Playing;
        Debug.Log($"GameManager: {State} (tutorial já concluído)", this);
    }

    private void Finish(GameState outcome)
    {
        if (IsOver)
            return;

        SetState(outcome);
    }

    private void SetState(GameState next)
    {
        if (next == State)
            return;

        State = next;
        Debug.Log($"GameManager: {next}", this);
        StateChanged?.Invoke(next);
    }

    void OnDestroy()
    {
        if (Current == this)
            Current = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Current = null;
        StateChanged = null;
        tutorialCompleted = false;
    }
}
