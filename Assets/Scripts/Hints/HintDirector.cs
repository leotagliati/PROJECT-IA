using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>O que encerra um passo roteirizado.</summary>
public enum HintWait
{
    /// <summary>Fica Amount segundos na tela.</summary>
    Duration,
    /// <summary>Uma das Actions foi apertada Amount vezes.</summary>
    Press,
    /// <summary>As Actions ficaram em uso por Amount segundos somados (segurar, andar, mexer o mouse).</summary>
    Use,
    /// <summary>O Signal tocou (<see cref="HintSignals"/>): algo no mundo, não um input.</summary>
    Signal,
}

[Serializable]
public class HintStep
{
    [Tooltip("Identificador do passo. É o que fica lembrado entre reloads.")]
    public string id;

    [Tooltip("Marcação do HintController: {Action}, [Tecla], *destaque*.")]
    [TextArea(2, 4)]
    public string text;

    [Tooltip("Sinal que libera o passo. O roteiro para aqui até ele tocar, e o delay só conta " +
             "depois. Vazio = libera assim que o passo anterior termina.")]
    public string after;

    [Tooltip("Segundos de espera antes de mostrar, contados depois do passo anterior (ou do After).")]
    [Min(0f)] public float delay = 1.5f;

    public HintWait waitFor = HintWait.Duration;

    [Tooltip("Nomes das actions do mapa Player, separados por vírgula; qualquer uma conta. Para Press e Use.")]
    public string actions;

    [Tooltip("Sinal que cumpre o passo. Para Signal.")]
    public string signal;

    [Tooltip("Duration: segundos. Press: quantas vezes. Use: segundos em uso. Signal: ignorado.")]
    [Min(0f)] public float amount = 3f;

    [Tooltip("Segundos máximos na tela sem ser cumprido; depois o roteiro segue. 0 = espera para " +
             "sempre. Para dica opcional, que não pode travar as seguintes.")]
    [Min(0f)] public float timeout;

    [Tooltip("Se o jogador já fez isso sozinho antes do passo, o passo nem aparece.")]
    public bool skipIfAlreadyDone = true;
}

/// <summary>
/// Roteiro de dicas que não dependem de lugar: uma sequência que espera o jogador fazer o que
/// a dica pede antes de passar para a próxima. Dicas de lugar avulsas são
/// <see cref="HintTrigger"/>; dicas de situação (perseguição, fôlego) são quem detecta a
/// situação chamando <see cref="HintController.Show"/> direto.
///
/// Passo que depende do mundo (pegou a chave, abriu a porta) espera um sinal de
/// <see cref="HintSignals"/> em vez de virar trigger solto: só a sequência garante a ordem, e
/// trigger nenhum sabe dizer "ainda não aconteceu".
///
/// O uso das actions e os sinais são contados desde o início da cena, não só com a dica na
/// tela — é o que deixa pular "aperte F" para quem já ligou a lanterna sozinho, e "procure a
/// chave" para quem já a pegou.
/// </summary>
public class HintDirector : MonoBehaviour
{
    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private HintController controller;

    [SerializeField] private bool playOnStart = true;

    [Tooltip("O GameManager recarrega a cena a cada morte. Ligado, passo cumprido não volta no " +
             "retry; desligado, o roteiro recomeça a cada tentativa.")]
    [SerializeField] private bool rememberAcrossReloads = true;

    [Tooltip("Tempo mínimo de cada dica na tela, mesmo se cumprida antes. Sem isso, quem já está " +
             "segurando a tecla vê a dica piscar.")]
    [SerializeField, Min(0f)] private float minVisibleTime = 1.5f;

    [SerializeField] private List<HintStep> steps = new()
    {
        new HintStep
        {
            id = "lanterna", text = "Aperte {Flashlight} para ligar a *lanterna*",
            waitFor = HintWait.Press, actions = "Flashlight", amount = 1f,
        },
        new HintStep
        {
            id = "mover", text = "Use {Move} para se mover e {Look} para olhar ao redor",
            waitFor = HintWait.Use, actions = "Move", amount = 1.5f,
        },
        new HintStep
        {
            id = "agachar", text = "{Crouch} para *agachar*. Agachado, seus passos quase não fazem barulho",
            waitFor = HintWait.Press, actions = "Crouch", amount = 1f, delay = 2f,
        },
        new HintStep
        {
            id = "interagir", text = "Aperte {Interact} para *interagir* com objetos",
            waitFor = HintWait.Press, actions = "Interact", amount = 1f, delay = 2f,
        },
        new HintStep
        {
            id = "chave", text = "Procure pela *chave* para abrir a porta",
            waitFor = HintWait.Signal, signal = "chave_coletada", delay = 1f,
        },
        new HintStep
        {
            id = "espiar", text = "Segure {PeekLeft} ou {PeekRight} para *espiar* pelas quinas",
            after = "porta_trancada_aberta", waitFor = HintWait.Use, actions = "PeekLeft, PeekRight",
            amount = 0.5f, delay = 1f, timeout = 12f,
        },
        new HintStep
        {
            id = "saida", text = "\"Preciso encontrar a saída desse lugar... deve haver uma porta.\"",
            waitFor = HintWait.Signal, signal = "porta_principal", delay = 2f,
        },
        new HintStep
        {
            id = "correr", text = "Segure {Sprint} para *correr*",
            after = "tremor_terminou", waitFor = HintWait.Use, actions = "Sprint", amount = 1f, delay = 0.3f,
        },
    };

    // Estático sobrevive ao reload de cena, que é o objetivo. ResetStatics limpa no Play
    // seguinte quando o domain reload está desligado.
    private static readonly HashSet<string> completedIds = new();

    private readonly Dictionary<InputAction, int> pressCount = new();
    private readonly Dictionary<InputAction, float> useSeconds = new();
    private readonly List<InputAction> trackedActions = new();
    private readonly HashSet<string> raisedSignals = new();
    private InputAction[][] stepActions;
    private Coroutine running;
    private HintHandle currentHint;

    public bool IsPlaying => running != null;

    private void Awake()
    {
        if (controller == null)
            controller = FindFirstObjectByType<HintController>();

        if (controller == null)
        {
            Debug.LogError($"{name}: sem {nameof(HintController)} na cena.", this);
            enabled = false;
            return;
        }

        stepActions = new InputAction[steps.Count][];
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].waitFor == HintWait.Signal && string.IsNullOrWhiteSpace(steps[i].signal))
                Debug.LogWarning($"{name}: passo '{steps[i].id}' espera sinal mas não diz qual — vai travar o roteiro.", this);

            stepActions[i] = ResolveActions(steps[i]);

            // Uma entrada por action, mesmo citada por vários passos: senão o mesmo frame
            // contaria duas vezes.
            foreach (InputAction action in stepActions[i])
            {
                if (pressCount.TryAdd(action, 0))
                {
                    useSeconds.Add(action, 0f);
                    trackedActions.Add(action);
                }
            }
        }
    }

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
        HintSignals.Raised += HandleSignal;
    }

    private void OnDisable()
    {
        HintSignals.Raised -= HandleSignal;
        PlayerInputProvider.Release();
        Stop();
    }

    // Guarda mesmo sinal que nenhum passo espera agora: o passo que depende dele pode estar
    // mais adiante no roteiro, e aí é pulado em vez de esperar algo que já passou.
    private void HandleSignal(string id) => raisedSignals.Add(id);

    private void Start()
    {
        if (playOnStart)
            Play();
    }

    /// <summary>Começa (ou recomeça) o roteiro do primeiro passo ainda não cumprido.</summary>
    public void Play()
    {
        Stop();
        running = StartCoroutine(Run());
    }

    public void Stop()
    {
        if (running != null)
        {
            StopCoroutine(running);
            running = null;
        }

        if (controller != null)
            controller.Hide(currentHint);
        currentHint = default;
    }

    private void Update()
    {
        // Pausado o mapa Player está desligado, então nada conta aqui de qualquer jeito.
        foreach (InputAction action in trackedActions)
        {
            if (action.WasPerformedThisFrame())
                pressCount[action]++;

            // IsPressed usa o press point também em action de valor: segurar o Move ou mexer
            // o mouse no Look conta como uso, mouse parado não.
            if (action.IsPressed())
                useSeconds[action] += Time.deltaTime;
        }
    }

    private IEnumerator Run()
    {
        for (int i = 0; i < steps.Count; i++)
        {
            HintStep step = steps[i];

            if (rememberAcrossReloads && completedIds.Contains(step.id))
                continue;

            if (!string.IsNullOrWhiteSpace(step.after))
                yield return new WaitUntil(() => HasSignal(step.after));

            // WaitForSeconds usa tempo escalado: a pausa congela o roteiro também.
            if (step.delay > 0f)
                yield return new WaitForSeconds(step.delay);

            if (!(step.skipIfAlreadyDone && step.waitFor != HintWait.Duration && Measure(step, i) >= Target(step)))
            {
                float baseline = Measure(step, i);
                float shownAt = Time.time;
                currentHint = controller.Show(step.text);

                yield return new WaitUntil(() =>
                {
                    float onScreen = Time.time - shownAt;
                    return onScreen >= minVisibleTime &&
                           (IsDone(step, i, baseline, onScreen) || (step.timeout > 0f && onScreen >= step.timeout));
                });

                controller.Hide(currentHint);
                currentHint = default;
            }

            // Passo que saiu por timeout também entra: a dica foi vista, e repeti-la a cada
            // morte é só ruído.
            if (rememberAcrossReloads && !string.IsNullOrEmpty(step.id))
                completedIds.Add(step.id);
        }

        running = null;
    }

    // Duration conta o tempo de tela; Press/Use contam o input desde que a dica apareceu. Signal
    // não desconta o baseline: sinal é estado, não contagem — com skipIfAlreadyDone desligado,
    // sinal que já tinha tocado cumpre o passo assim que o tempo mínimo de tela passa.
    private bool IsDone(HintStep step, int index, float baseline, float onScreen) => step.waitFor switch
    {
        HintWait.Duration => onScreen >= step.amount,
        HintWait.Signal => Measure(step, index) >= Target(step),
        _ => Measure(step, index) - baseline >= Target(step),
    };

    private static float Target(HintStep step) => step.waitFor switch
    {
        HintWait.Press => Mathf.Max(1f, Mathf.Round(step.amount)),
        HintWait.Signal => 1f,
        _ => step.amount,
    };

    private bool HasSignal(string id) => !string.IsNullOrWhiteSpace(id) && raisedSignals.Contains(id.Trim());

    private float Measure(HintStep step, int index)
    {
        if (step.waitFor == HintWait.Signal)
            return HasSignal(step.signal) ? 1f : 0f;

        float total = 0f;

        foreach (InputAction action in stepActions[index])
            total += step.waitFor == HintWait.Press ? pressCount[action] : useSeconds[action];

        return total;
    }

    private InputAction[] ResolveActions(HintStep step)
    {
        if (string.IsNullOrWhiteSpace(step.actions))
        {
            if (step.waitFor is HintWait.Press or HintWait.Use)
                Debug.LogWarning($"{name}: passo '{step.id}' espera input mas não lista nenhuma action — vai travar o roteiro.", this);
            return Array.Empty<InputAction>();
        }

        var result = new List<InputAction>();

        foreach (string raw in step.actions.Split(','))
        {
            string actionName = raw.Trim();
            if (actionName.Length == 0)
                continue;

            InputAction action = PlayerInputProvider.Player.Get().FindAction(actionName);
            if (action != null)
                result.Add(action);
            else
                Debug.LogWarning($"{name}: passo '{step.id}' cita a action '{actionName}', que não existe no mapa Player.", this);
        }

        return result.ToArray();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => completedIds.Clear();
}
