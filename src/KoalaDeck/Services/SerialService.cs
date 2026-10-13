using System.IO;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

public enum SerialConnectionState
{
    Disconnected,
    Searching,
    Connected,
}

public sealed class SerialStateChangedEventArgs(SerialConnectionState state, string? portName, string message, bool isProblem) : EventArgs
{
    public SerialConnectionState State { get; } = state;

    public string? PortName { get; } = portName;

    public string Message { get; } = message;

    /// <summary>True for errors worth highlighting (port busy, connection lost).</summary>
    public bool IsProblem { get; } = isProblem;
}

/// <summary>
/// USB serial link to the ESP32: COM port discovery (with ESP32 detection by USB VID), connect / disconnect,
/// a background line reader and a watchdog that notices an unplugged cable and reconnects when it comes back.
/// <para>Events are raised on background threads. Handlers must marshal to the UI thread and must not block.</para>
/// </summary>
public sealed partial class SerialService : IDisposable
{
    private const int WatchdogIntervalMs = 1500;
    private const int ReadTimeoutMs = 500;
    private const int MaxLineLength = 128;
    private const string PortsClassGuid = "{4d36e978-e325-11ce-bfc1-08002be10318}";

    private readonly object _sync = new();
    private readonly Timer _watchdog;
    private SerialPort? _port;
    private Stream? _stream;
    private Thread? _reader;
    private int _session;
    private int _faultedSession = -1;
    private string? _faultReason;
    private int _watchdogBusy;
    private bool _disposed;

    private string? _knownPortsSignature;
    private string? _autoScanSignature;
    private string? _autoCandidate;
    private string? _lastOpenError;
    private Dictionary<string, SerialPortInfo> _portInfo = new(StringComparer.OrdinalIgnoreCase);

    private SerialConnectionState _state = SerialConnectionState.Disconnected;
    private string? _statePort;
    private string? _stateMessage;

    public SerialService()
    {
        _watchdog = new Timer(OnWatchdogTick, null, WatchdogIntervalMs, WatchdogIntervalMs);
    }

    /// <summary>A complete, non-empty line received from the device (reader thread).</summary>
    public event EventHandler<string>? LineReceived;

    public event EventHandler<SerialStateChangedEventArgs>? StateChanged;

    /// <summary>A COM port was added or removed (USB plugged / unplugged).</summary>
    public event EventHandler? PortsChanged;

    public int BaudRate { get; set; } = 115200;

    /// <summary>Null = automatic (see <see cref="AppSettings.DtrEnable"/>).</summary>
    public bool? DtrEnable { get; set; }

    /// <summary>When the wanted port is missing, connect to any port that looks like an ESP32.</summary>
    public bool AutoDetect { get; set; } = true;

    /// <summary>Port the user wants (null = auto-detect).</summary>
    public string? TargetPort { get; private set; }

    /// <summary>True while the user wants a connection; the watchdog keeps (re)connecting.</summary>
    public bool KeepConnected { get; private set; }

    public string? ConnectedPort { get; private set; }

    public SerialConnectionState State => _state;

    // ------------------------------------------------------------------ public API

    /// <summary>Connects to <paramref name="portName"/>, or auto-detects an ESP32 when null, and keeps the link alive.</summary>
    public void Connect(string? portName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            KeepConnected = true;
            TargetPort = NormalizePortName(portName);
            if (_port is not null && string.Equals(ConnectedPort, TargetPort, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ClosePort();
            _autoScanSignature = null;
            SetState(SerialConnectionState.Searching, TargetPort,
                TargetPort is null ? "Searching for ESP32…" : $"Connecting to {TargetPort}…");
        }

        KickWatchdog();
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            KeepConnected = false;
            ClosePort();
            SetState(SerialConnectionState.Disconnected, null, "Not connected");
        }
    }

    /// <summary>Closes and reopens the link (tray "Reconnect Serial").</summary>
    public void Reconnect(string? portName = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            KeepConnected = true;
            if (portName is not null)
            {
                TargetPort = NormalizePortName(portName);
            }

            ClosePort();
            _autoScanSignature = null;
            SetState(SerialConnectionState.Searching, TargetPort, "Reconnecting…");
        }

        KickWatchdog();
    }

    /// <summary>Lists COM ports with friendly names and USB IDs. Uses WMI, so call it off the UI thread.</summary>
    public IReadOnlyList<SerialPortInfo> ScanPorts()
    {
        List<SerialPortInfo> ports = QueryPorts();
        Volatile.Write(ref _portInfo, ToLookup(ports));
        return ports;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            KeepConnected = false;
            ClosePort();
        }

        _watchdog.Dispose();
    }

    // ------------------------------------------------------------------ watchdog

    private void KickWatchdog()
    {
        try
        {
            _watchdog.Change(0, WatchdogIntervalMs);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnWatchdogTick(object? state)
    {
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            return;
        }

        try
        {
            CheckConnection();
        }
        catch (Exception ex)
        {
            Logger.Error("Serial watchdog error", ex);
        }
        finally
        {
            Volatile.Write(ref _watchdogBusy, 0);
        }
    }

    private void CheckConnection()
    {
        string[] names = GetPortNames();
        string signature = string.Join("|", names);
        bool portsChanged = _knownPortsSignature is not null && signature != _knownPortsSignature;
        _knownPortsSignature = signature;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_port is not null)
            {
                // Some drivers (CH340) never fail the pending read when unplugged, so also check the port list.
                bool removed = !names.Contains(ConnectedPort, StringComparer.OrdinalIgnoreCase);
                bool faulted = Volatile.Read(ref _faultedSession) == _session;
                if (removed || faulted)
                {
                    string reason = removed ? $"{ConnectedPort} was unplugged" : $"{ConnectedPort}: {_faultReason}";
                    Logger.Warn($"Serial connection lost: {reason}");
                    ClosePort();
                    SetState(KeepConnected ? SerialConnectionState.Searching : SerialConnectionState.Disconnected,
                        TargetPort, $"Connection lost – {reason}", isProblem: true);
                }
            }

            if (_port is null && KeepConnected)
            {
                string? candidate = FindCandidate(names, signature);
                if (candidate is null)
                {
                    SetState(SerialConnectionState.Searching, TargetPort,
                        TargetPort is null ? "Searching for ESP32…" : $"Waiting for {TargetPort}…");
                }
                else
                {
                    TryOpen(candidate);
                }
            }
        }

        if (portsChanged)
        {
            Raise(PortsChanged);
        }
    }

    private string? FindCandidate(string[] names, string signature)
    {
        if (TargetPort is not null && names.Contains(TargetPort, StringComparer.OrdinalIgnoreCase))
        {
            return TargetPort;
        }

        if (TargetPort is not null && !AutoDetect)
        {
            return null;
        }

        // WMI is slow, so only rescan when the set of COM ports changes.
        if (signature != _autoScanSignature)
        {
            _autoScanSignature = signature;
            List<SerialPortInfo> ports = QueryPorts();
            Volatile.Write(ref _portInfo, ToLookup(ports));
            _autoCandidate = ports
                .Where(p => p.IsLikelyEsp32)
                .OrderByDescending(p => p.Esp32Score)
                .Select(p => p.PortName)
                .FirstOrDefault();

            if (_autoCandidate is not null)
            {
                Logger.Info($"Auto-detect picked {_autoCandidate}");
            }
        }

        return _autoCandidate;
    }

    // ------------------------------------------------------------------ open / close (caller holds _sync)

    private void TryOpen(string portName)
    {
        var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = ReadTimeoutMs,
            WriteTimeout = 500,
            DtrEnable = ResolveDtr(portName),
            RtsEnable = false,
            Encoding = Encoding.ASCII,
        };

        try
        {
            port.Open();
            Stream stream = port.BaseStream;

            // Known .NET issue: if a USB-serial adapter vanishes while open, the SerialStream finalizer can throw on
            // the finalizer thread and kill the process. The stream is always closed explicitly in ClosePort instead.
            GC.SuppressFinalize(stream);
            try
            {
                port.DiscardInBuffer();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }

            int session = Interlocked.Increment(ref _session);
            _port = port;
            _stream = stream;
            ConnectedPort = portName;
            if (TargetPort is null || AutoDetect)
            {
                TargetPort = portName; // remember the port that worked
            }

            _lastOpenError = null;
            _reader = new Thread(() => ReadLoop(stream, session)) { IsBackground = true, Name = $"KoalaDeck serial {portName}" };
            _reader.Start();

            Logger.Info($"Serial connected: {portName} @ {BaudRate} baud, DTR {(port.DtrEnable ? "on" : "off")}");
            SetState(SerialConnectionState.Connected, portName, $"{portName} · {BaudRate} baud");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            SafeClose(port, null);
            string message = ex is UnauthorizedAccessException
                ? $"{portName} is in use by another program"
                : $"{portName}: {ex.Message}";
            if (message != _lastOpenError)
            {
                _lastOpenError = message;
                Logger.Warn($"Serial open failed: {message}");
            }

            SetState(KeepConnected ? SerialConnectionState.Searching : SerialConnectionState.Disconnected, portName, message, isProblem: true);
        }
    }

    private void ClosePort()
    {
        Interlocked.Increment(ref _session); // tells the reader loop to stop
        SerialPort? port = _port;
        Stream? stream = _stream;
        Thread? reader = _reader;
        _port = null;
        _stream = null;
        _reader = null;
        ConnectedPort = null;

        if (port is null)
        {
            return;
        }

        SafeClose(port, stream);
        if (reader is not null && reader != Thread.CurrentThread && !reader.Join(1000))
        {
            Logger.Warn("Serial reader thread did not stop in time");
        }

        Logger.Info("Serial port closed");
    }

    private static void SafeClose(SerialPort port, Stream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Closing serial stream: {ex.Message}");
        }

        try
        {
            port.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Closing serial port: {ex.Message}");
        }
    }

    private bool ResolveDtr(string portName)
    {
        if (DtrEnable is bool configured)
        {
            return configured;
        }

        // TinyUSB CDC (ESP32-S2/S3 native USB) only transmits once the host asserts DTR. Boards with a UART bridge
        // (CP210x / CH340) wire DTR into the auto-reset circuit (DTR alone pulls GPIO0 low), so leave it off there.
        Dictionary<string, SerialPortInfo> info = Volatile.Read(ref _portInfo);
        if (!info.ContainsKey(portName))
        {
            info = ToLookup(QueryPorts());
            Volatile.Write(ref _portInfo, info);
        }

        return info.TryGetValue(portName, out SerialPortInfo? port) && port.IsEspressifNativeUsb;
    }

    // ------------------------------------------------------------------ reader thread

    private void ReadLoop(Stream stream, int session)
    {
        var buffer = new byte[256];
        var line = new StringBuilder(MaxLineLength);

        while (Volatile.Read(ref _session) == session)
        {
            int count;
            try
            {
                count = stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _session) == session)
                {
                    // Cable pulled / driver error: let the watchdog close the port and start reconnecting.
                    _faultReason = ex.Message;
                    Volatile.Write(ref _faultedSession, session);
                    KickWatchdog();
                }

                return;
            }

            if (count <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                byte value = buffer[i];
                if (value is (byte)'\n' or (byte)'\r')
                {
                    if (line.Length > 0)
                    {
                        RaiseLine(line.ToString());
                        line.Clear();
                    }
                }
                else if (value is >= 0x20 and < 0x7F && line.Length < MaxLineLength)
                {
                    line.Append((char)value); // drop non-printable bytes (boot-ROM noise at the wrong baud rate)
                }
            }
        }
    }

    // ------------------------------------------------------------------ port discovery

    private static List<SerialPortInfo> QueryPorts()
    {
        string[] names = GetPortNames();
        var found = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE ClassGuid = '{PortsClassGuid}'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject device in results)
            {
                using (device)
                {
                    string name = device["Name"] as string ?? string.Empty;
                    string pnpId = device["PNPDeviceID"] as string ?? string.Empty;
                    Match com = ComPortRegex().Match(name);
                    if (!com.Success)
                    {
                        continue; // e.g. LPT printer ports
                    }

                    string portName = com.Groups[1].Value.ToUpperInvariant();
                    Match vid = VendorIdRegex().Match(pnpId);
                    Match pid = ProductIdRegex().Match(pnpId);
                    found[portName] = new SerialPortInfo(
                        portName,
                        name[..com.Index].Trim(),
                        vid.Success ? vid.Groups[1].Value.ToUpperInvariant() : null,
                        pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : null,
                        pnpId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            Logger.Warn($"WMI port query failed, using plain port names: {ex.Message}");
        }

        return names
            .Select(name => found.TryGetValue(name, out SerialPortInfo? info) ? info : new SerialPortInfo(name, "Serial port", null, null, false))
            .ToList();
    }

    private static string[] GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PortNumber)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Cannot list COM ports: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static int PortNumber(string name) =>
        name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(3), out int number)
            ? number
            : int.MaxValue;

    private static string? NormalizePortName(string? portName) =>
        string.IsNullOrWhiteSpace(portName) ? null : portName.Trim().ToUpperInvariant();

    private static Dictionary<string, SerialPortInfo> ToLookup(IEnumerable<SerialPortInfo> ports) =>
        ports.ToDictionary(p => p.PortName, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ events

    private void SetState(SerialConnectionState state, string? port, string message, bool isProblem = false)
    {
        if (state == _state && port == _statePort && message == _stateMessage)
        {
            return;
        }

        _state = state;
        _statePort = port;
        _stateMessage = message;
        try
        {
            StateChanged?.Invoke(this, new SerialStateChangedEventArgs(state, port, message, isProblem));
        }
        catch (Exception ex)
        {
            Logger.Error("Serial StateChanged handler failed", ex);
        }
    }

    private void RaiseLine(string line)
    {
        try
        {
            LineReceived?.Invoke(this, line);
        }
        catch (Exception ex)
        {
            Logger.Error($"Handling serial line '{line}' failed", ex);
        }
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error("Serial event handler failed", ex);
        }
    }

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortRegex();

    [GeneratedRegex(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorIdRegex();

    [GeneratedRegex(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex ProductIdRegex();
}
