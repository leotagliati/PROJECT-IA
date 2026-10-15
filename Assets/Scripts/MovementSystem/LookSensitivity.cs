using UnityEngine;

/// <summary>
/// Sensibilidade da câmera escolhida pelo jogador, no molde do <see cref="AudioProvider"/>:
/// estático, vive durante toda a sessão (sobrevive ao Restart da cena) e só zera num Play novo.
/// Escala o ajuste base de <see cref="PlayerCamera"/> (pointer e stick), não o substitui.
/// </summary>
public static class LookSensitivity
{
    public const float Min = 0.25f;
    public const float Max = 4f;
    public const float DefaultNormalized = 0.5f;

    private static float _normalized = DefaultNormalized;

    /// <summary>Posição do slider (0-1).</summary>
    public static float Normalized
    {
        get => _normalized;
        set => _normalized = Mathf.Clamp01(value);
    }

    /// <summary>
    /// Curva geométrica: sensibilidade é percebida em razão, então uma curva linear deixaria
    /// a metade lenta do slider sem resolução. 0 → Min, 0.5 → 1x (o tuning original), 1 → Max.
    /// </summary>
    public static float Multiplier => Min * Mathf.Pow(Max / Min, _normalized);

    // Estático sobrevive ao Stop quando o domain reload está desligado.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _normalized = DefaultNormalized;
}
