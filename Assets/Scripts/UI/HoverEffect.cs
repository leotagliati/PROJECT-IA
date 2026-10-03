//using UnityEngine;

//public class HoverEffect : MonoBehaviour
//{
//    public void OnHoverEnterEffect(GameObject go)
//    {
//        go.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
//    }
//    public void OnHoverExitEffect(GameObject go)
//    {
//        go.transform.localScale = Vector3.one ;
//    }
//}
using UnityEngine;
using TMPro;
using System.Collections;

public class HoverEffect : MonoBehaviour
{
    [Header("Scale")]
    [SerializeField] private float hoverScale = 1.2f;
    [SerializeField] private float animationSpeed = 0.15f;

    [Header("Color")]
    [SerializeField] private float brightness = 2f;

    private Coroutine currentAnimation;

    // Guarda a cor original do texto
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

    public void OnHoverEnterEffect(GameObject go)
    {
        if (text == null)
            return;

        // Clareia a cor original
        Color hoverColor = originalColor * brightness;

        // Garante que os valores não ultrapassem 1
        hoverColor.r = Mathf.Clamp01(hoverColor.r);
        hoverColor.g = Mathf.Clamp01(hoverColor.g);
        hoverColor.b = Mathf.Clamp01(hoverColor.b);

        StartHoverAnimation(go, hoverScale, hoverColor);
    }

    public void OnHoverExitEffect(GameObject go)
    {
        StartHoverAnimation(go, 1f, originalColor);
    }

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
            AnimateHover(go, targetScale, targetColor)
        );
    }

    private IEnumerator AnimateHover(
        GameObject go,
        float targetScale,
        Color targetColor
    )
    {
        TMP_Text text = go.GetComponentInChildren<TMP_Text>();

        Vector3 startScale = go.transform.localScale;

        Color startColor = text != null
            ? text.color
            : originalColor;

        float elapsed = 0f;

        while (elapsed < animationSpeed)
        {
            elapsed += Time.deltaTime;

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
            if (text != null)
            {
                text.color = Color.Lerp(
                    startColor,
                    targetColor,
                    t
                );
            }

            yield return null;
        }

        // Garante o valor final
        go.transform.localScale = Vector3.one * targetScale;

        if (text != null)
        {
            text.color = targetColor;
        }

        currentAnimation = null;
    }
}

