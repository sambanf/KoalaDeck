using System.IO;
using Microsoft.Win32;

namespace KoalaDeck.Services;

/// <summary>"Start with Windows" through HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).</summary>
public static class StartupManager
{
    /// <summary>Command-line switch used by the Run entry: start straight into the tray.</summary>
    public const string MinimizedArgument = "--minimized";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KoalaDeck";

    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArgument}";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logger.Warn($"Cannot read the startup entry: {ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Logger.Info($"Start with Windows {(enabled ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logger.Error("Cannot update the startup entry", ex);
            return false;
        }
    }

    /// <summary>Keeps the Run entry pointing at this exe if the app was moved or updated.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                Logger.Info("Updated the startup entry to the current executable path");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logger.Warn($"Cannot refresh the startup entry: {ex.Message}");
        }
    }
}
