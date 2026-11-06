using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Assets.Scripts.Free;
using Assets.Scripts.Seeker;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// Telemetria de playtest: manda os eventos da partida (contrato v1, <see cref="TelemetryEventTypes"/>) para o relay
/// HTTP do PC, que os repassa ao app de observação. Nada na cena: nasce sozinho no boot, se a
/// <see cref="PlaytestTelemetryConfig"/> deixar, e vive em DontDestroyOnLoad.
///
/// Persistente pelo mesmo motivo do progresso do tutorial: o game over recarrega a cena, e a sessão (sessionId, seq,
/// tentativas, garrafas) tem que atravessar o reload. É também por isso que a FILA mora aqui: o player_caught é
/// enfileirado neste objeto, e o reload que vem depois não leva nada junto.
///
/// Quase tudo é inferido sem tocar no gameplay: GameManager.StateChanged dá tutorial/captura/vitória, o sceneLoaded dá
/// sessão nova x tentativa nova, e o <see cref="SeekerTelemetryWatcher"/> lê o estado do monstro. Só chave e garrafa
/// chamam de fora (<see cref="Telemetry"/>), porque o id da chave e o instante do arremesso só existem lá.
///
/// Nunca bloqueia o jogo: envio em lote por UnityWebRequest numa corrotina, um request por vez. Relay desligado = a
/// fila segura (até o teto, perdendo os mais antigos), um aviso só no Console e nova tentativa a cada retryInterval.
/// </summary>
[DefaultExecutionOrder(-200)]
public sealed class PlaytestTelemetry : MonoBehaviour
{
    private const int ContractVersion = 1;
    private const int MaxBatch = 50;
    private const int RequestTimeoutSeconds = 2;

    // No quit não há próximo frame para a corrotina: espera o último lote no máximo isto, e o jogo fecha de qualquer jeito.
    private const double QuitFlushSeconds = 0.5;

    // Raio em torno de um NoiseEvents em que um ThrowableItem faz do barulho uma garrafa (o Emit sai do ponto de contato).
    private const float BottleNoiseRadius = 0.75f;

    private static PlaytestTelemetry instance;

    // Explícito em vez de ?.: o ?. de quem chama não enxerga o fake null do Unity depois do Destroy.
    public static PlaytestTelemetry Instance => instance != null ? instance : null;

    private PlaytestTelemetryConfig config;

    private readonly List<string> queue = new List<string>();
    private bool sending;
    private double nextSendTime;
    private bool warnedOffline;
    private bool warnedStatus;

    // Sessão (do session_start ao session_end).
    private bool sessionActive;
    private string sessionId;
    private int seq;
    private double sessionStartTime;
    private int attempt;
    private int bottlesThrown;
    private bool tutorialEnded;

    // Carga atual. O estado anterior é guardado aqui porque o StateChanged só manda o novo, e o tutorial_end é
    // justamente a passagem Preparing -> Playing (carga com o tutorial já feito abre direto em Playing).
    private bool inGameScene;
    private GameState lastGameState;

    // seeker_state: o último emitido e o candidato que ainda não durou o seekerStateHold.
    private string seekerEmitted = SeekerStateNames.Patrol;
    private string seekerCandidate;
    private double seekerCandidateSince;
    private string seekerCandidateReason;

    private readonly Collider[] overlap = new Collider[16];

    /// <summary>Último barulho (tempo real) que saiu de uma garrafa. O watcher usa para separar "bottle" de "sound".</summary>
    public double LastBottleNoiseTime { get; private set; } = double.NegativeInfinity;

    /// <summary>Último estado do seeker já emitido (o "from" da próxima transição).</summary>
    public string SeekerState => seekerEmitted;

    private static double Now => Time.realtimeSinceStartupAsDouble;

    private double SessionTime => Now - sessionStartTime;

    // ================================================================================
    // Boot
    // ================================================================================

    // Sem domain reload (Play Mode Options), o estático sobreviveria ao Stop apontando para um objeto destruído.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => instance = null;

    // BeforeSceneLoad: assina o sceneLoaded a tempo de ver a primeira cena. Desligado, nem o objeto existe.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        PlaytestTelemetryConfig config = PlaytestTelemetryConfig.Load();
        if (!config.IsEnabled)
            return;

        var host = new GameObject("[PlaytestTelemetry]");
        DontDestroyOnLoad(host);
        host.AddComponent<PlaytestTelemetry>().config = config;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
    }

    // Os eventos estáticos do jogo zeram no SubsystemRegistration; daqui (depois do BeforeSceneLoad) a assinatura fica.
    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        GameManager.StateChanged += OnGameStateChanged;
        NoiseEvents.Emitted += OnNoise;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameManager.StateChanged -= OnGameStateChanged;
        NoiseEvents.Emitted -= OnNoise;
    }

    private void Start() => StartCoroutine(SendLoop());

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    // mlagents-learn conectado: o Academy já subiu no OnEnable dos agentes da cena, antes do sceneLoaded.
    private static bool IsTrainingConnected() => Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;

    private void Shutdown()
    {
        Debug.Log("[PlaytestTelemetry] Academy conectada a um trainer: telemetria desligada neste Play.");
        queue.Clear();
        sessionActive = false;
        Destroy(gameObject);
    }

    // ================================================================================
    // Sessão, a partir do jogo
    // ================================================================================

    // O GameManager.Current já existe aqui: o sceneLoaded vem depois do Awake/OnEnable da cena e antes do Start (que
    // dispara o primeiro StateChanged).
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        if (IsTrainingConnected())
        {
            Shutdown();
            return;
        }

        ResetSeekerTracking();

        GameManager game = GameManager.Current;
        inGameScene = game != null;

        // Menu ou créditos com a sessão aberta: saiu pelo pause ou pelo game over. Depois da vitória ela já fechou.
        if (!inGameScene)
        {
            EndSession("quit");
            return;
        }

        lastGameState = game.State;

        if (!sessionActive)
        {
            StartSession(scene);
        }
        else
        {
            // Todo reload da cena de jogo é tentativa nova: o game over e o "Reiniciar" do pause.
            attempt++;
            Enqueue(TelemetryEventTypes.AttemptStart, new AttemptData { attempt = attempt });
        }

        AttachWatchers();
    }

    private void StartSession(Scene scene)
    {
        sessionActive = true;
        sessionId = Guid.NewGuid().ToString();
        seq = 0;
        sessionStartTime = Now;
        attempt = 1;
        bottlesThrown = 0;
        tutorialEnded = false;

        Enqueue(TelemetryEventTypes.SessionStart, new SessionStartData
        {
            build = Application.version,
            map = scene.name,
            model = FindModelName(),
        });
    }

    private void EndSession(string reason)
    {
        if (!sessionActive)
            return;

        Enqueue(TelemetryEventTypes.SessionEnd, new SessionEndData { reason = reason });
        sessionActive = false;
    }

    private void OnGameStateChanged(GameState state)
    {
        if (!sessionActive || !inGameScene)
            return;

        GameState previous = lastGameState;
        lastGameState = state;

        switch (state)
        {
            // StartMainLoop: o jogador chegou à porta do tutorial, as paredes temporárias saem e o seeker passa a agir.
            case GameState.Playing when previous == GameState.Preparing && !tutorialEnded:
                tutorialEnded = true;
                Enqueue(TelemetryEventTypes.TutorialEnd, new TutorialEndData { durationSec = (float)SessionTime });
                break;

            // Fila é deste objeto: o reload que o GameOverUI pede depois não leva o evento junto.
            case GameState.Lost:
                seekerCandidate = null;
                Enqueue(TelemetryEventTypes.PlayerCaught, new AttemptData { attempt = attempt });
                break;

            // O tempo é o mesmo da WinScreenUI (GameManager.ElapsedTime), não o relógio de t: aquele para no pause.
            case GameState.Won:
                seekerCandidate = null;
                GameManager game = GameManager.Current;
                Enqueue(TelemetryEventTypes.PlayerEscaped, new PlayerEscapedData
                {
                    totalTimeSec = game != null ? game.ElapsedTime : 0f,
                    attempts = attempt,
                });
                EndSession("escaped");
                break;
        }
    }

    private void OnApplicationQuit()
    {
        EndSession("quit");
        FlushOnQuit();
    }

    // ================================================================================
    // Chamadas de gameplay (via Telemetry)
    // ================================================================================

    // Só a chave da caça vira evento: o KeySpawner instancia as dele como filhas. A do tutorial fica solta na cena, é
    // gasta na porta do tutorial e não conta para a saída; enviada, aparecia no app como "1/0".
    public void KeyCollected(KeyItem key, int total)
    {
        if (!sessionActive || key == null)
            return;

        KeySpawner spawner = key.GetComponentInParent<KeySpawner>();
        if (spawner == null)
            return;

        Enqueue(TelemetryEventTypes.KeyCollected, new KeyCollectedData
        {
            keyId = KeyRootName(key, spawner),
            total = total,
            // As chaves que o spawner pôs no mapa: ele já avisa no Console quando não batem com os cadeados.
            required = spawner.SpawnedKeys.Count,
        });
    }

    // O filho direto do spawner no caminho até a chave: é a instância, a que leva o nome com o ponto de spawn.
    private static string KeyRootName(KeyItem key, KeySpawner spawner)
    {
        Transform node = key.transform;
        while (node.parent != null && node.parent != spawner.transform)
            node = node.parent;
        return node.name;
    }

    public void BottleThrown()
    {
        if (!sessionActive)
            return;

        bottlesThrown++;
        Enqueue(TelemetryEventTypes.BottleThrown, new BottleThrownData { total = bottlesThrown });
    }

    // ================================================================================
    // Seeker
    // ================================================================================

    // Um por seeker da cena. Os do treino nunca chegam aqui (sem GameManager não há sessão), e o filtro de GameMode
    // tira arenas de teste que estejam na mesma cena.
    private void AttachWatchers()
    {
        foreach (FreeExplorerManager agent in FindObjectsByType<FreeExplorerManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            FreeArenaController arena = agent.GetComponentInParent<FreeArenaController>(true);
            if (arena == null || !arena.GameMode)
                continue;

            GetOrAddWatcher(agent.gameObject).BindAgent(
                agent.GetComponentInChildren<Assets.Scripts.Graph.GraphLocomotion>(true),
                agent.GetComponentInChildren<Assets.Scripts.Graph.GraphHiderPerception>(true),
                agent.GetComponentInChildren<FreeTrackSense>(true));
        }

        // O boneco do final começa desativado; o watcher só roda quando a EscapeCutscene o liga.
        foreach (SeekerChasePuppet puppet in FindObjectsByType<SeekerChasePuppet>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            GetOrAddWatcher(puppet.gameObject).BindPuppet(puppet.GetComponentInChildren<SeekerChaseState>(true));
    }

    private static SeekerTelemetryWatcher GetOrAddWatcher(GameObject target)
    {
        var watcher = target.GetComponent<SeekerTelemetryWatcher>();
        return watcher != null ? watcher : target.AddComponent<SeekerTelemetryWatcher>();
    }

    // O monstro renasce em patrulha a cada carga; a transição que sobrou da tentativa anterior não vale para esta.
    private void ResetSeekerTracking()
    {
        seekerEmitted = SeekerStateNames.Patrol;
        seekerCandidate = null;
    }

    /// <summary>
    /// Chamado todo frame pelo watcher ativo com o estado atual. Só emite o estado que durou seekerStateHold, e com o
    /// t do instante em que ele começou (o seq sai na hora de emitir, então pode vir depois de um evento com t maior).
    /// </summary>
    public void ObserveSeekerState(string state, string reason)
    {
        if (!sessionActive)
            return;

        if (state == seekerEmitted)
        {
            seekerCandidate = null;
            return;
        }

        double now = Now;
        if (state != seekerCandidate)
        {
            seekerCandidate = state;
            seekerCandidateSince = now;
            seekerCandidateReason = reason;
            return;
        }

        if (now - seekerCandidateSince < config.SeekerStateHold)
            return;

        Enqueue(TelemetryEventTypes.SeekerState, new SeekerStateData
        {
            from = seekerEmitted,
            to = state,
            reason = seekerCandidateReason,
        }, seekerCandidateSince - sessionStartTime);

        seekerEmitted = state;
        seekerCandidate = null;
    }

    // Barulho de garrafa = havia um ThrowableItem no ponto do Emit (ele emite antes de se desligar ao quebrar).
    private void OnNoise(Vector3 position, float hearingMeters)
    {
        if (!sessionActive)
            return;

        int hits = Physics.OverlapSphereNonAlloc(position, BottleNoiseRadius, overlap, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < hits; i++)
        {
            if (overlap[i] != null && overlap[i].GetComponentInParent<ThrowableItem>() != null)
            {
                LastBottleNoiseTime = Now;
                return;
            }
        }
    }

    private static string FindModelName()
    {
        foreach (FreeExplorerManager agent in FindObjectsByType<FreeExplorerManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior != null && behavior.Model != null)
                return behavior.Model.name;
        }

        return "";
    }

    // ================================================================================
    // Fila e envio
    // ================================================================================

    private void Enqueue(string type, object data) => Enqueue(type, data, SessionTime);

    // Serializado na criação: seq e t ficam presos ao momento do evento, não ao do envio.
    private void Enqueue(string type, object data, double t)
    {
        var json = new StringBuilder(160);
        json.Append("{\"v\":").Append(ContractVersion)
            .Append(",\"sessionId\":\"").Append(sessionId)
            .Append("\",\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture))
            .Append(",\"type\":\"").Append(type)
            .Append("\",\"t\":").Append(Math.Max(0.0, t).ToString("0.000", CultureInfo.InvariantCulture))
            .Append(",\"data\":").Append(JsonUtility.ToJson(data))
            .Append('}');
        seq++;

        queue.Add(json.ToString());
        TrimQueue();
    }

    private void TrimQueue()
    {
        int excess = queue.Count - config.MaxQueue;
        if (excess > 0)
            queue.RemoveRange(0, excess);
    }

    private IEnumerator SendLoop()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(config.FlushInterval);

            if (sending || queue.Count == 0 || Now < nextSendTime)
                continue;

            yield return SendBatch();
        }
    }

    // O lote sai da fila enquanto voa; falhou, volta para a frente (e o teto corta os mais antigos, se encheu).
    private IEnumerator SendBatch()
    {
        int count = Mathf.Min(queue.Count, MaxBatch);
        List<string> batch = queue.GetRange(0, count);
        queue.RemoveRange(0, count);
        sending = true;

        UnityWebRequest request = CreateRequest(batch);
        UnityWebRequestAsyncOperation operation = TrySend(request);
        if (operation != null)
            yield return operation;

        bool ok = operation != null && request.result == UnityWebRequest.Result.Success && request.responseCode == 204;
        if (!ok)
        {
            queue.InsertRange(0, batch);
            TrimQueue();
            nextSendTime = Now + config.RetryInterval;
            WarnOnce(request, operation != null);
        }

        request.Dispose();
        sending = false;
    }

    private UnityWebRequest CreateRequest(List<string> batch)
    {
        string body = "[" + string.Join(",", batch) + "]";
        var request = new UnityWebRequest(config.RelayUrl, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)) { contentType = "application/json" },
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = RequestTimeoutSeconds,
        };
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
    }

    // HTTP sem TLS fora de localhost é recusado na hora pelo "Allow downloads over HTTP" do Player Settings.
    private UnityWebRequestAsyncOperation TrySend(UnityWebRequest request)
    {
        try
        {
            return request.SendWebRequest();
        }
        catch (InvalidOperationException e)
        {
            if (!warnedStatus)
            {
                warnedStatus = true;
                Debug.LogWarning($"[PlaytestTelemetry] Envio recusado pelo Unity ({e.Message}). Relay fora de " +
                                 "127.0.0.1 precisa de \"Allow downloads over HTTP\" no Player Settings.");
            }

            return null;
        }
    }

    // Um aviso só por Play: relay desligado é o caso normal fora do playtest, e o jogo não pode encher o Console.
    private void WarnOnce(UnityWebRequest request, bool sent)
    {
        if (!sent)
            return;

        bool reachedRelay = request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError;
        if (reachedRelay && !warnedStatus)
        {
            warnedStatus = true;
            Debug.LogWarning($"[PlaytestTelemetry] Relay respondeu {request.responseCode} (esperado 204); os eventos " +
                             $"ficam na fila e são reenviados a cada {config.RetryInterval:0.#} s.");
        }
        else if (!reachedRelay && !warnedOffline)
        {
            warnedOffline = true;
            Debug.LogWarning($"[PlaytestTelemetry] Relay inacessível em {config.RelayUrl} ({request.error}). O jogo " +
                             $"segue normal; nova tentativa a cada {config.RetryInterval:0.#} s, sem novos avisos.");
        }
    }

    // Sem frame seguinte no quit: manda o que der num lote só e espera, com teto, para o session_end ter chance de sair.
    private void FlushOnQuit()
    {
        if (queue.Count == 0)
            return;

        UnityWebRequest request = CreateRequest(queue);
        UnityWebRequestAsyncOperation operation = TrySend(request);
        if (operation != null)
        {
            var clock = Stopwatch.StartNew();
            while (!operation.isDone && clock.Elapsed.TotalSeconds < QuitFlushSeconds)
                System.Threading.Thread.Sleep(10);
        }

        queue.Clear();
        request.Dispose();
    }
}
