using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Corta a câmera do player para um ponto durante o diálogo e devolve depois. Vai na câmera.
///
/// Não move a câmera "de fora": SpineLook/CameraJuice/ShoulderPeek/CameraShake reescrevem a pose inteira todo
/// LateUpdate, então qualquer pose escrita antes seria apagada. Aqui (90) a pose deles já está pronta e é trocada pela
/// do ponto; como eles nunca param de calcular a pose normal, a volta cai exatamente onde o jogador estava olhando.
/// Antes da Flashlight (100) para o feixe acompanhar a câmera.
/// </summary>
[DefaultExecutionOrder(90)]
public class DialogueCamera : MonoBehaviour
{
    [Tooltip("Malhas escondidas em primeira pessoa (cabeça, pescoço, cabelo: ShadowsOnly) que aparecem enquanto a " +
             "câmera está fora da cabeça.")]
    [SerializeField] private Renderer[] hiddenInFirstPerson;

    private Transform target;
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

    public void Release()
    {
        target = null;
    }

    private void LateUpdate()
    {
        SetRevealed(target != null);

        if (target != null)
            transform.SetPositionAndRotation(target.position, target.rotation);
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
