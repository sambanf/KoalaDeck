using System.Windows.Media;
using KoalaDeck.Models;

namespace KoalaDeck.ViewModels;

/// <summary>One app in a channel's app list (a group member, or an unassigned app on the master card).</summary>
public sealed class AppRowViewModel(string name, bool isMember) : ObservableObject
{
    private ImageSource? _icon;
    private string? _title;
    private bool _isRunning;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _isDragging;
    private double _level;
    private string _status = "Not running";

    public string Name { get; } = name;

    /// <summary>True for rows of an app channel (can be removed); false for the master's unassigned list.</summary>
    public bool IsMember { get; } = isMember;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>Friendly name ("Microsoft Edge"), when known. <see cref="Name"/> stays the process name used for matching.</summary>
    public string? Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title;

    public string ToolTipText => $"{Name}.exe · drag onto another channel";

    /// <summary>The app is running (has a window or an audio session).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    /// <summary>Audible level 0..100 (peak × app volume).</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>True while this row is being dragged (dims the source row).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    /// <param name="snapshot">Audio state, or null when the app has no audio session.</param>
    /// <param name="isRunning">The app has a window open (relevant when it has no audio session).</param>
    internal void Apply(AppSnapshot? snapshot, bool isRunning)
    {
        IsRunning = snapshot is not null || isRunning;
        IsPlaying = snapshot?.IsPlaying == true;
        IsMuted = snapshot?.IsMuted == true;

        int percent = snapshot is null ? 0 : (int)MathF.Round(snapshot.Volume * 100);
        Status = snapshot switch
        {
            // Group members without audio are "prepared": their volume and output apply once they start playing.
            null when IsMember => isRunning ? "Ready · not playing" : "Ready · not running",
            null => "Not playing",
            { IsMuted: true } => $"Muted · {percent}%",
            { IsPlaying: true } => $"Playing · {percent}%",
            _ => $"Idle · {percent}%",
        };

        double target = snapshot is { IsMuted: false } ? Math.Clamp(snapshot.Peak * snapshot.Volume * 100, 0, 100) : 0;
        double next = target >= _level ? target : Math.Max(target, _level * 0.75);
        Level = next < 0.5 ? 0 : Math.Round(next, 1);
    }
}
