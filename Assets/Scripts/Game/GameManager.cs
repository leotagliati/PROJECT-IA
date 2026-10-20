using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

public enum GameState
{
    Preparing,
    Playing,
    // Cutscene final (EscapeCutscene): a caça acabou mas a vitória ainda não. A IA e o pause só
    // funcionam em Preparing/Playing, então ninguém captura nem pausa o jogador no meio dela.
    Escaping,
    Won,
    Lost,
}

[DefaultExecutionOrder(-100)]
public class GameManager : MonoBehaviour
{
    // A ida ao menu depois da vitória mora aqui, e não na tela: acontece mesmo com uma UI mal
    // configurada ou ausente (a WinScreenUI faz o clarão e o texto, mas não decide quando a partida acaba).
    // A derrota NÃO recarrega sozinha: a tela de game over espera o jogador escolher um botão.
    //
    // Tutorial já concluído: o spawn fica fora da sala do tutorial e a partida já abre em Playing.
    // A memória é estática porque o reload recria o GameManager; só o menu principal a apaga (ForgetTutorial).
    [Header("Fim de jogo")]
    [Tooltip("Segundos depois de escapar até carregar o menu principal (clarão 2s + mensagem).")]
    [FormerlySerializedAs("reloadDelayAfterWon")]
    [SerializeField, Min(0f)] private float menuDelayAfterWon = 5.5f;

    [Tooltip("Cena carregada depois da vitória (precisa estar no Build Settings).")]
    [SerializeField] private string mainMenuScene = "MainMenu";

    [Tooltip("Onde o jogador nasce se o tutorial já foi concluído nesta sessão.")]
    [SerializeField] private Transform respawnPoint;

    [Tooltip("GameObject do player (que tem o CharacterController).")]
    [SerializeField] private Transform player;

    [Tooltip("Dispara no reinício quando a partida já começa no game state Playing")]
    [SerializeField] private UnityEvent tutorialSkipped;

    // Aqui, e não no finished do DialogueTrigger da porta: o tutorialSkipped chama o Skip() dele a cada reinício,
    // e a abertura tocaria toda vez que o jogador renasce. Só o StartMainLoop é "o tutorial acabou agora".
    [Tooltip("Dispara uma vez, na passagem do tutorial para a caça (Preparing → Playing). Não dispara no reinício.")]
    [SerializeField] private UnityEvent gameStarted;

    private static bool tutorialCompleted;

    public static GameManager Current { get; private set; }

    public static event Action<GameState> StateChanged;

    public GameState State { get; private set; } = GameState.Preparing;
    public bool IsPlaying => State == GameState.Playing;
    public bool IsOver => State == GameState.Won || State == GameState.Lost;

    /// <summary>
    /// Trava do pause para uma sequência que não pode ser interrompida: a cutscene final segura do trigger até o jogador
    /// ser pego ou vencer, onde um Esc sem querer viraria "Reiniciar". Quem liga é quem desliga.
    /// </summary>
    public bool PauseLocked { get; set; }

    /// <summary>
    /// Pausar em qualquer momento da partida, menos no fim e com a trava ligada. Escaping sozinho não bloqueia: depois de
    /// ser pego na fuga final o jogador renasce em Escaping (a IA de verdade segue parada) e pode pausar até passar pelo
    /// trigger de novo.
    /// </summary>
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
        gameStarted.Invoke();
    }

    /// <summary>Esquece que o tutorial foi concluído: o próximo load passa por ele de novo.</summary>
    public static void ResetTutorial() => tutorialCompleted = false;

    public void PlayerCaught() => Finish(GameState.Lost);

    public void PlayerEscaped() => Finish(GameState.Won);

    /// <summary>Início da cutscene final: a caça para (a IA só age em Playing), mas o jogo ainda não terminou.</summary>
    public void BeginEscape()
    {
        if (State == GameState.Playing)
            SetState(GameState.Escaping);
    }

    /// <summary>
    /// Voltar ao menu encerra a sessão: um jogo novo passa pelo tutorial, pelas dicas e pelo diálogo de novo.
    /// Fonte única para o pause e para o fim da vitória.
    /// </summary>
    public static void LoadMainMenu(string scene)
    {
        ResetTutorial();
        HintDirector.ForgetProgress();
        DialogueTrigger.ForgetDialog();

        // O PlayerCamera trava e esconde o cursor, e nada no menu destrava: pelo pause funcionava porque o Pause() já
        // tinha soltado o cursor, mas a vitória chega aqui com ele travado e os botões não respondem ao mouse.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        SceneManager.LoadScene(scene);
    }

    private void ReturnToMainMenu() => LoadMainMenu(mainMenuScene);

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

        if (outcome == GameState.Won)
            Invoke(nameof(ReturnToMainMenu), menuDelayAfterWon);
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
