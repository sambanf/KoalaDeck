using System.ComponentModel;
using System.Diagnostics;
using KoalaDeck.Interop;

namespace KoalaDeck.Services;

/// <summary>A running app the user can add to a group, even before it plays audio.</summary>
public sealed record RunningApp(string Name, string? ExecutablePath);

/// <summary>
/// Lists running desktop apps: processes that own a visible app window. Shell and background helpers are skipped;
/// apps that only have an audio session (no window) come from the audio snapshot instead. Takes a few milliseconds,
/// so call it off the UI thread and at a slow rate.
/// </summary>
public static class RunningAppScanner
{
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "LockApp", "ShellHost", "Widgets", "dwm",
    };

    public static IReadOnlyList<RunningApp> Scan()
    {
        uint self = (uint)Environment.ProcessId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (uint pid in NativeMethods.GetAppWindowProcessIds())
        {
            if (pid == self)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                if (!ShellProcesses.Contains(name) && !apps.ContainsKey(name))
                {
                    apps[name] = new RunningApp(name, NativeMethods.GetProcessImagePath(pid));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // exited while scanning
            }
        }

        return apps.Values.ToList();
    }
}
