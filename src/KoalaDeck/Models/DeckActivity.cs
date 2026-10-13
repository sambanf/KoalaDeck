namespace KoalaDeck.Models;

public enum ActivitySource
{
    Device,
    Simulator,
    Dashboard,
    System,
}

public enum ActivityKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One line in the dashboard activity log. Channel: 1..3, 0 = microphone, -1 = none.</summary>
public sealed record DeckActivity(DateTime Time, ActivitySource Source, string Command, string Message, ActivityKind Kind, int Channel);
