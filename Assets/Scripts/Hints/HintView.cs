using System;
using TMPro;
using UnityEngine;

/// <summary>
/// A linha de dica na tela. Só view: recebe o rich text já pronto do
/// <see cref="HintController"/> e cuida de aparecer, trocar e sumir. O layout (fonte,
/// posição, tamanho) é do prefab/cena, não daqui.
///
/// <see cref="SetText"/> pode ser chamado todo frame: texto igual ao atual é no-op.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class HintView : MonoBehaviour
{
    [SerializeField] private TMP_Text label;

    [SerializeField, Min(0.01f)] private float fadeInTime = 0.45f;

    [SerializeField, Min(0.01f)] private float fadeOutTime = 0.3f;

    [Tooltip("Quanto o texto sobe enquanto aparece, em pixels do canvas.")]
    [SerializeField] private float riseDistance = 12f;

    private CanvasGroup group;
    private RectTransform labelRect;
    private Vector2 restPosition;

    private string requested;
    private string shown;
    private float alpha;

    public TMP_Text Label => label;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        if (label == null)
            label = GetComponentInChildren<TMP_Text>();

        if (label == null)
        {
            Debug.LogError($"{name}: {nameof(HintView)} sem TMP_Text.", this);
            enabled = false;
            return;
        }

        labelRect = label.rectTransform;
        restPosition = labelRect.anchoredPosition;
    }

    /// <summary>Rich text a mostrar. Null ou vazio esconde.</summary>
    public void SetText(string richText)
    {
        requested = string.IsNullOrEmpty(richText) ? null : richText;
    }

    private void Update()
    {
        // Unscaled: com o jogo pausado (timeScale 0) a dica ainda precisa conseguir sumir.
        float dt = Time.unscaledDeltaTime;

        // Troca passa pelo zero: some a antiga inteira e entra a nova. Trocar o texto no meio
        // do fade faria a nova surgir já meio transparente e sem a subida.
        bool swapping = !string.Equals(requested, shown, StringComparison.Ordinal);
        float goal = !swapping && shown != null ? 1f : 0f;

        alpha = Mathf.MoveTowards(alpha, goal, dt / (goal > alpha ? fadeInTime : fadeOutTime));

        // O texto antigo fica no label durante o fade out; só é trocado com alpha zero.
        if (swapping && alpha <= 0f)
        {
            shown = requested;
            if (shown != null)
                label.text = shown;
        }

        group.alpha = alpha;

        float eased = 1f - (1f - alpha) * (1f - alpha);
        labelRect.anchoredPosition = restPosition + Vector2.down * (riseDistance * (1f - eased));
    }
}
