using System;

/// <summary>
/// Contrato de eventos v1 com o app de playtest: um <c>data</c> por tipo. Os nomes dos campos SÃO o contrato (o
/// JsonUtility serializa pelo nome do campo): renomear aqui quebra o app sem erro nenhum deste lado. Combine antes.
/// Classes em vez de dicionário porque o JsonUtility não serializa dicionário.
/// </summary>
public static class TelemetryEventTypes
{
    public const string SessionStart = "session_start";
    public const string TutorialEnd = "tutorial_end";
    public const string SeekerState = "seeker_state";
    public const string PlayerCaught = "player_caught";
    public const string AttemptStart = "attempt_start";
    public const string KeyCollected = "key_collected";
    public const string BottleThrown = "bottle_thrown";
    public const string PlayerEscaped = "player_escaped";
    public const string SessionEnd = "session_end";
}

/// <summary>Nomes do contrato para o estado do seeker; os internos (Patrol/Alert/Chase) são mapeados para estes.</summary>
public static class SeekerStateNames
{
    public const string Patrol = "patrol";
    public const string Alert = "alert";
    public const string Chase = "chase";
}

[Serializable]
public class SessionStartData
{
    public string build;
    public string map;
    public string model;
}

[Serializable]
public class TutorialEndData
{
    public float durationSec;
}

[Serializable]
public class SeekerStateData
{
    public string from;
    public string to;
    public string reason;
}

[Serializable]
public class AttemptData
{
    public int attempt;
}

[Serializable]
public class KeyCollectedData
{
    public string keyId;
    public int total;
    public int required;
}

[Serializable]
public class BottleThrownData
{
    public int total;
}

[Serializable]
public class PlayerEscapedData
{
    public float totalTimeSec;
    public int attempts;
}

[Serializable]
public class SessionEndData
{
    public string reason;
}

/// <summary>
/// Ponto de entrada dos scripts de gameplay: uma linha por ponto, sem saber se a telemetria existe. Desligada (build da
/// banca, treino do ML-Agents, config) cada chamada é um no-op.
/// </summary>
public static class Telemetry
{
    /// <param name="key">A chave pega. Só conta a que saiu de um KeySpawner (a do tutorial não entra no total).</param>
    /// <param name="total">Chaves com o jogador depois de pegar esta.</param>
    public static void KeyCollected(KeyItem key, int total) => PlaytestTelemetry.Instance?.KeyCollected(key, total);

    /// <summary>O total é da sessão e mora no objeto persistente: não zera entre tentativas.</summary>
    public static void BottleThrown() => PlaytestTelemetry.Instance?.BottleThrown();
}
