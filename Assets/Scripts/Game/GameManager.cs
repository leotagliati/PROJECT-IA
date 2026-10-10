using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState
{
    Preparing,
    Playing,
    Won,
    Lost,
}

[DefaultExecutionOrder(-100)]
public class GameManager : MonoBehaviour
{
    // O reload mora aqui, e não nas telas de fim: a partida recomeça mesmo com uma UI mal
    // configurada (a WinScreenUI sem labels se desliga no Awake e nunca reiniciaria).
    // Os tempos cobrem a sequência de cada fim mais a leitura da mensagem.
    [Header("Reinício automático")]
    [Tooltip("Segundos depois de ser pego até recarregar a cena (virada ~0,6s + mensagem).")]
    [SerializeField, Min(0f)] private float reloadDelayAfterLost = 4f;

    [Tooltip("Segundos depois de escapar até recarregar a cena (clarão 2s + mensagem).")]
    [SerializeField, Min(0f)] private float reloadDelayAfterWon = 5.5f;

    // Desligado, a partida começa ao carregar a cena: o monstro (modo de jogo) já sai caçando e o jogador só
    // nasce. Para a cena final enquanto não há porta nem gatilho de início ligado ao StartMainLoop; com o
    // gatilho pronto, ligue de novo. Ligado (padrão), nada muda para as cenas que já usam o gatilho.
    [Header("Início da partida")]
    [Tooltip("Ligado: espera alguém chamar StartMainLoop (ex.: o DoorTrigger da entrada). Desligado: começa ao carregar a cena.")]
    [SerializeField] private bool waitForStartTrigger = true;

    public static GameManager Current { get; private set; }

    public static event Action<GameState> StateChanged;

    public GameState State { get; private set; } = GameState.Preparing;
    public bool IsPlaying => State == GameState.Playing;
    public bool IsOver => State == GameState.Won || State == GameState.Lost;

    public float ElapsedTime { get; private set; }

    void Awake()
    {
        if (Current != null && Current != this)
        {
            Debug.LogError($"{name}: já existe um GameManager na cena ({Current.name}).", this);
            enabled = false;
            return;
        }

        Current = this;
    }

    void Start()
    {
        StateChanged?.Invoke(State);

        // No Start, não no Awake: quem ouve StateChanged (o monstro assina no OnEnable) já está pronto.
        // O gatilho que chegar depois não faz nada (StartMainLoop só sai de Preparing).
        if (!waitForStartTrigger)
            StartMainLoop();
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
    
    public void StartMainLoop()
    {
        if (State != GameState.Preparing)
            return;

        SetState(GameState.Playing);
    }

    public void PlayerCaught() => Finish(GameState.Lost);

    public void PlayerEscaped() => Finish(GameState.Won);

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

    private void Finish(GameState outcome)
    {
        if (IsOver)
            return;

        SetState(outcome);

        float delay = outcome == GameState.Won ? reloadDelayAfterWon : reloadDelayAfterLost;
        Invoke(nameof(Restart), delay);
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
    }
}
