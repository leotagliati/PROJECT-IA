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

    private Coroutine currentAnimation;

    // Cor original do texto
    private Color originalColor;

    private TMP_Text text;

    private void Awake()
    {
        text = GetComponentInChildren<TMP_Text>();

        if (text != null)
        {
            originalColor = text.color;
        }
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

        // Clareia a cor original
        Color hoverColor = originalColor * brightness;

        // Evita ultrapassar 1
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
        StartHoverAnimation(
            go,
            1f,
            originalColor
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
        TMP_Text targetText = go.GetComponentInChildren<TMP_Text>();

        Vector3 startScale = go.transform.localScale;

        Color startColor = targetText != null
            ? targetText.color
            : originalColor;

        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            // IMPORTANTE:
            // Time.deltaTime para quando o jogo está pausado.
            // unscaledDeltaTime continua funcionando.
            elapsed += Time.unscaledDeltaTime;

            float t = elapsed / animationSpeed;

            // Suaviza a animação
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

        // Garante o valor final
        go.transform.localScale =
            Vector3.one * targetScale;

        if (targetText != null)
        {
            targetText.color = targetColor;
        }

        currentAnimation = null;
    }
}

