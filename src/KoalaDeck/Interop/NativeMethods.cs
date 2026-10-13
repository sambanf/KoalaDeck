using System.Runtime.InteropServices;
using System.Text;

namespace KoalaDeck.Interop;

internal static class NativeMethods
{
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwcpRoundSmall = 3;

    /// <summary>Dark native title bar (Win10 1809+) and, on Windows 11, a caption / border color that matches the app.</summary>
    public static void ApplyDarkTitleBar(IntPtr hwnd, int captionRgb, int borderRgb)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }

        int caption = ToColorRef(captionRgb);
        int border = ToColorRef(borderRgb);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int)); // ignored before Windows 11
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
    }

    /// <summary>Rounded corners for popup windows such as the tray menu (Windows 11 only, no-op elsewhere).</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int preference = DwmwcpRoundSmall;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>Full exe path of a process, or null when it exited or access is denied (protected processes).</summary>
    public static string? GetProcessImagePath(uint processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Process IDs that own a top-level window a user would recognize as an app (visible, titled, not a tool window, not cloaked).</summary>
    public static HashSet<uint> GetAppWindowProcessIds()
    {
        const int GwOwner = 4;
        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x80;
        const int DwmwaCloaked = 14;

        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd)
                && GetWindow(hwnd, GwOwner) == IntPtr.Zero
                && (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) == 0
                && GetWindowTextLength(hwnd) > 0
                && !(DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                pids.Add(pid);
            }

            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>0xRRGGBB → COLORREF (0x00BBGGRR).</summary>
    private static int ToColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
