using System.IO;
using System.Text.Json;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in %AppData%\KoalaDeck.</summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _sync = new();
    private readonly Timer _saveTimer;

    public SettingsService()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KoalaDeck");
        _path = Path.Combine(directory, "settings.json");
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    public AppSettings Current { get; }

    /// <summary>Saves after a short delay so rapid changes (typing, toggling) end up as a single write.</summary>
    public void SaveDelayed()
    {
        try
        {
            _saveTimer.Change(500, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Logger.Error("Could not save settings", ex);
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
    }

    private AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Error("Settings file is unreadable, using defaults", ex);
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }
}
