using System.Windows;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

/// <summary>Change the choices made in first-run setup: how this PC is used, start with Windows, automatic updates.</summary>
public partial class SettingsWindow : PageView
{
    private readonly AppSettingsStore _store;
    private AppSettings _settings;
    private readonly bool _sessionActive;

    /// <param name="who">Rig ID for a sim centre rig, or the signed-in driver's name on an individual's PC.</param>
    private readonly RigApiClient? _api;

    /// <param name="api">Own PC only: lets the driver link Discord here.</param>
    public SettingsWindow(AppSettingsStore store, AppSettings settings, string? who, bool sessionActive, RigApiClient? api = null, string? discordUsername = null)
    {
        _store = store;
        _settings = settings;
        _sessionActive = sessionActive;
        _api = api;
        InitializeComponent();
        if (api is not null)
        {
            DiscordSection.Visibility = Visibility.Visible;
            ShowDiscord(discordUsername);
        }
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
        AutoUpdateBox.IsChecked = settings.AutoUpdate;
        ShowMode(who);
    }

    /// <summary>True when how the PC is used changed (the main window then checks everything again).</summary>
    public bool ModeChanged { get; private set; }

    /// <summary>True when Discord was linked here (the main window reloads the driver).</summary>
    public bool DiscordChanged { get; private set; }

    private void ShowDiscord(string? discordUsername)
    {
        DiscordText.Text = discordUsername is null
            ? "Not linked. Link it to sign in with Discord on another PC, and to keep your SRT races together."
            : $"@{discordUsername} ✓  Linked to your SRT driver profile.";
        LinkDiscordBtn.Visibility = discordUsername is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LinkDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_api is not { } api) return;
        var window = new DiscordWindow("LINK DISCORD",
            "Links your Discord to your SRT driver profile. If you already raced with SRT using Discord, your history comes together on one profile.",
            () => api.StartDiscordAsync("link"), poll => api.PollDiscordAsync(poll), sharedPc: false);
        await Navigator!.ShowAsync(window);
        if (window.Result is not { } result) return;
        DiscordChanged = true;
        if (result.Outcome == "pending-review")
        {
            DiscordText.Text = $"@{result.DiscordUsername}: waiting for an SRT admin to confirm your older SRT profile.";
            LinkDiscordBtn.Visibility = Visibility.Collapsed;
        }
        else ShowDiscord(result.DiscordUsername);
    }

    private void ShowMode(string? who)
    {
        if (_settings.Mode == AppMode.Individual)
        {
            ModeText.Text = "YOUR OWN PC";
            ModeDetail.Text = who is null ? "Not signed in yet." : $"Signed in as {who}. Every session on this PC is yours.";
        }
        else
        {
            ModeText.Text = "SIM CENTRE RIG";
            ModeDetail.Text = who is null ? "Not paired yet." : $"Paired as {who}. Drivers sign in with their email for each session.";
        }
    }

    private async void ChangeMode_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionActive)
        {
            ShowError("A session is running. End Session & Restore first, then change this.");
            return;
        }
        var setup = new SetupWindow(_store, _settings with { StartWithWindows = StartWithWindowsBox.IsChecked == true, AutoUpdate = AutoUpdateBox.IsChecked == true },
            changingMode: true);
        if (!await Navigator!.ShowAsync(setup) || setup.Result is null) return;
        _settings = setup.Result;
        ModeChanged = true;
        Close(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var startWithWindows = StartWithWindowsBox.IsChecked == true;
        if (!StartupRegistration.Set(startWithWindows))
        {
            ShowError("Windows would not change the start-up setting. Try again, or use Task Manager → Startup apps.");
            return;
        }
        _settings = _settings with { StartWithWindows = startWithWindows, AutoUpdate = AutoUpdateBox.IsChecked == true };
        _store.Save(_settings);
        Close(true);
    }

    private void ShowError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
    }
}
