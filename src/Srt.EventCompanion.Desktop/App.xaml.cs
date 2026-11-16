using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Srt.EventCompanion.Desktop;

/// <summary>
/// Only one copy runs per Windows user. Opening the app again (shortcut, Start menu, autostart) brings the running copy
/// to the front instead of starting a second one that would fight over the session, Steam and Content Manager.
/// </summary>
public partial class App : Application
{
    // The installer uses the same name (AppMutex) to wait for the app to close during an update.
    public const string SingleInstanceName = "SRT.EventCompanion.SingleInstance";
    private const string ActivateEventName = "SRT.EventCompanion.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unexpected error must never make the app silently vanish on a rig: log it and tell staff what happened.
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) WriteCrashLog(ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { WriteCrashLog(args.Exception); args.SetObserved(); };

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var firstInstance);
        if (!firstInstance)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { /* The other copy is still starting; it will show itself. */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var signal = _activateSignal;
        var listener = new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(BringToFront);
            }
            catch (ObjectDisposedException) { /* App is closing. */ }
        }) { IsBackground = true, Name = "Single instance listener" };
        listener.Start();

        // First run asks how the app will be used. A rig updated from 1.0.x is already paired: keep it as it was.
        var settingsStore = new Srt.EventCompanion.Core.AppSettingsStore();
        var settings = settingsStore.Load();
        if (settings is null)
        {
            Srt.EventCompanion.Core.RigCredential? credential = null;
            try { credential = new Srt.EventCompanion.Core.RigCredentialStore().Load(); } catch (Exception) { /* unreadable: set up again */ }
            if (credential is not null)
            {
                settings = Srt.EventCompanion.Core.AppSettings.ForExistingRig(StartupRegistration.IsEnabled());
                settingsStore.Save(settings);
            }
        }
        // Not set up yet: the main window opens on the setup page.
        var window = new MainWindow(settingsStore, settings ?? Srt.EventCompanion.Core.AppSettings.NotSetUp);
        MainWindow = window;
        window.Show();
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        // A Topmost toggle reliably raises the window over full-screen apps such as Content Manager.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "logs");

    /// <summary>Saves the error and returns the log file path (or null if even that failed).</summary>
    public static string? WriteCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"SRT Event Companion {Srt.EventCompanion.Core.AppVersion.Current}{Environment.NewLine}{DateTimeOffset.Now:O}{Environment.NewLine}{Environment.NewLine}{ex}");
            return path;
        }
        catch (Exception) { return null; }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = WriteCrashLog(e.Exception);
        // Keep running: most UI errors only affect the action that was clicked, and a session may be in progress.
        e.Handled = true;
        MessageBox.Show(MainWindow,
            $"Something went wrong: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            (log is null ? "" : $"The details were saved to:{Environment.NewLine}{log}{Environment.NewLine}{Environment.NewLine}") +
            "If this keeps happening, send that file to the SRT Labs team.",
            "SRT Event Companion", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        _activateSignal?.Dispose();
        base.OnExit(e);
    }
}
