using System.Windows.Media;

namespace KoalaDeck.ViewModels;

/// <summary>An app offered by the "+" add-app popup (running or installed).</summary>
public sealed class AudioAppItem(string name) : ObservableObject
{
    private ImageSource? _icon;
    private bool _isPlaying;
    private string? _title;

    /// <summary>Process name — what a group stores and matches.</summary>
    public string Name { get; } = name;

    /// <summary>Friendly Start menu name ("Visual Studio Code"), when known.</summary>
    public string? Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ProcessLabel));
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title;

    /// <summary>"Code.exe" under "Visual Studio Code"; null when the title already is the process name.</summary>
    public string? ProcessLabel => string.Equals(DisplayName, Name, StringComparison.OrdinalIgnoreCase) ? null : Name + ".exe";

    /// <summary>Icon extracted from the exe or shortcut; null shows a generic app glyph (it may arrive a moment later).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>Currently playing audio (green badge).</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>True when the search text matches the name or the title.</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    public override string ToString() => DisplayName;
}
