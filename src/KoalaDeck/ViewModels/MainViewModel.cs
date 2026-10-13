using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KoalaDeck.Models;
using KoalaDeck.Services;

namespace KoalaDeck.ViewModels;

/// <summary>Dashboard state: connection bar, channel cards (app groups), microphone card, activity log and settings footer.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxActivityItems = 200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settings;
    private readonly AudioService _audio;
    private readonly SerialService _serial;
    private readonly DeckController _deck;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _deviceTimer;

    private SerialPortInfo? _selectedPort;
    private SerialConnectionState _connectionState = SerialConnectionState.Disconnected;
    private string _connectionDetail = "Not connected";
    private bool _autoStart;
    private string _simulateText = "VOL_UP_1";
    private bool _pollBusy;
    private bool _pollErrorLogged;
    private bool _refreshingPorts;
    private bool _refreshingDevices;
    private string? _deviceSignature;
    private bool _scanningApps;
    private IReadOnlyList<AppSnapshot> _lastApps = Array.Empty<AppSnapshot>();
    private Dictionary<string, string?> _runningApps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, InstalledApp> _installed = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _installedScannedUtc = DateTime.MinValue;
    private bool _scanningInstalled;
    private string? _installedListKey;

    public MainViewModel(SettingsService settings, AudioService audio, SerialService serial, DeckController deck)
    {
        _settings = settings;
        _audio = audio;
        _serial = serial;
        _deck = deck;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _autoStart = StartupManager.IsEnabled();

        Channels =
        [
            new ChannelViewModel(this, 1, "Master Audio", isMaster: true,
                Resource("Channel1Color", Colors.DeepSkyBlue), Resource<Geometry>("IconSpeaker", Geometry.Empty), RunningApps, InstalledApps),
            new ChannelViewModel(this, 2, "Chat", isMaster: false,
                Resource("Channel2Color", Colors.SlateBlue), Resource<Geometry>("IconHeadset", Geometry.Empty), RunningApps, InstalledApps),
            new ChannelViewModel(this, 3, "Music", isMaster: false,
                Resource("Channel3Color", Colors.LimeGreen), Resource<Geometry>("IconMusic", Geometry.Empty), RunningApps, InstalledApps),
        ];
        Mic = new MicViewModel(ToggleMicrophone);

        if (!StepOptions.Contains(settings.Current.VolumeStepPercent))
        {
            StepOptions = StepOptions.Append(settings.Current.VolumeStepPercent).Order().ToList();
        }

        RefreshPortsCommand = new RelayCommand(() => _ = RefreshPortsAsync());
        ToggleConnectionCommand = new RelayCommand(ToggleConnection);
        SimulateCommand = new RelayCommand(Simulate, () => !string.IsNullOrWhiteSpace(SimulateText));
        ClearActivityCommand = new RelayCommand(Activity.Clear);
        OpenLogsCommand = new RelayCommand(OpenLogs);

        _pollTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _deviceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = DeviceRefreshInterval };
        _deviceTimer.Tick += (_, _) =>
        {
            _ = RefreshOutputDevicesAsync();
            _ = RefreshRunningAppsAsync();
        };

        _serial.StateChanged += OnSerialStateChanged;
        _serial.PortsChanged += OnSerialPortsChanged;
        _deck.Activity += OnDeckActivity;
    }

    // ------------------------------------------------------------------ bindable state

    public IReadOnlyList<ChannelViewModel> Channels { get; }

    public MicViewModel Mic { get; }

    public ObservableCollection<SerialPortInfo> Ports { get; } = new();

    /// <summary>Running apps, playing ones marked (for the "+" add-app popups).</summary>
    public ObservableCollection<AudioAppItem> RunningApps { get; } = new();

    /// <summary>Installed Start menu apps that are not running ("+" popups only, never the master list).</summary>
    public ObservableCollection<AudioAppItem> InstalledApps { get; } = new();

    public ObservableCollection<ActivityItem> Activity { get; } = new();

    public IReadOnlyList<int> StepOptions { get; } = [1, 2, 5, 10];

    public ICommand RefreshPortsCommand { get; }

    public ICommand ToggleConnectionCommand { get; }

    public ICommand SimulateCommand { get; }

    public ICommand ClearActivityCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public SerialPortInfo? SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    public SerialConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(ConnectionText));
                OnPropertyChanged(nameof(ConnectButtonText));
            }
        }
    }

    public string ConnectionText => ConnectionState switch
    {
        SerialConnectionState.Connected => "Connected",
        SerialConnectionState.Searching => "Searching",
        _ => "Disconnected",
    };

    public string ConnectionDetail
    {
        get => _connectionDetail;
        private set => SetProperty(ref _connectionDetail, value);
    }

    public string ConnectButtonText => ConnectionState == SerialConnectionState.Disconnected ? "Connect" : "Disconnect";

    public bool AutoConnect
    {
        get => _settings.Current.AutoConnect;
        set
        {
            if (_settings.Current.AutoConnect == value)
            {
                return;
            }

            _settings.Current.AutoConnect = value;
            _serial.AutoDetect = value;
            _settings.SaveDelayed();
            OnPropertyChanged();
        }
    }

    /// <summary>"Start with Windows" (HKCU Run key). Shared with the tray menu toggle.</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (value != _autoStart)
            {
                if (StartupManager.SetEnabled(value))
                {
                    _autoStart = value;
                    AddSystemActivity("AUTOSTART", value ? "Koala Deck will start with Windows" : "Start with Windows disabled", ActivityKind.Info);
                }
                else
                {
                    AddSystemActivity("AUTOSTART", "Could not update the Windows startup entry", ActivityKind.Error);
                }
            }

            OnPropertyChanged(); // also reverts the toggle if the registry write failed
        }
    }

    public int VolumeStep
    {
        get => _settings.Current.VolumeStepPercent;
        set
        {
            if (_settings.Current.VolumeStepPercent == value)
            {
                return;
            }

            _settings.Current.VolumeStepPercent = Math.Clamp(value, 1, 25);
            _settings.SaveDelayed();
            OnPropertyChanged();
        }
    }

    public string SimulateText
    {
        get => _simulateText;
        set => SetProperty(ref _simulateText, value);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync()
    {
        AddSystemActivity("START", "Koala Deck is ready", ActivityKind.Info);

        // Groups first: hardware commands may arrive as soon as the serial link is up.
        PushChannelConfig(2);
        PushChannelConfig(3);

        await RefreshOutputDevicesAsync();
        await RefreshRunningAppsAsync();
        _ = RefreshInstalledAppsAsync(); // a few hundred ms; the "+" popups fill in when it is done
        await RefreshPortsAsync();

        AppSettings s = _settings.Current;
        if (s.AutoConnect)
        {
            await RunSerialAsync(() => _serial.Connect(s.LastPort), "auto-connect");
        }
    }

    /// <summary>Live meters and device lists only refresh while the dashboard is visible (idle in the tray).</summary>
    public void SetLiveUpdates(bool enabled)
    {
        if (enabled)
        {
            _pollTimer.Start();
            _deviceTimer.Start();
            _ = RefreshOutputDevicesAsync();
            _ = RefreshRunningAppsAsync();
        }
        else
        {
            _pollTimer.Stop();
            _deviceTimer.Stop();
        }
    }

    public void Reconnect()
    {
        string? port = _serial.TargetPort ?? SelectedPort?.PortName ?? _settings.Current.LastPort;
        _ = RunSerialAsync(() => _serial.Reconnect(port), "reconnect");
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _deviceTimer.Stop();
        _serial.StateChanged -= OnSerialStateChanged;
        _serial.PortsChanged -= OnSerialPortsChanged;
        _deck.Activity -= OnDeckActivity;
    }

    // ------------------------------------------------------------------ app groups

    /// <summary>
    /// Moves an app into a group (channel 2 or 3), or out of every group when <paramref name="channel"/> is 1 (master).
    /// Used by drag &amp; drop, the "+" popup and the remove button.
    /// </summary>
    public void MoveApp(string name, int channel)
    {
        name = AppNames.Normalize(name);
        if (name.Length == 0)
        {
            return;
        }

        var released = new List<int>();
        bool added = false;
        for (int index = 2; index <= AudioService.ChannelCount; index++)
        {
            ChannelSettings group = _settings.Current.ForChannel(index)!;
            if (index == channel)
            {
                if (!group.Apps.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    group.Apps.Add(name);
                    added = true;
                }
            }
            else if (group.Apps.RemoveAll(app => string.Equals(app, name, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                released.Add(index);
            }
        }

        if (!added && released.Count == 0)
        {
            return;
        }

        // Release first, then route: otherwise "back to default" of the old group would undo the new group's device.
        foreach (int index in released)
        {
            PushChannelConfig(index);
        }

        if (added)
        {
            PushChannelConfig(channel);
        }

        _settings.SaveDelayed();
        string destination = channel == AudioService.MasterChannel ? "no group (Master)" : Channels[channel - 1].Title;
        AddActivity(new DeckActivity(DateTime.Now, ActivitySource.Dashboard, "GROUP", $"{TitleFor(name) ?? name} → {destination}", ActivityKind.Info, -1));
        ApplyApps(_lastApps);
    }

    internal void OnOutputSelected(ChannelViewModel channel, OutputDeviceItem output)
    {
        if (channel.IsMaster)
        {
            if (output.Id is not null)
            {
                _ = SetDefaultOutputAsync(output.Id);
            }

            return;
        }

        ChannelSettings group = _settings.Current.ForChannel(channel.Index)!;
        if (output.IsMissing || (group.OutputMode == output.Mode && group.OutputDeviceId == output.Id))
        {
            return;
        }

        group.OutputMode = output.Mode;
        group.OutputDeviceId = output.Id;
        group.OutputDeviceName = output.Id is null ? null : output.Name;
        _settings.SaveDelayed();
        PushChannelConfig(channel.Index);
        AddActivity(new DeckActivity(DateTime.Now, ActivitySource.Dashboard, "OUTPUT",
            $"{channel.Title} → {output.Name}", ActivityKind.Info, channel.Index));
    }

    /// <summary>Sends a group to the audio engine (queued in call order) and reports routing refusals in the activity log.</summary>
    private async void PushChannelConfig(int channel)
    {
        ChannelSettings group = _settings.Current.ForChannel(channel)!;
        try
        {
            string? warning = await _audio.ConfigureChannelAsync(
                channel, group.Apps.ToArray(), group.OutputMode ?? ChannelOutputMode.SameAsMaster, group.OutputDeviceId);
            if (warning is not null)
            {
                AddSystemActivity("OUTPUT", warning, ActivityKind.Warning);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"Configuring channel {channel} failed", ex);
        }
    }

    private async Task SetDefaultOutputAsync(string deviceId)
    {
        try
        {
            AudioActionResult result = await _audio.SetDefaultOutputAsync(deviceId);
            AddActivity(new DeckActivity(DateTime.Now, ActivitySource.Dashboard, "OUTPUT", result.Message, ActivityKind.Info, 1));
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Logger.Error("Changing the default output failed", ex);
            AddSystemActivity("OUTPUT", $"Could not change the default output: {ex.Message}", ActivityKind.Error);
        }

        _deviceSignature = null; // force the dropdowns to show the new default
        await RefreshOutputDevicesAsync();
    }

    /// <summary>Rebuilds the output dropdowns when devices are plugged, unplugged or the default changes.</summary>
    private async Task RefreshOutputDevicesAsync()
    {
        if (_refreshingDevices)
        {
            return;
        }

        _refreshingDevices = true;
        try
        {
            IReadOnlyList<OutputDeviceInfo> devices = await _audio.GetOutputDevicesAsync();
            AppSettings s = _settings.Current;
            var signature = string.Join("|", devices.Select(d => $"{d.Id}/{d.Name}/{d.IsDefault}/{d.IsCommunications}"))
                + $"|{s.Channel2.OutputMode}/{s.Channel2.OutputDeviceId}|{s.Channel3.OutputMode}/{s.Channel3.OutputDeviceId}";
            if (signature == _deviceSignature)
            {
                return;
            }

            _deviceSignature = signature;
            OutputDeviceInfo? defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            OutputDeviceInfo? communications = devices.FirstOrDefault(d => d.IsCommunications);
            List<OutputDeviceItem> deviceItems = devices
                .Select(d => new OutputDeviceItem(ChannelOutputMode.Device, d.Id, d.Name, string.Empty, d.IsHeadphones, false))
                .ToList();

            Channels[0].SetOutputOptions(
                deviceItems.Select(d => d.Id == defaultDevice?.Id ? d with { Detail = "Windows default" } : d),
                item => item.Id == defaultDevice?.Id);

            for (int index = 2; index <= AudioService.ChannelCount; index++)
            {
                ChannelSettings group = s.ForChannel(index)!;
                var options = new List<OutputDeviceItem>
                {
                    new(ChannelOutputMode.SameAsMaster, null, "Same as Master", defaultDevice?.Name ?? "Windows default",
                        defaultDevice?.IsHeadphones == true, false),
                    new(ChannelOutputMode.Communications, null, "Communications device", communications?.Name ?? "Windows default",
                        communications?.IsHeadphones == true, false),
                };
                options.AddRange(deviceItems);
                if (group.OutputMode == ChannelOutputMode.Device && devices.All(d => d.Id != group.OutputDeviceId))
                {
                    options.Add(new OutputDeviceItem(ChannelOutputMode.Device, group.OutputDeviceId,
                        group.OutputDeviceName ?? "Saved device", "Unplugged", false, true));
                }

                Channels[index - 1].SetOutputOptions(options, item => item.Mode == group.OutputMode
                    && (item.Mode != ChannelOutputMode.Device || item.Id == group.OutputDeviceId));
                Channels[index - 1].CanChooseOutput = _audio.IsAppRoutingSupported;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("Listing output devices failed", ex);
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    /// <summary>Rescans running desktop apps (slow rate: every 2 s while the dashboard is visible).</summary>
    private async Task RefreshRunningAppsAsync()
    {
        if (_scanningApps)
        {
            return;
        }

        _scanningApps = true;
        try
        {
            IReadOnlyList<RunningApp> running = await Task.Run(RunningAppScanner.Scan);
            _runningApps = running.ToDictionary(app => app.Name, app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            ApplyApps(_lastApps);
        }
        catch (Exception ex)
        {
            Logger.Error("Scanning running apps failed", ex);
        }
        finally
        {
            _scanningApps = false;
        }
    }

    /// <summary>Called when a "+" popup opens: installed apps change rarely, so rescan at most every 2 minutes.</summary>
    internal void RefreshInstalledAppsIfStale()
    {
        if (DateTime.UtcNow - _installedScannedUtc > TimeSpan.FromMinutes(2))
        {
            _ = RefreshInstalledAppsAsync();
        }
    }

    private async Task RefreshInstalledAppsAsync()
    {
        if (_scanningInstalled)
        {
            return;
        }

        _scanningInstalled = true;
        try
        {
            IReadOnlyList<InstalledApp> installed = await Task.Run(InstalledAppScanner.Scan);
            _installed = installed.ToDictionary(app => app.Name, StringComparer.OrdinalIgnoreCase);
            _installedScannedUtc = DateTime.UtcNow;
            _installedListKey = null; // rebuild the list

            // Start menu titles win over exe descriptions: let every list pick its title again.
            foreach (AppRowViewModel row in Channels.SelectMany(channel => channel.Apps))
            {
                row.Title = null;
            }

            foreach (AudioAppItem item in RunningApps)
            {
                item.Title = null;
            }
            ApplyApps(_lastApps);
        }
        catch (Exception ex)
        {
            Logger.Error("Scanning installed apps failed", ex);
        }
        finally
        {
            _scanningInstalled = false;
        }
    }

    /// <summary>Installed apps that are not running; rebuilt only when that set changes.</summary>
    private void SyncInstalledApps(HashSet<string> running)
    {
        List<InstalledApp> apps = _installed.Values
            .Where(app => !running.Contains(app.Name))
            .OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        string key = string.Join("|", apps.Select(app => app.Name));
        if (key != _installedListKey)
        {
            _installedListKey = key;
            InstalledApps.Clear();
            foreach (InstalledApp app in apps)
            {
                InstalledApps.Add(new AudioAppItem(app.Name) { Title = app.Title });
            }
        }

        foreach (AudioAppItem item in InstalledApps)
        {
            item.Icon ??= AppIconProvider.GetOrQueue(_installed.TryGetValue(item.Name, out InstalledApp? app) ? app.IconPath : null);
        }
    }

    /// <summary>
    /// Syncs the app lists. Group members are always listed; the master card and the "+" popup list every running app
    /// (open window or audio session). Apps with audio come first, then alphabetical order — playing or not only
    /// changes the status dot, so rows do not jump around under the cursor.
    /// </summary>
    private void ApplyApps(IReadOnlyList<AppSnapshot> apps)
    {
        var live = new Dictionary<string, AppSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (AppSnapshot app in apps)
        {
            live.TryAdd(app.Name, app);
        }

        var running = new HashSet<string>(live.Keys, StringComparer.OrdinalIgnoreCase);
        running.UnionWith(_runningApps.Keys);

        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 2; index <= AudioService.ChannelCount; index++)
        {
            List<string> members = _settings.Current.ForChannel(index)!.Apps;
            grouped.UnionWith(members);
            SyncRows(Channels[index - 1], members, live, running, isMember: true);
        }

        List<string> available = running
            .OrderBy(name => live.ContainsKey(name) ? 0 : 1)
            .ThenBy(name => TitleFor(name) ?? name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SyncRows(Channels[0], available.Where(name => !grouped.Contains(name)).ToList(), live, running, isMember: false);
        SyncRunningApps(available, live);
        SyncInstalledApps(running);
    }

    private void SyncRows(
        ChannelViewModel channel,
        IReadOnlyList<string> names,
        Dictionary<string, AppSnapshot> live,
        HashSet<string> running,
        bool isMember)
    {
        ObservableCollection<AppRowViewModel> rows = channel.Apps;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(rows[i].Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            int existing = IndexOf(rows, names[i]);
            AppRowViewModel row;
            if (existing < 0)
            {
                row = new AppRowViewModel(names[i], isMember);
                rows.Insert(i, row);
            }
            else
            {
                row = rows[existing];
                if (existing != i)
                {
                    rows.Move(existing, i);
                }
            }

            live.TryGetValue(names[i], out AppSnapshot? snapshot);
            row.Apply(snapshot, running.Contains(names[i]));
            string? path = ExecutablePath(names[i]);
            row.Icon ??= AppIconProvider.GetOrQueue(path);
            row.Title ??= TitleFor(names[i]);
            if (isMember)
            {
                RememberPath(names[i], path);
            }
        }
    }

    /// <summary>Feeds the "+" popups. Rebuilt only when the set of apps changes; the playing badge updates in place.</summary>
    private void SyncRunningApps(IReadOnlyList<string> names, Dictionary<string, AppSnapshot> live)
    {
        bool same = names.Count == RunningApps.Count
            && names.Select((name, i) => string.Equals(name, RunningApps[i].Name, StringComparison.OrdinalIgnoreCase)).All(match => match);
        if (!same)
        {
            RunningApps.Clear();
            foreach (string name in names)
            {
                RunningApps.Add(new AudioAppItem(name));
            }
        }

        foreach (AudioAppItem item in RunningApps)
        {
            item.IsPlaying = live.TryGetValue(item.Name, out AppSnapshot? snapshot) && snapshot.IsPlaying;
            item.Title ??= TitleFor(item.Name);
            item.Icon ??= AppIconProvider.GetOrQueue(ExecutablePath(item.Name));
        }
    }

    /// <summary>Exe path from the audio session, the window scan, or the last one seen (kept for group members).</summary>
    private string? ExecutablePath(string name) =>
        _lastApps.FirstOrDefault(app => string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase))?.ExecutablePath
        ?? (_runningApps.TryGetValue(name, out string? running) ? running : null)
        ?? (_installed.TryGetValue(name, out InstalledApp? installed) ? installed.IconPath : null)
        ?? (_settings.Current.AppPaths.TryGetValue(name, out string? saved) ? saved : null);

    /// <summary>Friendly name: the Start menu title, else the exe's description ("msedge" → "Microsoft Edge"), else null.</summary>
    private string? TitleFor(string name) =>
        (_installed.TryGetValue(name, out InstalledApp? installed) ? installed.Title : null)
        ?? AppTitleProvider.FromExecutable(ExecutablePath(name));

    /// <summary>Persists a group member's exe path so its icon still shows while the app is closed.</summary>
    private void RememberPath(string name, string? path)
    {
        if (path is not null && (!_settings.Current.AppPaths.TryGetValue(name, out string? known) || known != path))
        {
            _settings.Current.AppPaths[name] = path;
            _settings.SaveDelayed();
        }
    }

    private static int IndexOf(ObservableCollection<AppRowViewModel> rows, string name)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ called by child view models

    internal void SetChannelVolume(ChannelViewModel channel, double value) =>
        Forget(_audio.SetVolumeAsync(channel.Index, (float)(value / 100.0)), "Set volume");

    internal void ToggleChannelMute(ChannelViewModel channel) =>
        Forget(_deck.ExecuteAsync(new DeckCommand(DeckAction.ToggleMute, channel.Index), ActivitySource.Dashboard), "Toggle mute");

    private void ToggleMicrophone() =>
        Forget(_deck.ExecuteAsync(new DeckCommand(DeckAction.MicToggle, 0), ActivitySource.Dashboard), "Toggle microphone");

    // ------------------------------------------------------------------ live polling

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_pollBusy)
        {
            return;
        }

        _pollBusy = true;
        try
        {
            AudioSnapshot snapshot = await _audio.GetSnapshotAsync();
            for (int i = 0; i < Channels.Count && i < snapshot.Channels.Count; i++)
            {
                Channels[i].Apply(snapshot.Channels[i]);
            }

            Mic.Apply(snapshot.Microphone);
            _lastApps = snapshot.Apps;
            ApplyApps(snapshot.Apps);
            _pollErrorLogged = false;
        }
        catch (ObjectDisposedException)
        {
            _pollTimer.Stop();
        }
        catch (Exception ex)
        {
            if (!_pollErrorLogged)
            {
                _pollErrorLogged = true;
                Logger.Error("Audio snapshot failed", ex);
            }
        }
        finally
        {
            _pollBusy = false;
        }
    }

    // ------------------------------------------------------------------ serial

    private void ToggleConnection()
    {
        if (ConnectionState == SerialConnectionState.Disconnected)
        {
            string? port = SelectedPort?.PortName; // null = auto-detect
            _ = RunSerialAsync(() => _serial.Connect(port), "connect");
        }
        else
        {
            _ = RunSerialAsync(_serial.Disconnect, "disconnect");
        }
    }

    /// <summary>Serial calls can block (closing a port, slow drivers), so keep them off the UI thread.</summary>
    private static async Task RunSerialAsync(Action action, string what)
    {
        try
        {
            await Task.Run(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"Serial {what} failed", ex);
        }
    }

    private async Task RefreshPortsAsync()
    {
        if (_refreshingPorts)
        {
            return;
        }

        _refreshingPorts = true;
        try
        {
            IReadOnlyList<SerialPortInfo> ports = await Task.Run(_serial.ScanPorts);
            string? keep = _serial.ConnectedPort ?? SelectedPort?.PortName ?? _settings.Current.LastPort;

            Ports.Clear();
            foreach (SerialPortInfo port in ports)
            {
                Ports.Add(port);
            }

            SelectedPort = Ports.FirstOrDefault(p => string.Equals(p.PortName, keep, StringComparison.OrdinalIgnoreCase))
                ?? Ports.Where(p => p.IsLikelyEsp32).OrderByDescending(p => p.Esp32Score).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error("Refreshing COM ports failed", ex);
        }
        finally
        {
            _refreshingPorts = false;
        }
    }

    private void OnSerialPortsChanged(object? sender, EventArgs e) =>
        _dispatcher.InvokeAsync(() => _ = RefreshPortsAsync());

    private void OnSerialStateChanged(object? sender, SerialStateChangedEventArgs e) =>
        _dispatcher.InvokeAsync(() => ApplySerialState(e));

    private void ApplySerialState(SerialStateChangedEventArgs e)
    {
        ConnectionState = e.State;
        ConnectionDetail = e.Message;

        ActivityKind kind = e.IsProblem ? ActivityKind.Warning
            : e.State == SerialConnectionState.Connected ? ActivityKind.Success
            : ActivityKind.Info;
        AddSystemActivity(e.State.ToString().ToUpperInvariant(), e.Message, kind);

        if (e.State == SerialConnectionState.Connected && e.PortName is not null)
        {
            _settings.Current.LastPort = e.PortName;
            _settings.SaveDelayed();

            SerialPortInfo? match = Ports.FirstOrDefault(p => string.Equals(p.PortName, e.PortName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _ = RefreshPortsAsync();
            }
            else
            {
                SelectedPort = match;
            }
        }
    }

    // ------------------------------------------------------------------ activity log

    private void OnDeckActivity(object? sender, DeckActivity activity) =>
        _dispatcher.InvokeAsync(() =>
        {
            AddActivity(activity);
            if (activity.Source == ActivitySource.System)
            {
                return;
            }

            if (activity.Channel is >= 1 and <= 3)
            {
                Channels[activity.Channel - 1].Flash();
            }
            else if (activity.Channel == 0)
            {
                Mic.Flash();
            }
        });

    private void AddSystemActivity(string command, string message, ActivityKind kind) =>
        AddActivity(new DeckActivity(DateTime.Now, ActivitySource.System, command, message, kind, -1));

    private void AddActivity(DeckActivity activity)
    {
        Activity.Insert(0, new ActivityItem(activity));
        while (Activity.Count > MaxActivityItems)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }

    private void Simulate()
    {
        string text = SimulateText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (CommandParser.TryParse(text, out DeckCommand command))
        {
            Forget(_deck.ExecuteAsync(command, ActivitySource.Simulator), "Simulated command");
        }
        else
        {
            AddActivity(new DeckActivity(DateTime.Now, ActivitySource.Simulator, text.ToUpperInvariant(),
                "Unknown command (see the protocol in README)", ActivityKind.Warning, -1));
        }
    }

    // ------------------------------------------------------------------ misc

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Logger.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Opening the log folder failed", ex);
        }
    }

    private static async void Forget(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Logger.Warn($"{what}: audio device not responding (0x{ex.HResult:X8})");
        }
        catch (Exception ex)
        {
            Logger.Error($"{what} failed", ex);
        }
    }

    private static T Resource<T>(string key, T fallback) =>
        Application.Current?.TryFindResource(key) is T value ? value : fallback;
}
