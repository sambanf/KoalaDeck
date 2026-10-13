using System.Diagnostics;
using System.IO;

namespace KoalaDeck.Services;

/// <summary>Tiny thread-safe file logger (%LocalAppData%\KoalaDeck\koaladeck.log, rolled at 1 MB).</summary>
public static class Logger
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoalaDeck");

    private static string LogFile => Path.Combine(LogDirectory, "koaladeck.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > MaxFileBytes)
                {
                    File.Move(LogFile, LogFile + ".old", overwrite: true);
                }

                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
