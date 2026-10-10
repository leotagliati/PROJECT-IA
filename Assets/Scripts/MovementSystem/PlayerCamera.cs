using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class PlayerCamera : MonoBehaviour
{
    [Header("Settings")]
    public Camera playerCamera; // Referência da nossa câmera

    [Tooltip("Graus por unidade de delta do mouse/pointer. Independente do FPS.")]
    public float pointerSensitivity = 0.5f;

    [Tooltip("Graus por segundo por unidade do stick (já escalado pelo processor do action).")]
    [FormerlySerializedAs("mouseSensitivity")]
    public float stickSensitivity = 30f;

    private Vector2 lookInput;
    private float xRotation = 0f;

    /// <summary>Pitch atual em graus. Positivo = olhando para baixo (convenção do Euler X do Unity).</summary>
    public float Pitch => xRotation;

    private void OnEnable()
    {
        PlayerInputProvider.Acquire();
    }

    private void OnDisable()
    {
        PlayerInputProvider.Release();
    }

    private void Start()
    {
        // Trava o mouse no centro da tela e deixa ele invisível
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        InputAction look = PlayerInputProvider.Player.Look;
        lookInput = look.ReadValue<Vector2>();

        bool isDelta = look.activeControl == null || look.activeControl.device is Pointer;
        Vector2 degrees = isDelta
            ? lookInput * pointerSensitivity
            : lookInput * (stickSensitivity * Time.deltaTime);

        xRotation -= degrees.y;

        xRotation = Mathf.Clamp(xRotation, -90f, 90f);
        playerCamera.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * degrees.x);
    }
}
