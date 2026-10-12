using UnityEngine;

[CreateAssetMenu(menuName = "Interactions/Swing Door Sound Profile", fileName = "SwingDoorSounds")]
public class SwingDoorSoundProfile : ScriptableObject
{
    [Header("Rangido (vazio = sem)")]
    public string creakSoundId = "";

    [Tooltip("Graus/s abaixo dos quais não range.")]
    public float creakMinSpeed = 8f;

    [Tooltip("Graus/s do volume cheio.")]
    public float creakFullSpeed = 120f;

    public Vector2 creakPitchRange = new Vector2(0.85f, 1.15f);

    [Tooltip("Tempo de resposta do volume (s).")]
    public float creakResponse = 0.08f;

    [Header("Batente")]
    [Tooltip("Chegando no batente a partir de slamSpeed.")]
    public string slamSoundId = "";

    public float slamSpeed = 150f;

    [Tooltip("Chegando no batente entre latchMinSpeed e slamSpeed.")]
    public string latchSoundId = "";

    public float latchMinSpeed = 5f;

    [Tooltip("Saindo do batente (trinco, vedação).")]
    public string openSoundId = "";

    [Header("Abertura total")]
    public string openStopSoundId = "";

    public float openStopMinSpeed = 100f;

    [Tooltip("Volume da batida no limiar → na velocidade máxima.")]
    public Vector2 impactVolumeRange = new Vector2(0.5f, 1f);
}
