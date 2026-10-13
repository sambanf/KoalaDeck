using System.Runtime.InteropServices;
using KoalaDeck.Services;

namespace KoalaDeck.Interop;

/// <summary>
/// Per-app output device routing — the same mechanism as Windows Settings › Sound › Volume mixer
/// ("Output device" per app). Uses the undocumented WinRT factory <c>Windows.Media.Internal.AudioPolicyConfig</c>
/// (Windows 10 1803+), called through its vtable because .NET 5+ has no built-in WinRT COM interop.
/// Windows persists the choice per app, so it survives app and PC restarts.
/// </summary>
internal sealed class AudioPolicyConfig : IDisposable
{
    private const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";

    // IUnknown (3) + IInspectable (3) + 19 methods we do not use → SetPersistedDefaultAudioEndpoint is slot 25.
    private const int SetPersistedDefaultAudioEndpointSlot = 25;

    private const string MmDevApiPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const int FlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;

    private static readonly Guid[] FactoryIids =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"), // Windows 10 21H2 / Windows 11
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // Windows 10 1803 – 21H1
    ];

    private readonly IntPtr _factory;
    private readonly SetPersistedDefaultAudioEndpointFn _setPersisted;

    private AudioPolicyConfig(IntPtr factory)
    {
        _factory = factory;
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr method = Marshal.ReadIntPtr(vtable, SetPersistedDefaultAudioEndpointSlot * IntPtr.Size);
        _setPersisted = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointFn>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    /// <summary>Returns null when this Windows version does not support per-app routing. Call on a COM-initialized thread.</summary>
    public static AudioPolicyConfig? TryCreate()
    {
        IntPtr classId = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(ActivatableClassId, ActivatableClassId.Length, out classId) < 0)
            {
                return null;
            }

            foreach (Guid iid in FactoryIids)
            {
                Guid id = iid;
                if (RoGetActivationFactory(classId, ref id, out IntPtr factory) >= 0 && factory != IntPtr.Zero)
                {
                    return new AudioPolicyConfig(factory);
                }
            }

            Logger.Warn("Per-app audio routing is not available on this Windows version");
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            Logger.Warn($"Per-app audio routing unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (classId != IntPtr.Zero)
            {
                WindowsDeleteString(classId);
            }
        }
    }

    /// <summary>
    /// Routes a process to an output device, or back to the Windows default when <paramref name="deviceId"/> is null.
    /// Returns the HRESULT (negative = failure).
    /// </summary>
    public int SetAppOutput(uint processId, string? deviceId)
    {
        IntPtr device = IntPtr.Zero;
        try
        {
            if (deviceId is not null)
            {
                string path = MmDevApiPrefix + deviceId + RenderInterfaceSuffix;
                int created = WindowsCreateString(path, path.Length, out device);
                if (created < 0)
                {
                    return created;
                }
            }

            int multimedia = _setPersisted(_factory, processId, FlowRender, RoleMultimedia, device);
            return multimedia < 0 ? multimedia : _setPersisted(_factory, processId, FlowRender, RoleConsole, device);
        }
        finally
        {
            if (device != IntPtr.Zero)
            {
                WindowsDeleteString(device);
            }
        }
    }

    public void Dispose()
    {
        Marshal.Release(_factory);
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
