using System.Windows;
using KoalaDeck.Services;
using KoalaDeck.ViewModels;

namespace KoalaDeck;

/// <summary>Composition root: wires services, tray icon and dashboard window, and owns the app lifetime.</summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private SettingsService? _settings;
    private AudioService? _audio;
    private SerialService? _serial;
    private DeckController? _deck;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIconService? _tray;
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            SingleInstance.ActivateFirstInstance();
            Shutdown();
            return;
        }

        bool startInTray = e.Args.Any(a => string.Equals(a, StartupManager.MinimizedArgument, StringComparison.OrdinalIgnoreCase));
        Logger.Info($"Koala Deck {typeof(App).Assembly.GetName().Version} starting{(startInTray ? " in the tray" : string.Empty)}");

        SettingsService settings = _settings = new SettingsService();
        StartupManager.RefreshPathIfEnabled();

        _audio = new AudioService(); // app groups are pushed by MainViewModel.InitializeAsync

        _serial = new SerialService
        {
            BaudRate = settings.Current.BaudRate,
            DtrEnable = settings.Current.DtrEnable,
            AutoDetect = settings.Current.AutoConnect,
        };
        _deck = new DeckController(_serial, _audio, () => settings.Current.VolumeStepPercent);

        MainViewModel viewModel = _viewModel = new MainViewModel(settings, _audio, _serial, _deck);
        _window = new MainWindow(viewModel);
        _window.HiddenToTray += OnHiddenToTray;

        _tray = new TrayIconService(StartupManager.IsEnabled);
        _tray.OpenDashboardRequested += (_, _) => ShowDashboard();
        _tray.AutoStartToggleRequested += (_, _) => viewModel.AutoStart = !viewModel.AutoStart;
        _tray.ReconnectRequested += (_, _) => viewModel.Reconnect();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _serial.StateChanged += (_, args) => Dispatcher.InvokeAsync(() => _tray?.UpdateStatus(args.State, args.PortName));

        _singleInstance.ListenForActivation(() => Dispatcher.InvokeAsync(ShowDashboard));
        _singleInstance.ListenForExit(() => Dispatcher.InvokeAsync(() =>
        {
            Logger.Info("Exit requested by Setup");
            ExitApplication();
        }));
        SessionEnding += (_, _) => Cleanup(); // Windows sign-out / shutdown

        if (!startInTray)
        {
            _window.Show();
        }

        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ShowDashboard() => _window?.ShowDashboard();

    private void ExitApplication()
    {
        Cleanup();
        Shutdown();
    }

    private void OnHiddenToTray(object? sender, EventArgs e)
    {
        if (_settings is null || _settings.Current.TrayHintShown)
        {
            return;
        }

        _tray?.ShowNotification("Koala Deck is still running",
            "It keeps listening to your deck from the system tray. Right-click the koala icon to exit.");
        _settings.Current.TrayHintShown = true;
        _settings.SaveDelayed();
    }

    private void Cleanup()
    {
        if (_cleanedUp || _settings is null) // nothing was started (e.g. second instance)
        {
            return;
        }

        _cleanedUp = true;
        Logger.Info("Shutting down");
        TryRun(() => _settings?.Save());
        TryRun(() => _window?.CloseForExit());
        TryRun(() => _viewModel?.Dispose());
        TryRun(() => _deck?.Dispose());
        TryRun(() => _serial?.Dispose());
        TryRun(() => _audio?.Dispose());
        TryRun(() => _tray?.Dispose());
        TryRun(() => _settings?.Dispose());
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error("Cleanup step failed", ex);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // a tray utility should survive a glitch rather than vanish
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
