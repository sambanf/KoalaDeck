using System.Globalization;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>
/// Parses the ESP32 text protocol (one command per line, case-insensitive):
/// <c>VOL_UP_n</c>, <c>VOL_DOWN_n</c>, <c>MUTE_TOGGLE_n</c> (n = 1..3), <c>MIC_MUTE</c>, <c>MIC_UNMUTE</c>, <c>MIC_TOGGLE</c>,
/// <c>GET_SYNC</c>. The reply direction (<c>SYNC:...</c>) is built by <see cref="DeckSync"/>.
/// </summary>
public static class CommandParser
{
    public const int ChannelCount = 3;

    public static bool TryParse(string? line, out DeckCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string text = line.Trim().ToUpperInvariant();
        switch (text)
        {
            case "MIC_MUTE":
                command = new DeckCommand(DeckAction.MicMute, 0);
                return true;
            case "MIC_UNMUTE":
                command = new DeckCommand(DeckAction.MicUnmute, 0);
                return true;
            case "MIC_TOGGLE":
                command = new DeckCommand(DeckAction.MicToggle, 0);
                return true;
            case "GET_SYNC":
                command = new DeckCommand(DeckAction.GetSync, -1);
                return true;
        }

        int separator = text.LastIndexOf('_');
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int channel)
            || channel is < 1 or > ChannelCount)
        {
            return false;
        }

        DeckAction? action = text[..separator] switch
        {
            "VOL_UP" => DeckAction.VolumeUp,
            "VOL_DOWN" => DeckAction.VolumeDown,
            "MUTE_TOGGLE" => DeckAction.ToggleMute,
            _ => null,
        };

        if (action is null)
        {
            return false;
        }

        command = new DeckCommand(action.Value, channel);
        return true;
    }
}
