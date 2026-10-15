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
    // O reload da vitória mora aqui, e não na tela: a partida recomeça mesmo com uma UI mal
    // configurada (a WinScreenUI sem labels se desliga no Awake e nunca reiniciaria).
    // A derrota NÃO recarrega sozinha: a tela de game over espera o jogador escolher um botão.
    [Header("Reinício automático")]
    [Tooltip("Segundos depois de escapar até recarregar a cena (clarão 2s + mensagem).")]
    [SerializeField, Min(0f)] private float reloadDelayAfterWon = 5.5f;

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

        if (outcome == GameState.Won)
            Invoke(nameof(Restart), reloadDelayAfterWon);
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
