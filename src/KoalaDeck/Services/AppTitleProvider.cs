using System.Diagnostics;
using System.IO;

namespace KoalaDeck.Services;

/// <summary>
/// Friendly app names for process names: the exe's own description ("msedge.exe" → "Microsoft Edge"), cached per path.
/// Start menu titles take precedence and are supplied by the caller.
/// </summary>
public static class AppTitleProvider
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    /// <summary>FileDescription (or ProductName) of the exe, or null when it has none or cannot be read.</summary>
    public static string? FromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Sync)
        {
            if (!Cache.TryGetValue(executablePath, out string? title))
            {
                title = Read(executablePath);
                Cache[executablePath] = title;
            }

            return title;
        }
    }

    private static string? Read(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string? title = Clean(info.FileDescription) ?? Clean(info.ProductName);
            return title;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null; // e.g. protected WindowsApps folder
        }
    }

    /// <summary>Rejects empty or placeholder descriptions.</summary>
    private static string? Clean(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > 60 || text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }
}
