using UnityEngine;

public class PauseAudio : MonoBehaviour
{
    private void OnEnable()
    {
        PauseControler.OnPaused += HandlePaused;
        PauseControler.OnResumed += HandleResumed;
    }

    private void OnDisable()
    {
        PauseControler.OnPaused -= HandlePaused;
        PauseControler.OnResumed -= HandleResumed;
        AudioListener.pause = false;
    }

    private void HandlePaused() => AudioListener.pause = true;

    private void HandleResumed() => AudioListener.pause = false;
}
