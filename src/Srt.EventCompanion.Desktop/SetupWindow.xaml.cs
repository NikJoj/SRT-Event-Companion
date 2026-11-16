using System.Windows;
using System.Windows.Input;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

/// <summary>
/// First-run setup: sim centre or own PC, start with Windows, automatic updates. An individual then signs in with
/// their email, which registers this PC with SRT; a sim centre pairs the rig with a code (now or later).
/// Also opened from Settings to change how this PC is used: nothing changes unless the new registration succeeds.
/// </summary>
public partial class SetupWindow : PageView
{
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _current;
    private readonly bool _changingMode;
    private readonly IndividualSignUpService _signUp = new();
    private string? _email;

    /// <param name="changingMode">Opened from Settings on a PC that is already set up.</param>
    public SetupWindow(AppSettingsStore settingsStore, AppSettings current, bool changingMode = false)
    {
        _settingsStore = settingsStore;
        _current = current;
        _changingMode = changingMode;
        InitializeComponent();
        StartWithWindowsBox.IsChecked = current.StartWithWindows;
        AutoUpdateBox.IsChecked = current.AutoUpdate;
        if (changingMode)
        {
            ModeKicker.Text = "SETTINGS";
            CancelBtn.Visibility = Visibility.Visible;
            (current.Mode == AppMode.Individual ? IndividualChoice : CentreChoice).IsChecked = true;
        }
    }

    /// <summary>The settings that were saved, once setup finished.</summary>
    public AppSettings? Result { get; private set; }

    private AppMode SelectedMode => IndividualChoice.IsChecked == true ? AppMode.Individual : AppMode.Centre;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        ContinueBtn.IsEnabled = true;
        ModeError.Visibility = Visibility.Collapsed;
        // Suggest the usual choice for the mode on first run; Settings keeps what the user had.
        if (!_changingMode) StartWithWindowsBox.IsChecked = AppSettings.Defaults(SelectedMode).StartWithWindows;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);

    /// <summary>Esc: back a page; on first run there is nothing to go back to.</summary>
    public override bool RequestBack()
    {
        if (SignUpPage.Visibility == Visibility.Visible) { Back_Click(this, new RoutedEventArgs()); return true; }
        if (LinkPage.Visibility == Visibility.Visible) { Finish(AppMode.Individual); return true; }
        if (_changingMode) { Close(false); return true; }
        return false;
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMode == AppMode.Individual)
        {
            ModePage.Visibility = Visibility.Collapsed;
            SignUpPage.Visibility = Visibility.Visible;
            EmailInput.Focus();
            return;
        }

        // Sim centre: pair now. On first run the rig can also be paired later from the main screen.
        var paired = await Navigator!.ShowAsync(new PairRigWindow());
        if (!paired && _changingMode)
        {
            ModeError.Text = "This PC stays as it was. Pair it with a code from SRT admin to switch it to a sim centre rig.";
            ModeError.Visibility = Visibility.Visible;
            return;
        }
        Finish(AppMode.Centre);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SignUpPage.Visibility = Visibility.Collapsed;
        ModePage.Visibility = Visibility.Visible;
    }

    private void EmailInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SendCodeBtn.IsEnabled) SendCode_Click(sender, e);
    }

    private void CodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VerifyBtn.IsEnabled) Verify_Click(sender, e);
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        SignUpError.Visibility = Visibility.Collapsed;
        var email = EmailAddress.Normalize(EmailInput.Text);
        if (!EmailAddress.IsValid(email)) { ShowError("Enter your email address, for example name@gmail.com."); return; }
        SendCodeBtn.IsEnabled = false;
        try
        {
            var sent = await _signUp.SendCodeAsync(email);
            _email = email;
            if (!string.IsNullOrWhiteSpace(sent.Notice)) EmailNotice.Text = sent.Notice;
            CodePanel.Visibility = Visibility.Visible;
            NamePanel.Visibility = Visibility.Collapsed;
            CodeStatus.Text = $"Code sent to {sent.Email}. It works for {Math.Max(1, sent.ExpiresInSeconds / 60)} minutes. Not there? Check spam or promotions, then send again.";
            SendCodeBtn.Content = "SEND AGAIN";
            VerifyBtn.IsEnabled = true;
            CodeInput.Focus();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SendCodeBtn.IsEnabled = true; }
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        SignUpError.Visibility = Visibility.Collapsed;
        if (_email is null) { ShowError("Send a code to your email first."); return; }
        if (NamePanel.Visibility == Visibility.Visible && NameInput.Text.Trim().Length < 2) { ShowError("Enter the name to show on the leaderboard (at least 2 characters)."); return; }
        VerifyBtn.IsEnabled = false;
        try
        {
            var name = NamePanel.Visibility == Visibility.Visible ? NameInput.Text.Trim() : null;
            var result = await _signUp.VerifyAsync(_email, CodeInput.Text, SteamAccount.SignedInSteamId(), name);
            if (result.NeedsName)
            {
                NamePanel.Visibility = Visibility.Visible;
                NameInput.Focus();
                return;
            }
            try { new RigCredentialStore().Save(result.Credential!); }
            catch (Exception ex)
            {
                ShowError($"You're signed in, but Windows would not let the app save your sign-in ({ex.Message}). Run the app as your normal Windows user and sign in again.");
                return;
            }
            SignUpStatus.Text = $"✓ Signed in as {result.Driver!.DisplayName}." +
                (result.MovedFromAnotherPc ? " Your account now uses this PC; the app on your previous PC is signed out." : string.Empty);
            SignUpStatus.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            _credential = result.Credential;
            // Offer to link Discord (optional); skipped when it's already linked.
            if (result.Driver.DiscordUsername is null)
            {
                SignUpPage.Visibility = Visibility.Collapsed;
                LinkPage.Visibility = Visibility.Visible;
                return;
            }
            Finish(AppMode.Individual);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { VerifyBtn.IsEnabled = true; }
    }

    private RigCredential? _credential;

    /// <summary>Own PC with a Discord account already linked to an SRT driver: no email needed.</summary>
    private async void DiscordSignIn_Click(object sender, RoutedEventArgs e)
    {
        SignUpError.Visibility = Visibility.Collapsed;
        var steamId = SteamAccount.SignedInSteamId();
        var window = new DiscordWindow("SIGN IN WITH DISCORD",
            "Scan the code with your phone, or open it in this PC's browser, and sign in to Discord. SRT drivers get their existing profile; new drivers get one made from their Discord.",
            () => _signUp.StartDiscordAsync(steamId), poll => _signUp.PollDiscordAsync(poll), sharedPc: false);
        await Navigator!.ShowAsync(window);
        if (window.Result is not { } result) return;
        try { new RigCredentialStore().Save(_signUp.CredentialFrom(result)); }
        catch (Exception ex) { ShowError($"Signed in, but the app could not save your sign-in ({ex.Message}). Try again."); return; }
        Finish(AppMode.Individual);
    }

    private async void LinkDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_credential is null) { Finish(AppMode.Individual); return; }
        var api = new RigApiClient(_credential);
        var window = new DiscordWindow("LINK DISCORD",
            "Scan the code with your phone, or open it in this PC's browser, and sign in to Discord.",
            () => api.StartDiscordAsync("link"), poll => api.PollDiscordAsync(poll), sharedPc: false);
        await Navigator!.ShowAsync(window);
        Finish(AppMode.Individual);
    }

    private void SkipLink_Click(object sender, RoutedEventArgs e) => Finish(AppMode.Individual);

    private void Finish(AppMode mode)
    {
        var settings = _current with
        {
            SetupDone = true, Mode = mode,
            StartWithWindows = StartWithWindowsBox.IsChecked == true, AutoUpdate = AutoUpdateBox.IsChecked == true
        };
        StartupRegistration.Set(settings.StartWithWindows);
        _settingsStore.Save(settings);
        Result = settings;
        Close(true);
    }

    private void ShowError(string message)
    {
        SignUpError.Text = message;
        SignUpError.Visibility = Visibility.Visible;
    }
}
