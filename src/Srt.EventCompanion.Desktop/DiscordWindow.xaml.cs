using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

/// <summary>
/// Discord sign-in without Discord on this PC: SRT gives a short code, shown here as a QR code. The driver signs in to
/// Discord on their phone (or in this PC's browser) and this window polls SRT until that is done.
/// </summary>
public partial class DiscordWindow : PageView
{
    private readonly Func<Task<DiscordLinkStart>> _start;
    private readonly Func<string, Task<DiscordLinkPoll>> _poll;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DiscordLinkStart? _link;
    private DateTimeOffset _expiresAt;
    private bool _polling;
    private bool _started;
    private string _inviteUrl = "https://discord.gg/NrsGgCkw32";

    /// <param name="sharedPc">A sim centre rig: suggest the phone, and signing out if the browser here is used.</param>
    public DiscordWindow(string heading, string intro, Func<Task<DiscordLinkStart>> start, Func<string, Task<DiscordLinkPoll>> poll, bool sharedPc)
    {
        _start = start;
        _poll = poll;
        InitializeComponent();
        Heading.Text = heading;
        IntroText.Text = intro;
        SharedPcHint.Visibility = sharedPc ? Visibility.Visible : Visibility.Collapsed;
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => { if (_link is null && !_started) { _started = true; await StartAsync(); } };
        PageClosed += (_, _) => _timer.Stop();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>The finished result (Status done), or null if the window was closed first.</summary>
    public DiscordLinkPoll? Result { get; private set; }

    private async Task StartAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        RetryBtn.Visibility = Visibility.Collapsed;
        WaitPanel.Visibility = Visibility.Visible;
        DonePanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Getting a code from SRT…";
        try
        {
            _link = await _start();
            if (!string.IsNullOrWhiteSpace(_link.InviteUrl)) _inviteUrl = _link.InviteUrl!;
            _expiresAt = DateTimeOffset.Now.AddSeconds(Math.Max(60, _link.ExpiresInSeconds));
            QrImage.Source = QrCode(_link.Url);
            StatusText.Text = $"Waiting for Discord… The code works for {Math.Max(1, _link.ExpiresInSeconds / 60)} minutes.";
            _timer.Start();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task PollAsync()
    {
        if (_link is null || _polling) return;
        if (DateTimeOffset.Now > _expiresAt.AddSeconds(30)) { _timer.Stop(); ShowError("The code expired. Select Start again for a new one."); return; }
        _polling = true;
        try
        {
            var status = await _poll(_link.PollToken);
            if (!status.IsFinished) return;
            _timer.Stop();
            if (status.Status == "expired") { ShowError("The code expired. Select Start again for a new one."); return; }
            if (status.Status == "error") { ShowError(status.Error ?? "Discord sign-in didn't work. Select Start again."); return; }
            Result = status;
            ShowDone(status);
        }
        catch (RigApiException ex) when (ex.StatusCode is null) { /* offline for a moment: keep trying */ }
        catch (Exception ex) { _timer.Stop(); ShowError(ex.Message); }
        finally { _polling = false; }
    }

    private void ShowDone(DiscordLinkPoll status)
    {
        WaitPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
        var who = status.DiscordUsername is null ? "your Discord" : $"@{status.DiscordUsername}";
        (DoneTitle.Text, DoneText.Text) = status.Outcome switch
        {
            "pending-review" => ("✓ ALMOST THERE",
                $"{who} matches an existing SRT driver profile. An SRT admin will confirm it's you, then your email and that profile become one. You can race now."),
            "signed-in" => ($"✓ SIGNED IN AS {status.Driver?.DisplayName?.ToUpperInvariant() ?? who.ToUpperInvariant()}",
                status.PendingReview == true
                    ? $"With Discord {who}. It matches an SRT driver profile an admin made; once an admin confirms it's you, your races come together on that profile."
                    : status.NewProfile == true ? $"With Discord {who}. Welcome to SRT: your driver profile was just created." : $"With Discord {who}."),
            _ => ("✓ DISCORD LINKED", status.Merged == true
                ? $"{who} is linked. Your email and your existing SRT driver profile ({status.Driver?.DisplayName}) are now one profile."
                : $"{who} is linked to {status.Driver?.DisplayName ?? "your SRT driver profile"}. Next time you can sign in with Discord.")
        };
        JoinPanel.Visibility = Visibility.Visible;
        if (status.InGuild == true)
        {
            JoinTitle.Text = "✓ YOU'RE IN THE SRT DISCORD";
            JoinText.Text = "See you on track.";
            JoinBtn.Visibility = Visibility.Collapsed;
        }
        CloseBtn.Content = "DONE";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
        RetryBtn.Visibility = Visibility.Visible;
    }

    private static BitmapImage QrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OpenHere_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not null) OpenUrl(_link.Url);
    }

    private void Join_Click(object sender, RoutedEventArgs e) => OpenUrl(_inviteUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser: the QR code still works */ }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await StartAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close(Result is not null);

    public override bool RequestBack() { Close(Result is not null); return true; }
}
