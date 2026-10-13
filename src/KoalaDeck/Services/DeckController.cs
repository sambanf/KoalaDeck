using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>Glue between the serial protocol and the audio engine: parses incoming lines and executes them.</summary>
public sealed class DeckController : IDisposable
{
    private readonly SerialService _serial;
    private readonly AudioService _audio;
    private readonly Func<int> _volumeStepPercent;

    public DeckController(SerialService serial, AudioService audio, Func<int> volumeStepPercent)
    {
        _serial = serial;
        _audio = audio;
        _volumeStepPercent = volumeStepPercent;
        _serial.LineReceived += OnLineReceived;
    }

    /// <summary>Raised (on a background thread) after every executed or rejected command.</summary>
    public event EventHandler<DeckActivity>? Activity;

    /// <summary>Executes a command. Commands are queued in arrival order on the audio thread.</summary>
    public async Task<AudioActionResult?> ExecuteAsync(DeckCommand command, ActivitySource source)
    {
        try
        {
            int step = Math.Clamp(_volumeStepPercent(), 1, 50);
            AudioActionResult result = command.Action switch
            {
                DeckAction.VolumeUp => await _audio.ChangeVolumeAsync(command.Channel, step),
                DeckAction.VolumeDown => await _audio.ChangeVolumeAsync(command.Channel, -step),
                DeckAction.ToggleMute => await _audio.ToggleMuteAsync(command.Channel),
                DeckAction.MicMute => await _audio.SetMicrophoneMuteAsync(true),
                DeckAction.MicUnmute => await _audio.SetMicrophoneMuteAsync(false),
                DeckAction.MicToggle => await _audio.SetMicrophoneMuteAsync(null),
                _ => AudioActionResult.Fail("?", "Unsupported command"),
            };

            ActivityKind kind = result.IsPending ? ActivityKind.Info
                : result.Success ? ActivityKind.Success
                : ActivityKind.Warning;
            Raise(source, command, result.Message, kind);
            return result;
        }
        catch (ObjectDisposedException)
        {
            return null; // shutting down
        }
        catch (Exception ex)
        {
            Logger.Error($"Command {command} failed", ex);
            Raise(source, command, $"Failed: {ex.Message}", ActivityKind.Error);
            return null;
        }
    }

    public void Dispose()
    {
        _serial.LineReceived -= OnLineReceived;
    }

    private void OnLineReceived(object? sender, string line)
    {
        if (CommandParser.TryParse(line, out DeckCommand command))
        {
            // Not awaited: the audio call is queued synchronously, so arrival order is preserved.
            _ = ExecuteAsync(command, ActivitySource.Device);
            return;
        }

        Logger.Info($"Ignored serial line: {line}");
        Activity?.Invoke(this, new DeckActivity(DateTime.Now, ActivitySource.Device, line, "Unknown command – ignored", ActivityKind.Info, -1));
    }

    private void Raise(ActivitySource source, DeckCommand command, string message, ActivityKind kind)
    {
        try
        {
            Activity?.Invoke(this, new DeckActivity(DateTime.Now, source, command.ToString(), message, kind,
                command.IsMicrophone ? 0 : command.Channel));
        }
        catch (Exception ex)
        {
            Logger.Error("Activity handler failed", ex);
        }
    }
}
