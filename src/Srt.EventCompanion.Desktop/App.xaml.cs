using System.Threading;
using System.Windows;

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

        var window = new MainWindow();
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
