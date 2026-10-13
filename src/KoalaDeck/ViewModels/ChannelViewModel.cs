using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KoalaDeck.Models;
using KoalaDeck.Services;

namespace KoalaDeck.ViewModels;

/// <summary>
/// One channel card: output device, volume gauge and an app list. Channel 1 (master) controls the Windows default
/// output and lists apps that are playing but belong to no group; channels 2 and 3 are app groups.
/// </summary>
public sealed class ChannelViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly DispatcherTimer _flashTimer;
    private OutputDeviceItem? _selectedOutput;
    private bool _updatingOutputs;
    private bool _canChooseOutput = true;
    private double _volume;
    private double _level;
    private bool _isMuted;
    private bool _isAvailable;
    private bool _isFlashing;
    private bool _isDropTarget;

    public ChannelViewModel(
        MainViewModel owner,
        int index,
        string title,
        bool isMaster,
        Color accent,
        Geometry icon,
        ObservableCollection<AudioAppItem> runningApps,
        ObservableCollection<AudioAppItem> installedApps)
    {
        _owner = owner;
        Index = index;
        Title = title;
        IsMaster = isMaster;
        Icon = icon;
        RunningApps = runningApps;
        InstalledApps = installedApps;
        AccentColor = accent;
        Accent = Frozen(new SolidColorBrush(accent));
        AccentSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)));
        AccentPeak = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B)));

        ToggleMuteCommand = new RelayCommand(() => _owner.ToggleChannelMute(this));
        AddAppCommand = new RelayCommand<string>(name => _owner.MoveApp(name, Index));
        RemoveAppCommand = new RelayCommand<string>(name => _owner.MoveApp(name, AudioService.MasterChannel));
        AddAppOpeningCommand = new RelayCommand(_owner.RefreshInstalledAppsIfStale);
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            IsFlashing = false;
        };
    }

    public int Index { get; }

    public string Badge => $"CHANNEL {Index}";

    public string Title { get; }

    public bool IsMaster { get; }

    public Geometry Icon { get; }

    public Color AccentColor { get; }

    public Brush Accent { get; }

    public Brush AccentSoft { get; }

    public Brush AccentPeak { get; }

    /// <summary>Master: running apps without a group. App channels: the group members.</summary>
    public ObservableCollection<AppRowViewModel> Apps { get; } = new();

    /// <summary>Running apps (playing ones marked), offered by the "+" button.</summary>
    public ObservableCollection<AudioAppItem> RunningApps { get; }

    /// <summary>Installed (Start menu) apps that are not running, offered by the "+" button only.</summary>
    public ObservableCollection<AudioAppItem> InstalledApps { get; }

    /// <summary>Runs when the "+" popup opens (rescans installed apps when the list is old).</summary>
    public ICommand AddAppOpeningCommand { get; }

    public ObservableCollection<OutputDeviceItem> OutputOptions { get; } = new();

    public string AppsCaption => IsMaster ? "OTHER APPS" : "APPS";

    public string EmptyAppsHint => IsMaster
        ? "Running apps show up here. Drag one onto a channel."
        : "Drag an app here, or press + to add one.";

    public ICommand ToggleMuteCommand { get; }

    /// <summary>Parameter: process name.</summary>
    public ICommand AddAppCommand { get; }

    /// <summary>Parameter: process name. Moves the app back to the master card.</summary>
    public ICommand RemoveAppCommand { get; }

    public OutputDeviceItem? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value) || _updatingOutputs || value is null)
            {
                return; // ignore the transient null while the list is rebuilt
            }

            _owner.OnOutputSelected(this, value);
        }
    }

    /// <summary>False when Windows cannot route apps (app channels on Windows older than 10 1803).</summary>
    public bool CanChooseOutput
    {
        get => _canChooseOutput;
        set => SetProperty(ref _canChooseOutput, value);
    }

    /// <summary>Volume 0..100. Setting it from the UI (slider) changes the real volume.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (SetProperty(ref _volume, value))
            {
                OnPropertyChanged(nameof(VolumeText));
                _owner.SetChannelVolume(this, value);
            }
        }
    }

    public string VolumeText => IsAvailable ? _volume.ToString("0") : "—";

    /// <summary>Audible level 0..100 (peak meter × volume) for the inner ring.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(VolumeText));
            }
        }
    }

    /// <summary>Briefly true after a hardware command hit this channel (card border highlight).</summary>
    public bool IsFlashing
    {
        get => _isFlashing;
        private set => SetProperty(ref _isFlashing, value);
    }

    /// <summary>True while an app is dragged over this card.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }

    /// <summary>Set while the user drags the slider so live polling does not fight the thumb.</summary>
    public bool IsUserAdjusting { get; set; }

    public void Flash()
    {
        IsFlashing = true;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>Replaces the dropdown entries without the rebuild being mistaken for a user choice.</summary>
    internal void SetOutputOptions(IEnumerable<OutputDeviceItem> options, string? selectedId)
    {
        _updatingOutputs = true;
        try
        {
            OutputOptions.Clear();
            foreach (OutputDeviceItem option in options)
            {
                OutputOptions.Add(option);
            }

            SelectedOutput = OutputOptions.FirstOrDefault(o => o.Id == selectedId) ?? OutputOptions.FirstOrDefault();
        }
        finally
        {
            _updatingOutputs = false;
        }
    }

    internal void Apply(ChannelSnapshot snapshot)
    {
        IsAvailable = snapshot.IsAvailable;
        IsMuted = snapshot.IsMuted;

        if (!IsUserAdjusting)
        {
            double volume = Math.Round(snapshot.Volume * 100);
            if (volume != _volume)
            {
                _volume = volume; // bypass the setter: this value came from Windows
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(VolumeText));
            }
        }

        double target = snapshot.IsAvailable && !snapshot.IsMuted
            ? Math.Clamp(snapshot.Peak * snapshot.Volume * 100, 0, 100)
            : 0;
        double next = target >= _level ? target : Math.Max(target, _level * 0.8); // fast attack, smooth release
        Level = next < 0.5 ? 0 : Math.Round(next, 1);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
