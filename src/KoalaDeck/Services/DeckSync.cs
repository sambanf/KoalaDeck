using System.Globalization;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>
/// Two-way sync: keeps the deck's display equal to the real Windows values by sending
/// <c>SYNC:vol1,vol2,vol3,mute1,mute2,mute3,micMute\n</c> (volumes 0-100, flags 0/1).
/// <list type="bullet">
/// <item>after every command from the deck, and when the deck sends <c>GET_SYNC</c> (always sent);</item>
/// <item>when the serial link connects (always sent);</item>
/// <item>when Windows reports a change — mouse, volume keys, Volume mixer, another app (sent only if something changed);</item>
/// <item>every second as a safety net while connected (sent only if something changed).</item>
/// </list>
/// The values are read back from Windows each time, so the deck shows 12 % when Windows is at 12 %, whatever the step.
/// All work happens off the UI thread and serial writes are queued, so nothing here ever blocks the UI.
/// </summary>
public sealed class DeckSync : IDisposable
{
    private const int CoalesceMs = 40;      // a mouse drag fires dozens of notifications: send at most ~25 lines/s
    private const int SafetyPollMs = 1000;  // catches what has no notification (an app opening its first session)
    private const int ConnectDelayMs = 250; // let a freshly opened port (and a board that resets on open) settle

    private readonly SerialService _serial;
    private readonly AudioService _audio;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Timer _coalesceTimer;
    private readonly Timer _pollTimer;

    // Last known values, kept while a channel has nothing to read (e.g. its app is closed).
    private readonly int[] _volumes = new int[AudioService.ChannelCount];
    private readonly bool[] _mutes = new bool[AudioService.ChannelCount];
    private bool _micMuted;
    private string? _lastSent;
    private int _scheduled;
    private volatile bool _disposed;

    public DeckSync(SerialService serial, AudioService audio)
    {
        _serial = serial;
        _audio = audio;
        _coalesceTimer = new Timer(_ =>
        {
            Volatile.Write(ref _scheduled, 0);
            _ = SendAsync(force: false);
        });
        _pollTimer = new Timer(_ =>
        {
            if (_serial.IsConnected)
            {
                _ = SendAsync(force: false);
            }
        }, null, SafetyPollMs, SafetyPollMs);

        _audio.StateChanged += OnAudioStateChanged;
        _serial.StateChanged += OnSerialStateChanged;
    }

    /// <summary>The wire format, e.g. <c>SYNC:12,50,80,0,0,0,1\n</c>.</summary>
    public static string Format(IReadOnlyList<int> volumes, IReadOnlyList<bool> mutes, bool micMuted) =>
        string.Create(CultureInfo.InvariantCulture,
            $"SYNC:{volumes[0]},{volumes[1]},{volumes[2]},{Flag(mutes[0])},{Flag(mutes[1])},{Flag(mutes[2])},{Flag(micMuted)}\n");

    /// <summary>
    /// Reads the current values from Windows and sends them to the deck. With <paramref name="force"/> the line is sent
    /// even when it equals the previous one (the deck asked, or just changed something). Returns the line without its
    /// newline, or null when the audio state could not be read. Thread-safe; lines leave in call order.
    /// </summary>
    public async Task<string?> SendAsync(bool force)
    {
        if (_disposed)
        {
            return null;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            AudioSnapshot state = await _audio.GetDeckStateAsync().ConfigureAwait(false);
            string line = BuildLine(state);
            if ((force || line != _lastSent) && _serial.TrySend(line))
            {
                _lastSent = line;
            }

            return line.TrimEnd('\n');
        }
        catch (ObjectDisposedException)
        {
            return null; // shutting down
        }
        catch (Exception ex)
        {
            Logger.Warn($"Deck sync could not read the audio state: {ex.Message.Trim()}");
            return null;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _audio.StateChanged -= OnAudioStateChanged;
        _serial.StateChanged -= OnSerialStateChanged;
        _coalesceTimer.Dispose();
        _pollTimer.Dispose();
    }

    private string BuildLine(AudioSnapshot state)
    {
        for (int i = 0; i < _volumes.Length && i < state.Channels.Count; i++)
        {
            ChannelSnapshot channel = state.Channels[i];
            if (channel.IsAvailable)
            {
                _volumes[i] = Percent(channel.Volume);
                _mutes[i] = channel.IsMuted;
            }
        }

        if (state.Microphone.IsAvailable)
        {
            _micMuted = state.Microphone.IsMuted;
        }

        return Format(_volumes, _mutes, _micMuted);
    }

    /// <summary>Windows notification thread: coalesce the burst into one read a few milliseconds later.</summary>
    private void OnAudioStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_serial.IsConnected || Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        try
        {
            _coalesceTimer.Change(CoalesceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnSerialStateChanged(object? sender, SerialStateChangedEventArgs e)
    {
        if (e.State != SerialConnectionState.Connected)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(ConnectDelayMs).ConfigureAwait(false);
            await SendAsync(force: true).ConfigureAwait(false);
        });
    }

    /// <summary>Same rounding as the Windows volume flyout: 0.125 → 13.</summary>
    internal static int Percent(float scalar) =>
        (int)Math.Clamp(MathF.Round(scalar * 100f, MidpointRounding.AwayFromZero), 0f, 100f);

    private static int Flag(bool value) => value ? 1 : 0;
}
