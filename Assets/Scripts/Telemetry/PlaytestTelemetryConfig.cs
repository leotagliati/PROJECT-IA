using UnityEngine;

/// <summary>
/// Liga/desliga a telemetria de playtest e diz para onde ela manda. Lido uma vez no boot de
/// <c>Resources/PlaytestTelemetryConfig</c>; sem o asset valem os padrões daqui (Editor ligado, build desligada), para
/// que apagar o asset nunca faça a build da banca começar a enviar.
/// </summary>
[CreateAssetMenu(menuName = "PROJECT-IA/Playtest Telemetry Config", fileName = "PlaytestTelemetryConfig")]
public class PlaytestTelemetryConfig : ScriptableObject
{
    public const string ResourcePath = "PlaytestTelemetryConfig";

    [Tooltip("Envia quando roda no Editor.")]
    [SerializeField] private bool enabledInEditor = true;

    [Tooltip("Envia na build. Desligado na build entregue à banca: com isto falso nada é criado, nem a fila.")]
    [SerializeField] private bool enabledInBuilds = false;

    [Tooltip("POST de eventos do relay que roda no PC (repositório do app, relay/).")]
    [SerializeField] private string relayUrl = "http://127.0.0.1:8787/events";

    [Tooltip("Intervalo (s, tempo real) entre esvaziamentos da fila em lote.")]
    [SerializeField, Min(0.05f)] private float flushInterval = 0.2f;

    [Tooltip("Depois de um envio que falhou, espera isto (s) antes de tentar de novo. É o que evita spam com o relay desligado.")]
    [SerializeField, Min(0.5f)] private float retryInterval = 3f;

    [Tooltip("Teto da fila; passando disto, os eventos mais antigos são descartados.")]
    [SerializeField, Min(1)] private int maxQueue = 500;

    [Tooltip("Um estado novo do seeker só vira evento se durar isto (s). Sem isso, a borda da visão gera rajadas chase/alert.")]
    [SerializeField, Min(0f)] private float seekerStateHold = 0.3f;

    public bool IsEnabled => Application.isEditor ? enabledInEditor : enabledInBuilds;
    public string RelayUrl => relayUrl;
    public float FlushInterval => flushInterval;
    public float RetryInterval => retryInterval;
    public int MaxQueue => maxQueue;
    public float SeekerStateHold => seekerStateHold;

    public static PlaytestTelemetryConfig Load()
    {
        var config = Resources.Load<PlaytestTelemetryConfig>(ResourcePath);
        return config != null ? config : CreateInstance<PlaytestTelemetryConfig>();
    }
}
