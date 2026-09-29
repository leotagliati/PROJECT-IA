using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class PauseControler : MonoBehaviour
{
    [SerializeField] private GameObject pauseMenuUI;
    [SerializeField] private GameObject firstButtonSelected;

    public static bool IsPaused { get; private set; }

    public static event Action OnPaused;
    public static event Action OnResumed;

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
        PlayerInputProvider.Player.Pause.performed += OnToggleInput;
        PlayerInputProvider.UI.Cancel.performed += OnToggleInput;
    }

    private void OnDisable()
    {
        PlayerInputProvider.Player.Pause.performed -= OnToggleInput;
        PlayerInputProvider.UI.Cancel.performed -= OnToggleInput;
        PlayerInputProvider.Release();
    }

    private void Start()
    {
        pauseMenuUI.SetActive(false);
    }

    private void OnDestroy()
    {
        if (!IsPaused)
            return;

        IsPaused = false;
        Time.timeScale = 1f;
        PlayerInputProvider.SetMode(InputMode.Player);
    }

    private void OnToggleInput(InputAction.CallbackContext context)
    {
        if (IsPaused)
            Resume();
        else
            Pause();
    }

    public void Pause()
    {
        if (IsPaused || (GameManager.Current != null && GameManager.Current.IsOver))
            return;

        IsPaused = true;
        PlayerInputProvider.SetMode(InputMode.UI);
        Time.timeScale = 0f;
        SetCursor(false);
        pauseMenuUI.SetActive(true);

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(firstButtonSelected);

        OnPaused?.Invoke();
    }

    public void Resume()
    {
        if (!IsPaused)
            return;

        ApplyResume();
        OnResumed?.Invoke();
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void ApplyResume()
    {
        IsPaused = false;
        PlayerInputProvider.SetMode(InputMode.Player);
        Time.timeScale = 1f;
        SetCursor(true);
        pauseMenuUI.SetActive(false);

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    private static void SetCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        IsPaused = false;
        OnPaused = null;
        OnResumed = null;
    }
}
