using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Porta que abre quando não sobra nenhum <see cref="DoorLock"/> fechado. Os cadeados são
/// quem lida com chave e inventário; a porta só os conta, via evento, e recusa a interação
/// enquanto houver algum. A lista é a única fonte de verdade — não há contador separado.
/// </summary>
public class LockedDoor : MonoBehaviour, IInteractable
{
    [Tooltip("Cadeados desta porta. Vazio = porta destrancada.")]
    [SerializeField] private List<DoorLock> locks = new List<DoorLock>();

    [Header("Prompt")]
    [SerializeField] private string openPrompt = "Abrir porta";

    [Tooltip("Mensagem ao tentar abrir com cadeado. {0} = cadeados restantes.")]
    [SerializeField] private string lockedMessageFormat = "Ainda há {0} cadeado(s)";

    [Header("Áudio")]
    [Tooltip("Toca quando o jogador tenta abrir com cadeado. Vazio = sem som.")]
    [SerializeField] private string lockedSoundId = "lockedDoor";

    [Header("Animação")]
    [Tooltip("Animator do modelo da porta. Vazio: procura nos filhos — o modelo é filho deste objeto.")]
    [SerializeField] private Animator doorAnimator;

    private bool isOpen;

    // Trigger do DoorAC, que leva de Idle para o clipe de abrir. Nome em hash: o Animator
    // resolve string toda chamada, e um erro de digitação aqui não dá erro nenhum em runtime —
    // a porta só não abre.
    private static readonly int OpenedHash = Animator.StringToHash("Opened");

    public int RemainingLocks => locks.Count;

    public bool IsUnlocked => locks.Count == 0;

    // O prompt é sempre o verbo ("Abrir porta"); o porquê de não dar vai no ErrorMessage.
    public string Prompt => isOpen ? null : openPrompt;

    public string ErrorMessage => IsUnlocked ? null : string.Format(lockedMessageFormat, locks.Count);

    void Awake()
    {
        // Slot vazio no Inspector e cadeado já aberto na cena não contam como tranca.
        locks.RemoveAll(l => l == null || l.IsUnlocked);

        if (doorAnimator == null)
            doorAnimator = GetComponentInChildren<Animator>();

        if (doorAnimator == null)
            Debug.LogWarning($"{name}: nenhum Animator no modelo da porta — ela abre sem animar.", this);
    }

    void OnEnable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked += HandleLockUnlocked;
    }

    void OnDisable()
    {
        foreach (DoorLock doorLock in locks)
            doorLock.Unlocked -= HandleLockUnlocked;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        if (isOpen)
            return InteractionResult.Success;

        if (!IsUnlocked)
        {
            // A porta chacoalha na tentativa, não ao olhar: é feedback da ação recusada.
            if (!string.IsNullOrEmpty(lockedSoundId))
                AudioProvider.PlayAt(lockedSoundId, transform.position);

            return InteractionResult.Fail(ErrorMessage);
        }

        OpenDoor();
        return InteractionResult.Success;
    }

    private void HandleLockUnlocked(DoorLock doorLock)
    {
        doorLock.Unlocked -= HandleLockUnlocked;
        locks.Remove(doorLock);
    }

    private void OpenDoor()
    {
        isOpen = true;

        // Trigger, e não bool: abrir é um evento único e a porta não volta a fechar. Sem
        // Animator a porta continua "aberta" para o resto do jogo — só não anima.
        if (doorAnimator != null)
            doorAnimator.SetTrigger(OpenedHash);
    }
}
