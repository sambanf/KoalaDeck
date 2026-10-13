using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KoalaDeck.Services;

/// <summary>Extracts the icon embedded in an app's exe as a frozen WPF image, cached per path. Loading runs off the UI thread.</summary>
public static class AppIconProvider
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the icon if it is already loaded. Otherwise starts loading it in the background and returns null;
    /// a later call gets the result. A null result for a loaded path means the exe has no readable icon.
    /// </summary>
    public static ImageSource? GetOrQueue(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (Cache.TryGetValue(executablePath, out ImageSource? icon))
        {
            return icon;
        }

        Pending.GetOrAdd(executablePath, path => Task.Run(() =>
        {
            Cache[path] = Load(path);
            Pending.TryRemove(path, out _);
        }));
        return null;
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            // .ico files (some launchers' shortcut icons) are read directly; anything else gives its embedded icon.
            using System.Drawing.Icon? icon = path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? new System.Drawing.Icon(path, 32, 32)
                : System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); // created on a worker thread, used on the UI thread
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            Logger.Warn($"No icon for {path}: {ex.Message}");
            return null; // the UI falls back to a generic app glyph
        }
    }
}
