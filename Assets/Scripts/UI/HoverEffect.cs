using UnityEngine;
using TMPro;
using System.Collections;
using UnityEngine.EventSystems;

public class HoverEffect : MonoBehaviour, ISelectHandler, IDeselectHandler
{
    [Header("Scale")]
    [SerializeField] private float hoverScale = 1.2f;
    [SerializeField] private float animationSpeed = 0.15f;

    [Header("Color")]
    [SerializeField] private float brightness = 2f;

    [Header("Sound")]
    [SerializeField] private AudioClip hoverSound;
    [SerializeField] private float soundVolume = 1f;

    private Coroutine currentAnimation;

    private Color originalColor;
    private TMP_Text text;

    private AudioSource audioSource;

    // Impede o som de tocar novamente enquanto
    // o botão já estiver em estado de hover.
    private bool isHovered = false;

    private void Awake()
    {
        text = GetComponentInChildren<TMP_Text>();

        if (text != null)
        {
            originalColor = text.color;
        }

        audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = false;

        // IMPORTANTE para o menu de Pause
        audioSource.ignoreListenerPause = true;
    }

    // =========================================================
    // MOUSE
    // =========================================================

    public void OnHoverEnterEffect(GameObject go)
    {
        ActivateHover(go);
    }

    public void OnHoverExitEffect(GameObject go)
    {
        DeactivateHover(go);
    }

    // =========================================================
    // EVENT SYSTEM / TECLADO / CONTROLE
    // =========================================================

    public void OnSelect(BaseEventData eventData)
    {
        ActivateHover(gameObject);
    }

    public void OnDeselect(BaseEventData eventData)
    {
        DeactivateHover(gameObject);
    }

    // =========================================================
    // HOVER
    // =========================================================

    private void ActivateHover(GameObject go)
    {
        if (text == null)
            return;

        // Se já está em hover, não toca o som novamente.
        if (!isHovered)
        {
            isHovered = true;
            PlayHoverSound();
        }

        Color hoverColor = originalColor * brightness;

        hoverColor.r = Mathf.Clamp01(hoverColor.r);
        hoverColor.g = Mathf.Clamp01(hoverColor.g);
        hoverColor.b = Mathf.Clamp01(hoverColor.b);

        StartHoverAnimation(
            go,
            hoverScale,
            hoverColor
        );
    }

    private void DeactivateHover(GameObject go)
    {
        isHovered = false;

        StartHoverAnimation(
            go,
            1f,
            originalColor
        );
    }

    // =========================================================
    // SOM
    // =========================================================

    private void PlayHoverSound()
    {
        if (hoverSound == null || audioSource == null)
            return;

        audioSource.PlayOneShot(
            hoverSound,
            soundVolume
        );
    }

    // =========================================================
    // ANIMAÇÃO
    // =========================================================

    private void StartHoverAnimation(
        GameObject go,
        float targetScale,
        Color targetColor
    )
    {
        if (currentAnimation != null)
        {
            StopCoroutine(currentAnimation);
        }

        currentAnimation = StartCoroutine(
            AnimateHover(
                go,
                targetScale,
                targetColor
            )
        );
    }

    private IEnumerator AnimateHover(
        GameObject go,
        float targetScale,
        Color targetColor
    )
    {
        TMP_Text targetText =
            go.GetComponentInChildren<TMP_Text>();

        Vector3 startScale =
            go.transform.localScale;

        Color startColor = targetText != null
            ? targetText.color
            : originalColor;

        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            // Continua funcionando mesmo com o jogo pausado
            elapsed += Time.unscaledDeltaTime;

            float t = elapsed / animationSpeed;

            t = Mathf.SmoothStep(0f, 1f, t);

            // Scale
            go.transform.localScale = Vector3.Lerp(
                startScale,
                Vector3.one * targetScale,
                t
            );

            // Cor
            if (targetText != null)
            {
                targetText.color = Color.Lerp(
                    startColor,
                    targetColor,
                    t
                );
            }

            yield return null;
        }

        go.transform.localScale =
            Vector3.one * targetScale;

        if (targetText != null)
        {
            targetText.color = targetColor;
        }

        currentAnimation = null;
    }
}
