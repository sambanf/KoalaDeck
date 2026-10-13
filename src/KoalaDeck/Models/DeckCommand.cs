namespace KoalaDeck.Models;

public enum DeckAction
{
    VolumeUp,
    VolumeDown,
    ToggleMute,
    MicMute,
    MicUnmute,
    MicToggle,
}

/// <summary>A parsed serial command. <see cref="Channel"/> is 1..3 for volume commands and 0 for microphone commands.</summary>
public readonly record struct DeckCommand(DeckAction Action, int Channel)
{
    public bool IsMicrophone => Action is DeckAction.MicMute or DeckAction.MicUnmute or DeckAction.MicToggle;

    /// <summary>The wire format, e.g. <c>VOL_UP_2</c>.</summary>
    public override string ToString() => Action switch
    {
        DeckAction.VolumeUp => $"VOL_UP_{Channel}",
        DeckAction.VolumeDown => $"VOL_DOWN_{Channel}",
        DeckAction.ToggleMute => $"MUTE_TOGGLE_{Channel}",
        DeckAction.MicMute => "MIC_MUTE",
        DeckAction.MicUnmute => "MIC_UNMUTE",
        DeckAction.MicToggle => "MIC_TOGGLE",
        _ => Action.ToString(),
    };
}
