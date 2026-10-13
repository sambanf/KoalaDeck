namespace KoalaDeck.Services;

/// <summary>
/// Ensures one Koala Deck per user session (two apps reading one COM port cannot work).
/// A second launch signals the first one to show its dashboard and then exits. The installer and uninstaller
/// signal the exit event to close a running Koala Deck cleanly before replacing or removing its files.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Names are shared with installer\KoalaDeck.iss — keep them in sync.
    private const string MutexName = @"Local\KoalaDeck.SingleInstance";
    private const string ActivateEventName = @"Local\KoalaDeck.Activate";
    private const string ExitEventName = @"Local\KoalaDeck.Exit";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private readonly EventWaitHandle? _exitEvent;
    private readonly List<RegisteredWaitHandle> _registrations = new();

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
        if (createdNew)
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        }
    }

    public bool IsFirstInstance { get; }

    public static void ActivateFirstInstance()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up or already exiting.
        }
    }

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch is attempted.</summary>
    public void ListenForActivation(Action onActivate) => Listen(_activateEvent, onActivate);

    /// <summary>Invokes <paramref name="onExit"/> (on a thread-pool thread) when Setup asks the app to close.</summary>
    public void ListenForExit(Action onExit) => Listen(_exitEvent, onExit);

    public void Dispose()
    {
        foreach (RegisteredWaitHandle registration in _registrations)
        {
            registration.Unregister(null);
        }

        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; the OS releases it when the process exits.
            }
        }

        _mutex.Dispose();
    }

    private void Listen(EventWaitHandle? signal, Action callback)
    {
        if (signal is not null)
        {
            _registrations.Add(ThreadPool.RegisterWaitForSingleObject(
                signal, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false));
        }
    }
}
