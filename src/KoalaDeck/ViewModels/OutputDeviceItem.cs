namespace KoalaDeck.ViewModels;

/// <summary>An entry of a channel's output device dropdown.</summary>
/// <param name="Id">Endpoint ID; null = "follow the Windows default".</param>
/// <param name="Name">Device name, or "Windows default".</param>
/// <param name="Detail">Secondary text (the current default device, or "unplugged").</param>
/// <param name="IsHeadphones">Headset / headphones (picks the icon).</param>
/// <param name="IsMissing">Configured device that is currently unplugged.</param>
public sealed record OutputDeviceItem(string? Id, string Name, string Detail, bool IsHeadphones, bool IsMissing)
{
    public bool IsFollowDefault => Id is null;

    public override string ToString() => Name;
}
