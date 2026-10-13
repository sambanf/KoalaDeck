using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using KoalaDeck.Interop;
using KoalaDeck.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace KoalaDeck.Services;

/// <summary>
/// Windows Core Audio access: master volume and default output, per-app groups (Volume Mixer sessions + per-app
/// output routing) and microphone mute.
/// <para>
/// Every COM call runs on one dedicated MTA worker thread. Core Audio objects must not be shared between the STA UI
/// thread and thread-pool threads, and session notifications require an MTA. Public members are thread-safe and
/// return Tasks that complete once the work has executed on that thread (continuations never run on it).
/// </para>
/// </summary>
public sealed class AudioService : IDisposable
{
    public const int MasterChannel = 1;
    public const int ChannelCount = 3;

    private static readonly TimeSpan TopologyMaxAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MissingSessionRetryAge = TimeSpan.FromMilliseconds(300);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    // ---- Worker-thread-only state ----
    private readonly string[][] _channelApps = new string[ChannelCount + 1][];
    private readonly string?[] _channelOutputs = new string?[ChannelCount + 1];
    private readonly Dictionary<string, MMDevice> _renderDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, ProcessIdentity> _processes = new();
    private readonly Dictionary<uint, string> _routedPids = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _output;
    private MMDevice? _input;
    private MMDevice? _inputComms;
    private AudioPolicyConfig? _policy;
    private bool _policyChecked;
    private List<SessionEntry> _sessions = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _topologyDirty = true;
    private string? _lastRefreshError;

    public AudioService()
    {
        for (int i = 0; i < _channelApps.Length; i++)
        {
            _channelApps[i] = Array.Empty<string>();
        }

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "KoalaDeck audio" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>False when this Windows version cannot route individual apps to a device (known after the first call).</summary>
    public bool IsAppRoutingSupported { get; private set; } = true;

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Sets the apps of an app channel (2 or 3) and its output device (null = follow the Windows default).
    /// Apps that leave a routed group go back to the default device; apps that join are routed.
    /// Returns a warning when Windows refused to route some apps, otherwise null.
    /// </summary>
    public Task<string?> ConfigureChannelAsync(int channel, IReadOnlyCollection<string> apps, string? outputDeviceId) =>
        InvokeAsync(() => ConfigureChannel(channel, AppNames.Clean(apps).ToArray(), outputDeviceId));

    /// <summary>Makes a device the Windows default output (master channel).</summary>
    public Task<AudioActionResult> SetDefaultOutputAsync(string deviceId) =>
        InvokeAsync(() => SetDefaultOutput(deviceId));

    public Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync() =>
        InvokeAsync<IReadOnlyList<OutputDeviceInfo>>(GetOutputDevices);

    public Task<AudioActionResult> ChangeVolumeAsync(int channel, int deltaPercent) =>
        InvokeAsync(() => ChangeVolume(channel, deltaPercent / 100f));

    public Task<AudioActionResult> SetVolumeAsync(int channel, float volume) =>
        InvokeAsync(() => SetVolume(channel, volume));

    public Task<AudioActionResult> ToggleMuteAsync(int channel) =>
        InvokeAsync(() => ToggleMute(channel));

    /// <summary>Mutes (true), unmutes (false) or toggles (null) the default recording device(s).</summary>
    public Task<AudioActionResult> SetMicrophoneMuteAsync(bool? mute) =>
        InvokeAsync(() => SetMicrophoneMute(mute));

    public Task<AudioSnapshot> GetSnapshotAsync() => InvokeAsync(CreateSnapshot);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        if (_worker.Join(TimeSpan.FromSeconds(2)))
        {
            _queue.Dispose();
        }
    }

    // ------------------------------------------------------------------ worker plumbing

    private Task<T> InvokeAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            completion.SetException(new ObjectDisposedException(nameof(AudioService)));
            return completion.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(RunWithRecovery(func));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(AudioService)));
        }

        return completion.Task;
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
            {
                if (_topologyDirty || DateTime.UtcNow - _lastRefreshUtc > TopologyMaxAge)
                {
                    TryRefreshTopology();
                }

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Logger.Error("Audio worker error", ex);
                }
            }
        }
        finally
        {
            ReleaseAll();
        }
    }

    private T RunWithRecovery<T>(Func<T> func)
    {
        try
        {
            return func();
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            // Typical causes: device unplugged, default device switched, app closed its session.
            Logger.Warn($"Audio call failed ({ex.Message}); refreshing devices and retrying once");
            _topologyDirty = true;
            TryRefreshTopology();
            return func();
        }
    }

    private static bool IsAudioFailure(Exception ex) =>
        ex is COMException or InvalidComObjectException or InvalidCastException or UnauthorizedAccessException;

    // ------------------------------------------------------------------ device & session discovery

    private void TryRefreshTopology()
    {
        try
        {
            RefreshTopology();
            _lastRefreshError = null;
        }
        catch (Exception ex)
        {
            // Retry at the next interval instead of on every queued call.
            _topologyDirty = false;
            _lastRefreshUtc = DateTime.UtcNow;
            if (ex.Message != _lastRefreshError)
            {
                _lastRefreshError = ex.Message;
                Logger.Error("Audio device refresh failed", ex);
            }
        }
    }

    private void RefreshTopology()
    {
        _enumerator ??= new MMDeviceEnumerator();
        if (!_policyChecked)
        {
            _policyChecked = true; // COM is initialized on this thread now
            _policy = AudioPolicyConfig.TryCreate();
            IsAppRoutingSupported = _policy is not null;
        }

        _output = ReplaceDefault(_output, DataFlow.Render, Role.Multimedia);
        _input = ReplaceDefault(_input, DataFlow.Capture, Role.Console);
        _inputComms = ReplaceDefault(_inputComms, DataFlow.Capture, Role.Communications);

        RefreshRenderDevices();
        RefreshSessions();
        RouteNewSessions();

        _lastRefreshUtc = DateTime.UtcNow;
        _topologyDirty = false;
    }

    private MMDevice? ReplaceDefault(MMDevice? current, DataFlow flow, Role role)
    {
        MMDevice? fresh = null;
        try
        {
            if (_enumerator!.HasDefaultAudioEndpoint(flow, role))
            {
                fresh = _enumerator.GetDefaultAudioEndpoint(flow, role);
            }
        }
        catch (COMException ex)
        {
            Logger.Warn($"No default {flow} device ({role}): {ex.Message}");
        }

        if (fresh is not null && current is not null && SameDevice(current, fresh))
        {
            fresh.Dispose();
            return current; // keep the existing wrapper (and its activated interfaces)
        }

        SafeDispose(current);
        return fresh;
    }

    /// <summary>Apps can play on any output (e.g. Discord on a headset), so sessions are collected from all active outputs.</summary>
    private void RefreshRenderDevices()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MMDevice device in _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            string id;
            try
            {
                id = device.ID;
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                SafeDispose(device);
                continue;
            }

            seen.Add(id);
            if (_renderDevices.ContainsKey(id))
            {
                device.Dispose();
            }
            else
            {
                _renderDevices[id] = device;
            }
        }

        foreach (string id in _renderDevices.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            SafeDispose(_renderDevices[id]);
            _renderDevices.Remove(id);
        }
    }

    private void RefreshSessions()
    {
        var fresh = new List<SessionEntry>();
        foreach (MMDevice device in _renderDevices.Values)
        {
            try
            {
                AudioSessionManager manager = device.AudioSessionManager;
                manager.RefreshSessions();
                SessionCollection sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl control = sessions[i];
                    if (TryCreateEntry(control, out SessionEntry? entry))
                    {
                        fresh.Add(entry);
                    }
                    else
                    {
                        SafeDispose(control);
                    }
                }
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                Logger.Warn($"Skipping the sessions of one output device: {ex.Message}");
            }
        }

        foreach (SessionEntry old in _sessions)
        {
            SafeDispose(old.Control);
        }

        _sessions = fresh;

        var livePids = fresh.Select(s => s.ProcessId).ToHashSet();
        foreach (uint pid in _processes.Keys.Where(pid => !livePids.Contains(pid)).ToList())
        {
            _processes.Remove(pid);
        }

        foreach (uint pid in _routedPids.Keys.Where(pid => !livePids.Contains(pid)).ToList())
        {
            _routedPids.Remove(pid); // re-applied (idempotent) if the app opens a session again
        }
    }

    private bool TryCreateEntry(AudioSessionControl control, [NotNullWhen(true)] out SessionEntry? entry)
    {
        entry = null;
        try
        {
            if (control.IsSystemSoundsSession || control.State == AudioSessionState.AudioSessionStateExpired)
            {
                return false;
            }

            uint pid = control.GetProcessID;
            ProcessIdentity? process = GetProcess(pid);
            if (process is null)
            {
                return false;
            }

            entry = new SessionEntry(control, pid, process.Name, process.ExecutablePath);
            return true;
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            return false;
        }
    }

    private ProcessIdentity? GetProcess(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        if (_processes.TryGetValue(pid, out ProcessIdentity? cached))
        {
            return cached;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            // QueryFullProcessImageName also works for elevated processes, unlike Process.MainModule.
            var identity = new ProcessIdentity(process.ProcessName, NativeMethods.GetProcessImagePath(pid));
            _processes[pid] = identity;
            return identity;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null; // the process already exited
        }
    }

    private List<SessionEntry> SessionsFor(int channel, bool refreshIfMissing)
    {
        string[] apps = _channelApps[channel];
        if (apps.Length == 0)
        {
            return new List<SessionEntry>();
        }

        List<SessionEntry> matches = Match();
        if (matches.Count == 0 && refreshIfMissing && DateTime.UtcNow - _lastRefreshUtc > MissingSessionRetryAge)
        {
            // The app may have just started playing: look again before giving up.
            TryRefreshTopology();
            matches = Match();
        }

        return matches;

        List<SessionEntry> Match() =>
            _sessions.Where(s => apps.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private string ChannelLabel(int channel)
    {
        if (channel == MasterChannel)
        {
            return "Master";
        }

        string[] apps = _channelApps[channel];
        return apps.Length switch
        {
            0 => $"Channel {channel}",
            1 => apps[0],
            _ => $"{apps[0]} +{apps.Length - 1}",
        };
    }

    // ------------------------------------------------------------------ groups & output routing (worker thread)

    private string? ConfigureChannel(int channel, string[] apps, string? outputDeviceId)
    {
        if (channel is <= MasterChannel or > ChannelCount)
        {
            return null;
        }

        string[] previousApps = _channelApps[channel];
        string? previousOutput = _channelOutputs[channel];
        _channelApps[channel] = apps;
        _channelOutputs[channel] = outputDeviceId;

        if (previousOutput is null && outputDeviceId is null)
        {
            return null; // nothing routed before or now
        }

        if (_policy is null)
        {
            return "This Windows version cannot send individual apps to another output";
        }

        // Apps that left a routed group, or every app when the group goes back to "default", return to the default device.
        IEnumerable<string> release = outputDeviceId is null
            ? previousApps
            : previousApps.Except(apps, StringComparer.OrdinalIgnoreCase);
        foreach (string app in release)
        {
            RouteApp(app, null);
        }

        var refused = new List<string>();
        if (outputDeviceId is not null)
        {
            bool outputChanged = !string.Equals(previousOutput, outputDeviceId, StringComparison.Ordinal);
            IEnumerable<string> route = outputChanged ? apps : apps.Except(previousApps, StringComparer.OrdinalIgnoreCase);
            foreach (string app in route)
            {
                if (!RouteApp(app, outputDeviceId))
                {
                    refused.Add(app);
                }
            }
        }

        _topologyDirty = true; // streams move to another device: rescan sessions on the next call
        if (refused.Count == 0)
        {
            return null;
        }

        string device = DeviceName(outputDeviceId!);
        string hint = device.Contains("Remote Audio", StringComparison.OrdinalIgnoreCase)
            ? " (Remote Desktop audio cannot be a per-app output)"
            : string.Empty;
        return $"Windows refused to send {string.Join(", ", refused)} to {device}{hint}";
    }

    /// <summary>
    /// Routes the app's audio processes to a device (null restores the default). Only processes with an audio
    /// session are routed — Windows rejects the others; later sessions are picked up by <see cref="RouteNewSessions"/>.
    /// Returns false when Windows refused every process.
    /// </summary>
    private bool RouteApp(string app, string? deviceId)
    {
        if (_policy is null)
        {
            return false;
        }

        var pids = _sessions
            .Where(s => string.Equals(s.ProcessName, app, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ProcessId)
            .ToHashSet();
        if (pids.Count == 0)
        {
            return true; // not playing: routed as soon as it opens a session
        }

        int routed = 0;
        int lastError = 0;
        foreach (uint pid in pids)
        {
            int hr = _policy.SetAppOutput(pid, deviceId);
            if (hr < 0)
            {
                lastError = hr;
                if (deviceId is not null)
                {
                    _routedPids[pid] = deviceId; // attempted: RouteNewSessions must not retry it every refresh
                }

                continue;
            }

            routed++;
            if (deviceId is null)
            {
                _routedPids.Remove(pid);
            }
            else
            {
                _routedPids[pid] = deviceId;
            }
        }

        string target = deviceId is null ? "the default device" : DeviceName(deviceId);
        string error = lastError < 0 ? $", last error 0x{lastError:X8}" : string.Empty;
        Logger.Info($"Routed {app} ({routed}/{pids.Count} processes) to {target}{error}");
        return routed > 0;
    }

    /// <summary>Apps of a routed group that opened a new audio session get routed too.</summary>
    private void RouteNewSessions()
    {
        if (_policy is null)
        {
            return;
        }

        for (int channel = 2; channel <= ChannelCount; channel++)
        {
            string? deviceId = _channelOutputs[channel];
            if (deviceId is null)
            {
                continue;
            }

            foreach (SessionEntry session in SessionsFor(channel, refreshIfMissing: false))
            {
                if (_routedPids.TryGetValue(session.ProcessId, out string? applied) && applied == deviceId)
                {
                    continue;
                }

                int hr = _policy.SetAppOutput(session.ProcessId, deviceId);
                _routedPids[session.ProcessId] = deviceId; // attempted once, also when refused
                if (hr >= 0)
                {
                    Logger.Info($"Routed new {session.ProcessName} session (pid {session.ProcessId}) to {DeviceName(deviceId)}");
                }
                else
                {
                    Logger.Warn($"Routing {session.ProcessName} (pid {session.ProcessId}) failed: 0x{hr:X8}");
                }
            }
        }
    }

    private AudioActionResult SetDefaultOutput(string deviceId)
    {
        PolicyConfig.SetDefaultOutput(deviceId);
        _topologyDirty = true;
        TryRefreshTopology();
        string name = DeviceName(deviceId);
        Logger.Info($"Windows default output set to {name}");
        return new AudioActionResult(true, "Master", 0, false, $"Output → {name}");
    }

    private IReadOnlyList<OutputDeviceInfo> GetOutputDevices()
    {
        string? defaultId = _output is null ? null : SafeId(_output);
        var devices = new List<OutputDeviceInfo>();
        foreach ((string id, MMDevice device) in _renderDevices)
        {
            try
            {
                devices.Add(new OutputDeviceInfo(id, device.FriendlyName, id == defaultId, IsHeadphones(device)));
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                _topologyDirty = true;
            }
        }

        return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsHeadphones(MMDevice device)
    {
        try
        {
            PropertyStore properties = device.Properties;
            if (properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
            {
                // EndpointFormFactor: Headphones = 3, Headset = 5, Handset = 6
                int formFactor = Convert.ToInt32(properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value);
                if (formFactor is 3 or 5 or 6)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsAudioFailure(ex) || ex is FormatException or InvalidCastException or OverflowException)
        {
        }

        string name = device.FriendlyName;
        return name.Contains("head", StringComparison.OrdinalIgnoreCase)
            || name.Contains("earphone", StringComparison.OrdinalIgnoreCase)
            || name.Contains("buds", StringComparison.OrdinalIgnoreCase);
    }

    private string DeviceName(string deviceId)
    {
        try
        {
            return _renderDevices.TryGetValue(deviceId, out MMDevice? device) ? device.FriendlyName : "an unplugged device";
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            return "an unavailable device";
        }
    }

    // ------------------------------------------------------------------ actions (worker thread)

    private AudioActionResult ChangeVolume(int channel, float delta)
    {
        if (channel == MasterChannel)
        {
            if (_output is null)
            {
                return AudioActionResult.Fail("Master", "No playback device");
            }

            AudioEndpointVolume endpoint = _output.AudioEndpointVolume;
            float next = Clamp(endpoint.MasterVolumeLevelScalar + delta);
            endpoint.MasterVolumeLevelScalar = next;
            if (delta > 0 && endpoint.Mute)
            {
                endpoint.Mute = false; // same as the Windows volume keys
            }

            return Success("Master", next, endpoint.Mute);
        }

        if (!TryGetSessions(channel, out string label, out List<SessionEntry> sessions, out AudioActionResult? failure))
        {
            return failure;
        }

        float target = Clamp(ReferenceVolume(sessions) + delta);
        if (!ForEachSession(sessions, volume =>
            {
                volume.Volume = target;
                if (delta > 0)
                {
                    volume.Mute = false;
                }
            }))
        {
            return AudioActionResult.Fail(label, $"{label}: audio session closed");
        }

        return Success(label, target, delta <= 0 && sessions.All(IsSessionMuted));
    }

    private AudioActionResult SetVolume(int channel, float value)
    {
        float target = Clamp(value);
        if (channel == MasterChannel)
        {
            if (_output is null)
            {
                return AudioActionResult.Fail("Master", "No playback device");
            }

            _output.AudioEndpointVolume.MasterVolumeLevelScalar = target;
            return Success("Master", target, _output.AudioEndpointVolume.Mute);
        }

        if (!TryGetSessions(channel, out string label, out List<SessionEntry> sessions, out AudioActionResult? failure))
        {
            return failure;
        }

        ForEachSession(sessions, volume => volume.Volume = target);
        return Success(label, target, sessions.All(IsSessionMuted));
    }

    private AudioActionResult ToggleMute(int channel)
    {
        if (channel == MasterChannel)
        {
            if (_output is null)
            {
                return AudioActionResult.Fail("Master", "No playback device");
            }

            AudioEndpointVolume endpoint = _output.AudioEndpointVolume;
            endpoint.Mute = !endpoint.Mute;
            return Success("Master", endpoint.MasterVolumeLevelScalar, endpoint.Mute);
        }

        if (!TryGetSessions(channel, out string label, out List<SessionEntry> sessions, out AudioActionResult? failure))
        {
            return failure;
        }

        // If anything is still audible, mute everything; otherwise unmute everything.
        bool mute = sessions.Any(s => !IsSessionMuted(s));
        ForEachSession(sessions, volume => volume.Mute = mute);
        return Success(label, ReferenceVolume(sessions), mute);
    }

    private AudioActionResult SetMicrophoneMute(bool? mute)
    {
        // Mute the default recording device and, when different, the default communications device
        // (Discord / Teams usually record from the latter).
        var devices = new List<MMDevice>();
        if (_input is not null)
        {
            devices.Add(_input);
        }

        if (_inputComms is not null && (_input is null || !SameDevice(_input, _inputComms)))
        {
            devices.Add(_inputComms);
        }

        if (devices.Count == 0)
        {
            return AudioActionResult.Fail("Microphone", "No recording device");
        }

        bool next = mute ?? !devices[0].AudioEndpointVolume.Mute;
        foreach (MMDevice device in devices)
        {
            device.AudioEndpointVolume.Mute = next;
        }

        return new AudioActionResult(true, "Microphone", devices[0].AudioEndpointVolume.MasterVolumeLevelScalar, next,
            next ? "Microphone muted" : "Microphone live");
    }

    private bool TryGetSessions(int channel, out string label, out List<SessionEntry> sessions, [NotNullWhen(false)] out AudioActionResult? failure)
    {
        label = ChannelLabel(channel);
        sessions = channel is > MasterChannel and <= ChannelCount ? SessionsFor(channel, refreshIfMissing: true) : new List<SessionEntry>();
        if (sessions.Count > 0)
        {
            failure = null;
            return true;
        }

        // A group whose apps are silent (or closed) is simply waiting for them: not an error.
        failure = _channelApps[channel].Length == 0
            ? AudioActionResult.Fail(label, $"Channel {channel} has no apps — drag one onto it")
            : AudioActionResult.Pending(label, $"{label} ready — not playing yet");
        return false;
    }

    /// <summary>Applies an action to each session; returns false if none of them accepted it.</summary>
    private bool ForEachSession(List<SessionEntry> sessions, Action<SimpleAudioVolume> action)
    {
        bool any = false;
        foreach (SessionEntry session in sessions)
        {
            try
            {
                action(session.Control.SimpleAudioVolume);
                any = true;
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                _topologyDirty = true;
            }
        }

        return any;
    }

    // ------------------------------------------------------------------ snapshot (worker thread)

    private AudioSnapshot CreateSnapshot()
    {
        var channels = new ChannelSnapshot[ChannelCount];
        channels[0] = SnapshotMaster();
        for (int channel = 2; channel <= ChannelCount; channel++)
        {
            channels[channel - 1] = SnapshotChannel(channel);
        }

        return new AudioSnapshot(channels, SnapshotMicrophone(), SnapshotApps());
    }

    private ChannelSnapshot SnapshotMaster()
    {
        if (_output is null)
        {
            return ChannelSnapshot.Unavailable(MasterChannel, "No playback device");
        }

        try
        {
            AudioEndpointVolume endpoint = _output.AudioEndpointVolume;
            return new ChannelSnapshot(MasterChannel, true, endpoint.MasterVolumeLevelScalar, endpoint.Mute,
                _output.AudioMeterInformation.MasterPeakValue, 1, _output.FriendlyName);
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            _topologyDirty = true;
            return ChannelSnapshot.Unavailable(MasterChannel, "Playback device unavailable");
        }
    }

    private ChannelSnapshot SnapshotChannel(int channel)
    {
        List<SessionEntry> sessions = SessionsFor(channel, refreshIfMissing: false);
        if (sessions.Count == 0)
        {
            return ChannelSnapshot.Unavailable(channel, _channelApps[channel].Length == 0 ? "No apps in this group" : "Group apps are silent");
        }

        try
        {
            float volume = ReferenceVolume(sessions);
            bool muted = true;
            float peak = 0;
            foreach (SessionEntry session in sessions)
            {
                muted &= session.Control.SimpleAudioVolume.Mute;
                peak = Math.Max(peak, session.Control.AudioMeterInformation?.MasterPeakValue ?? 0f);
            }

            return new ChannelSnapshot(channel, true, volume, muted, peak, sessions.Count, string.Empty);
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            _topologyDirty = true;
            return ChannelSnapshot.Unavailable(channel, "Audio session closed");
        }
    }

    private List<AppSnapshot> SnapshotApps()
    {
        var apps = new List<AppSnapshot>();
        foreach (IGrouping<string, SessionEntry> group in _sessions.GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var sessions = group.ToList();
                bool muted = true;
                float peak = 0;
                foreach (SessionEntry session in sessions)
                {
                    muted &= session.Control.SimpleAudioVolume.Mute;
                    peak = Math.Max(peak, session.Control.AudioMeterInformation?.MasterPeakValue ?? 0f);
                }

                apps.Add(new AppSnapshot(
                    group.Key,
                    sessions.Select(s => s.ExecutablePath).FirstOrDefault(path => path is not null),
                    sessions.Any(IsSessionActive),
                    peak,
                    ReferenceVolume(sessions),
                    muted));
            }
            catch (Exception ex) when (IsAudioFailure(ex))
            {
                _topologyDirty = true;
            }
        }

        return apps;
    }

    private MicSnapshot SnapshotMicrophone()
    {
        if (_input is null)
        {
            return new MicSnapshot(false, false, 0, "No recording device");
        }

        try
        {
            return new MicSnapshot(true, _input.AudioEndpointVolume.Mute, _input.AudioMeterInformation.MasterPeakValue, _input.FriendlyName);
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            _topologyDirty = true;
            return new MicSnapshot(false, false, 0, "Recording device unavailable");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Prefer a session that is actually playing; apps like Discord keep several around.</summary>
    private static float ReferenceVolume(List<SessionEntry> sessions)
    {
        SessionEntry reference = sessions.FirstOrDefault(IsSessionActive) ?? sessions[0];
        return reference.Control.SimpleAudioVolume.Volume;
    }

    private static bool IsSessionActive(SessionEntry session)
    {
        try
        {
            return session.Control.State == AudioSessionState.AudioSessionStateActive;
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            return false;
        }
    }

    private static bool IsSessionMuted(SessionEntry session)
    {
        try
        {
            return session.Control.SimpleAudioVolume.Mute;
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            return true;
        }
    }

    private static bool SameDevice(MMDevice a, MMDevice b) => SafeId(a) is { } id && id == SafeId(b);

    private static string? SafeId(MMDevice device)
    {
        try
        {
            return device.ID;
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            return null;
        }
    }

    private static AudioActionResult Success(string label, float volume, bool muted)
    {
        int percent = (int)MathF.Round(volume * 100);
        return new AudioActionResult(true, label, volume, muted, muted ? $"{label} muted ({percent}%)" : $"{label} {percent}%");
    }

    private static float Clamp(float value) => MathF.Round(Math.Clamp(value, 0f, 1f) * 100f) / 100f;

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            // Already released by the OS (device removed).
        }
    }

    private void ReleaseAll()
    {
        foreach (SessionEntry session in _sessions)
        {
            SafeDispose(session.Control);
        }

        _sessions.Clear();
        foreach (MMDevice device in _renderDevices.Values)
        {
            SafeDispose(device);
        }

        _renderDevices.Clear();
        SafeDispose(_output);
        SafeDispose(_input);
        SafeDispose(_inputComms);
        SafeDispose(_enumerator);
        _policy?.Dispose();
        _output = _input = _inputComms = null;
        _enumerator = null;
        _policy = null;
    }

    private sealed record SessionEntry(AudioSessionControl Control, uint ProcessId, string ProcessName, string? ExecutablePath);

    private sealed record ProcessIdentity(string Name, string? ExecutablePath);
}
