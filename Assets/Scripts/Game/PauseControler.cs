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
    [SerializeField] private GameObject pauseScreen;
    [SerializeField] private GameObject settingsScreen;
    [Tooltip("Recebe o foco ao voltar das configurações.")]
    [SerializeField] private Selectable settingsButton;

    private GameObject activeScreen;

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
        settingsScreen.SetActive(false);
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

    private GameObject FirstSelectable() => FirstSelectableIn(activeScreen);

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
        // Pausar por cima do diálogo devolveria timeScale e mapa Player com a caixa ainda aberta.
        if (DialogueController.IsOpen)
            return;

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

        // Sempre abre no Pause, mesmo se o menu foi fechado estando nas configurações.
        pauseMenuUI.SetActive(true);
        ShowScreen(pauseScreen, null, false);

        OnPaused?.Invoke();
    }

    public void Resume()
    {
        if (!IsPaused)
            return;

        ApplyResume();
        OnResumed?.Invoke();
    }

    public void OpenSettings() => ShowScreen(settingsScreen, FirstSelectableIn(settingsScreen), true);

    public void CloseSettings() =>
        ShowScreen(pauseScreen, settingsButton != null ? settingsButton.gameObject : null, true);

    // Troca de tela dentro do menu. Aberto por Submit (teclado/controle), o foco já cai no
    // destino; por clique, fica sem seleção para o mouse não ver dois destaques — o destino
    // vira lastSelected e a primeira navegação acorda nele.
    private void ShowScreen(GameObject screen, GameObject focus, bool allowSubmitFocus)
    {
        pauseScreen.SetActive(screen == pauseScreen);
        settingsScreen.SetActive(screen == settingsScreen);
        activeScreen = screen;

        var eventSystem = EventSystem.current;
        lastSelected = focus;

        if (eventSystem == null)
            return;

        bool bySubmit = allowSubmitFocus && SubmitPressed(eventSystem);
        eventSystem.SetSelectedGameObject(bySubmit ? focus : null);
    }

    // Ordem da hierarquia = ordem visual do menu; botão desabilitado não pode receber o foco.
    // includeInactive: a tela de destino ainda está desligada quando o foco é escolhido.
    private static GameObject FirstSelectableIn(GameObject screen)
    {
        foreach (var selectable in screen.GetComponentsInChildren<Selectable>(true))
        {
            if (selectable.IsInteractable())
                return selectable.gameObject;
        }

        return null;
    }

    private static bool SubmitPressed(EventSystem eventSystem)
    {
        if (eventSystem.currentInputModule is not InputSystemUIInputModule module)
            return false;

        var action = module.submit != null ? module.submit.action : null;
        return action != null && action.WasPressedThisFrame();
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
