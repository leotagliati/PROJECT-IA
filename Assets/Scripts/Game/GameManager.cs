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
    [SerializeField] private float preparationSeconds = 15f;

    public static GameManager Current { get; private set; }

    public static event Action<GameState> StateChanged;

    public GameState State { get; private set; } = GameState.Preparing;
    public bool IsPlaying => State == GameState.Playing;
    public bool IsOver => State == GameState.Won || State == GameState.Lost;

    public float PreparationRemaining { get; private set; }

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
        PreparationRemaining = Mathf.Max(0f, preparationSeconds);
        if (PreparationRemaining <= 0f)
            State = GameState.Playing;

        StateChanged?.Invoke(State);
    }

    void Update()
    {
        switch (State)
        {
            case GameState.Preparing:
                PreparationRemaining -= Time.deltaTime;
                if (PreparationRemaining <= 0f)
                {
                    PreparationRemaining = 0f;
                    SetState(GameState.Playing);
                }
                break;

            case GameState.Playing:
                ElapsedTime += Time.deltaTime;
                break;
        }
    }

    public void PlayerCaught() => Finish(GameState.Lost);

    public void PlayerEscaped() => Finish(GameState.Won);

    public void Restart() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

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
    }
}
