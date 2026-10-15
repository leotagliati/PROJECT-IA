using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Slider))]
public class SensitivitySliderUI : MonoBehaviour
{
    private Slider slider;

    private void Awake() => slider = GetComponent<Slider>();

    private void OnEnable()
    {
        slider.SetValueWithoutNotify(LookSensitivity.Normalized);
        slider.onValueChanged.AddListener(OnValueChanged);
    }

    private void OnDisable() => slider.onValueChanged.RemoveListener(OnValueChanged);

    private void OnValueChanged(float value) => LookSensitivity.Normalized = value;
}
