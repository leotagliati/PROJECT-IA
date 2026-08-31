using UnityEngine;

[RequireComponent(typeof(PlayerMovement))]
public class CameraJuice : MonoBehaviour
{
    [Header("Referências")]
    [SerializeField] private Camera targetCamera;

    [Header("Head bob")]
    [SerializeField] private float walkBobAmount = 0.035f;
    [SerializeField] private float runBobAmount = 0.06f;

    [SerializeField] private float walkBobFrequency = 8f;
    [SerializeField] private float runBobFrequency = 12f;

    [SerializeField] private float bobSmoothing = 8f;

    [Header("Tilt lateral")]
    [SerializeField] private float strafeTilt = 1.6f;
    [SerializeField] private float tiltSmoothing = 6f;

    [Header("FOV")]
    [SerializeField] private float runFovKick = 8f;
    [SerializeField] private float fovSmoothing = 6f;

    [Header("Aterrissagem")]
    [SerializeField] private float landDipAmount = 0.12f;
    [SerializeField] private float landDipRecover = 0.18f;

    private PlayerMovement movement;
    private Transform cameraTransform;

    private Vector3 baseLocalPosition;
    private float baseFov;

    private float bobTimer;
    private float currentBobAmount;
    private float currentBobFrequency;
    private float currentTilt;

    private float dipOffset;
    private float dipVelocity;

    private PlayerState previousState = PlayerState.Idle;

    private void Awake()
    {
        movement = GetComponent<PlayerMovement>();

        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();
    }

    private void OnEnable()
    {
        movement.StateChanged += HandleStateChanged;
    }

    private void OnDisable()
    {
        movement.StateChanged -= HandleStateChanged;

        if (cameraTransform != null)
            cameraTransform.localPosition = baseLocalPosition;
    }

    private void Start()
    {
        if (targetCamera == null)
            return;

        cameraTransform = targetCamera.transform;
        baseLocalPosition = cameraTransform.localPosition;
        baseFov = targetCamera.fieldOfView;

        currentBobFrequency = walkBobFrequency;
    }

    private void HandleStateChanged(PlayerState state)
    {
        // Saiu de Jumping = encostou no chão. Único jeito de sair desse estado.
        if (previousState == PlayerState.Jumping && state != PlayerState.Jumping)
            dipOffset = -landDipAmount;

        previousState = state;
    }

    private void LateUpdate()
    {
        if (cameraTransform == null)
            return;

        UpdateBob();
        UpdateDip();
        UpdateFov();
        UpdateTilt();

        float bobY = Mathf.Sin(bobTimer * 2f) * currentBobAmount;  
        float bobX = Mathf.Cos(bobTimer) * currentBobAmount * 0.5f; 

        cameraTransform.localPosition = baseLocalPosition + new Vector3(bobX, bobY + dipOffset, 0f);

        cameraTransform.localRotation *= Quaternion.Euler(0f, 0f, currentTilt);
    }

    private void UpdateBob()
    {
        float targetAmount;
        float targetFrequency;

        switch (movement.CurrentState)
        {
            case PlayerState.Walking:
                targetAmount = walkBobAmount;
                targetFrequency = walkBobFrequency;
                break;

            case PlayerState.Running:
                targetAmount = runBobAmount;
                targetFrequency = runBobFrequency;
                break;

            default:
                targetAmount = 0f;
                targetFrequency = currentBobFrequency;
                break;
        }

        currentBobAmount = Mathf.Lerp(currentBobAmount, targetAmount, Time.deltaTime * bobSmoothing);
        currentBobFrequency = Mathf.Lerp(currentBobFrequency, targetFrequency, Time.deltaTime * bobSmoothing);

        bobTimer += Time.deltaTime * currentBobFrequency;

        if (bobTimer > Mathf.PI * 2f)
            bobTimer -= Mathf.PI * 2f;
    }

    private void UpdateDip()
    {
        dipOffset = Mathf.SmoothDamp(dipOffset, 0f, ref dipVelocity, landDipRecover);
    }

    private void UpdateFov()
    {
        float targetFov = movement.CurrentState == PlayerState.Running
            ? baseFov + runFovKick
            : baseFov;

        targetCamera.fieldOfView = Mathf.Lerp(targetCamera.fieldOfView, targetFov, Time.deltaTime * fovSmoothing);
    }

    private void UpdateTilt()
    {
        float targetTilt = -movement.MoveInput.x * strafeTilt;

        if (movement.CurrentState == PlayerState.Jumping)
            targetTilt = 0f;

        currentTilt = Mathf.Lerp(currentTilt, targetTilt, Time.deltaTime * tiltSmoothing);
    }
}
