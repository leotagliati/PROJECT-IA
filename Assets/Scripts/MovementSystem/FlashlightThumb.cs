using UnityEngine;

public class FlashlightThumb : MonoBehaviour
{
    [SerializeField] private Flashlight flashlight;

    [Tooltip("Osso do dedão que aperta o botão.")]
    [SerializeField] private Transform thumbBone;

    [Tooltip("Eixo local do osso em torno do qual o dedão gira para apertar.")]
    [SerializeField] private Vector3 pressAxis = Vector3.right;

    [Tooltip("Graus no fundo do aperto. Negativo inverte o sentido.")]
    [SerializeField] private float pressAngle = 25f;

    [SerializeField, Min(0.01f)] private float pressDuration = 0.18f;

    [Tooltip("Peso do aperto ao longo do tempo normalizado: desce rápido, volta devagar.")]
    [SerializeField] private AnimationCurve pressCurve = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(0.3f, 1f),
        new Keyframe(0.45f, 1f),
        new Keyframe(1f, 0f));

    private float pressTime = float.PositiveInfinity;
    private Quaternion baseRotation;
    private bool applied;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponentInParent<Flashlight>();
    }

    private void OnEnable()
    {
        if (flashlight != null)
            flashlight.Toggled += OnToggled;
    }

    private void OnDisable()
    {
        if (flashlight != null)
            flashlight.Toggled -= OnToggled;

        Restore();
    }

    private void OnToggled(bool on) => pressTime = 0f;

    // Um osso sem curva no clipe não é reescrito pelo Animator: sem desfazer o offset
    // antes da animação, o aperto acumularia frame a frame.
    private void Update() => Restore();

    private void LateUpdate()
    {
        if (thumbBone == null || pressTime >= pressDuration)
            return;

        pressTime += Time.deltaTime;
        float weight = pressCurve.Evaluate(Mathf.Clamp01(pressTime / pressDuration));

        baseRotation = thumbBone.localRotation;
        thumbBone.localRotation = baseRotation * Quaternion.AngleAxis(pressAngle * weight, pressAxis);
        applied = true;
    }

    private void Restore()
    {
        if (!applied || thumbBone == null)
            return;

        thumbBone.localRotation = baseRotation;
        applied = false;
    }
}
