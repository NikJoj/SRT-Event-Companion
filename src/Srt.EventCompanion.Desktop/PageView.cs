using System.Windows;
using System.Windows.Controls;

namespace Srt.EventCompanion.Desktop;

/// <summary>Shows pages inside the main window, one on top of another (Settings → Change… → Pair), instead of new windows.</summary>
public interface IPageNavigator
{
    /// <summary>Shows the page over the current screen; completes with the page's result when it closes.</summary>
    Task<bool> ShowAsync(PageView page);
}

/// <summary>
/// A page shown inside the main window (setup, settings, pairing, Discord, event mods). It replaces a dialog window:
/// Close(result) goes back to the screen underneath and hands the result to whoever opened it.
/// </summary>
public class PageView : UserControl
{
    private TaskCompletionSource<bool>? _completion;

    /// <summary>Set when the page is shown; pages use it to open the next page.</summary>
    public IPageNavigator? Navigator { get; private set; }

    /// <summary>The main window, for message boxes.</summary>
    protected Window HostWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    internal Task<bool> Attach(IPageNavigator navigator)
    {
        Navigator = navigator;
        _completion = new TaskCompletionSource<bool>();
        return _completion.Task;
    }

    /// <summary>Raised when the page closes (stop timers and the like).</summary>
    public event EventHandler? PageClosed;

    /// <summary>Goes back to the screen underneath with this result.</summary>
    protected void Close(bool result)
    {
        if (_completion is null || _completion.Task.IsCompleted) return;
        PageClosed?.Invoke(this, EventArgs.Empty);
        _completion.SetResult(result);
    }

    /// <summary>Esc: the page decides (default: go back, like Cancel). Return false to stay.</summary>
    public virtual bool RequestBack()
    {
        Close(false);
        return true;
    }

    /// <summary>The app is closing: false asks to stay (for example during an install). Default: fine to close.</summary>
    public virtual bool CanAppClose() => true;
}
