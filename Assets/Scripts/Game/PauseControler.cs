using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class PauseControler : MonoBehaviour
{
    [SerializeField] private GameObject pauseMenuUI;
    [SerializeField] private GameObject pauseScreen;
    [SerializeField] private GameObject settingsScreen;
    [Tooltip("Recebe o foco ao voltar das configurações.")]
    [SerializeField] private Selectable settingsButton;
    [Tooltip("No root do menu: acorda a seleção quando o jogador navega por teclado/controle.")]
    [SerializeField] private MenuSelectionWaker selectionWaker;
    [Tooltip("Cena carregada pelo botão de voltar ao menu (precisa estar no Build Settings).")]
    [SerializeField] private string mainMenuScene = "MainMenu";

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
        if (IsPaused || (GameManager.Current != null && !GameManager.Current.AllowsPause))
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

    public void OpenSettings() =>
        ShowScreen(settingsScreen, MenuSelectionWaker.FirstSelectableIn(settingsScreen, true), true);

    public void CloseSettings() =>
        ShowScreen(pauseScreen, settingsButton != null ? settingsButton.gameObject : null, true);

    // Troca de tela dentro do menu. Aberto por Submit (teclado/controle), o foco já cai no
    // destino; por clique, fica sem seleção para o mouse não ver dois destaques — o destino
    // vira o alvo do MenuSelectionWaker e a primeira navegação acorda nele.
    private void ShowScreen(GameObject screen, GameObject focus, bool allowSubmitFocus)
    {
        pauseScreen.SetActive(screen == pauseScreen);
        settingsScreen.SetActive(screen == settingsScreen);

        if (selectionWaker != null)
            selectionWaker.Remember(focus);

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
            return;

        bool bySubmit = allowSubmitFocus && MenuSelectionWaker.SubmitPressed(eventSystem);
        eventSystem.SetSelectedGameObject(bySubmit ? focus : null);
    }

    // O OnDestroy daqui e o PauseAudio.OnDisable já desfazem pausa, input e áudio no reload.
    public void Restart()
    {
        if (GameManager.Current != null)
            GameManager.Current.Restart();
        else
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // O OnDestroy e o PauseAudio.OnDisable desfazem pausa, input e áudio quando a cena descarrega.
    public void BackToMenu() => GameManager.EndSession(mainMenuScene);

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
