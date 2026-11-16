using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

/// <summary>
/// Shows the chosen event's required content on this PC and installs what's missing.
/// Closes with true when every package is installed or skipped.
/// </summary>
public partial class ContentWindow : PageView
{
    private readonly EventServerConfig _event;
    private readonly RigApiClient _api;
    private readonly AssettoCorsaPaths _paths;
    private readonly ContentInstaller _installer;
    private readonly ContentStateStore _store;
    private readonly GameProcessController _gameProcesses;
    private IReadOnlyList<ContentCheck> _checks = Array.Empty<ContentCheck>();
    private CancellationTokenSource? _cancel;
    private bool _closeWhenStopped;

    public ContentWindow(EventServerConfig evt, RigApiClient api, AssettoCorsaPaths paths, ContentInstaller installer,
        ContentStateStore store, GameProcessController gameProcesses)
    {
        InitializeComponent();
        _event = evt; _api = api; _paths = paths; _installer = installer; _store = store; _gameProcesses = gameProcesses;
        EventLabel.Text = $"EVENT CONTENT · {evt.Name.ToUpperInvariant()}";
        Refresh();
    }

    public bool AllReady => _checks.All(c => c.IsReady);

    /// <summary>How many required packages are neither installed nor skipped.</summary>
    public static int CountNotReady(IEnumerable<ContentCheck> checks) => checks.Count(c => !c.IsReady);

    private void Refresh()
    {
        _checks = ContentChecker.CheckAll(_event.RequiredContent, _paths.InstallDirectory, _store);
        PackageList.Children.Clear();
        foreach (var check in _checks) PackageList.Children.Add(BuildRow(check));

        var todo = _checks.Where(c => !c.IsReady).ToList();
        var bytes = todo.Sum(c => c.Package.SizeBytes);
        InstallBtn.Visibility = todo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipBtn.Visibility = todo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.Content = todo.Count == 1 ? $"INSTALL ({FormatBytes(bytes)})" : $"INSTALL {todo.Count} PACKAGES ({FormatBytes(bytes)})";
        CloseBtn.Content = todo.Count == 0 ? "CONTINUE  →" : "CLOSE";
        DoneText.Visibility = todo.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneText.Text = "✓ This PC has everything the event needs.";
    }

    private UIElement BuildRow(ContentCheck check)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = check.Package.Name, FontFamily = (FontFamily)FindResource("BodySemiFont"), FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = $"{FormatBytes(check.Package.SizeBytes)}  ·  {string.Join(", ", check.Package.Targets)}",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = check.Label, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(check.IsReady ? Color.FromRgb(0x3D, 0xDC, 0x84) : check.Status == ContentStatus.FoundOnRig ? Color.FromRgb(0xFF, 0xB6, 0x4C) : Color.FromRgb(0xFF, 0x8A, 0x98))
        });
        grid.Children.Add(text);

        return new Border
        {
            Child = grid, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 6),
            Background = (Brush)FindResource("FieldBg"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1)
        };
    }

    /// <summary>Staff override: this PC already has these exact mods (installed by hand, from another source, etc.).</summary>
    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        var todo = _checks.Where(c => !c.IsReady).ToList();
        if (todo.Count == 0 || _cancel is not null) return;
        var names = string.Join("\n", todo.Select(c => $"  •  {c.Package.Name}"));
        if (MessageBox.Show(HostWindow, $"Mark {(todo.Count == 1 ? "this mod" : $"these {todo.Count} mods")} as already installed on this PC?\n\n{names}\n\n" +
                "Only do this if this PC has exactly these mods. A different version can stop the driver joining the server.",
                "Skip install", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var check in todo) _installer.MarkSkipped(check.Package);
        Refresh();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var todo = _checks.Where(c => !c.IsReady).Select(c => c.Package).ToList();
        if (todo.Count == 0) return;

        InstallBtn.IsEnabled = false;
        SkipBtn.IsEnabled = false;
        PackageList.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        _cancel = new CancellationTokenSource();
        try
        {
            // Files can't be replaced while the game has them open.
            if (_gameProcesses.IsAnyRunning())
            {
                ProgressText.Text = "Closing Assetto Corsa and Content Manager…";
                var stillOpen = await _gameProcesses.CloseAllAsync(TimeSpan.FromSeconds(10), _cancel.Token);
                if (stillOpen.Count > 0) throw new InvalidOperationException($"Close {string.Join(", ", stillOpen)} and try again.");
            }

            var notes = new List<string>();
            for (var i = 0; i < todo.Count; i++)
            {
                var package = todo[i];
                var prefix = todo.Count > 1 ? $"{i + 1} of {todo.Count} · {package.Name}" : package.Name;
                var progress = new Progress<ContentProgress>(p =>
                {
                    ProgressText.Text = $"{prefix}: {p.Stage} {(p.Fraction > 0 && p.Fraction < 1 ? $"{p.Fraction:P0}" : string.Empty)}";
                    ProgressBar.Value = p.Fraction;
                });
                var result = await _installer.InstallAsync(package, ct => _api.GetPackageDownloadAsync(package.Id, ct),
                    _paths.InstallDirectory, _paths.UserDocumentsDirectory, progress, _cancel.Token);
                if (result.BackupFolder is not null)
                    notes.Add($"{package.Name}: the previous copy was saved in {result.BackupFolder}.");
            }
            ProgressText.Text = "Done.";
            ProgressBar.Value = 1;
            Refresh();
            if (notes.Count > 0) { DoneText.Visibility = Visibility.Visible; DoneText.Text = "✓ Installed.\n" + string.Join("\n", notes); }
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Stopped. Select Install to continue; the download resumes where it stopped.";
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Windows did not allow the app to write to the Assetto Corsa folder. Close Assetto Corsa and Content Manager and try again; if it repeats, contact SRT Labs.");
        }
        catch (Exception ex)
        {
            ShowError(ex is RigApiException api && api.StatusCode is null
                ? "SRT could not be reached. Check the internet connection and try again."
                : ex.Message);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            InstallBtn.IsEnabled = true;
            SkipBtn.IsEnabled = true;
            PackageList.IsEnabled = true;
            Refresh();
            if (_closeWhenStopped) Close(AllReady);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestBack();

    /// <summary>Leaving during an install asks first, then stops it (the download resumes next time).</summary>
    public override bool RequestBack()
    {
        if (_cancel is not null)
        {
            if (!ConfirmStop()) return false;
            _closeWhenStopped = true;
            _cancel.Cancel();
            return true; // closes once the install has stopped
        }
        Close(AllReady);
        return true;
    }

    public override bool CanAppClose()
    {
        if (_cancel is null) return true;
        if (!ConfirmStop()) return false;
        _cancel.Cancel();
        return true;
    }

    private bool ConfirmStop() => MessageBox.Show(HostWindow, "Stop installing? You can continue later; the download resumes where it stopped.",
        "Event content", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.00} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.0} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} KB",
        _ => $"{bytes} B"
    };
}
