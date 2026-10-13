using KoalaDeck.Models;

namespace KoalaDeck.ViewModels;

/// <summary>An entry of a channel's output device dropdown.</summary>
/// <param name="Mode">Same as Master, the communications device, or a specific device (also used for the master list).</param>
/// <param name="Id">Endpoint ID for <see cref="ChannelOutputMode.Device"/>; null for the other modes.</param>
/// <param name="Name">Device name, or the name of the mode.</param>
/// <param name="Detail">Secondary text (the device a mode resolves to, or "Unplugged").</param>
/// <param name="IsHeadphones">Headset / headphones (picks the icon).</param>
/// <param name="IsMissing">Configured device that is currently unplugged.</param>
public sealed record OutputDeviceItem(ChannelOutputMode Mode, string? Id, string Name, string Detail, bool IsHeadphones, bool IsMissing)
{
    public bool IsFollowDefault => Mode == ChannelOutputMode.SameAsMaster;

    public bool IsCommunications => Mode == ChannelOutputMode.Communications;

    public override string ToString() => Name;
}
