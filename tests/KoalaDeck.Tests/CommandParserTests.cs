using KoalaDeck.Models;
using KoalaDeck.Services;

namespace KoalaDeck.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("VOL_UP_1", DeckAction.VolumeUp, 1)]
    [InlineData("VOL_DOWN_1", DeckAction.VolumeDown, 1)]
    [InlineData("MUTE_TOGGLE_1", DeckAction.ToggleMute, 1)]
    [InlineData("VOL_UP_2", DeckAction.VolumeUp, 2)]
    [InlineData("VOL_DOWN_2", DeckAction.VolumeDown, 2)]
    [InlineData("MUTE_TOGGLE_2", DeckAction.ToggleMute, 2)]
    [InlineData("VOL_UP_3", DeckAction.VolumeUp, 3)]
    [InlineData("VOL_DOWN_3", DeckAction.VolumeDown, 3)]
    [InlineData("MUTE_TOGGLE_3", DeckAction.ToggleMute, 3)]
    [InlineData("MIC_MUTE", DeckAction.MicMute, 0)]
    [InlineData("MIC_UNMUTE", DeckAction.MicUnmute, 0)]
    [InlineData("MIC_TOGGLE", DeckAction.MicToggle, 0)]
    [InlineData("GET_SYNC", DeckAction.GetSync, -1)]
    public void Parses_every_protocol_command(string line, DeckAction action, int channel)
    {
        Assert.True(CommandParser.TryParse(line, out DeckCommand command));
        Assert.Equal(action, command.Action);
        Assert.Equal(channel, command.Channel);
        Assert.Equal(line, command.ToString()); // round-trips to the wire format
    }

    [Theory]
    [InlineData("  vol_up_2\r")]
    [InlineData("Mic_Mute")]
    [InlineData("\tMUTE_TOGGLE_3 ")]
    public void Is_case_and_whitespace_tolerant(string line)
    {
        Assert.True(CommandParser.TryParse(line, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("VOL_UP")]
    [InlineData("VOL_UP_")]
    [InlineData("VOL_UP_0")]
    [InlineData("VOL_UP_4")]
    [InlineData("VOL_UP_-1")]
    [InlineData("VOL_UP_ 1")]
    [InlineData("VOL_SIDEWAYS_1")]
    [InlineData("MIC")]
    [InlineData("ets Jun  8 2016 00:22:57")]
    [InlineData("rst:0x1 (POWERON_RESET),boot:0x13 (SPI_FAST_FLASH_BOOT)")]
    public void Rejects_anything_else(string? line)
    {
        Assert.False(CommandParser.TryParse(line, out _));
    }
}
