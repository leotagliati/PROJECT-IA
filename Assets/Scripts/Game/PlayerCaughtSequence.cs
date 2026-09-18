using System;
using System.Collections;
using UnityEngine;

public class PlayerCaughtSequence : MonoBehaviour
{
    [Tooltip("Desligados ao ser pego. Vazio: movimento, câmera, SpineLook, CameraJuice, ShoulderPeek e InteractionController deste objeto.")]
    [SerializeField] private Behaviour[] disableOnCaught;

    [SerializeField] private Flashlight flashlight;

    [SerializeField] private Camera playerCamera;

    [Header("Virada")]
    [SerializeField] private float turnDuration = 0.4f;
    [SerializeField] private float lookHeight = 1.6f;
    [SerializeField, Range(0.3f, 1f)] private float fovMultiplier = 0.8f;
    [SerializeField] private float shakeAmplitude = 0.03f;

    [Header("Áudio")]
    [Tooltip("Vazio = sem som.")]
    [SerializeField] private string stingerSoundId = "caught";

    public event Action CutToBlack;

    private void Awake()
    {
        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();

        if (flashlight == null)
            flashlight = GetComponentInChildren<Flashlight>();

        // Tudo que lê input do Player entra aqui, não só quem move a câmera: com o SpineLook
        // desligado a câmera é escrita pela coroutine, mas o InteractionController continuaria
        // aceitando E em porta/cadeado durante a morte e na tela preta.
        if (disableOnCaught == null || disableOnCaught.Length == 0)
        {
            disableOnCaught = new Behaviour[]
            {
                GetComponentInChildren<PlayerMovement>(),
                GetComponentInChildren<PlayerCamera>(),
                GetComponentInChildren<SpineLook>(),
                GetComponentInChildren<CameraJuice>(),
                GetComponentInChildren<ShoulderPeek>(),
                GetComponentInChildren<InteractionController>(),
            };
        }
    }

    private void OnEnable() => GameManager.StateChanged += HandleState;

    private void OnDisable() => GameManager.StateChanged -= HandleState;

    private void HandleState(GameState state)
    {
        if (state == GameState.Lost)
            StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        foreach (Behaviour behaviour in disableOnCaught)
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }

        if (flashlight != null)
            flashlight.InputLocked = true;

        if (!string.IsNullOrEmpty(stingerSoundId))
            AudioProvider.PlayFollowing(stingerSoundId, playerCamera.transform);

        SeekerManager seeker = FindFirstObjectByType<SeekerManager>();
        Transform cam = playerCamera.transform;
        Vector3 basePosition = cam.position;
        Quaternion from = cam.rotation;
        float fromFov = playerCamera.fieldOfView;
        float toFov = fromFov * fovMultiplier;

        for (float t = 0f; t < turnDuration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / turnDuration);

            if (seeker != null)
                cam.rotation = Quaternion.Slerp(from, LookAtSeeker(seeker, basePosition), k);

            playerCamera.fieldOfView = Mathf.Lerp(fromFov, toFov, k);
            cam.position = basePosition + UnityEngine.Random.insideUnitSphere * (shakeAmplitude * k);
            yield return null;
        }

        // O loop termina com t < turnDuration, então o último frame fica um pouco antes do
        // alvo e com o shake no máximo. Hoje o corte esconde isso; se a tela preta atrasar
        // ou sair, é este snap que garante a câmera parada e olhando pro seeker.
        if (seeker != null)
            cam.rotation = LookAtSeeker(seeker, basePosition);

        playerCamera.fieldOfView = toFov;
        cam.position = basePosition;

        CutToBlack?.Invoke();
    }

    private Quaternion LookAtSeeker(SeekerManager seeker, Vector3 eye)
    {
        Vector3 target = seeker.transform.position + Vector3.up * lookHeight;
        return Quaternion.LookRotation(target - eye);
    }
}
