using System.IO;
using System.Runtime.InteropServices;
using KoalaDeck.Interop;
using KoalaDeck.Models;

namespace KoalaDeck.Services;

/// <summary>An installed app found in the Start menu.</summary>
/// <param name="Name">Process name it runs as (what a group matches).</param>
/// <param name="Title">Shortcut name shown to the user, e.g. "Visual Studio Code".</param>
/// <param name="IconPath">File holding the app's icon (its exe, or the shortcut's icon file for launcher-started apps).</param>
public sealed record InstalledApp(string Name, string Title, string IconPath);

/// <summary>
/// Lists installed desktop apps from the Start menu shortcuts of the current user and of all users. Uninstallers,
/// setup tools and Windows' own utilities are skipped. Store (UWP) apps have no .lnk shortcut and are not listed.
/// Takes a few hundred milliseconds: call it off the UI thread and cache the result.
/// </summary>
public static class InstalledAppScanner
{
    private static readonly string[] SkippedFolders =
        ["Administrative Tools", "Windows Administrative Tools", "Windows Tools", "System Tools", "Accessibility", "Startup"];

    private static readonly string[] SkippedNameParts =
        ["unins", "uninstall", "setup", "installer", "updater", "crash", "repair"];

    /// <summary>Windows' own tools that can still carry audio.</summary>
    private static readonly string[] AllowedWindowsTools = ["mstsc"];

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public static IReadOnlyList<InstalledApp> Scan()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var link = new ShellLink();
            foreach (string shortcut in Shortcuts())
            {
                if (link.TryRead(shortcut, out string target, out string arguments, out string icon)
                    && ToApp(shortcut, target, arguments, icon) is { } app)
                {
                    apps.TryAdd(app.Name, app);
                }
            }
        }
        catch (COMException ex)
        {
            Logger.Warn($"Reading Start menu shortcuts failed: {ex.Message}");
        }

        return apps.Values.OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Maps a shortcut to the process it starts; null for documents, links, installers, Windows tools and the like.</summary>
    /// <param name="iconLocation">The shortcut's custom icon file, if any.</param>
    internal static InstalledApp? ToApp(string shortcutPath, string targetPath, string arguments, string iconLocation = "")
    {
        string title = Path.GetFileNameWithoutExtension(shortcutPath);
        if (!targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || SkippedNameParts.Any(part => title.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string process = Path.GetFileNameWithoutExtension(targetPath);
        if (targetPath.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase)
            && !AllowedWindowsTools.Contains(process, StringComparer.OrdinalIgnoreCase))
        {
            return null; // Control Panel, MMC, Character Map, MSI advertised shortcuts (\Windows\Installer) ...
        }

        // Normally the exe holds the icon; launcher-started apps keep theirs in the shortcut.
        string iconPath = targetPath;
        if (process.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            // Squirrel apps (Discord, Slack, ...) start through "Update.exe --processStart Discord.exe".
            process = SquirrelProcess(arguments) ?? string.Empty;
            iconPath = iconLocation.Length > 0 ? iconLocation : shortcutPath;
        }
        else if (SkippedNameParts.Any(part => process.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        process = AppNames.Normalize(process);
        return process.Length == 0 || process.Equals("KoalaDeck", StringComparison.OrdinalIgnoreCase)
            ? null
            : new InstalledApp(process, title, iconPath);
    }

    private static string? SquirrelProcess(string arguments)
    {
        string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int flag = Array.FindIndex(tokens, token => token.StartsWith("--processStart", StringComparison.OrdinalIgnoreCase));
        return flag >= 0 && flag + 1 < tokens.Length ? tokens[flag + 1].Trim('"') : null;
    }

    private static IEnumerable<string> Shortcuts()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ];

        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                string relative = Path.GetRelativePath(root, file);
                if (!SkippedFolders.Any(folder => relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return file;
                }
            }
        }
    }
}
