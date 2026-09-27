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
    [Tooltip("Para onde a câmera aponta ao ser pego (a cabeça do seeker, por exemplo). " +
             "Vazio: a câmera não vira, só dá o zoom.")]
    [SerializeField] private Transform lookTarget;

    [SerializeField, Min(0.01f)] private float turnDuration = 0.4f;

    [Tooltip("Tempo encarando o alvo depois da virada, antes da tela preta. Sem isso o corte cai " +
             "no mesmo frame em que a virada termina e a virada passa despercebida.")]
    [SerializeField, Min(0f)] private float holdDuration = 0.35f;

    [SerializeField, Range(0.3f, 1f)] private float fovMultiplier = 0.8f;

    // Tremor de ROTAÇÃO: tremor de posição, a curta distância da captura, tira o alvo de quadro.
    [Tooltip("Tremor da câmera, em graus.")]
    [SerializeField, Min(0f)] private float shakeAmplitude = 1.5f;

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

        if (lookTarget == null)
            Debug.LogWarning($"{name}: lookTarget vazio — ao ser pego a câmera só dá zoom.", this);
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

        Transform cam = playerCamera.transform;

        if (!string.IsNullOrEmpty(stingerSoundId))
            AudioProvider.PlayFollowing(stingerSoundId, cam);

        Quaternion from = cam.rotation;
        float fromFov = playerCamera.fieldOfView;
        float toFov = fromFov * fovMultiplier;

        // O alvo é lido todo frame: o seeker ainda se mexe um pouco depois do contato.
        for (float t = 0f; t < turnDuration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / turnDuration);

            cam.rotation = Quaternion.Slerp(from, LookRotation(cam, from), k) * Shake(k);
            playerCamera.fieldOfView = Mathf.Lerp(fromFov, toFov, k);
            yield return null;
        }

        // Encarando, com o tremor morrendo.
        for (float t = 0f; t < holdDuration; t += Time.deltaTime)
        {
            cam.rotation = LookRotation(cam, from) * Shake(1f - t / holdDuration);
            playerCamera.fieldOfView = toFov;
            yield return null;
        }

        // Os loops param com t um pouco antes do fim, ainda com resto de tremor: se a tela preta
        // atrasar ou sair, é este snap que garante a câmera parada no alvo.
        cam.rotation = LookRotation(cam, from);
        playerCamera.fieldOfView = toFov;

        CutToBlack?.Invoke();
    }

    private Quaternion LookRotation(Transform cam, Quaternion fallback)
    {
        if (lookTarget == null)
            return fallback;

        Vector3 direction = lookTarget.position - cam.position;

        // Alvo em cima do olho (seeker atravessando a câmera): LookRotation de vetor nulo loga erro.
        return direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : fallback;
    }

    private Quaternion Shake(float strength)
    {
        return Quaternion.Euler(UnityEngine.Random.insideUnitSphere * (shakeAmplitude * strength));
    }
}
