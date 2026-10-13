using System.Text.Json;
using KoalaDeck.Models;

namespace KoalaDeck.Tests;

public class TargetAndPortTests
{
    [Fact]
    public void App_names_are_normalized()
    {
        string[] apps = AppNames.Parse(" Discord.exe, spotify ;C:\\Apps\\Chrome.EXE,, discord ").ToArray();
        Assert.Equal(new[] { "Discord", "spotify", "Chrome" }, apps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ; ")]
    public void Empty_text_means_no_app(string? text)
    {
        Assert.Empty(AppNames.Parse(text));
    }

    [Fact]
    public void Version_1_settings_are_migrated_into_groups()
    {
        const string json = """{ "Channel2Target": "Discord.exe, TeamSpeak3", "Channel3Target": "Spotify" }""";
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.Normalize();

        Assert.Equal(new[] { "Discord", "TeamSpeak3" }, settings.Channel2.Apps);
        Assert.Equal(new[] { "Spotify" }, settings.Channel3.Apps);
        Assert.Null(settings.Channel2Target);
        Assert.DoesNotContain("Channel2Target", JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void An_app_belongs_to_one_group_only()
    {
        var settings = new AppSettings();
        settings.Channel2.Apps = ["Discord", "discord.exe"];
        settings.Channel3.Apps = ["Spotify", "DISCORD"];
        settings.Normalize();

        Assert.Equal(new[] { "Discord" }, settings.Channel2.Apps);
        Assert.Equal(new[] { "Spotify" }, settings.Channel3.Apps);
    }

    [Fact]
    public void New_install_defaults_to_discord_and_spotify()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        settings.Normalize();

        Assert.Equal(new[] { "Discord" }, settings.Channel2.Apps);
        Assert.Equal(new[] { "Spotify" }, settings.Channel3.Apps);
        Assert.Null(settings.Channel2.OutputDeviceId);
    }

    [Fact]
    public void Esp32_ports_are_ranked_above_unknown_ports()
    {
        var native = new SerialPortInfo("COM7", "USB Serial Device", "303A", "1001", false);
        var cp210x = new SerialPortInfo("COM5", "Silicon Labs CP210x USB to UART Bridge", "10C4", "EA60", false);
        var ch340 = new SerialPortInfo("COM6", "USB-SERIAL CH340", "1A86", "7523", false);
        var unknown = new SerialPortInfo("COM1", "Communications Port", null, null, false);
        var bluetooth = new SerialPortInfo("COM9", "Standard Serial over Bluetooth link", null, null, true);

        Assert.True(native.Esp32Score > cp210x.Esp32Score);
        Assert.True(cp210x.IsLikelyEsp32);
        Assert.True(ch340.IsLikelyEsp32);
        Assert.False(unknown.IsLikelyEsp32);
        Assert.False(bluetooth.IsLikelyEsp32);
        Assert.True(native.IsEspressifNativeUsb);
        Assert.False(cp210x.IsEspressifNativeUsb);
    }
}
