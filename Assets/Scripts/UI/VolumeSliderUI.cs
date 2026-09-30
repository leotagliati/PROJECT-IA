using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Slider))]
public class VolumeSliderUI : MonoBehaviour
{
    private Slider slider;

    private void Awake() => slider = GetComponent<Slider>();

    private void OnEnable()
    {
        slider.SetValueWithoutNotify(AudioProvider.MasterVolume);
        slider.onValueChanged.AddListener(OnValueChanged);
    }

    private void OnDisable() => slider.onValueChanged.RemoveListener(OnValueChanged);

    private void OnValueChanged(float value) => AudioProvider.MasterVolume = value;
}
