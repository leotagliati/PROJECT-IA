using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Assets.Scripts.Seeker;
using UnityEngine;

/// <summary>
/// Lê o estado de alerta do seeker para a telemetria, sem que o agente saiba: só propriedades públicas, nada escrito,
/// nenhum evento novo no código do treino. Posto em runtime pelo <see cref="PlaytestTelemetry"/>, que só existe fora do
/// treino; quem segura a transição pelo tempo mínimo e emite é ele (<see cref="PlaytestTelemetry.ObserveSeekerState"/>).
///
/// Dois corpos, um monstro só para o app: o agente de verdade em Playing (GraphLocomotion Patrol/Alert/Chase) e o
/// boneco do final em Escaping (SeekerChasePuppet, que só persegue). Cada um só fala no estado do jogo em que age.
///
/// O motivo sai dos contadores por episódio do FreeTrackSense (barulho, pegada, luz): o que subiu logo antes da
/// mudança é a pista que a causou. As flags por step não servem, o agente as zera no mesmo step de física.
/// </summary>
public sealed class SeekerTelemetryWatcher : MonoBehaviour
{
    // Quanto tempo uma pista continua sendo "a" causa de uma mudança para Alerta.
    private const double CueWindow = 1.0;

    // Barulho ouvido até isto depois de uma garrafa bater = foi a garrafa.
    private const double BottleWindow = 1.5;

    private GraphLocomotion locomotion;
    private GraphHiderPerception perception;
    private FreeTrackSense trackSense;
    private SeekerChaseState puppetChase;

    private int noises;
    private int tracks;
    private int lights;
    private string lastCue;
    private double lastCueTime = double.NegativeInfinity;

    public void BindAgent(GraphLocomotion agentLocomotion, GraphHiderPerception agentPerception, FreeTrackSense agentTrackSense)
    {
        locomotion = agentLocomotion;
        perception = agentPerception;
        trackSense = agentTrackSense;
        SnapshotCounters();
    }

    public void BindPuppet(SeekerChaseState chaseState) => puppetChase = chaseState;

    private void Update()
    {
        PlaytestTelemetry telemetry = PlaytestTelemetry.Instance;
        GameManager game = GameManager.Current;
        if (telemetry == null || game == null)
            return;

        if (puppetChase != null)
        {
            if (game.State == GameState.Escaping && puppetChase.IsChasing)
                telemetry.ObserveSeekerState(SeekerStateNames.Chase, "escape");
            return;
        }

        if (locomotion == null)
            return;

        // Fora de Playing também: a pista ouvida no último frame do tutorial ainda é a causa do primeiro Alerta.
        TrackCues(telemetry);

        if (game.State != GameState.Playing)
            return;

        string state = Map(locomotion.State);
        telemetry.ObserveSeekerState(state, ReasonFor(telemetry.SeekerState, state));
    }

    private static string Map(GraphLocomotion.Awareness awareness) => awareness switch
    {
        GraphLocomotion.Awareness.Chase => SeekerStateNames.Chase,
        GraphLocomotion.Awareness.Alert => SeekerStateNames.Alert,
        _ => SeekerStateNames.Patrol,
    };

    // Só importa no frame em que o candidato aparece; o PlaytestTelemetry guarda o daquele frame.
    private string ReasonFor(string from, string to)
    {
        switch (to)
        {
            // Sem ver, a Perseguição só vem de ouvir o alvo CORRENDO (FreeTrackSense.ChaseCue).
            case SeekerStateNames.Chase:
                return perception != null && perception.IsSeeing ? "seen" : "sound";

            case SeekerStateNames.Alert:
                if (from == SeekerStateNames.Chase)
                    return "lost_sight";
                return lastCue != null && Time.realtimeSinceStartupAsDouble - lastCueTime <= CueWindow ? lastCue : "other";

            // O Alerta acabou sem pista nova.
            default:
                return "timeout";
        }
    }

    private void TrackCues(PlaytestTelemetry telemetry)
    {
        if (trackSense == null)
            return;

        int noisesNow = trackSense.EpisodeNoisesHeard;
        int tracksNow = trackSense.EpisodeTracksSeen;
        int lightsNow = trackSense.EpisodeLightsSeen;

        // Contador caiu = episódio novo do agente (ResetEpisode): só realinha.
        if (noisesNow < noises || tracksNow < tracks || lightsNow < lights)
        {
            SnapshotCounters();
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;

        // Barulho por último: no mesmo frame, ele é a pista mais específica.
        if (tracksNow > tracks)
            Cue("footprint", now);
        if (lightsNow > lights)
            Cue("light", now);
        if (noisesNow > noises)
            Cue(now - telemetry.LastBottleNoiseTime <= BottleWindow ? "bottle" : "sound", now);

        noises = noisesNow;
        tracks = tracksNow;
        lights = lightsNow;
    }

    private void Cue(string cue, double now)
    {
        lastCue = cue;
        lastCueTime = now;
    }

    private void SnapshotCounters()
    {
        if (trackSense == null)
            return;

        noises = trackSense.EpisodeNoisesHeard;
        tracks = trackSense.EpisodeTracksSeen;
        lights = trackSense.EpisodeLightsSeen;
    }
}
