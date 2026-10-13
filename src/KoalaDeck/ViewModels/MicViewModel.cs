using System.Windows.Input;
using System.Windows.Threading;
using KoalaDeck.Models;

namespace KoalaDeck.ViewModels;

/// <summary>Microphone status card.</summary>
public sealed class MicViewModel : ObservableObject
{
    private readonly DispatcherTimer _flashTimer;
    private bool _isMuted;
    private bool _isAvailable = true;
    private bool _isFlashing;
    private double _level;
    private string _deviceName = "Detecting microphone…";

    public MicViewModel(Action toggle)
    {
        ToggleCommand = new RelayCommand(toggle);
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            IsFlashing = false;
        };
    }

    public ICommand ToggleCommand { get; }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    /// <summary>Input level 0..100.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public bool IsFlashing
    {
        get => _isFlashing;
        private set => SetProperty(ref _isFlashing, value);
    }

    public void Flash()
    {
        IsFlashing = true;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    internal void Apply(MicSnapshot snapshot)
    {
        IsAvailable = snapshot.IsAvailable;
        IsMuted = snapshot.IsMuted;
        DeviceName = snapshot.DeviceName;

        double target = snapshot.IsAvailable && !snapshot.IsMuted ? Math.Clamp(snapshot.Peak * 100, 0, 100) : 0;
        double next = target >= _level ? target : Math.Max(target, _level * 0.8);
        Level = next < 0.5 ? 0 : Math.Round(next, 1);
    }
}
