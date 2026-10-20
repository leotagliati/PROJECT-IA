using UnityEngine;
using TMPro;
using System.Collections;
using UnityEngine.EventSystems;

public class HoverEffect : MonoBehaviour, ISelectHandler, IDeselectHandler
{
    [Header("Deslocamento")]
    [Tooltip("Quanto o texto anda ao ser selecionado, em unidades do Canvas: abre espaço para a MenuSelectionArrow.")]
    [SerializeField] private Vector2 hoverOffset = new(20f, 0f);
    [SerializeField] private float animationSpeed = 0.15f;

    [Header("Color")]
    [SerializeField] private float brightness = 2f;

    [Header("Sound")]
    [SerializeField] private AudioClip hoverSound;
    [SerializeField] private float soundVolume = 1f;

    private Coroutine currentAnimation;

    // Move o texto e não o botão: a área de clique fica parada, senão o botão fugiria do cursor e o hover
    // piscaria. Também não briga com Layout Group, que reposiciona o botão.
    private RectTransform movedRect;
    private Vector2 originalPosition;

    private Color originalColor;
    private TMP_Text text;

    private AudioSource audioSource;

    // Impede o som de tocar de novo enquanto o botão já está destacado.
    private bool isHovered = false;

    private void Awake()
    {
        text = GetComponentInChildren<TMP_Text>();

        if (text != null)
            originalColor = text.color;

        movedRect = text != null ? text.rectTransform : (RectTransform)transform;
        originalPosition = movedRect.anchoredPosition;

        audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = false;

        // IMPORTANTE para o menu de Pause
        audioSource.ignoreListenerPause = true;
    }

    // Painel desativado com o botão destacado não recebe OnDeselect: sem isto o botão
    // reaparece deslocado e aceso na próxima vez que a tela abrir.
    private void OnDisable()
    {
        if (currentAnimation != null)
        {
            StopCoroutine(currentAnimation);
            currentAnimation = null;
        }

        isHovered = false;
        movedRect.anchoredPosition = originalPosition;

        if (text != null)
            text.color = originalColor;
    }

    public void OnHoverEnterEffect(GameObject go) => ActivateHover();

    public void OnHoverExitEffect(GameObject go) => DeactivateHover();

    public void OnSelect(BaseEventData eventData) => ActivateHover();

    public void OnDeselect(BaseEventData eventData) => DeactivateHover();

    private void ActivateHover()
    {
        if (text == null)
            return;

        if (!isHovered)
        {
            isHovered = true;
            PlayHoverSound();
        }

        Color hoverColor = originalColor * brightness;

        hoverColor.r = Mathf.Clamp01(hoverColor.r);
        hoverColor.g = Mathf.Clamp01(hoverColor.g);
        hoverColor.b = Mathf.Clamp01(hoverColor.b);

        StartHoverAnimation(originalPosition + hoverOffset, hoverColor);
    }

    private void DeactivateHover()
    {
        isHovered = false;
        StartHoverAnimation(originalPosition, originalColor);
    }

    private void PlayHoverSound()
    {
        if (hoverSound == null || audioSource == null)
            return;

        audioSource.PlayOneShot(hoverSound, soundVolume);
    }

    private void StartHoverAnimation(Vector2 targetPosition, Color targetColor)
    {
        if (currentAnimation != null)
            StopCoroutine(currentAnimation);

        currentAnimation = StartCoroutine(AnimateHover(targetPosition, targetColor));
    }

    private IEnumerator AnimateHover(Vector2 targetPosition, Color targetColor)
    {
        Vector2 startPosition = movedRect.anchoredPosition;
        Color startColor = text != null ? text.color : originalColor;

        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            // Continua funcionando mesmo com o jogo pausado
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.SmoothStep(0f, 1f, elapsed / animationSpeed);

            movedRect.anchoredPosition = Vector2.Lerp(startPosition, targetPosition, t);

            if (text != null)
                text.color = Color.Lerp(startColor, targetColor, t);

            yield return null;
        }

        movedRect.anchoredPosition = targetPosition;

        if (text != null)
            text.color = targetColor;

        currentAnimation = null;
    }
}
