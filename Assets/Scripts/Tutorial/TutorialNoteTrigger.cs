using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Ponte entre UnityEvent e o <see cref="TutorialNoteController"/>: o objeto do jogo só expõe um evento (a garrafa
/// tem <c>pickedUp</c>) e é aqui, pelo Inspector, que ele vira bilhete — sem gameplay depender de Tutorial.
/// Para um passo do HintDirector esperar o bilhete, ligue um HintSignalEmitter no <see cref="closed"/>.
/// </summary>
public class TutorialNoteTrigger : MonoBehaviour
{
    [SerializeField] private TutorialNote note;

    [Tooltip("Bilhete já visto não abre de novo (vale entre reloads se o controller lembrar).")]
    [SerializeField] private bool once = true;

    [SerializeField] private UnityEvent closed;

    public void Show()
    {
        TutorialNoteController controller = TutorialNoteController.Current;
        if (controller == null)
        {
            Debug.LogWarning($"[TutorialNoteTrigger] '{name}': nenhum TutorialNoteController ativo na cena.", this);
            return;
        }

        if (once && controller.HasSeen(note))
            return;

        controller.Show(note, closed.Invoke, once);
    }
}
