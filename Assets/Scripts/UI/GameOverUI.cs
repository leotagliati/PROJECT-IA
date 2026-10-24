using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// Tela de game over. Entra no corte da PlayerCaughtSequence, não no evento Lost, para a vinheta
/// vermelha ter aparecido antes. Sequência: fundo preto → texto → animação do texto → botões.
/// Só ao fim o jogador recebe a UI de volta.
///
/// Aqui NÃO há reload automático: a partida só recomeça pelo botão (ver <see cref="GameManager"/>).
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class GameOverUI : MonoBehaviour
{
    [Tooltip("Vazio: procura na cena.")]
    [SerializeField] private PlayerCaughtSequence sequence;

    [Header("Texto")]
    [Tooltip("O texto é o escrito na cena; aqui só a opacidade é controlada.")]
    [SerializeField] private TMP_Text messageLabel;

    [Tooltip("Animator que leva o texto à posição final. Fica desligado até o texto terminar de aparecer.")]
    [SerializeField] private Animator textAnimator;

    [Header("Botões")]
    [Tooltip("Na ordem de navegação. O primeiro recebe o foco ao fim da sequência.")]
    [SerializeField] private Selectable[] buttons;

    [Tooltip("Cena do botão de sair (fora do Editor). Arraste a cena; precisa estar ligada no Build Settings.")]
    [SerializeField] private SceneReference menuScene;

    [Header("Tempos (segundos)")]
    [SerializeField, Min(0.01f)] private float backgroundFadeDuration = 1f;

    [FormerlySerializedAs("messageDelay")]
    [Tooltip("Espera com a tela já preta antes do texto aparecer.")]
    [SerializeField, Min(0f)] private float textDelay = 1f;

    [SerializeField, Min(0.01f)] private float textFadeDuration = 0.8f;
    [SerializeField, Min(0.01f)] private float buttonsFadeDuration = 0.6f;

    private CanvasGroup group;
    private CanvasGroup[] buttonGroups;
    private bool tookUiControl;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;
        // Invisível não pode clicar: durante a sequência os botões ainda não existem para o jogador.
        group.interactable = false;
        group.blocksRaycasts = false;

        if (messageLabel == null)
        {
            Debug.LogError($"{name}: messageLabel não atribuído no Inspector.", this);
            enabled = false;
            return;
        }

        messageLabel.alpha = 0f;

        if (textAnimator != null)
            textAnimator.enabled = false;

        // CanvasGroup por botão, e não a cor do texto: o HoverEffect guarda a cor original no Awake.
        buttonGroups = new CanvasGroup[buttons != null ? buttons.Length : 0];
        for (int i = 0; i < buttonGroups.Length; i++)
        {
            var button = buttons[i];
            if (button == null)
                continue;

            var buttonGroup = button.GetComponent<CanvasGroup>();
            if (buttonGroup == null)
                buttonGroup = button.gameObject.AddComponent<CanvasGroup>();

            buttonGroup.alpha = 0f;
            buttonGroups[i] = buttonGroup;
        }
    }

    private void Start()
    {
        if (sequence == null)
            sequence = FindFirstObjectByType<PlayerCaughtSequence>();

        if (sequence != null)
            sequence.CutToBlack += Show;
        else
            Debug.LogError($"{name}: PlayerCaughtSequence não encontrada na cena.", this);
    }

    // O modo de input é estático e sobrevive ao reload: sem devolver o Player, a partida nova
    // abriria com o mapa do jogador desligado. O cursor é retravado pelo PlayerCamera.Start.
    private void OnDestroy()
    {
        if (sequence != null)
            sequence.CutToBlack -= Show;

        if (tookUiControl)
            PlayerInputProvider.SetMode(InputMode.Player);
    }

    public void Show() => StartCoroutine(ShowRoutine());

    public void Restart()
    {
        if (GameManager.Current != null)
            GameManager.Current.Restart();
        else
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    public void Quit()
    {
        GameManager.EndSession();
        menuScene.Load();
    }

    private IEnumerator ShowRoutine()
    {
        yield return Fade(a => group.alpha = a, backgroundFadeDuration);

        yield return new WaitForSeconds(textDelay);

        yield return Fade(a => messageLabel.alpha = a, textFadeDuration);

        if (textAnimator != null)
        {
            textAnimator.enabled = true;
            // Um frame para o Animator entrar no estado; antes disso normalizedTime ainda é do
            // estado anterior e o loop sairia na hora.
            yield return null;
            while (textAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime < 1f)
                yield return null;
        }

        yield return Fade(a =>
        {
            foreach (var buttonGroup in buttonGroups)
            {
                if (buttonGroup != null)
                    buttonGroup.alpha = a;
            }
        }, buttonsFadeDuration);

        GiveControl();
    }

    // Termina sempre em 1: o loop sai com t < duration, e a tela ficaria quase opaca para sempre.
    private static IEnumerator Fade(System.Action<float> apply, float duration)
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            apply(Mathf.SmoothStep(0f, 1f, t / duration));
            yield return null;
        }

        apply(1f);
    }

    private void GiveControl()
    {
        group.interactable = true;
        group.blocksRaycasts = true;

        PlayerInputProvider.SetMode(InputMode.UI);
        tookUiControl = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (EventSystem.current != null && buttons != null && buttons.Length > 0 && buttons[0] != null)
            EventSystem.current.SetSelectedGameObject(buttons[0].gameObject);
    }
}
