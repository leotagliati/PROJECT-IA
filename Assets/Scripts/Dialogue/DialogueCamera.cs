using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Leva a câmera do player até um ponto durante o diálogo e devolve depois. Vai na câmera.
///
/// Não move a câmera "de fora": SpineLook/CameraJuice/ShoulderPeek/CameraShake reescrevem a pose inteira todo
/// LateUpdate, então qualquer pose escrita antes seria apagada. Aqui (90) a pose deles já está pronta e é misturada
/// com a do ponto pelo peso da transição; como eles nunca param de calcular a pose normal, a volta cai exatamente
/// onde o jogador estava olhando. Antes da Flashlight (100) para o feixe acompanhar a câmera.
/// </summary>
[DefaultExecutionOrder(90)]
public class DialogueCamera : MonoBehaviour
{
    [SerializeField, Min(0.01f)] private float blendTime = 0.6f;

    [Tooltip("Malhas escondidas em primeira pessoa (cabeça, pescoço, cabelo: ShadowsOnly) que aparecem enquanto a " +
             "câmera está fora da cabeça.")]
    [SerializeField] private Renderer[] hiddenInFirstPerson;

    [Tooltip("Peso da transição a partir do qual as malhas aparecem. Mostrar logo no início põe a câmera dentro da " +
             "cabeça por alguns frames.")]
    [SerializeField, Range(0f, 1f)] private float revealAt = 0.25f;

    private Transform target;
    private Transform lastTarget;
    private float weight;
    private ShadowCastingMode[] firstPersonModes;
    private bool revealed;

    public static DialogueCamera Current { get; private set; }

    private void Awake()
    {
        firstPersonModes = new ShadowCastingMode[hiddenInFirstPerson.Length];
        for (int i = 0; i < hiddenInFirstPerson.Length; i++)
            if (hiddenInFirstPerson[i] != null)
                firstPersonModes[i] = hiddenInFirstPerson[i].shadowCastingMode;
    }

    private void OnEnable()
    {
        Current = this;
    }

    private void OnDisable()
    {
        if (Current == this)
            Current = null;

        SetRevealed(false);
    }

    public void Focus(Transform point)
    {
        target = point;
    }

    /// <summary>Solta o foco; a câmera volta suave. O ponto é lembrado até o peso zerar.</summary>
    public void Release()
    {
        target = null;
    }

    private void LateUpdate()
    {
        if (target != null)
            lastTarget = target;

        // Não escalado: o diálogo costuma rodar com o jogo pausado.
        float step = Time.unscaledDeltaTime / blendTime;
        weight = Mathf.Clamp01(weight + (target != null ? step : -step));

        SetRevealed(lastTarget != null && weight > revealAt);

        if (weight <= 0f || lastTarget == null)
            return;

        float k = Mathf.SmoothStep(0f, 1f, weight);
        transform.SetPositionAndRotation(Vector3.Lerp(transform.position, lastTarget.position, k),
                                         Quaternion.Slerp(transform.rotation, lastTarget.rotation, k));
    }

    private void SetRevealed(bool value)
    {
        if (revealed == value || firstPersonModes == null)
            return;

        revealed = value;
        for (int i = 0; i < hiddenInFirstPerson.Length; i++)
            if (hiddenInFirstPerson[i] != null)
                hiddenInFirstPerson[i].shadowCastingMode = value ? ShadowCastingMode.On : firstPersonModes[i];
    }
}
