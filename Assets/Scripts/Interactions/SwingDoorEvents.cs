using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Expõe o <see cref="SwingDoor.LeftClosed"/> (evento de C#, invisível no Inspector) como
/// UnityEvent, para a cena ligar o que acontece quando a porta abre (um sinal de dica, um
/// som, um trigger) sem o SwingDoor conhecer ninguém. Fica junto do SwingDoor que o jogador
/// realmente empurra.
///
/// Dispara uma vez por carregamento de cena: "abrir" aqui é a primeira vez que sai do
/// batente. Porta trancada não se move, então o evento já respeita os cadeados.
/// </summary>
[RequireComponent(typeof(SwingDoor))]
public class SwingDoorEvents : MonoBehaviour
{
    [Tooltip("Primeira vez que a porta sai do batente.")]
    [SerializeField] private UnityEvent opened;

    private SwingDoor door;
    private bool fired;

    private void Awake() => door = GetComponent<SwingDoor>();

    private void OnEnable() => door.LeftClosed += HandleLeftClosed;

    private void OnDisable() => door.LeftClosed -= HandleLeftClosed;

    private void HandleLeftClosed()
    {
        if (fired)
            return;

        fired = true;
        opened.Invoke();
    }
}
