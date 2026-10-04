using System;
using UnityEngine;

/// <summary>
/// Porta básica da sequência da intro: só abre e fecha. 
/// Abre sozinha quando o estado do jogo entra em Playing;
/// </summary>

public class DoorController : MonoBehaviour
{
    [SerializeField] private bool startsOpen = false;

    public bool IsOpen { get; private set; }

    public event Action Opened;
    public event Action Closed;

    private void Awake() => IsOpen = startsOpen;

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable() => GameManager.StateChanged -= HandleGameState;

    // Sincroniza com o estado atual: o GameManager pode já ter emitido o estado antes desta
    // assinatura (mesmo molde do SeekerManager.OnEnable).
    private void Start()
    {
        if (GameManager.Current != null)
            HandleGameState(GameManager.Current.State);
    }

    private void HandleGameState(GameState state)
    {
        if (state == GameState.Playing)
            Open();
    }

    public void Open() => SetOpen(true);

    public void Close() => SetOpen(false);

    // Idempotente: sem mudança de estado, sem evento.
    private void SetOpen(bool open)
    {
        if (IsOpen == open)
            return;

        IsOpen = open;

        if (open)
            Opened?.Invoke();
        else
            Closed?.Invoke();
    }
}
