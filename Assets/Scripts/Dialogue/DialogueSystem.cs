using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class DialogueSystem : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private TMP_Text dialogueText;
    [SerializeField] private TMP_Text characterNameText;
    [SerializeField] private GameObject continueIndicator;
    [SerializeField] private Button continueButton;

    [Header("Typing")]
    [SerializeField] private float charactersPerSecond = 45f;
    [SerializeField] private int soundEveryCharacters = 2;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip typingSound;
    [SerializeField, Range(0f, 1f)] private float typingVolume = 0.5f;
    [SerializeField] private AudioClip dialogueEndSound;

    [Header("Dialogue")]
    [SerializeField] private string[] dialogueLines;
    [SerializeField] private string characterName = "DICA";

    private int currentLine;
    private Coroutine typingCoroutine;
    private bool isTyping;
    private bool isDialogueActive;

    private float previousTimeScale;
    private CursorLockMode previousCursorLock;
    private bool previousCursorVisibility;

    public bool IsDialogueActive => isDialogueActive;

    private void Awake()
    {
        dialoguePanel.SetActive(false);

        if (continueIndicator != null)
            continueIndicator.SetActive(false);

        if (continueButton != null)
            continueButton.onClick.AddListener(AdvanceDialogue);
    }

    private void OnDestroy()
    {
        if (continueButton != null)
            continueButton.onClick.RemoveListener(AdvanceDialogue);
    }

    // Inicia as falas configuradas no Inspector.
    public void BeginDialogue()
    {
        BeginDialogue(dialogueLines, characterName);
    }

    // Também permite iniciar diálogos diferentes por código.
    public void BeginDialogue(string[] lines, string speaker = "")
    {
        if (isDialogueActive || lines == null || lines.Length == 0)
            return;

        // Evita iniciar um diálogo sobre o menu de Pause.
        if (PauseControler.IsPaused)
            return;

        previousTimeScale = Time.timeScale;
        previousCursorLock = Cursor.lockState;
        previousCursorVisibility = Cursor.visible;

        Time.timeScale = 0f;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        dialogueLines = lines;
        characterName = speaker;
        currentLine = 0;
        isDialogueActive = true;

        dialoguePanel.SetActive(true);

        if (characterNameText != null)
        {
            characterNameText.text = characterName;
            characterNameText.gameObject.SetActive(
                !string.IsNullOrEmpty(characterName)
            );
        }

        ShowLine();
    }

    private void ShowLine()
    {
        if (currentLine >= dialogueLines.Length)
        {
            EndDialogue();
            return;
        }

        dialogueText.text = dialogueLines[currentLine];
        dialogueText.maxVisibleCharacters = 0;
        dialogueText.ForceMeshUpdate();

        if (continueIndicator != null)
            continueIndicator.SetActive(false);

        if (typingCoroutine != null)
            StopCoroutine(typingCoroutine);

        typingCoroutine = StartCoroutine(TypeLine());
    }

    private IEnumerator TypeLine()
    {
        isTyping = true;

        int totalCharacters = dialogueText.textInfo.characterCount;
        int visibleCharacters = 0;
        int soundCounter = 0;

        float characterInterval =
            1f / Mathf.Max(charactersPerSecond, 1f);

        float timer = 0f;

        while (visibleCharacters < totalCharacters)
        {
            // Continua funcionando mesmo com Time.timeScale = 0.
            timer += Time.unscaledDeltaTime;

            while (timer >= characterInterval &&
                   visibleCharacters < totalCharacters)
            {
                timer -= characterInterval;
                visibleCharacters++;

                dialogueText.maxVisibleCharacters = visibleCharacters;

                // Toca o som a cada N caracteres.
                soundCounter++;

                if (typingSound != null &&
                    soundCounter >= Mathf.Max(soundEveryCharacters, 1))
                {
                    soundCounter = 0;

                    if (audioSource != null)
                    {
                        audioSource.PlayOneShot(
                            typingSound,
                            typingVolume
                        );
                    }
                }
            }

            yield return null;
        }

        dialogueText.maxVisibleCharacters = totalCharacters;

        isTyping = false;
        typingCoroutine = null;

        if (continueIndicator != null)
            continueIndicator.SetActive(true);
    }


    public void AdvanceDialogue()
    {
        if (!isDialogueActive)
            return;

        // Se o texto ainda está sendo digitado, revela a linha inteira.
        if (isTyping)
        {
            if (typingCoroutine != null)
            {
                StopCoroutine(typingCoroutine);
                typingCoroutine = null;
            }

            isTyping = false;
            dialogueText.maxVisibleCharacters = dialogueText.textInfo.characterCount;

            if (continueIndicator != null)
                continueIndicator.SetActive(true);

            return;
        }

        // Avança para a próxima linha.
        currentLine++;

        if (currentLine >= dialogueLines.Length)
        {
            EndDialogue();
            return;
        }

        // Toca o som de transição entre linhas.
        if (dialogueEndSound != null && audioSource != null)
            audioSource.PlayOneShot(dialogueEndSound);

        // Reinicia a animação de digitação para a nova linha.
        ShowLine();
    }



    private void EndDialogue()
    {
        if (typingCoroutine != null)
            StopCoroutine(typingCoroutine);

        typingCoroutine = null;
        isTyping = false;
        isDialogueActive = false;

        dialoguePanel.SetActive(false);

        if (continueIndicator != null)
            continueIndicator.SetActive(false);

        Time.timeScale = previousTimeScale;

        Cursor.lockState = previousCursorLock;
        Cursor.visible = previousCursorVisibility;
    }
}