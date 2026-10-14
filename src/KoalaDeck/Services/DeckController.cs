using System.Runtime.InteropServices;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>
/// Glue between the serial protocol and the audio engine: parses incoming lines, executes them and, for commands from
/// the deck, answers with the real Windows state (<see cref="DeckSync"/>) so the deck's display never drifts.
/// </summary>
public sealed class DeckController : IDisposable
{
    private readonly SerialService _serial;
    private readonly AudioService _audio;
    private readonly DeckSync _sync;
    private readonly Func<int> _volumeStepPercent;

    public DeckController(SerialService serial, AudioService audio, DeckSync sync, Func<int> volumeStepPercent)
    {
        _serial = serial;
        _audio = audio;
        _sync = sync;
        _volumeStepPercent = volumeStepPercent;
        _serial.LineReceived += OnLineReceived;
    }

    /// <summary>Raised (on a background thread) after every executed or rejected command.</summary>
    public event EventHandler<DeckActivity>? Activity;

    /// <summary>Executes a command. Commands are queued in arrival order on the audio thread.</summary>
    public async Task<AudioActionResult?> ExecuteAsync(DeckCommand command, ActivitySource source)
    {
        if (command.Action == DeckAction.GetSync)
        {
            return await ReplyToGetSyncAsync(command, source);
        }

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
        catch (COMException ex)
        {
            // Windows' audio stack refused even after a device refresh and retry (e.g. 0x8007001F: the output device
            // was unplugged, went to sleep, is switching, or its driver hung). Not an app bug: report it plainly.
            Logger.Warn($"Command {command}: audio device not responding (0x{ex.HResult:X8} {ex.Message.Trim()})");
            Raise(source, command, $"Audio device not responding (0x{ex.HResult:X8}) — is it unplugged or asleep?", ActivityKind.Warning);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Command {command} failed", ex);
            Raise(source, command, $"Failed: {ex.Message}", ActivityKind.Error);
            return null;
        }
        finally
        {
            if (source == ActivitySource.Device)
            {
                // Always answer the deck with what Windows really did — also when nothing changed (already at 100 %)
                // or the command failed — so its display snaps back to the true value.
                await _sync.SendAsync(force: true);
            }
        }
    }

    public void Dispose()
    {
        _serial.LineReceived -= OnLineReceived;
    }

    private async Task<AudioActionResult?> ReplyToGetSyncAsync(DeckCommand command, ActivitySource source)
    {
        string? line = await _sync.SendAsync(force: true);
        string message = line is null ? "Could not read the audio state"
            : _serial.IsConnected ? $"Sent {line}"
            : $"{line} (not connected, nothing sent)";
        Raise(source, command, message, line is null ? ActivityKind.Warning : ActivityKind.Info);
        return line is null ? null : new AudioActionResult(true, "Deck", 0, false, message);
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
