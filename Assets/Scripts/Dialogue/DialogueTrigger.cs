using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Abre uma conversa ao interagir. É a única ponte entre o sistema de interação e o de diálogo; para um passo de
/// tutorial esperar a conversa, ligue um HintSignalEmitter no <see cref="finished"/>.
/// </summary>
public class DialogueTrigger : MonoBehaviour, IInteractable
{
    [SerializeField] private DialogueAsset dialogue;
    [SerializeField] private string prompt = "Conversar";

    [Tooltip("Opcional. Onde a câmera para durante a conversa (posição e rotação: o eixo azul é para onde ela " +
             "olha). Precisa de um DialogueCamera na câmera do player.")]
    [SerializeField] private Transform cameraPoint;

    [Tooltip("Depois da primeira conversa completa, o objeto deixa de ser interagível.")]
    [SerializeField] private bool once;

    [SerializeField] private UnityEvent finished;

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
        DialogueController controller = DialogueController.Current;
        if (controller == null)
        {
            Debug.LogWarning($"[DialogueTrigger] '{name}': nenhum DialogueController ativo na cena.", this);
            return InteractionResult.Fail("Sem caixa de diálogo na cena");
        }

        if ((once && done) || DialogueController.IsOpen)
            return InteractionResult.Success;

        controller.Play(dialogue, OnFinished, cameraPoint);
        return InteractionResult.Success;
    }

    private void OnFinished()
    {
        done = true;
        finished.Invoke();
    }

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
