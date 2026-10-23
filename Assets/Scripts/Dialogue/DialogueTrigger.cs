using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Abre uma conversa ao interagir. É a única ponte entre o sistema de interação e o de diálogo; para um passo de
/// tutorial esperar a conversa, ligue um HintSignalEmitter no <see cref="finished"/>.
/// </summary>
public class DialogueTrigger : MonoBehaviour, IInteractable
{
    [SerializeField] private DialogueAsset dialogue;

    [Tooltip("Opcional. Toca no lugar do dialogue depois da primeira conversa completa, e fica repetindo. Sem efeito " +
             "com once ligado.")]
    [SerializeField] private DialogueAsset repeatDialogue;

    [SerializeField] private string prompt = "Conversar";

    [Tooltip("Opcional. Onde a câmera para durante a conversa (posição e rotação: o eixo azul é para onde ela " +
             "olha). Precisa de um DialogueCamera na câmera do player.")]
    [SerializeField] private Transform cameraPoint;

    [Tooltip("Depois da primeira conversa completa, este componente se remove e o objeto deixa de ser interagível " +
             "por aqui: um IInteractable desligado ainda seria o alvo do raycast e esconderia o que está no mesmo " +
             "objeto (uma porta, por exemplo).")]
    [SerializeField] private bool once;

    [Tooltip("Vazio = não lembra. Com um id, a conversa completa fica lembrada entre reloads da cena (o GameManager " +
             "recarrega a cada morte): na tentativa seguinte ela não abre, mas o finished dispara do mesmo jeito, " +
             "para o que vem depois dela não travar.")]
    [SerializeField] private string rememberAs;

    [Tooltip("Dispara quando a conversa de fato abre (não se o Play recusar), antes de o jogador ler a primeira " +
             "fala. Gancho para mexer na cena enquanto o diálogo segura o jogador, sem esperar o Finished.")]
    [SerializeField] private UnityEvent started;

    [SerializeField] private UnityEvent finished;

    // Estático sobrevive ao reload de cena, que é o objetivo. ResetStatics limpa no Play seguinte quando o domain
    // reload está desligado.
    private static readonly HashSet<string> remembered = new();

    private bool done;

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt result)
    {
        result = InteractionPrompt.Instant(prompt);

        if (once && done)
            return false;

        if (DialogueController.Current == null)
            result = result.Blocked("Sem caixa de diálogo na cena");

        return !DialogueController.IsOpen;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        Open();
        return InteractionResult.Success;
    }

    /// <summary>Abre a conversa sem passar pela interação: alvo de UnityEvent (um volume de trigger, por exemplo).</summary>
    public void Play() => Open();

    /// <summary>Dá a conversa por vista sem abri-la (reinício com o tutorial já concluído): dispara o finished.</summary>
    public void Skip() => OnFinished();

    private void Open()
    {
        DialogueController controller = DialogueController.Current;
        if (controller == null)
        {
            Debug.LogWarning($"[DialogueTrigger] '{name}': nenhum DialogueController ativo na cena.", this);
            return;
        }

        if ((once && done) || DialogueController.IsOpen)
            return;

        if (!string.IsNullOrEmpty(rememberAs) && remembered.Contains(rememberAs))
        {
            OnFinished();
            return;
        }

        // O Play recusa com o jogo pausado, asset vazio ou conversa aberta: nesses casos nada abriu e o started
        // não pode disparar.
        DialogueAsset current = done && repeatDialogue != null ? repeatDialogue : dialogue;
        if (controller.Play(current, OnFinished, cameraPoint))
            started.Invoke();
    }

    private void OnFinished()
    {
        done = true;

        if (!string.IsNullOrEmpty(rememberAs))
            remembered.Add(rememberAs);

        finished.Invoke();

        // Só desligar não basta: o InteractionController escolhe pelo GetComponentInParent<IInteractable>, que acha
        // componente desligado, e esta conversa taparia o IInteractable de baixo (a SwingDoor) para sempre.
        if (once)
            Destroy(this);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => remembered.Clear();

    /// <summary>Apaga as conversas lembradas: um jogo novo pelo menu repassa o tutorial inteiro.</summary>
    public static void ForgetDialog() => remembered.Clear();

    // Prévia do enquadramento: o frustum da câmera principal saindo do ponto.
    private void OnDrawGizmosSelected()
    {
        if (cameraPoint == null)
            return;

        Camera cam = Camera.main;
        Gizmos.color = Color.cyan;
        Gizmos.matrix = Matrix4x4.TRS(cameraPoint.position, cameraPoint.rotation, Vector3.one);
        Gizmos.DrawFrustum(Vector3.zero, cam != null ? cam.fieldOfView : 60f, 1.5f, 0.05f,
                           cam != null ? cam.aspect : 16f / 9f);
    }
}
