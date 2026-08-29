using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private PlayerInputActions playerInput;
    private CharacterController controller;

    [Header("Movement settings")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -9.81f;

    [Header("Footsteps")]
    [SerializeField] private string footstepSoundId = "footstep";

    // Cadência por distância, não por tempo: assim andar devagar dá passo espaçado sem
    // precisar de nenhum ajuste, e mudar moveSpeed não desincroniza nada.
    [SerializeField] private float stepDistance = 2f;

    // Altura em que o som nasce, relativa ao pivô do player. 0 = no pé, que é onde o barulho é.
    [SerializeField] private float footstepHeightOffset = 0f;

    private Vector2 moveInput;
    private Vector3 velocity;
    private bool isGrounded;

    private Vector3 lastFootstepPosition;
    private float distanceSinceStep;
    public bool IsMoving => moveInput.magnitude > 0f;

    private void Awake()
    {

        playerInput = new PlayerInputActions();
        controller = GetComponent<CharacterController>();

        playerInput.Player.Jump.performed += ctx => Jump();

        lastFootstepPosition = transform.position;
    }

    private void OnEnable()
    {

        playerInput.Player.Enable();
    }

    private void OnDisable()
    {
        playerInput.Player.Disable();
    }

    private void Update()
    {

        isGrounded = controller.isGrounded;

        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        moveInput = playerInput.Player.Move.ReadValue<Vector2>();
        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * moveSpeed * Time.deltaTime);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        UpdateFootsteps();
    }

    /// <summary>
    /// Roda depois dos dois Move do frame, então mede o deslocamento que de fato aconteceu —
    /// esbarrar numa parede não gera passo, porque a posição não mudou.
    /// </summary>
    private void UpdateFootsteps()
    {
        Vector3 delta = transform.position - lastFootstepPosition;
        delta.y = 0f;   // cair ou subir rampa não conta como caminhada

        distanceSinceStep += delta.magnitude;
        lastFootstepPosition = transform.position;

        if (distanceSinceStep < stepDistance)
            return;

        distanceSinceStep = 0f;
        PlayFootstep(footstepSoundId);
    }

    private void PlayFootstep(string soundId)
    {
        if (string.IsNullOrEmpty(soundId))
            return;

        // PlayAt e não PlayFollowing: o passo fica onde o pé bateu, não anda junto com o player.
        AudioSystem.PlayAt(soundId, transform.position + Vector3.up * footstepHeightOffset);
    }

    private void Jump()
    {
        if (isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
    }
}