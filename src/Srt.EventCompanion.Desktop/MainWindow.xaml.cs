using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly RigProfileService _rigProfileService = new();
    private readonly ContentManagerSettingsService _cmSettings = new();
    private readonly ActiveSessionStore _activeSessionStore = new();
    private readonly SessionRestorationService _sessionRestorationService = new();
    private readonly GameProcessController _gameProcesses = new();
    private readonly OutboxStore _outbox = new();
    private readonly RetryBackoff _backoff = new();
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Rig health: a check-in at app start, driver check-in, session start and End Session tells admins the rig's version, state and sync backlog.
    // Updates published by an admin: checked at start-up and after each End Session.
    // No periodic check-ins: anything on a timer keeps the free Neon database awake while a rig sits idle.
    // The rig talks to SRT only at app start, driver check-in, session start and End Session.
    private bool _startupCheckDone;
    private AppRelease? _pendingRelease;
    private bool _installingUpdate;

    // Lap timing: read AC's shared memory ten times a second while a session is active.
    private readonly DispatcherTimer _lapTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<CompletedLap> _laps = new();
    private AcSharedMemoryReader? _acReader;
    private LapDetector _lapDetector = new();
    private ActiveRigSession? _recordingSession;

    // Set during initialisation
    private AssettoCorsaPaths? _acPaths;
    private RigCredential? _credential;
    private RigApiClient? _api;
    private OutboxSender? _outboxSender;
    private bool _flushing;

    // Driver step
    private enum DriverMode { None, Registered, Claim, Create }
    private DriverMode _driverMode = DriverMode.None;
    private DriverMatch? _selectedDriver;
    private readonly FavouriteDriverStore _favourites = new();
    private IReadOnlyList<DriverMatch> _centreDrivers = Array.Empty<DriverMatch>();
    private bool _centreDriversLoaded;
    private bool _showingSearchResults;

    // Fetched from SRT just before launch; never stored on disk because it contains the server password.
    private RigConfig? _config;
    private SessionPreparation? _preparation;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RunInitChecks();
        _syncTimer.Tick += async (_, _) => await SyncTickAsync();
        _lapTimer.Tick += (_, _) => LapTick();
        VersionText.Text = $"V{AppVersion.Current}";
        Closed += (_, _) => StopLapRecording(flush: false);
    }

    // ── STEP 1: INITIALISATION ───────────────────────────────────────────────

    private void RunInitChecks()
    {
        // Credential first: an active session restored after a restart still needs it to sync and end.
        _credential = new RigCredentialStore().Load();
        if (_credential is not null)
        {
            _api = new RigApiClient(_credential, Http);
            _outboxSender = new OutboxSender(_outbox, (body, token) => _api.PostIngestAsync(body, token));
            _syncTimer.Start();
            if (!_startupCheckDone)
            {
                _startupCheckDone = true;
                _ = SendHeartbeatAsync();
                // Nobody is working on the rig-check screen yet, so a published update can install straight away.
                _ = CheckForUpdateAsync(autoInstall: true);
            }
            SetStatus(StatusRig, "✓ Paired", "#3DDC84");
            RigLabel.Text = _credential.RigId;
        }
        else
        {
            SetStatus(StatusRig, "Not paired", "#FF4D5E");
            SetError(InitError, "This rig has not been paired. Click 'Pair this rig' to register it with SRT.");
        }

        _acPaths = AssettoCorsaPaths.Detect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // A session is still open (app closed or rig restarted mid-session): go straight to the session screen.
        var active = _activeSessionStore.Load();
        if (active is not null)
        {
            var steamWarning = _credential is not null && SteamAccount.Check(_credential.SteamId) is { IsOk: false } activeSteam
                ? $"{Environment.NewLine}{Environment.NewLine}⚠ {activeSteam.Message}" : string.Empty;
            ShowActiveSession(active, $"Active session for {active.DisplayName} ({active.SrtUsername}).{steamWarning}{Environment.NewLine}{Environment.NewLine}" +
                "Select End Session & Restore when the driver is finished. Assetto Corsa and Content Manager will be closed for you.");
            _ = ShowPasswordForActiveSessionAsync();
            StartLapRecording(active);
            return;
        }

        if (_acPaths is not null)
            SetStatus(StatusAC, "✓ Found", "#3DDC84");
        else
        {
            SetStatus(StatusAC, "Not found", "#FF4D5E");
            SetError(InitError, "Assetto Corsa installation was not detected. Install Assetto Corsa via Steam and ensure the game has been launched at least once.");
        }

        // The Steam account signed in right now must be the one registered for this rig.
        var steamMatches = false;
        if (_credential is not null)
        {
            var steam = SteamAccount.Check(_credential.SteamId);
            steamMatches = steam.IsOk;
            SetStatus(StatusSteam, steam.Label, steam.IsOk ? "#3DDC84" : "#FF4D5E");
            if (!steam.IsOk) SetError(InitError, steam.Message);
        }
        else
        {
            SetStatus(StatusSteam, "—", "#8A919C");
        }

        ProceedBtn.IsEnabled = _credential is not null && _acPaths is not null && steamMatches;
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        ResetDriverStep();
        ShowDriverStep();
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => Recheck();

    private void PairRig_Click(object sender, RoutedEventArgs e)
    {
        new PairRigWindow { Owner = this }.ShowDialog();
        Recheck();
    }

    private void Recheck()
    {
        ClearErrors();
        SetStatus(StatusRig, "Checking…", "#8A919C");
        SetStatus(StatusAC, "Checking…", "#8A919C");
        SetStatus(StatusSteam, "Checking…", "#8A919C");
        RunInitChecks();
    }

    // ── STEP 2: DRIVER ───────────────────────────────────────────────────────

    private void ShowDriverStep()
    {
        StepInit.Visibility = Visibility.Collapsed;
        StepDriver.Visibility = Visibility.Visible;
        StepLaunch.Visibility = Visibility.Collapsed;
        DriverError.Visibility = Visibility.Collapsed;
        RenderFavourites();
        RenderDriverList();
        _ = LoadCentreDriversAsync();
        SearchInput.Focus();
    }

    // ── Favourites (max 5, stored on this PC) ────────────────────────────────

    private void RenderFavourites()
    {
        FavouritesPanel.Children.Clear();
        var favourites = _favourites.Load();
        foreach (var favourite in favourites)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("FavouriteChip"),
                Content = $"★ {favourite.DisplayName}",
                ToolTip = $"@{favourite.SrtUsername} · in-game name: {favourite.InGameName}",
                Tag = favourite
            };
            chip.Click += (_, _) => SelectDriver(new DriverMatch(favourite.ProfileId, favourite.DisplayName, favourite.SrtUsername, null,
                favourite.SrtUsername, favourite.InGameName));
            FavouritesPanel.Children.Add(chip);
        }
        FavouritesHint.Visibility = favourites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (_driverMode != DriverMode.Registered || _selectedDriver?.SrtUsername is not { } username) return;
        if (_favourites.Contains(username))
        {
            _favourites.Remove(username);
        }
        else
        {
            var inGameName = string.IsNullOrWhiteSpace(DisplayNameInput.Text) ? _selectedDriver.PreferredInGameName : DisplayNameInput.Text.Trim();
            var result = _favourites.Add(new FavouriteDriver(username, _selectedDriver.DisplayName, inGameName, _selectedDriver.ProfileId));
            if (result == FavouriteResult.Full)
                ShowSearchStatus($"You already have {FavouriteDriverStore.MaxFavourites} favourites. Select one of them and choose ★ Unfavourite first.");
        }
        RenderFavourites();
        UpdateFavouriteButton();
    }

    private void UpdateFavouriteButton()
    {
        var username = _driverMode == DriverMode.Registered ? _selectedDriver?.SrtUsername : null;
        FavouriteBtn.Visibility = username is null ? Visibility.Collapsed : Visibility.Visible;
        if (username is not null) FavouriteBtn.Content = _favourites.Contains(username) ? "★ UNFAVOURITE" : "☆ FAVOURITE";
    }

    // ── Drivers who have raced at this centre ────────────────────────────────

    private async Task LoadCentreDriversAsync()
    {
        if (_api is null) return;
        if (!_centreDriversLoaded) DriverListLabel.Text = "Loading drivers who have raced at this centre…";
        try
        {
            _centreDrivers = await _api.RecentDriversAsync();
            _centreDriversLoaded = true;
        }
        catch (Exception ex)
        {
            if (!_centreDriversLoaded) DriverListLabel.Text = $"Couldn't load this centre's drivers ({ex.Message}). You can still use Search SRT.";
            return;
        }
        if (!_showingSearchResults) RenderDriverList();
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _showingSearchResults = false;
        RenderDriverList();
    }

    private void RenderDriverList()
    {
        if (_showingSearchResults) return;
        var filtered = DriverFilter.Filter(_centreDrivers, SearchInput.Text);
        DriverMatchesList.Items.Clear();
        foreach (var driver in filtered.Take(100))
            DriverMatchesList.Items.Add(new ListBoxItem { Content = DriverFilter.Describe(driver), Tag = driver });
        DriverMatchesList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var typed = SearchInput.Text.Trim();
        DriverListLabel.Text = !_centreDriversLoaded ? DriverListLabel.Text
            : _centreDrivers.Count == 0 ? "No one has raced at this centre yet. Use Search SRT for existing SRT drivers, or New driver."
            : filtered.Count == 0 ? $"No driver from this centre matches “{typed}”. Type their exact Discord or SRT username and choose Search SRT, or New driver."
            : typed.Length == 0 ? $"Drivers who have raced at this centre ({_centreDrivers.Count}), most recent first:"
            : $"{filtered.Count} driver(s) from this centre match “{typed}”:";
    }

    private void ResetDriverStep()
    {
        _driverMode = DriverMode.None;
        _selectedDriver = null;
        _config = null;
        _preparation = null;
        _showingSearchResults = false;
        SearchInput.Text = string.Empty;
        SearchStatus.Visibility = Visibility.Collapsed;
        DriverMatchesList.Items.Clear();
        DriverMatchesList.Visibility = Visibility.Collapsed;
        DriverPanel.Visibility = Visibility.Collapsed;
        SrtUsernameInput.Text = string.Empty;
        DisplayNameInput.Text = string.Empty;
    }

    private void BackToInit_Click(object sender, RoutedEventArgs e)
    {
        StepDriver.Visibility = Visibility.Collapsed;
        StepInit.Visibility = Visibility.Visible;
        StepLaunch.Visibility = Visibility.Collapsed;
    }

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // One match in this centre's list: pick it. Otherwise look the exact username up across SRT.
        if (!_showingSearchResults && DriverMatchesList.Items.Count == 1 && SearchInput.Text.Trim().Length > 0)
            DriverMatchesList.SelectedIndex = 0;
        else
            SearchDriver_Click(sender, e);
    }

    private async void SearchDriver_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        DriverMatchesList.Visibility = Visibility.Collapsed;
        DriverMatchesList.Items.Clear();
        DriverPanel.Visibility = Visibility.Collapsed;
        _selectedDriver = null;
        _driverMode = DriverMode.None;

        if (_api is null) { SetError(DriverError, "This rig is not paired with SRT."); return; }
        if (SrtUsername.Normalize(SearchInput.Text).Length == 0) { SetError(DriverError, "Enter the driver's Discord or SRT username."); return; }

        SearchBtn.IsEnabled = false;
        ShowSearchStatus("Searching SRT…");
        try
        {
            var lookup = await _api.FindDriverAsync(SearchInput.Text);
            if (lookup.Matches.Count == 0)
            {
                ShowSearchStatus($"No SRT driver has the username @{lookup.Query}." +
                    (lookup.UsernameAvailable ? " If this is a new driver, choose New driver." : string.Empty));
                return;
            }

            if (lookup.Matches.Count == 1)
            {
                SearchStatus.Visibility = Visibility.Collapsed;
                SelectDriver(lookup.Matches[0]);
                return;
            }

            ShowSearchStatus("More than one driver matches. Pick the right one:");
            _showingSearchResults = true;
            DriverListLabel.Text = "SRT search results:";
            foreach (var match in lookup.Matches)
                DriverMatchesList.Items.Add(new ListBoxItem { Content = DescribeMatch(match), Tag = match });
            DriverMatchesList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SearchStatus.Visibility = Visibility.Collapsed;
            SetError(DriverError, ex.Message);
        }
        finally
        {
            SearchBtn.IsEnabled = true;
        }
    }

    private void DriverMatch_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DriverMatchesList.SelectedItem is ListBoxItem { Tag: DriverMatch match }) SelectDriver(match);
    }

    private void SelectDriver(DriverMatch match)
    {
        _selectedDriver = match;
        DriverPanel.Visibility = Visibility.Visible;
        DisplayNameInput.Text = match.PreferredInGameName;

        if (match.SrtUsername is not null)
        {
            _driverMode = DriverMode.Registered;
            DriverPanelTitle.Text = $"✓ {match.DisplayName}" + (match.DiscordUsername is null ? string.Empty : $"  ·  Discord: {match.DiscordUsername}");
            SrtUsernameInput.Text = match.SrtUsername;
            SrtUsernameInput.IsReadOnly = true;
            SrtUsernameHint.Text = "This driver already has an SRT username.";
        }
        else
        {
            _driverMode = DriverMode.Claim;
            DriverPanelTitle.Text = $"✓ {match.DisplayName}  ·  Discord: {match.DiscordUsername}";
            SrtUsernameInput.Text = match.SuggestedSrtUsername ?? string.Empty;
            SrtUsernameInput.IsReadOnly = false;
            SrtUsernameHint.Text = "Existing SRT driver. Keep their Discord username as the SRT username, or change it. Their Discord account stays linked either way.";
        }
        DriverError.Visibility = Visibility.Collapsed;
        UpdateFavouriteButton();
    }

    private void NewDriver_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        DriverMatchesList.Visibility = Visibility.Collapsed;
        SearchStatus.Visibility = Visibility.Collapsed;
        _selectedDriver = null;
        _driverMode = DriverMode.Create;
        FavouriteBtn.Visibility = Visibility.Collapsed;
        DriverPanel.Visibility = Visibility.Visible;
        DriverPanelTitle.Text = "New driver";
        var typed = SrtUsername.Normalize(SearchInput.Text);
        SrtUsernameInput.Text = SrtUsername.IsValid(typed) ? typed : string.Empty;
        SrtUsernameInput.IsReadOnly = false;
        SrtUsernameHint.Text = "Choose a permanent SRT username: 2–32 characters, lowercase letters, numbers, \"_\" or \".\". It cannot be a username another SRT driver already uses.";
        DisplayNameInput.Text = string.Empty;
        SrtUsernameInput.Focus();
    }

    private async void PreviewJoin_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        if (_api is null) { SetError(DriverError, "This rig is not paired with SRT."); return; }
        if (_driverMode == DriverMode.None) { SetError(DriverError, "Search for the driver, or choose New driver."); return; }

        var username = SrtUsername.Normalize(SrtUsernameInput.Text);
        var displayName = DisplayNameInput.Text.Trim();
        if (!SrtUsername.IsValid(username)) { SetError(DriverError, "SRT username must be 2–32 characters: lowercase letters, numbers, \"_\" or \".\"."); return; }
        if (displayName.Length == 0) { SetError(DriverError, "Enter the in-game display name."); return; }
        if (_credential is not null && SteamAccount.Check(_credential.SteamId) is { IsOk: false } previewSteam) { SetError(DriverError, previewSteam.Message); return; }

        PreviewBtn.IsEnabled = false;
        try
        {
            // Save the SRT username first, so the session is linked to the driver's profile.
            if (_driverMode == DriverMode.Claim && _selectedDriver is not null)
            {
                _selectedDriver = await _api.ClaimDriverAsync(_selectedDriver.ProfileId, _selectedDriver.DiscordUsername ?? string.Empty, username);
                SelectDriver(_selectedDriver);
                DisplayNameInput.Text = displayName;
            }
            else if (_driverMode == DriverMode.Create)
            {
                _selectedDriver = await _api.CreateDriverAsync(username, displayName);
                SelectDriver(_selectedDriver);
                DisplayNameInput.Text = displayName;
            }

            // Always fetch the live event just before launch, so admin changes apply immediately.
            _config = await _api.GetConfigAsync();
            _ = SendHeartbeatAsync(); // driver check-in
            if (_config.Event is null)
                throw new InvalidOperationException("No Kerala Race Wars event is live. Ask an SRT admin to make an event live in the dashboard (Kerala Race Wars → Event & server).");

            _preparation = BuildPreparation(_config, SrtUsername.Display(username), displayName);
            var cmPreview = _cmSettings.PreviewPlayerNameChange(_cmSettings.DefaultValuesPath, displayName);
            var evt = _config.Event;

            var text =
                $"Event:         {evt.Name}{Environment.NewLine}" +
                (evt.Track is null && evt.Car is null ? string.Empty : $"Track / car:   {string.Join(" · ", new[] { evt.Track, evt.Car }.Where(v => v is not null))}{Environment.NewLine}") +
                $"Server:        {evt.ServerHost}:{evt.HttpPort}{Environment.NewLine}" +
                $"Username:      {_preparation.Request.Driver.SrtUsername}{Environment.NewLine}" +
                $"Display name:  {displayName}{Environment.NewLine}{Environment.NewLine}" +
                $"AC name now:   {_preparation.ProfileChange.Current.OfflineDriverName ?? "—"}{Environment.NewLine}" +
                $"AC name after: {_preparation.ProfileChange.Proposed.OfflineDriverName}{Environment.NewLine}" +
                $"CM name now:   {cmPreview.CurrentPlayerName ?? "—"}{Environment.NewLine}" +
                $"CM name after: {cmPreview.ProposedPlayerName}{Environment.NewLine}{Environment.NewLine}" +
                $"Join URI:{Environment.NewLine}{_preparation.ContentManagerJoinUri}";

            ShowLaunchStep(text, sessionActive: false);
            ShowPassword(evt.ServerPassword);
        }
        catch (Exception ex)
        {
            SetError(DriverError, ex.Message);
        }
        finally
        {
            PreviewBtn.IsEnabled = true;
        }
    }

    // ── STEP 3: LAUNCH ───────────────────────────────────────────────────────

    private void ShowLaunchStep(string previewText, bool sessionActive)
    {
        StepInit.Visibility = Visibility.Collapsed;
        StepDriver.Visibility = Visibility.Collapsed;
        StepLaunch.Visibility = Visibility.Visible;
        LaunchError.Visibility = Visibility.Collapsed;
        PreviewOutput.Text = previewText;
        LaunchHeading.Text = sessionActive ? "Session in progress" : "Ready to launch";
        LaunchHint.Text = sessionActive
            ? "The driver is on the server. End the session here when they are done."
            : "Review the details below, then launch Content Manager to begin the session.";
        LaunchBtn.Visibility = sessionActive ? Visibility.Collapsed : Visibility.Visible;
        EditDriverBtn.Visibility = sessionActive ? Visibility.Collapsed : Visibility.Visible;
        EndSessionBtn.Visibility = sessionActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateSyncStatus(null);
    }

    private void ShowActiveSession(ActiveRigSession session, string text)
    {
        ShowLaunchStep(text, sessionActive: true);
        PasswordPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowPassword(string? password)
    {
        PasswordText.Text = password ?? string.Empty;
        PasswordPanel.Visibility = string.IsNullOrEmpty(password) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ShowPasswordForActiveSessionAsync()
    {
        if (_api is null) return;
        try
        {
            _config = await _api.GetConfigAsync();
            ShowPassword(_config.Event?.ServerPassword);
        }
        catch { /* Offline: the password simply is not shown. */ }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(PasswordText.Text); } catch { /* Clipboard busy; staff can still read it. */ }
    }

    private void BackToDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionStore.Load() is not null)
        {
            SetError(LaunchError, "An active session is in progress. End Session & Restore first.");
            return;
        }
        PasswordPanel.Visibility = Visibility.Collapsed;
        ShowDriverStep();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        LaunchError.Visibility = Visibility.Collapsed;
        LaunchBtn.IsEnabled = false;
        try
        {
            if (_activeSessionStore.Load() is not null)
                throw new InvalidOperationException("End or restore the existing session before launching another driver.");
            if (_preparation is null || _config?.Event is null || _credential is null)
                throw new InvalidOperationException("Preview the session again before launching.");
            // Steam could have been switched since startup; laps are matched to drivers by this account.
            if (SteamAccount.Check(_credential.SteamId) is { IsOk: false } launchSteam)
                throw new InvalidOperationException(launchSteam.Message);

            var paths = RequirePaths();
            var displayName = _preparation.Request.Driver.DisplayName;

            // Content Manager keeps its settings in memory and writes them back when it exits, and the join link
            // is handed to an already-open CM. If CM (or AC) is open, the new name would be silently ignored or
            // overwritten, so close them before changing anything.
            if (_gameProcesses.IsAnyRunning())
            {
                SyncStatus.Text = "Closing Content Manager so the driver name can be changed…";
                var stillOpen = await _gameProcesses.CloseAllAsync(TimeSpan.FromSeconds(10));
                if (stillOpen.Count > 0)
                    throw new InvalidOperationException($"Could not close {string.Join(", ", stillOpen)}. Close it manually, then select Launch again.");
            }
            var now = DateTimeOffset.Now;

            // Change both names; if the second change fails, put the first one back so the rig is never half-changed.
            var cmBackup = _cmSettings.ApplyPlayerNameChange(_cmSettings.DefaultValuesPath, displayName, now);
            string raceBackup;
            try
            {
                raceBackup = _rigProfileService.ApplyDriverNameChange(paths.RaceIniPath, displayName, now);
                // Read both back, so a name change that didn't stick is caught here and not on the server.
                if (_cmSettings.ReadPlayerName(_cmSettings.DefaultValuesPath) != displayName)
                    throw new InvalidOperationException("Content Manager's driver name did not change. Close Content Manager and try again.");
                if (_rigProfileService.Read(paths.RaceIniPath).OfflineDriverName != displayName)
                    throw new InvalidOperationException("Assetto Corsa's driver name (race.ini) did not change. Try again, or contact SRT Labs.");
            }
            catch
            {
                SessionRestorationService.RestoreFile(cmBackup, _cmSettings.DefaultValuesPath);
                throw;
            }

            var session = new ActiveRigSession(
                _preparation.SessionId,
                _preparation.Request.Driver.SrtUsername,
                displayName,
                paths.RaceIniPath, raceBackup,
                _cmSettings.DefaultValuesPath, cmBackup,
                now,
                _config.Event.Id,
                _config.Rig.DisplayName);
            _activeSessionStore.Save(session);
            _favourites.UpdateInGameName(SrtUsername.Normalize(session.SrtUsername), displayName);

            _outbox.Enqueue(IngestMessages.StartSession(_credential.RigId, session));
            new ContentManagerLauncher().Launch(_config.Event.ToJoinTarget());

            ShowLaunchStep(
                $"✓ Content Manager launched.{Environment.NewLine}" +
                $"Event:  {_config.Event.Name}{Environment.NewLine}" +
                $"Driver: {displayName} ({session.SrtUsername}){Environment.NewLine}{Environment.NewLine}" +
                $"AC + CM names updated. Backups saved.{Environment.NewLine}" +
                $"AC backup:  {raceBackup}{Environment.NewLine}" +
                $"CM backup:  {cmBackup}{Environment.NewLine}{Environment.NewLine}" +
                "When the driver is finished, select End Session & Restore.",
                sessionActive: true);
            ShowPassword(_config.Event.ServerPassword);
            StartLapRecording(session);
            _ = SendHeartbeatAsync();

            await FlushOutboxAsync();
        }
        catch (Exception ex)
        {
            // If the names were already changed, stay on the session screen so staff can End Session & Restore.
            if (_activeSessionStore.Load() is { } started)
            {
                ShowActiveSession(started, $"Session for {started.DisplayName} started, but Content Manager could not be opened.{Environment.NewLine}Open Content Manager manually, or select End Session & Restore to undo.");
                StartLapRecording(started);
            }
            SetError(LaunchError, ex.Message);
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
        }
    }

    private async void EndSession_Click(object sender, RoutedEventArgs e)
    {
        LaunchError.Visibility = Visibility.Collapsed;
        EndSessionBtn.IsEnabled = false;
        try
        {
            var session = _activeSessionStore.Load()
                ?? throw new InvalidOperationException("There is no active session to end.");

            // 1. Keep any lap finished in the last moment, then close the game and the launcher.
            //    Nothing can be logged after this point.
            StopLapRecording(flush: true);
            SyncStatus.Text = "Closing Assetto Corsa and Content Manager…";
            var stillRunning = await _gameProcesses.CloseAllAsync(TimeSpan.FromSeconds(10));
            if (stillRunning.Count > 0)
                throw new InvalidOperationException($"Could not close {string.Join(", ", stillRunning)}. Close it manually, then select End Session & Restore again.");
            var endedAt = DateTimeOffset.Now;

            // 2. Queue the end of the session behind any laps still waiting, then send everything.
            if (_credential is not null) _outbox.Enqueue(IngestMessages.EndSession(_credential.RigId, session.SessionId, endedAt));
            SyncStatus.Text = "Sending session data to SRT…";
            var flush = await FlushOutboxAsync();

            // 3. Put the rig's own name back: the launch backup, or the rig name from SRT if a backup is missing.
            string? rigName = session.RigDefaultName;
            if (rigName is null && _api is { } api && (!File.Exists(session.ContentManagerBackupPath) || !File.Exists(session.RaceIniBackupPath)))
            {
                try { rigName = (await api.GetConfigAsync()).Rig.DisplayName; } catch { /* handled by Restore */ }
            }
            var outcome = _sessionRestorationService.Restore(session, rigName, DateTimeOffset.Now);
            _activeSessionStore.Clear();

            ResetDriverStep();
            _centreDriversLoaded = _centreDrivers.Count > 0; // refreshed by ShowDriverStep below
            PasswordPanel.Visibility = Visibility.Collapsed;
            LapPanel.Visibility = Visibility.Collapsed;
            ShowDriverStep();
            var lapCount = _laps.Count;
            var summary = $"Session ended with {lapCount} lap(s) recorded. Rig name restored" + (outcome.FallbackNameUsed is null ? " from backup." : $" to \"{outcome.FallbackNameUsed}\" (backup was missing).");
            summary += flush is { Remaining: > 0 }
                ? $" {flush.Remaining} item(s) are waiting for internet and will sync automatically."
                : " All session data is synced with SRT.";
            ShowSearchStatus(summary);
            _ = SendHeartbeatAsync();
            // Between drivers is the best moment to update; the next driver hasn't been set up yet.
            _ = CheckForUpdateAsync(autoInstall: true);
        }
        catch (Exception ex)
        {
            SetError(LaunchError, ex.Message);
            UpdateSyncStatus(null);
        }
        finally
        {
            EndSessionBtn.IsEnabled = true;
        }
    }

    // ── Lap timing ───────────────────────────────────────────────────────────

    private void StartLapRecording(ActiveRigSession session)
    {
        if (_recordingSession?.SessionId == session.SessionId && _lapTimer.IsEnabled) return;
        _recordingSession = session;
        _laps.Clear();
        _lapDetector = new LapDetector();
        _acReader ??= new AcSharedMemoryReader();
        LapPanel.Visibility = Visibility.Visible;
        LapStatus.Text = "Waiting for Assetto Corsa…";
        UpdateLapSummary();
        _lapTimer.Start();
    }

    private void StopLapRecording(bool flush)
    {
        _lapTimer.Stop();
        if (flush && _recordingSession is not null && _acReader is not null)
        {
            var lap = _lapDetector.FlushPending(_acReader.Read(), DateTimeOffset.Now);
            if (lap is not null) RecordLap(lap);
        }
        _acReader?.Dispose();
        _acReader = null;
        _recordingSession = null;
    }

    private void LapTick()
    {
        if (_recordingSession is null || _acReader is null) return;
        AcTelemetrySample? sample;
        try { sample = _acReader.Read(); }
        catch (Exception ex) { LapStatus.Text = $"Can't read lap times: {ex.Message}"; return; }

        LapStatus.Text = sample?.Status switch
        {
            null => "Waiting for Assetto Corsa…",
            AcStatus.Live => sample.IsInPit ? "● On track · in the pits" : "● On track",
            AcStatus.Pause => "Paused",
            AcStatus.Replay => "Replay",
            _ => "Waiting for the driver to join…"
        };
        LapStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(sample?.Status == AcStatus.Live ? "#3DDC84" : "#8A919C")!;
        if (sample is null) return;

        var lap = _lapDetector.Observe(sample, DateTimeOffset.Now);
        if (lap is not null)
        {
            RecordLap(lap);
            _ = FlushOutboxAsync();
        }
    }

    private void RecordLap(CompletedLap lap)
    {
        if (_recordingSession is null || _credential is null) return;
        // Written to the outbox first, so the lap survives no internet, an app crash or a restart.
        _outbox.Enqueue(IngestMessages.Lap(_credential.RigId, _recordingSession.SessionId, lap));
        _laps.Add(lap);
        UpdateLapSummary();
    }

    private void UpdateLapSummary()
    {
        if (_laps.Count == 0)
        {
            LapSummary.Text = "No laps yet";
            LapList.Text = "Laps appear here as the driver crosses the line.";
            return;
        }
        var best = _laps.Min(lap => lap.LapTimeMs);
        LapSummary.Text = $"{_laps.Count} lap(s) · best {LapTimeFormat.Format(best)}";
        LapList.Text = string.Join(Environment.NewLine, _laps.TakeLast(6).Reverse().Select(lap =>
            $"Lap {lap.LapNumber,-3} {LapTimeFormat.Format(lap.LapTimeMs)}" +
            (lap.TouchedPits ? "  pit" : string.Empty) +
            (lap.MaxTyresOut > 2 ? "  off track" : string.Empty) +
            (lap.LapTimeMs == best ? "  ★" : string.Empty)));
    }

    // ── Rig health + updates ─────────────────────────────────────────────────

    private async Task SendHeartbeatAsync()
    {
        if (_api is null || _credential is null) return;
        try
        {
            var steamOk = SteamAccount.Check(_credential.SteamId).IsOk;
            await _api.PostIngestAsync(IngestMessages.Heartbeat(_credential.RigId, AppVersion.Current, _outbox.Count,
                inSession: _activeSessionStore.Load() is not null, steamOk));
        }
        catch { /* Offline: the next check-in will catch up. Admin shows the rig as offline meanwhile. */ }
    }

    private async Task CheckForUpdateAsync(bool autoInstall)
    {
        if (_api is null || _installingUpdate) return;
        try
        {
            var release = (await _api.GetConfigAsync()).App;
            _pendingRelease = release is not null && AppVersion.IsNewer(release.Version, AppVersion.Current) ? release : null;
        }
        catch { return; }

        UpdateBtn.Visibility = _pendingRelease is null ? Visibility.Collapsed : Visibility.Visible;
        if (_pendingRelease is null) return;
        UpdateBtn.Content = $"UPDATE TO V{_pendingRelease.Version}";
        if (autoInstall && CanUpdateNow()) await InstallUpdateAsync();
    }

    /// <summary>Never during a session or while staff are setting up a driver.</summary>
    private bool CanUpdateNow() =>
        _activeSessionStore.Load() is null && StepLaunch.Visibility != Visibility.Visible &&
        (StepInit.Visibility == Visibility.Visible || (_driverMode == DriverMode.None && SearchInput.Text.Length == 0));

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionStore.Load() is not null)
        {
            MessageBox.Show(this, "A session is running. End Session & Restore first, then install the update.", "SRT Event Companion",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await InstallUpdateAsync();
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingRelease is not { } release || _installingUpdate) return;
        _installingUpdate = true;
        UpdateBtn.IsEnabled = false;
        UpdateBtn.Content = $"DOWNLOADING V{release.Version}…";
        try
        {
            var installer = await new AppUpdater().DownloadAsync(release);
            UpdateBtn.Content = "INSTALLING…";
            await FlushOutboxAsync();
            AppUpdater.StartInstaller(installer);
            // The installer waits for this app to close, installs, then reopens it.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _installingUpdate = false;
            UpdateBtn.IsEnabled = true;
            UpdateBtn.Content = $"UPDATE TO V{release.Version}";
            MessageBox.Show(this, $"The update could not be installed.\n\n{ex.Message}\n\nThe app keeps working on the current version. If this keeps happening, contact the SRT Labs team.",
                "SRT Event Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Sync with SRT ────────────────────────────────────────────────────────

    private async Task SyncTickAsync()
    {
        if (_flushing || _outboxSender is null || _outbox.Count == 0 || DateTimeOffset.Now < _backoff.NextAttemptAt) return;
        await FlushOutboxAsync();
    }

    private async Task<OutboxFlushResult?> FlushOutboxAsync()
    {
        if (_outboxSender is null || _flushing) return null;
        _flushing = true;
        try
        {
            var result = await _outboxSender.FlushAsync();
            if (result.Remaining == 0) _backoff.Succeeded();
            else _backoff.Failed(DateTimeOffset.Now);
            UpdateSyncStatus(result);
            return result;
        }
        catch (Exception ex)
        {
            _backoff.Failed(DateTimeOffset.Now);
            SyncStatus.Text = $"Sync problem: {ex.Message}";
            return null;
        }
        finally
        {
            _flushing = false;
        }
    }

    private void UpdateSyncStatus(OutboxFlushResult? result)
    {
        var pending = result?.Remaining ?? _outbox.Count;
        SyncStatus.Text = pending == 0
            ? "✓ All session data is synced with SRT."
            : $"{pending} item(s) waiting to sync with SRT. Retrying automatically" + (result?.LastError is null ? "." : $" ({result.LastError})");
        SyncStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(pending == 0 ? "#3DDC84" : "#F5A623")!;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SessionPreparation BuildPreparation(RigConfig config, string srtUsername, string displayName)
    {
        var rigConfiguration = new RigSessionConfiguration(
            config.Rig.Id,
            config.Event!.Id,
            ContentManagerIntegrationStatus.Verified,
            config.Event.ToJoinTarget());
        var request = new SessionRequest(new SessionDriver(srtUsername, displayName));
        return new SessionPreparationService(_rigProfileService).Prepare(rigConfiguration, request, RequirePaths().RaceIniPath);
    }

    private static string DescribeMatch(DriverMatch match) =>
        match.DisplayName +
        (match.SrtUsername is null ? string.Empty : $"  ·  SRT @{match.SrtUsername}") +
        (match.DiscordUsername is null ? string.Empty : $"  ·  Discord {match.DiscordUsername}");

    private AssettoCorsaPaths RequirePaths() =>
        _acPaths ?? AssettoCorsaPaths.Detect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        ?? throw new InvalidOperationException("Assetto Corsa installation not found. Return to the initialisation screen.");

    private void ShowSearchStatus(string message)
    {
        SearchStatus.Text = message;
        SearchStatus.Visibility = Visibility.Visible;
    }

    private static void SetStatus(TextBlock block, string text, string hexColor)
    {
        block.Text = text;
        block.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(hexColor)!;
    }

    private static void SetError(TextBlock block, string message)
    {
        block.Text = message;
        block.Visibility = Visibility.Visible;
    }

    private void ClearErrors()
    {
        InitError.Visibility = Visibility.Collapsed;
        InitError.Text = string.Empty;
    }
}
