using KoalaDeck.Models;

namespace KoalaDeck.ViewModels;

/// <summary>Immutable row of the activity log.</summary>
public sealed class ActivityItem(DeckActivity activity)
{
    public string Time { get; } = activity.Time.ToString("HH:mm:ss");

    public string Source { get; } = activity.Source switch
    {
        ActivitySource.Device => "ESP32",
        ActivitySource.Simulator => "SIM",
        ActivitySource.Dashboard => "UI",
        _ => "SYS",
    };

    public string Command { get; } = activity.Command;

    public string Message { get; } = activity.Message;

    public ActivityKind Kind { get; } = activity.Kind;
}
