using UnityEngine;

// Esconde o elemento (e os filhos) enquanto o menu de pause está aberto. Via CanvasGroup e não
// SetActive: desativar o objeto desinscreveria o componente dos eventos de pausa.
public class HideWhilePaused : MonoBehaviour
{
    private CanvasGroup group;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        if (group == null)
            group = gameObject.AddComponent<CanvasGroup>();
    }

    private void OnEnable()
    {
        PauseControler.OnPaused += HandlePaused;
        PauseControler.OnResumed += HandleResumed;
        group.alpha = PauseControler.IsPaused ? 0f : 1f;
    }

    private void OnDisable()
    {
        PauseControler.OnPaused -= HandlePaused;
        PauseControler.OnResumed -= HandleResumed;
    }

    private void HandlePaused() => group.alpha = 0f;

    private void HandleResumed() => group.alpha = 1f;
}
