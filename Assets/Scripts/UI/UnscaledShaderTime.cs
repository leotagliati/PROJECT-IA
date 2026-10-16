using UnityEngine;

/// <summary>
/// Publica o tempo não escalado como global de shader (<c>_UnscaledTime</c>). O pause zera
/// <c>Time.timeScale</c>, e o <c>_Time</c> do shader congelaria a estática do menu. Vai no
/// objeto do menu: só roda enquanto ele está ativo, que é quando a estática é vista.
/// </summary>
public class UnscaledShaderTime : MonoBehaviour
{
    private static readonly int UnscaledTimeId = Shader.PropertyToID("_UnscaledTime");

    private void Update() => Shader.SetGlobalFloat(UnscaledTimeId, Time.unscaledTime);
}
