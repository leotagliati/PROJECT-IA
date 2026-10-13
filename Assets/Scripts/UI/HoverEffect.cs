using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections;

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

    [Header("Slider")]
    [SerializeField] private float sliderHandleScale = 1.2f;
    [SerializeField] private AudioClip sliderMoveSound;
    [SerializeField] private float sliderSoundVolume = 1f;

    private Coroutine currentAnimation;
    private Coroutine handleAnimation;

    private Color originalColor;
    private TMP_Text text;
    private AudioSource audioSource;

    private Slider slider;
    private Transform sliderHandle;
    private Vector3 originalHandleScale;

    private bool isHovered = false;
    private float lastSliderSoundTime;
    private const float SliderSoundInterval = 0.05f;

    private void Awake()
    {
        slider = GetComponent<Slider>();

        if (slider != null)
        {
            if (slider.handleRect != null)
            {
                sliderHandle = slider.handleRect;
                originalHandleScale = sliderHandle.localScale;
            }

            slider.onValueChanged.AddListener(OnSliderValueChanged);
        }
        else
        {
            text = GetComponentInChildren<TMP_Text>();

            if (text != null)
                originalColor = text.color;
        }

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.ignoreListenerPause = true;
    }

    private void OnDestroy()
    {
        if (slider != null)
            slider.onValueChanged.RemoveListener(OnSliderValueChanged);
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
        if (slider != null)
        {
            ActivateSliderHandle();
            return;
        }

        ActivateHover(gameObject);
    }

    public void OnDeselect(BaseEventData eventData)
    {
        if (slider != null)
        {
            DeactivateSliderHandle();
            return;
        }

        DeactivateHover(gameObject);
    }

    // =========================================================
    // HOVER DOS BOTÕES
    // =========================================================

    private void ActivateHover(GameObject go)
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
        hoverColor.a = originalColor.a;

        StartHoverAnimation(go, hoverScale, hoverColor);
    }

    private void DeactivateHover(GameObject go)
    {
        isHovered = false;

        StartHoverAnimation(go, 1f, originalColor);
    }

    // =========================================================
    // SLIDER
    // =========================================================

    private void ActivateSliderHandle()
    {
        if (sliderHandle == null)
            return;

        AnimateHandle(sliderHandleScale);
    }

    private void DeactivateSliderHandle()
    {
        if (sliderHandle == null)
            return;

        AnimateHandle(1f);
    }

    private void AnimateHandle(float targetScale)
    {
        if (handleAnimation != null)
            StopCoroutine(handleAnimation);

        handleAnimation = StartCoroutine(
            AnimateHandleCoroutine(targetScale)
        );
    }

    private IEnumerator AnimateHandleCoroutine(float targetScale)
    {
        Vector3 startScale = sliderHandle.localScale;
        Vector3 target = originalHandleScale * targetScale;
        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.SmoothStep(
                0f,
                1f,
                elapsed / animationSpeed
            );

            sliderHandle.localScale = Vector3.Lerp(
                startScale,
                target,
                t
            );

            yield return null;
        }

        sliderHandle.localScale = target;
        handleAnimation = null;
    }

    private void OnSliderValueChanged(float value)
    {
        if (sliderMoveSound == null || audioSource == null)
            return;

        // Evita que o som seja disparado excessivamente
        // quando o slider é movido rapidamente.
        if (Time.unscaledTime - lastSliderSoundTime < SliderSoundInterval)
            return;

        lastSliderSoundTime = Time.unscaledTime;

        audioSource.PlayOneShot(
            sliderMoveSound,
            sliderSoundVolume
        );
    }

    // =========================================================
    // SOM DOS BOTÕES
    // =========================================================

    private void PlayHoverSound()
    {
        if (hoverSound == null || audioSource == null)
            return;

        audioSource.PlayOneShot(hoverSound, soundVolume);
    }

    // =========================================================
    // ANIMAÇÃO DOS BOTÕES
    // =========================================================

    private void StartHoverAnimation(
        GameObject go,
        float targetScale,
        Color targetColor)
    {
        if (currentAnimation != null)
            StopCoroutine(currentAnimation);

        currentAnimation = StartCoroutine(
            AnimateHover(go, targetScale, targetColor)
        );
    }

    private IEnumerator AnimateHover(
        GameObject go,
        float targetScale,
        Color targetColor)
    {
        TMP_Text targetText = go.GetComponentInChildren<TMP_Text>();

        Vector3 startScale = go.transform.localScale;

        Color startColor = targetText != null
            ? targetText.color
            : originalColor;

        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.SmoothStep(
                0f,
                1f,
                elapsed / animationSpeed
            );

            go.transform.localScale = Vector3.Lerp(
                startScale,
                Vector3.one * targetScale,
                t
            );

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

        go.transform.localScale = Vector3.one * targetScale;

        if (targetText != null)
            targetText.color = targetColor;

        currentAnimation = null;
    }
}
