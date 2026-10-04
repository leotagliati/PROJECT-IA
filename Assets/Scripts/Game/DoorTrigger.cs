using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Trigger genérico que avisa quando o jogador entra. Não sabe o que acontece depois: as ações
/// (abrir/fechar porta, iniciar o jogo) são plugadas no evento, pelo Inspector ou por código.
/// </summary>
[RequireComponent(typeof(Collider))]
public class DoorTrigger : MonoBehaviour
{
    [Tooltip("Desliga o collider depois do primeiro disparo, evitando repetição.")]
    [SerializeField] private bool triggerOnce = true;

    [SerializeField] private UnityEvent onTriggerEnter;

    private Collider triggerCollider;

    private void Awake()
    {
        triggerCollider = GetComponent<Collider>();
    }

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        // Por componente, e não por tag: o PlayerDummy usa a tag Goal (a que o Seeker enxerga
        // e captura), então filtrar por "Player" faria o trigger nunca disparar.
        if (other.GetComponentInParent<PlayerMovement>() == null)
            return;

        onTriggerEnter.Invoke();

        if (triggerOnce)
            triggerCollider.enabled = false;
    }
}
