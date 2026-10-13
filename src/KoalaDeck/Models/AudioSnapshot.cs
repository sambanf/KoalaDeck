namespace KoalaDeck.Models;

/// <summary>Live state of one deck channel. Volume and Peak are 0..1.</summary>
public sealed record ChannelSnapshot(int Channel, bool IsAvailable, float Volume, bool IsMuted, float Peak, int SessionCount, string Detail)
{
    public static ChannelSnapshot Unavailable(int channel, string detail) => new(channel, false, 0, false, 0, 0, detail);
}

/// <summary>Live state of the default recording device. Peak is 0..1.</summary>
public sealed record MicSnapshot(bool IsAvailable, bool IsMuted, float Peak, string DeviceName);

/// <summary>An app that owns at least one audio session (all its sessions merged). Peak and Volume are 0..1.</summary>
/// <param name="Name">Process name without ".exe".</param>
/// <param name="ExecutablePath">Full exe path for the icon; null if Windows denied access.</param>
/// <param name="IsPlaying">True while one of its streams is active.</param>
public sealed record AppSnapshot(string Name, string? ExecutablePath, bool IsPlaying, float Peak, float Volume, bool IsMuted);

public sealed record AudioSnapshot(IReadOnlyList<ChannelSnapshot> Channels, MicSnapshot Microphone, IReadOnlyList<AppSnapshot> Apps);

/// <summary>An active playback device.</summary>
/// <param name="Id">Core Audio endpoint ID.</param>
/// <param name="IsDefault">Current Windows default output.</param>
/// <param name="IsHeadphones">Headset / headphones form factor (picks the icon).</param>
public sealed record OutputDeviceInfo(string Id, string Name, bool IsDefault, bool IsHeadphones);

/// <summary>Outcome of a volume / mute action, used for the activity log.</summary>
/// <param name="IsPending">Nothing to change yet (the group's apps are not playing) — not an error.</param>
public sealed record AudioActionResult(bool Success, string Target, float Volume, bool IsMuted, string Message, bool IsPending = false)
{
    public static AudioActionResult Fail(string target, string message) => new(false, target, 0, false, message);

    public static AudioActionResult Pending(string target, string message) => new(true, target, 0, false, message, IsPending: true);
}
