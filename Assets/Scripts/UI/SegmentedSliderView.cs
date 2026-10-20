using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Desenha um Slider como blocos (estilo volume de PS1). Só a aparência: o valor, o arrasto do mouse e a navegação
/// continuam do próprio Slider, e quem grava a configuração (VolumeSliderUI, SensitivitySliderUI) não muda.
///
/// No teclado/controle o Slider já anda 10% por toque (sem Whole Numbers), que é um bloco com 10 segmentos; o
/// arrasto do mouse é encaixado no bloco mais próximo para o valor salvo bater com o que aparece.
/// </summary>
[RequireComponent(typeof(Slider))]
public class SegmentedSliderView : MonoBehaviour
{
    [SerializeField, Min(1)] private int segments = 10;

    [Tooltip("Onde os blocos são criados. Vazio = o Fill Area do Slider (o arrasto do mouse usa essa área).")]
    [SerializeField] private RectTransform segmentArea;

    [Tooltip("Sprite do bloco (Images/Menu/SliderSegment, 9-slice). Vazio = retângulo liso.")]
    [SerializeField] private Sprite segmentSprite;

    [SerializeField, Min(0f)] private float gap = 3f;

    [Tooltip("Quanto o bloco do valor atual cresce para cima e para baixo com o slider selecionado.")]
    [SerializeField, Min(0f)] private float selectedGrow = 2f;

    [Tooltip("Opcional. Mostra o número de blocos cheios.")]
    [SerializeField] private TMP_Text valueText;

    [Header("Cores")]
    [SerializeField] private Color filled = new(0.66f, 0.22f, 0.18f);
    [SerializeField] private Color filledSelected = new(0.85f, 0.12f, 0.12f);
    [SerializeField] private Color current = new(1f, 0.35f, 0.29f);
    [SerializeField] private Color empty = new(0.16f, 0.16f, 0.15f);

    private Slider slider;
    private Image[] blocks;
    private int shownCount = -1;
    private bool shownSelected;

    private void Awake()
    {
        slider = GetComponent<Slider>();

        if (segmentArea == null && slider.fillRect != null)
            segmentArea = (RectTransform)slider.fillRect.parent;

        // Só esconde o desenho padrão: os RectTransforms continuam, porque é por eles que o Slider mede o arrasto.
        HideGraphic(slider.fillRect);
        HideGraphic(slider.handleRect);

        Build();
    }

    private void OnEnable()
    {
        slider.onValueChanged.AddListener(Snap);
        shownCount = -1;
    }

    private void OnDisable() => slider.onValueChanged.RemoveListener(Snap);

    private void Build()
    {
        blocks = new Image[segments];

        for (int i = 0; i < segments; i++)
        {
            var go = new GameObject($"Segment {i + 1}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(segmentArea, false);

            // Por âncoras e não por Layout Group: acompanha a largura do slider sem rebuild de layout.
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2((float)i / segments, 0f);
            rect.anchorMax = new Vector2((float)(i + 1) / segments, 1f);
            rect.offsetMin = new Vector2(gap * 0.5f, 0f);
            rect.offsetMax = new Vector2(-gap * 0.5f, 0f);

            Image image = go.GetComponent<Image>();
            image.sprite = segmentSprite;
            image.type = segmentSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.raycastTarget = false;
            blocks[i] = image;
        }
    }

    private void Snap(float value)
    {
        float step = (slider.maxValue - slider.minValue) / segments;
        float snapped = slider.minValue + Mathf.Round((value - slider.minValue) / step) * step;

        // Atribuir dispara o onValueChanged de novo, já com o valor encaixado; o Slider ignora valor igual.
        if (!Mathf.Approximately(snapped, value))
            slider.value = snapped;
    }

    // LateUpdate e por estado: a seleção vem do EventSystem (mouse via PointerSelectable, teclado, controle) e não
    // tem evento único para assinar.
    private void LateUpdate()
    {
        int count = Mathf.RoundToInt(slider.normalizedValue * segments);
        bool selected = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == gameObject;

        if (count == shownCount && selected == shownSelected)
            return;

        shownCount = count;
        shownSelected = selected;

        for (int i = 0; i < segments; i++)
        {
            bool isCurrent = selected && i == count - 1;

            blocks[i].color = i >= count ? empty : isCurrent ? current : selected ? filledSelected : filled;

            float grow = isCurrent ? selectedGrow : 0f;
            var rect = blocks[i].rectTransform;
            rect.offsetMin = new Vector2(rect.offsetMin.x, -grow);
            rect.offsetMax = new Vector2(rect.offsetMax.x, grow);
        }

        if (valueText != null)
            valueText.text = count.ToString();
    }

    private static void HideGraphic(RectTransform rect)
    {
        if (rect != null && rect.TryGetComponent(out Graphic graphic))
            graphic.enabled = false;
    }
}
