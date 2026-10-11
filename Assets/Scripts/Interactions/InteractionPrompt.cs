public enum InteractionKind
{
    Instant,
    Hold,
    Drag
}

public readonly struct InteractionPrompt
{
    public string Label { get; }
    public InteractionKind Kind { get; }
    public float HoldDuration { get; }
    public bool Available { get; }
    public string BlockedReason { get; }

    private InteractionPrompt(string label, InteractionKind kind, float holdDuration, bool available, string blockedReason)
    {
        Label = label;
        Kind = kind;
        HoldDuration = holdDuration;
        Available = available;
        BlockedReason = blockedReason;
    }

    public static InteractionPrompt Instant(string label) =>
        new InteractionPrompt(label, InteractionKind.Instant, 0f, true, null);

    public static InteractionPrompt Hold(string label, float duration) =>
        new InteractionPrompt(label, InteractionKind.Hold, duration, true, null);

    public static InteractionPrompt Drag(string label) =>
        new InteractionPrompt(label, InteractionKind.Drag, 0f, true, null);

    public InteractionPrompt Blocked(string reason) =>
        new InteractionPrompt(Label, Kind, HoldDuration, false, reason);

    public InteractionPrompt WithKind(InteractionKind kind) =>
        new InteractionPrompt(Label, kind, HoldDuration, Available, BlockedReason);
}
