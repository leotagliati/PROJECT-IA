using System;
using UnityEngine;

public class DoorLock : MonoBehaviour, IInteractable, IHoldFeedback
{
    [SerializeField] private string prompt = "Destrancar";

    [SerializeField] private string noKeyMessage = "Requer uma chave";

    [SerializeField, Min(0f)] private float holdDuration = 1.2f;

    [Header("Balanço enquanto destranca")]
    [SerializeField] private float wiggleAngle = 4f;

    [SerializeField] private float wiggleFrequency = 22f;

    [Header("Áudio")]
    [SerializeField] private string openSoundId = "padlock_open";

    [Tooltip("Loop enquanto o jogador segura. Vazio = sem som.")]
    [SerializeField] private string workingSoundId = "";

    private Quaternion restRotation;
    private bool working;
    private AudioHandle workingSound;

    public bool IsUnlocked { get; private set; }

    public event Action<DoorLock> Unlocked;

    private void Awake()
    {
        restRotation = transform.localRotation;
    }

    public bool TryGetPrompt(InteractionController interactor, out InteractionPrompt result)
    {
        result = InteractionPrompt.Hold(prompt, holdDuration);

        if (IsUnlocked)
            return false;

        if (interactor.Inventory == null || interactor.Inventory.KeyCount < 1)
            result = result.Blocked(noKeyMessage);

        return true;
    }

    public InteractionResult Interact(InteractionController interactor)
    {
        StopWorking();

        if (IsUnlocked)
            return InteractionResult.Success;

        PlayerInventory inventory = interactor.Inventory;

        if (inventory == null || !inventory.TryUseKey(1))
            return InteractionResult.Fail(noKeyMessage);

        Unlock();
        return InteractionResult.Success;
    }

    public void OnHoldStarted()
    {
        working = true;

        if (!string.IsNullOrEmpty(workingSoundId))
            workingSound = AudioProvider.PlayLoop(workingSoundId, transform);
    }

    public void OnHoldCanceled() => StopWorking();

    /// <summary>Abre sem cobrar chave. Para scripts de cena, cutscene, debug.</summary>
    public virtual void Unlock()
    {
        if (IsUnlocked)
            return;

        IsUnlocked = true;
        StopWorking();

        AudioProvider.PlayAt(openSoundId, transform.position);
        Unlocked?.Invoke(this);

        gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!working)
            return;

        float wiggle = Mathf.Sin(Time.time * wiggleFrequency) * wiggleAngle;
        transform.localRotation = restRotation * Quaternion.Euler(0f, 0f, wiggle);
    }

    private void StopWorking()
    {
        if (!working)
            return;

        working = false;
        transform.localRotation = restRotation;
        AudioProvider.Stop(workingSound);
    }

    private void OnDisable() => StopWorking();
}
