using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace KoalaDeck.Interop;

/// <summary>Reads the target and arguments of a .lnk shortcut through the Windows shell (IShellLinkW).</summary>
internal sealed class ShellLink : IDisposable
{
    private const int MaxPath = 1024;

    private readonly IShellLinkW _link = (IShellLinkW)new CShellLink();

    /// <param name="iconPath">Custom icon file of the shortcut (environment variables expanded), or empty.</param>
    public bool TryRead(string shortcutPath, out string targetPath, out string arguments, out string iconPath)
    {
        targetPath = string.Empty;
        arguments = string.Empty;
        iconPath = string.Empty;
        try
        {
            ((IPersistFile)_link).Load(shortcutPath, 0); // STGM_READ
            var target = new StringBuilder(MaxPath);
            _link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(MaxPath);
            _link.GetArguments(args, args.Capacity);
            var icon = new StringBuilder(MaxPath);
            _link.GetIconLocation(icon, icon.Capacity, out _);
            targetPath = target.ToString();
            arguments = args.ToString();
            iconPath = Environment.ExpandEnvironmentVariables(icon.ToString());
            return targetPath.Length > 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            return false; // broken or protected shortcut
        }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_link);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    /// <summary>Only the vtable prefix up to GetIconLocation is declared; later methods are never called.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
    }
}
