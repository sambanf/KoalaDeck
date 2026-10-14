namespace KoalaDeck.Models;

public enum DeckAction
{
    VolumeUp,
    VolumeDown,
    ToggleMute,
    MicMute,
    MicUnmute,
    MicToggle,

    /// <summary>The ESP32 asks for the current state (sent when it boots or reconnects).</summary>
    GetSync,
}

/// <summary>A parsed serial command. <see cref="Channel"/> is 1..3 for volume commands, 0 for microphone commands and -1 for GET_SYNC.</summary>
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
        DeckAction.GetSync => "GET_SYNC",
        _ => Action.ToString(),
    };
}
