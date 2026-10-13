using System.Text.Json.Serialization;

namespace KoalaDeck.Models;

/// <summary>User settings persisted as JSON in %AppData%\KoalaDeck\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Last COM port that connected successfully (null = auto-detect).</summary>
    public string? LastPort { get; set; }

    /// <summary>Connect on launch and auto-detect the ESP32 when its port changes.</summary>
    public bool AutoConnect { get; set; } = true;

    public int BaudRate { get; set; } = 115200;

    /// <summary>Channel 2 group ("Chat").</summary>
    public ChannelSettings Channel2 { get; set; } = new() { Apps = ["Discord"] };

    /// <summary>Channel 3 group ("Music").</summary>
    public ChannelSettings Channel3 { get; set; } = new() { Apps = ["Spotify"] };

    /// <summary>Last known exe path per process name, so group members keep their icon while not running.</summary>
    public Dictionary<string, string> AppPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int VolumeStepPercent { get; set; } = 5;

    /// <summary>
    /// Force DTR on/off. Null = automatic: on for Espressif native USB (ESP32-S2/S3/C3),
    /// off for UART bridges (CP210x / CH340) where DTR drives the auto-reset circuit.
    /// </summary>
    public bool? DtrEnable { get; set; }

    /// <summary>The "still running in the tray" balloon is only shown once.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>v1.0 format ("Discord, TeamSpeak"); migrated into <see cref="Channel2"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel2Target { get; set; }

    /// <summary>v1.0 format; migrated into <see cref="Channel3"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel3Target { get; set; }

    /// <summary>Settings of an app channel (2 or 3); null for the master channel.</summary>
    public ChannelSettings? ForChannel(int channel) => channel switch
    {
        2 => Channel2,
        3 => Channel3,
        _ => null,
    };

    /// <summary>Repairs values from hand-edited or older settings files.</summary>
    public void Normalize()
    {
        VolumeStepPercent = Math.Clamp(VolumeStepPercent, 1, 25);
        if (BaudRate <= 0)
        {
            BaudRate = 115200;
        }

        Channel2 ??= new ChannelSettings();
        Channel3 ??= new ChannelSettings();
        AppPaths = new Dictionary<string, string>(AppPaths ?? new(), StringComparer.OrdinalIgnoreCase);

        if (Channel2Target is not null)
        {
            Channel2.Apps = AppNames.Parse(Channel2Target).ToList();
            Channel2Target = null;
        }

        if (Channel3Target is not null)
        {
            Channel3.Apps = AppNames.Parse(Channel3Target).ToList();
            Channel3Target = null;
        }

        Channel2.Apps = AppNames.Clean(Channel2.Apps).ToList();
        // An app belongs to one group only; channel 2 wins on conflicts.
        Channel3.Apps = AppNames.Clean(Channel3.Apps)
            .Where(app => !Channel2.Apps.Contains(app, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}

/// <summary>An app group bound to one deck channel.</summary>
public sealed class ChannelSettings
{
    /// <summary>Process names (without ".exe") whose volume this channel controls.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Output device the apps are routed to (Core Audio endpoint ID); null = follow the Windows default.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Friendly name of <see cref="OutputDeviceId"/>, shown while that device is unplugged.</summary>
    public string? OutputDeviceName { get; set; }
}

/// <summary>Process-name helpers shared by settings, audio and UI.</summary>
public static class AppNames
{
    /// <summary>"Discord.exe, spotify; C:\Apps\chrome.exe" → Discord, spotify, chrome.</summary>
    public static IEnumerable<string> Parse(string? text) =>
        Clean((text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Normalizes, drops empties and removes case-insensitive duplicates (first spelling wins).</summary>
    public static IEnumerable<string> Clean(IEnumerable<string>? names) =>
        (names ?? Enumerable.Empty<string>())
            .Where(name => name is not null)
            .Select(Normalize)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\Apps\Discord.exe" / "discord.exe" / " Discord " → "Discord" (matching is case-insensitive).</summary>
    public static string Normalize(string name)
    {
        name = name.Trim().Trim('"');
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }
}
