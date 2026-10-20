using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sons de um botão ou slider, por id da AudioLibrary. No Button toca no onClick (clique, Submit do teclado ou do
/// controle); no Slider toca um tique a cada bloco que o valor atravessa, com o pitch subindo junto do valor.
/// </summary>
public class UISound : MonoBehaviour
{
    [Tooltip("Id da AudioLibrary tocado ao apertar o botão. Vazio = sem som.")]
    [SerializeField] private string pressId = "ui_press";

    [Tooltip("Id da AudioLibrary tocado a cada passo do slider. Vazio = sem som.")]
    [SerializeField] private string dragId = "ui_slider";

    [Tooltip("Passos do slider que dão tique. Igual aos segmentos do SegmentedSliderView.")]
    [SerializeField, Min(1)] private int steps = 10;

    [Tooltip("Pitch no mínimo e no máximo do slider.")]
    [SerializeField] private Vector2 pitchRange = new(0.9f, 1.2f);

    private Button button;
    private Slider slider;
    private int lastStep;

    private void Awake()
    {
        button = GetComponent<Button>();
        slider = GetComponent<Slider>();
    }

    private void OnEnable()
    {
        if (button != null)
            button.onClick.AddListener(OnPressed);

        if (slider != null)
        {
            lastStep = StepOf(slider.normalizedValue);
            slider.onValueChanged.AddListener(OnValueChanged);
        }
    }

    private void OnDisable()
    {
        if (button != null)
            button.onClick.RemoveListener(OnPressed);

        if (slider != null)
            slider.onValueChanged.RemoveListener(OnValueChanged);
    }

    private void OnPressed() => AudioProvider.PlayUI(pressId);

    // Por passo e não por evento: o arrasto do mouse dispara onValueChanged a cada pixel, e o encaixe do
    // SegmentedSliderView dispara de novo com o valor corrigido. Só muda de bloco = um tique.
    private void OnValueChanged(float _)
    {
        int step = StepOf(slider.normalizedValue);
        if (step == lastStep)
            return;

        lastStep = step;
        AudioProvider.PlayUI(dragId, 1f, Mathf.Lerp(pitchRange.x, pitchRange.y, slider.normalizedValue));
    }

    private int StepOf(float normalized) => Mathf.RoundToInt(normalized * steps);
}
