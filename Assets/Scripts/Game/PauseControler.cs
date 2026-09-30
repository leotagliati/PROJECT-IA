using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class PauseControler : MonoBehaviour
{
    [SerializeField] private GameObject pauseMenuUI;

    private GameObject lastSelected;

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

    // Roda depois do EventSystem.Update: o módulo de UI já "gastou" o primeiro toque de
    // navegação (sem seleção ele só arma o repeat delay), então aqui o toque apenas acorda a
    // seleção em vez de pular direto para o segundo botão.
    private void LateUpdate()
    {
        if (!IsPaused || EventSystem.current == null)
            return;

        var eventSystem = EventSystem.current;
        var current = eventSystem.currentSelectedGameObject;

        if (current != null)
        {
            lastSelected = current;
            return;
        }

        if (!NavigateHeld(eventSystem))
            return;

        var target = lastSelected != null && lastSelected.activeInHierarchy ? lastSelected : FirstSelectable();
        if (target != null)
            eventSystem.SetSelectedGameObject(target);
    }

    // Ordem da hierarquia = ordem visual do menu; botão desabilitado não pode receber o foco.
    private GameObject FirstSelectable()
    {
        foreach (var selectable in pauseMenuUI.GetComponentsInChildren<Selectable>())
        {
            if (selectable.IsInteractable())
                return selectable.gameObject;
        }

        return null;
    }

    // Lê a mesma action que o módulo usa para mover a seleção: assets diferentes com
    // bindings diferentes fariam o menu acordar com uma tecla e navegar com outra.
    private static bool NavigateHeld(EventSystem eventSystem)
    {
        if (eventSystem.currentInputModule is not InputSystemUIInputModule module)
            return false;

        var action = module.move != null ? module.move.action : null;
        return action != null && action.ReadValue<Vector2>() != Vector2.zero;
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

        // Sem seleção inicial: só WASD/joystick ou o mouse (PointerSelectable) selecionam.
        lastSelected = null;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        OnPaused?.Invoke();
    }

    public void Resume()
    {
        if (!IsPaused)
            return;

        ApplyResume();
        OnResumed?.Invoke();
    }

    // O OnDestroy daqui e o PauseAudio.OnDisable já desfazem pausa, input e áudio no reload.
    public void Restart()
    {
        if (GameManager.Current != null)
            GameManager.Current.Restart();
        else
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
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

        lastSelected = null;
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
