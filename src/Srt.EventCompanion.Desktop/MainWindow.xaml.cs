using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

public partial class MainWindow : Window, IPageNavigator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly RigProfileService _rigProfileService = new();
    private readonly ContentManagerSettingsService _cmSettings = new();
    private readonly ActiveSessionStore _activeSessionStore = new();
    private readonly SessionRestorationService _sessionRestorationService = new();
    private readonly GameProcessController _gameProcesses = new();
    private readonly ContentStateStore _contentStore = new();
    private readonly ContentInstaller _contentInstaller;
    // Required packages of the chosen event that are neither installed nor skipped (null until checked); sent with check-ins.
    private int? _contentMissing;
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

    // Driver step: the driver signs in with their email and a 6-digit code. SRT refuses a session without that sign-in.
    private string? _driverEmail;          // where the code was sent
    private string? _driverToken;          // proof of sign-in, good for one session on this rig
    private DriverMatch? _selectedDriver;  // the verified driver; null until their profile exists
    private readonly DispatcherTimer _resendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _resendAvailableAt;

    // Fetched from SRT just before launch; never stored on disk because it contains the server password.
    private RigConfig? _config;
    private EventServerConfig? _event;     // the chosen event as fetched at preview: its server, password and mods
    private SessionPreparation? _preparation;

    // Set up in first-run setup: sim centre rig or an individual's own PC, start with Windows, automatic updates.
    private readonly AppSettingsStore _settingsStore;
    private AppSettings _settings;
    private bool IsIndividual => _settings.Mode == AppMode.Individual;
    private RigOwner? _owner;              // individual PCs: the signed-in driver (from SRT)

    // The event picked on the first screen (every live event has its own server and mods).
    private IReadOnlyList<EventServerConfig> _liveEvents = Array.Empty<EventServerConfig>();
    private EventServerConfig? _selectedEvent;
    private bool _eventsLoaded;
    private bool _showingEvents;

    public MainWindow(AppSettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _contentInstaller = new ContentInstaller(store: _contentStore);
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // First run: how this PC is used, inside this window. Nothing else happens until it's done.
            if (!_settings.SetupDone)
            {
                var setup = new SetupWindow(_settingsStore, _settings);
                await ShowAsync(setup);
                if (setup.Result is not null) _settings = setup.Result;
            }
            RunInitChecks();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _pages.Count > 0) { _pages[^1].RequestBack(); e.Handled = true; }
        };
        Closing += (_, e) => { if (_pages.Any(page => !page.CanAppClose())) e.Cancel = true; };
        _syncTimer.Tick += async (_, _) => await SyncTickAsync();
        _lapTimer.Tick += (_, _) => LapTick();
        _resendTimer.Tick += (_, _) => UpdateResendButton();
        var built = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value;
        VersionText.Text = built is null ? $"V{AppVersion.Current}" : $"V{AppVersion.Current}  ·  built {built}";
        Closed += (_, _) => StopLapRecording(flush: false);
    }

    // ── Pages (instead of separate windows) ──────────────────────────────────

    private readonly List<PageView> _pages = new();

    /// <summary>Shows a page over the current step; the page underneath comes back when it closes.</summary>
    public async Task<bool> ShowAsync(PageView page)
    {
        var closed = page.Attach(this);
        _pages.Add(page);
        ShowTopPage();
        try { return await closed; }
        finally
        {
            _pages.Remove(page);
            ShowTopPage();
        }
    }

    private void ShowTopPage()
    {
        var top = _pages.Count == 0 ? null : _pages[^1];
        PageContent.Content = top;
        PageHost.Visibility = top is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsBtn.IsEnabled = top is null;
        if (top is not null)
            Dispatcher.BeginInvoke(() => top.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
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
            var firstCheck = !_startupCheckDone;
            if (firstCheck)
            {
                _startupCheckDone = true;
                _ = SendHeartbeatAsync();
            }
            // One request loads the live events and, at start-up, any published update (nobody is working yet, so it can install).
            _ = LoadEventsAsync(checkForUpdate: firstCheck);
            SetStatus(StatusRig, IsIndividual ? "✓ Signed in" : "✓ Paired", "#3DDC84");
            RigLabel.Text = IsIndividual ? _owner?.DisplayName ?? "Signed in" : _credential.RigId;
        }
        else
        {
            SetStatus(StatusRig, IsIndividual ? "Not signed in" : "Not paired", "#FF4D5E");
            SetError(InitError, IsIndividual
                ? "You're not signed in on this PC. Select Sign in and use your email."
                : "This rig has not been paired. Click 'Pair this rig' to register it with SRT.");
            ShowEvents(Array.Empty<EventServerConfig>(), IsIndividual ? "Sign in to see the live events." : "Pair this rig to see the live events.");
        }
        ApplyModeUi();

        var acDetection = AssettoCorsaPaths.DetectOnThisPc(_acFolders.Load());
        _acPaths = acDetection.Paths;
        ShowAcPicker(acDetection);

        // A session is still open (app closed or rig restarted mid-session): go straight to the session screen.
        var active = _activeSessionStore.Load();
        if (active is not null)
        {
            var steamWarning = _credential is not null && SteamAccount.Check(_credential.SteamId) is { IsOk: false } activeSteam
                ? $"{Environment.NewLine}{Environment.NewLine}⚠ {activeSteam.Message}" : string.Empty;
            ShowActiveSession(active, $"Active session for {active.DisplayName} ({active.SrtUsername}).{steamWarning}{Environment.NewLine}{Environment.NewLine}" +
                "Select End Session & Restore when the driver is finished. Assetto Corsa and Content Manager will be closed for you.");
            _ = ShowPasswordForActiveSessionAsync(active);
            StartLapRecording(active);
            return;
        }

        if (_acPaths is not null)
            SetStatus(StatusAC, acDetection.UsedManualFolders ? "✓ Found (chosen manually)" : "✓ Found", "#3DDC84");
        else
        {
            SetStatus(StatusAC, "Not found", "#FF4D5E");
            SetError(InitError, acDetection.Problem ?? "Assetto Corsa was not found. Contact SRT Labs.");
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

        _pcReady = _credential is not null && _acPaths is not null && steamMatches;
        SelectEvent(_selectedEvent); // re-checks the event's mods now that Assetto Corsa's folder is known
    }

    private bool _pcReady;

    /// <summary>
    /// Prepare needs the event and Assetto Corsa; it turns green once every mod the event needs is installed (or skipped).
    /// Continue needs a ready PC, a chosen event, and Prepare green.
    /// </summary>
    private void UpdateInitButtons()
    {
        var contentReady = _selectedEvent is not null && _contentMissing == 0;
        PrepareBtn.IsEnabled = _credential is not null && _acPaths is not null && _selectedEvent is not null;
        PrepareBtn.Style = (Style)FindResource(contentReady ? "ReadyBtn" : "PrimaryBtn");
        PrepareBtn.Content = contentReady
            ? (_selectedEvent!.RequiredContent.Count == 0 ? "✓ NO MODS NEEDED" : "✓ EVENT MODS READY")
            : _contentMissing is > 0 ? $"PREPARE FOR EVENT ({_contentMissing} MOD{(_contentMissing == 1 ? "" : "S")})" : "PREPARE FOR EVENT";
        ProceedBtn.IsEnabled = _pcReady && contentReady;
        PrepareHint.Visibility = _selectedEvent is not null && _acPaths is not null && !contentReady ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Event choice ─────────────────────────────────────────────────────────

    private async Task LoadEventsAsync(bool checkForUpdate = false)
    {
        if (_api is null) return;
        RefreshEventsBtn.IsEnabled = false;
        if (!_eventsLoaded) EventStatus.Text = "Loading live events…";
        try
        {
            var config = await _api.GetConfigAsync();
            _eventsLoaded = true;
            ApplyRigInfo(config.Rig);
            ShowEvents(config.LiveEvents, config.LiveEvents.Count == 0
                ? (IsIndividual
                    ? "No events open to individual drivers are live right now. Check back when SRT opens one, then select Refresh."
                    : "No events open to sim centres are live right now. Check back when SRT opens one, then select Refresh.")
                : null);
            if (checkForUpdate) await CheckForUpdateAsync(autoInstall: true, config);
        }
        catch (RigApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized && IsIndividual)
        {
            ShowEvents(Array.Empty<EventServerConfig>(), "You were signed out on this PC.");
            SetStatus(StatusRig, "Signed out", "#FF4D5E");
            SetError(InitError, "You were signed out, usually because your account was signed in on another PC. Select Sign in to use this PC again.");
            ProceedBtn.IsEnabled = PrepareBtn.IsEnabled = false;
        }
        catch (Exception ex)
        {
            ShowEvents(_liveEvents, ex is RigApiException { StatusCode: null }
                ? "SRT could not be reached, so the live events could not be loaded. Check the internet connection, then select Refresh."
                : ex.Message);
        }
        finally { RefreshEventsBtn.IsEnabled = true; }
    }

    /// <summary>What SRT says about this PC: an individual's PC carries its owner, who drives every session.</summary>
    private void ApplyRigInfo(RigInfo rig)
    {
        // SRT knows best how this PC is registered (for example after re-pairing a former home PC as a rig).
        var mode = rig.IsIndividual ? AppMode.Individual : AppMode.Centre;
        if (mode != _settings.Mode)
        {
            _settings = _settings with { Mode = mode };
            _settingsStore.Save(_settings);
        }
        _owner = rig.IsIndividual ? rig.Owner : null;
        RigLabel.Text = IsIndividual ? _owner?.DisplayName ?? "Signed in" : rig.Id;
        ApplyModeUi();
    }

    private void ShowEvents(IReadOnlyList<EventServerConfig> events, string? status)
    {
        _liveEvents = events;
        var keep = _selectedEvent?.Id ?? _settings.LastEventId;
        _showingEvents = true;
        EventList.Items.Clear();
        foreach (var evt in events)
        {
            var detail = string.Join("  ·  ", new[] { evt.Track, evt.Car }.Where(v => !string.IsNullOrWhiteSpace(v))
                .Append(evt.RequiredContent.Count == 0 ? "no extra mods" : $"{evt.RequiredContent.Count} required mod(s)"));
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = evt.Name, FontFamily = (FontFamily)FindResource("BodySemiFont"), FontSize = 16 });
            content.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Hint") });
            EventList.Items.Add(new ListBoxItem { Content = content, Tag = evt });
        }
        var choice = EventChoice.Default(events, keep);
        EventList.SelectedItem = EventList.Items.OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, choice));
        _showingEvents = false;
        SelectEvent(choice);
        EventList.Visibility = events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EventStatus.Text = status ?? (events.Count == 1 ? "One event is live." : $"{events.Count} events are live. Select the one you're racing in.");
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingEvents) return;
        SelectEvent((EventList.SelectedItem as ListBoxItem)?.Tag as EventServerConfig);
    }

    private void SelectEvent(EventServerConfig? evt)
    {
        _selectedEvent = evt;
        HeaderKicker.Text = evt is null ? "SIM RACING THARAVADU" : evt.Name.ToUpperInvariant();
        if (evt is not null && !string.Equals(_settings.LastEventId, evt.Id, StringComparison.OrdinalIgnoreCase))
        {
            _settings = _settings with { LastEventId = evt.Id };
            try { _settingsStore.Save(_settings); } catch (IOException) { /* only the default choice next time */ }
        }
        // Mods missing for this event (a quick look at the folders, no download): Continue waits until there are none.
        _contentMissing = evt is not null && _acPaths is not null
            ? ContentWindow.CountNotReady(ContentChecker.CheckAll(evt.RequiredContent, _acPaths.InstallDirectory, _contentStore))
            : null;
        UpdateInitButtons();
    }

    private void RefreshEvents_Click(object sender, RoutedEventArgs e) => _ = LoadEventsAsync();

    /// <summary>Sim centre: pairing, staff sign drivers in. Own PC: the signed-in driver races; nothing about centres.</summary>
    private void ApplyModeUi()
    {
        var individual = IsIndividual;
        StatusRigLabel.Text = individual ? "SRT account" : "Rig registration";
        RigLabelCaption.Text = individual ? "DRIVER" : "RIG";
        if (_credential is null) RigLabel.Text = individual ? "Not signed in" : "Not paired";
        PairBtn.Content = individual ? "SIGN IN" : "PAIR THIS RIG";
        PairBtn.Visibility = individual && _credential is not null && _owner is not null ? Visibility.Collapsed : Visibility.Visible;
        InitHint.Text = individual
            ? "Pick the event to race in. The app uses that event's server and installs the mods it needs on your PC."
            : "Pick the event this rig is racing in. The app uses that event's server and installs the mods it needs.";
        SignInHelpCard.Visibility = individual ? Visibility.Collapsed : Visibility.Visible;
        DriverHeading.Text = individual ? "READY TO RACE" : "WHO'S DRIVING?";
        DriverHint.Text = individual
            ? "You're signed in on this PC. Check the name other drivers will see in the game, then select Preview."
            : "The driver signs in with their email (SRT emails a 6-digit code) or with their linked Discord. Only a signed-in driver can race.";
        ChangeDriverBtn.Visibility = individual ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var who = _credential is null ? null : IsIndividual ? _owner?.DisplayName ?? "your account" : _credential.RigId;
        var window = new SettingsWindow(_settingsStore, _settings, who, sessionActive: _activeSessionStore.Load() is not null,
            api: IsIndividual ? _api : null, discordUsername: _owner?.DiscordUsername);
        var saved = await ShowAsync(window);
        if (window.DiscordChanged && !window.ModeChanged)
        {
            await LoadEventsAsync();
            if (StepDriver.Visibility == Visibility.Visible) ResetDriverStep();
        }
        if (!saved) return;
        _settings = _settingsStore.Load() ?? _settings;
        if (!window.ModeChanged) return;
        // A new registration: start over on the first screen with the new sign-in.
        _owner = null;
        _eventsLoaded = false;
        _startupCheckDone = false;
        ResetDriverStep();
        BackToInit_Click(this, e);
        Recheck();
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        ResetDriverStep();
        ShowDriverStep();
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => Recheck();

    // ── Event content (mods the chosen event requires) ────────────────────────

    /// <summary>Installs the chosen event's mods ahead of time, before any driver is waiting.</summary>
    private async void PrepareForEvent_Click(object sender, RoutedEventArgs e)
    {
        InitError.Visibility = Visibility.Collapsed;
        if (_api is null || _acPaths is null) return;
        PrepareBtn.IsEnabled = false;
        try
        {
            if (_selectedEvent is null) { SetError(InitError, "Choose an event first."); return; }
            // Fetch again: admins may have changed the event's mods since the list loaded.
            var config = await _api.GetConfigAsync();
            var evt = config.FindEvent(_selectedEvent.Id);
            if (evt is null)
            {
                ShowEvents(config.LiveEvents, null);
                SetError(InitError, $"{_selectedEvent.Name} is no longer live. Choose another event.");
                return;
            }
            _selectedEvent = evt; // admins may have changed its mods
            if (evt.RequiredContent.Count == 0)
            {
                _contentMissing = 0;
                MessageBox.Show(this, $"{evt.Name} doesn't need any extra mods. This PC is ready.", "Prepare for event", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await EnsureEventContentAsync(evt, alwaysShow: true);
        }
        catch (Exception ex) { SetError(InitError, ex is RigApiException { StatusCode: null } ? "SRT could not be reached. Check the internet connection." : ex.Message); }
        finally { UpdateInitButtons(); _ = SendHeartbeatAsync(); }
    }

    /// <summary>
    /// True when every package the event requires is installed or skipped. Otherwise opens the content window
    /// (install, or skip what the rig already has) and returns whether everything is ready afterwards.
    /// </summary>
    private async Task<bool> EnsureEventContentAsync(EventServerConfig evt, bool alwaysShow = false)
    {
        if (_api is null) return true;
        var paths = RequirePaths();
        var checks = ContentChecker.CheckAll(evt.RequiredContent, paths.InstallDirectory, _contentStore);
        _contentMissing = ContentWindow.CountNotReady(checks);
        if (_contentMissing == 0 && !alwaysShow) return true;

        await ShowAsync(new ContentWindow(evt, _api, paths, _contentInstaller, _contentStore, _gameProcesses));
        _contentMissing = ContentWindow.CountNotReady(ContentChecker.CheckAll(evt.RequiredContent, paths.InstallDirectory, _contentStore));
        return _contentMissing == 0;
    }

    // ── Manual Assetto Corsa folders (when automatic detection fails) ─────────

    private readonly AcFolderOverrideStore _acFolders = new();

    private void ShowAcPicker(AssettoCorsaDetection detection)
    {
        var manual = _acFolders.Load();
        var hasManual = manual.InstallDirectory is not null || manual.DocumentsDirectory is not null;
        AcPickPanel.Visibility = detection.Paths is null || detection.UsedManualFolders ? Visibility.Visible : Visibility.Collapsed;
        AcResetBtn.Visibility = hasManual ? Visibility.Visible : Visibility.Collapsed;
        AcPickError.Visibility = Visibility.Collapsed;

        var game = detection.Paths?.InstallDirectory ?? detection.GameFolderFound ?? manual.InstallDirectory;
        SetStatus(AcGameFolderText, game is null ? "Not found yet" : $"✓ {game}", game is null ? "#8A919C" : "#3DDC84");
        var settings = detection.Paths?.UserDocumentsDirectory ?? manual.DocumentsDirectory;
        SetStatus(AcSettingsFolderText, settings is null ? "Not found yet" : $"✓ {settings}", settings is null ? "#8A919C" : "#3DDC84");
    }

    private void ChooseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Choose the Assetto Corsa game folder (the one with AssettoCorsa.exe)", @"C:\Program Files (x86)\Steam\steamapps\common");
        if (picked is null) return;
        var folder = AssettoCorsaPaths.ResolveGameFolder(picked);
        if (folder is null)
        {
            ShowAcPickError($"AssettoCorsa.exe isn't in {picked}. In Steam, right-click Assetto Corsa → Manage → Browse local files, and choose the folder that opens.");
            return;
        }
        _acFolders.Save(_acFolders.Load() with { InstallDirectory = folder });
        Recheck();
    }

    private void ChooseSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Choose the Assetto Corsa settings folder (Documents\\Assetto Corsa)",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (picked is null) return;
        var folder = AssettoCorsaPaths.ResolveSettingsFolder(picked);
        if (folder is null)
        {
            ShowAcPickError($"cfg\\race.ini isn't in {picked}. Choose the \"Assetto Corsa\" folder inside Documents. If there is none, start AC once through Content Manager, close it, and try again.");
            return;
        }
        _acFolders.Save(_acFolders.Load() with { DocumentsDirectory = folder });
        Recheck();
    }

    private void ResetAcFolders_Click(object sender, RoutedEventArgs e)
    {
        _acFolders.Clear();
        Recheck();
    }

    private string? PickFolder(string title, string startIn)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(startIn) ? startIn : string.Empty
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void ShowAcPickError(string message)
    {
        AcPickError.Text = message;
        AcPickError.Visibility = Visibility.Visible;
    }

    private async void PairRig_Click(object sender, RoutedEventArgs e)
    {
        if (IsIndividual)
        {
            // Sign in again with email (a new PC, or signed out because the account moved to another PC).
            var setup = new SetupWindow(_settingsStore, _settings, changingMode: true);
            if (await ShowAsync(setup) && setup.Result is not null) _settings = setup.Result;
            _owner = null;
        }
        else await ShowAsync(new PairRigWindow());
        _eventsLoaded = false;
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
        if (IsIndividual || _driverToken is not null) DisplayNameInput.Focus();
        else if (CodePanel.Visibility == Visibility.Visible) CodeInput.Focus();
        else EmailInput.Focus();
    }

    private void ResetDriverStep()
    {
        _driverEmail = null;
        _driverToken = null;
        _selectedDriver = null;
        _config = null;
        _event = null;
        _preparation = null;
        _resendTimer.Stop();
        EmailPanel.Visibility = Visibility.Visible;
        EmailInput.Text = string.Empty;
        EmailInput.IsEnabled = true;
        SendCodeBtn.Content = "SEND CODE";
        SendCodeBtn.IsEnabled = true;
        CodePanel.Visibility = Visibility.Collapsed;
        CodeInput.Text = string.Empty;
        CodeStatus.Text = string.Empty;
        DriverPanel.Visibility = Visibility.Collapsed;
        DiscordRow.Visibility = Visibility.Collapsed;
        NewProfilePanel.Visibility = Visibility.Collapsed;
        LeaderboardNameInput.Text = string.Empty;
        InGamePanel.Visibility = Visibility.Collapsed;
        DisplayNameInput.Text = string.Empty;
        DriverStatus.Visibility = Visibility.Collapsed;

        // Own PC: the signed-in driver races; no email code per session.
        if (IsIndividual && _owner is { } owner)
        {
            EmailPanel.Visibility = Visibility.Collapsed;
            DriverPanel.Visibility = Visibility.Visible;
            InGamePanel.Visibility = Visibility.Visible;
            _selectedDriver = new DriverMatch(owner.ProfileId, owner.DisplayName, owner.SrtUsername, null, null);
            DriverPanelTitle.Text = $"✓ {owner.DisplayName}";
            DriverPanelHint.Text = "Signed in on this PC. Your laps go on your SRT profile.";
            DisplayNameInput.Text = owner.DisplayName;
            ShowDiscordRow(owner.DiscordUsername, canLink: true);
        }
    }

    // ── Discord: sign in with a linked Discord, or link one (optional) ───────

    private void ShowDiscordRow(string? discordUsername, bool canLink)
    {
        DiscordRow.Visibility = Visibility.Visible;
        if (discordUsername is not null)
        {
            DiscordStatus.Text = $"Discord: @{discordUsername} ✓";
            LinkDiscordBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            DiscordStatus.Text = "Discord not linked. Optional: link it to sign in with Discord next time.";
            LinkDiscordBtn.Visibility = canLink ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void DiscordSignIn_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        if (_api is not { } api) { SetError(DriverError, "This rig is not paired with SRT."); return; }
        var window = new DiscordWindow("SIGN IN WITH DISCORD",
            "Scan the code with your phone and sign in to Discord there. SRT drivers get their existing profile; new drivers get one made from their Discord.",
            () => api.StartDiscordAsync("sign-in"), poll => api.PollDiscordAsync(poll), sharedPc: true);
        await ShowAsync(window);
        if (window.Result is not { DriverToken: { } token, Driver: { } driver } result) return;

        _driverEmail = null;
        _driverToken = token;
        _resendTimer.Stop();
        EmailPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Collapsed;
        DriverPanel.Visibility = Visibility.Visible;
        InGamePanel.Visibility = Visibility.Visible;
        ShowVerifiedDriver(driver with { DiscordUsername = driver.DiscordUsername ?? result.DiscordUsername });
        DisplayNameInput.Focus();
    }

    private async void LinkDiscord_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        if (_api is not { } api) return;
        string? token = null;
        if (!IsIndividual)
        {
            if (_driverToken is null) { SetError(DriverError, "Sign the driver in with their email first."); return; }
            token = _driverToken;
            // A new email: create the driver first, so Discord is linked to their profile.
            if (_selectedDriver is null)
            {
                if (LeaderboardNameInput.Text.Trim().Length < 2) { SetError(DriverError, "Enter the name to show on the leaderboard first."); return; }
                var typed = DisplayNameInput.Text;
                try { ShowVerifiedDriver(await api.CreateEmailProfileAsync(_driverToken, LeaderboardNameInput.Text.Trim())); }
                catch (Exception ex) { SetError(DriverError, ex.Message); return; }
                DisplayNameInput.Text = typed;
            }
        }
        var window = new DiscordWindow("LINK DISCORD",
            "Optional. Links the driver's Discord to their SRT profile, so they can sign in with Discord next time and their SRT races show up together.",
            () => api.StartDiscordAsync("link", token), poll => api.PollDiscordAsync(poll), sharedPc: !IsIndividual);
        await ShowAsync(window);
        if (window.Result is not { } result) return;
        var linkedName = result.Outcome == "linked" ? result.DiscordUsername : null;
        if (IsIndividual && _owner is { } owner)
        {
            // A merge can move the PC to the driver's older SRT profile: reload who owns it.
            _owner = owner with { DiscordUsername = linkedName ?? owner.DiscordUsername };
            if (result.Driver is { } merged) _owner = _owner with { ProfileId = merged.ProfileId, DisplayName = merged.DisplayName, SrtUsername = merged.SrtUsername ?? _owner.SrtUsername };
            _selectedDriver = new DriverMatch(_owner.ProfileId, _owner.DisplayName, _owner.SrtUsername, _owner.DiscordUsername, null);
            DriverPanelTitle.Text = $"✓ {_owner.DisplayName}";
            ShowDiscordRow(_owner.DiscordUsername, canLink: true);
            if (result.Outcome == "pending-review") DiscordStatus.Text = "Discord: waiting for an SRT admin to confirm your older SRT profile.";
        }
        else if (result.Driver is { } driver)
        {
            var typed = DisplayNameInput.Text;
            ShowVerifiedDriver(driver with { DiscordUsername = linkedName ?? driver.DiscordUsername });
            DisplayNameInput.Text = typed;
            if (result.Outcome == "pending-review") DiscordStatus.Text = "Discord: waiting for an SRT admin to confirm the driver's older SRT profile.";
        }
    }

    private void BackToInit_Click(object sender, RoutedEventArgs e)
    {
        StepDriver.Visibility = Visibility.Collapsed;
        StepInit.Visibility = Visibility.Visible;
        StepLaunch.Visibility = Visibility.Collapsed;
    }

    // ── Email sign-in ────────────────────────────────────────────────────────

    private void EmailInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && EmailInput.IsEnabled) _ = SendCodeAsync();
    }

    private void CodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) VerifyCode_Click(sender, e);
    }

    /// <summary>Send code, or once a code is on its way, start again with a different email.</summary>
    private void SendCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!EmailInput.IsEnabled)
        {
            ResetDriverStep();
            EmailInput.Focus();
            return;
        }
        _ = SendCodeAsync();
    }

    private void ResendCode_Click(object sender, RoutedEventArgs e) => _ = SendCodeAsync();

    private async Task SendCodeAsync()
    {
        DriverError.Visibility = Visibility.Collapsed;
        DriverStatus.Visibility = Visibility.Collapsed;
        if (_api is null) { SetError(DriverError, "This rig is not paired with SRT."); return; }
        var email = _driverEmail ?? EmailAddress.Normalize(EmailInput.Text);
        if (!EmailAddress.IsValid(email)) { SetError(DriverError, "Enter the driver's email address, for example name@gmail.com."); return; }

        SendCodeBtn.IsEnabled = false;
        ResendCodeBtn.IsEnabled = false;
        try
        {
            var sent = await _api.SendEmailCodeAsync(email);
            _driverEmail = email;
            EmailInput.Text = email;
            EmailInput.IsEnabled = false;
            SendCodeBtn.Content = "CHANGE EMAIL";
            if (!string.IsNullOrWhiteSpace(sent.Notice)) EmailNotice.Text = sent.Notice;
            CodePanel.Visibility = Visibility.Visible;
            CodeInput.Text = string.Empty;
            CodeStatus.Text = $"Code sent to {sent.Email}. It works for {Math.Max(1, sent.ExpiresInSeconds / 60)} minutes. Not there? Check spam or promotions.";
            _resendAvailableAt = DateTimeOffset.Now.AddSeconds(Math.Max(0, sent.ResendInSeconds));
            _resendTimer.Start();
            CodeInput.Focus();
        }
        catch (Exception ex)
        {
            SetError(DriverError, ex.Message);
        }
        finally
        {
            SendCodeBtn.IsEnabled = true;
            UpdateResendButton();
        }
    }

    private void UpdateResendButton()
    {
        var wait = (int)Math.Ceiling((_resendAvailableAt - DateTimeOffset.Now).TotalSeconds);
        if (wait > 0)
        {
            ResendCodeBtn.IsEnabled = false;
            ResendCodeBtn.Content = $"RESEND ({wait}s)";
            return;
        }
        _resendTimer.Stop();
        ResendCodeBtn.Content = "RESEND";
        ResendCodeBtn.IsEnabled = _driverEmail is not null && _driverToken is null;
    }

    private async void VerifyCode_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        if (_api is null || _driverEmail is null) { SetError(DriverError, "Send a code to the driver's email first."); return; }

        VerifyCodeBtn.IsEnabled = false;
        try
        {
            var result = await _api.VerifyEmailCodeAsync(_driverEmail, CodeInput.Text);
            _driverToken = result.DriverToken;
            _resendTimer.Stop();
            EmailPanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Collapsed;
            DriverPanel.Visibility = Visibility.Visible;
            InGamePanel.Visibility = Visibility.Visible;
            if (result.Driver is { } driver && !result.NeedsProfile)
            {
                ShowVerifiedDriver(driver);
                DisplayNameInput.Focus();
            }
            else
            {
                _selectedDriver = null;
                DriverPanelTitle.Text = "✓ Email verified  ·  new driver";
                DriverPanelHint.Text = _driverEmail;
                NewProfilePanel.Visibility = Visibility.Visible;
                ShowDiscordRow(null, canLink: true);
                LeaderboardNameInput.Focus();
            }
        }
        catch (Exception ex)
        {
            SetError(DriverError, ex.Message);
        }
        finally
        {
            VerifyCodeBtn.IsEnabled = true;
        }
    }

    private void ShowVerifiedDriver(DriverMatch driver)
    {
        _selectedDriver = driver;
        DriverPanelTitle.Text = $"✓ {driver.DisplayName}";
        DriverPanelHint.Text = _driverEmail is null ? "Signed in with Discord" : $"Email verified  ·  {_driverEmail}";
        NewProfilePanel.Visibility = Visibility.Collapsed;
        ShowDiscordRow(driver.DiscordUsername, canLink: _driverToken is not null);
        if (string.IsNullOrWhiteSpace(DisplayNameInput.Text)) DisplayNameInput.Text = driver.DisplayName;
    }

    /// <summary>New drivers: the in-game name follows the leaderboard name unless staff change it.</summary>
    private void LeaderboardName_TextChanged(object sender, TextChangedEventArgs e) => DisplayNameInput.Text = LeaderboardNameInput.Text.Trim();

    private void ChangeDriver_Click(object sender, RoutedEventArgs e)
    {
        ResetDriverStep();
        DriverError.Visibility = Visibility.Collapsed;
        EmailInput.Focus();
    }

    private async void PreviewJoin_Click(object sender, RoutedEventArgs e)
    {
        DriverError.Visibility = Visibility.Collapsed;
        if (_api is null) { SetError(DriverError, IsIndividual ? "You're not signed in on this PC. Go back and select Sign in." : "This rig is not paired with SRT."); return; }
        if (_selectedEvent is null) { SetError(DriverError, "Go back and choose an event first."); return; }
        if (IsIndividual && _selectedDriver is null) { SetError(DriverError, "Your SRT account hasn't loaded yet. Go back, select Refresh, and try again."); return; }
        if (!IsIndividual && _driverToken is null) { SetError(DriverError, "Sign the driver in first: send a code to their email, then enter it."); return; }
        if (_selectedDriver is null && LeaderboardNameInput.Text.Trim().Length < 2) { SetError(DriverError, "Enter the name to show on the leaderboard."); return; }

        var displayName = DisplayNameInput.Text.Trim();
        if (displayName.Length == 0) { SetError(DriverError, "Enter the in-game display name."); return; }
        if (_credential is not null && SteamAccount.Check(_credential.SteamId) is { IsOk: false } previewSteam) { SetError(DriverError, previewSteam.Message); return; }

        PreviewBtn.IsEnabled = false;
        try
        {
            // A new email: create the driver first, so the session is linked to their profile.
            if (_selectedDriver is null)
            {
                ShowVerifiedDriver(await _api.CreateEmailProfileAsync(_driverToken!, LeaderboardNameInput.Text.Trim()));
                DisplayNameInput.Text = displayName;
            }
            var username = SrtUsername.Normalize(_selectedDriver!.SrtUsername);
            if (!SrtUsername.IsValid(username))
                throw new InvalidOperationException("This driver's SRT profile is missing its handle. Select Change driver and sign in again, or ask an SRT admin.");

            // Always fetch the chosen event just before launch, so admin changes apply immediately.
            _config = await _api.GetConfigAsync(username);
            _ = SendHeartbeatAsync(); // driver check-in
            // A driver can only race on one rig at a time.
            if (_config.DriverBusy is { } busy) throw new InvalidOperationException(busy.Message);
            var evt = _config.FindEvent(_selectedEvent.Id);
            if (evt is null)
            {
                ShowEvents(_config.LiveEvents, null);
                throw new InvalidOperationException($"{_selectedEvent.Name} is no longer live. Select Back and choose another event.");
            }
            _event = evt;
            // The event's mods must be on the PC (installed by the app, or skipped) before the driver can join.
            if (!await EnsureEventContentAsync(evt))
                throw new InvalidOperationException("This event's mods are not all on this PC yet. Select Preview again to install them, or skip the ones already installed.");

            _preparation = BuildPreparation(_config.Rig, evt, SrtUsername.Display(username), displayName);
            var cmPreview = _cmSettings.PreviewPlayerNameChange(_cmSettings.DefaultValuesPath, displayName);

            var text =
                $"Event:         {evt.Name}{Environment.NewLine}" +
                (evt.Track is null && evt.Car is null ? string.Empty : $"Track / car:   {string.Join(" · ", new[] { evt.Track, evt.Car }.Where(v => v is not null))}{Environment.NewLine}") +
                $"Server:        {evt.ServerHost}:{evt.HttpPort}{Environment.NewLine}" +
                $"Driver:        {_selectedDriver.DisplayName}{(_driverEmail is null ? string.Empty : $"  ·  {_driverEmail}")}{Environment.NewLine}" +
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

    private async Task ShowPasswordForActiveSessionAsync(ActiveRigSession active)
    {
        if (_api is null) return;
        try
        {
            _config = await _api.GetConfigAsync();
            ShowPassword((_config.FindEvent(active.EventId) ?? (active.EventId is null ? _config.Event : null))?.ServerPassword);
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
            if (_preparation is null || _config is null || _event is null || _credential is null)
                throw new InvalidOperationException("Preview the session again before launching.");
            var evt = _event;
            // Steam could have been switched since startup; laps are matched to drivers by this account.
            if (SteamAccount.Check(_credential.SteamId) is { IsOk: false } launchSteam)
                throw new InvalidOperationException(launchSteam.Message);
            // Check again at launch: the driver may have been started on another rig since the preview.
            if (_api is not null)
            {
                RigConfig? latest = null;
                try { latest = await _api.GetConfigAsync(_preparation.Request.Driver.SrtUsername); }
                catch (Exception) { /* offline: SRT still refuses a second live session when the start reaches it */ }
                if (latest?.DriverBusy is { } busyAtLaunch) throw new InvalidOperationException(busyAtLaunch.Message);
                if (latest is not null)
                {
                    if (latest.FindEvent(evt.Id) is not { } latestEvent)
                        throw new InvalidOperationException($"{evt.Name} is no longer live. Select Edit driver, then Back, and choose another event.");
                    // Admins may have changed the server or added required content since the preview.
                    if (!await EnsureEventContentAsync(latestEvent))
                        throw new InvalidOperationException("This event's mods are not all on this PC yet. Install them, or skip the ones already installed, then launch again.");
                    evt = _event = latestEvent;
                }
            }

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
                evt.Id,
                _config.Rig.DisplayName,
                IsIndividual ? null : _driverToken);
            _activeSessionStore.Save(session);

            _outbox.Enqueue(IngestMessages.StartSession(_credential.RigId, session));
            new ContentManagerLauncher().Launch(evt.ToJoinTarget());

            ShowLaunchStep(
                $"✓ Content Manager launched.{Environment.NewLine}" +
                $"Event:  {evt.Name}{Environment.NewLine}" +
                $"Driver: {displayName}{(_driverEmail is null ? string.Empty : $" ({_driverEmail})")}{Environment.NewLine}{Environment.NewLine}" +
                $"AC + CM names updated. Backups saved.{Environment.NewLine}" +
                $"AC backup:  {raceBackup}{Environment.NewLine}" +
                $"CM backup:  {cmBackup}{Environment.NewLine}{Environment.NewLine}" +
                "When the driver is finished, select End Session & Restore.",
                sessionActive: true);
            ShowPassword(evt.ServerPassword);
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
            PasswordPanel.Visibility = Visibility.Collapsed;
            LapPanel.Visibility = Visibility.Collapsed;
            ShowDriverStep();
            var lapCount = _laps.Count;
            var summary = $"Session ended with {lapCount} lap(s) recorded. Rig name restored" + (outcome.FallbackNameUsed is null ? " from backup." : $" to \"{outcome.FallbackNameUsed}\" (backup was missing).");
            summary += flush is { Remaining: > 0 }
                ? $" {flush.Remaining} item(s) are waiting for internet and will sync automatically."
                : " All session data is synced with SRT.";
            ShowDriverStatus(summary);
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
                inSession: _activeSessionStore.Load() is not null, steamOk, _contentMissing));
        }
        catch { /* Offline: the next check-in will catch up. Admin shows the rig as offline meanwhile. */ }
    }

    /// <param name="config">Already fetched (start-up loads the events and the release in one request).</param>
    private async Task CheckForUpdateAsync(bool autoInstall, RigConfig? config = null)
    {
        if (_api is null || _installingUpdate) return;
        try
        {
            var release = (config ?? await _api.GetConfigAsync()).App;
            _pendingRelease = release is not null && AppVersion.IsNewer(release.Version, AppVersion.Current) ? release : null;
        }
        catch { return; }

        UpdateBtn.Visibility = _pendingRelease is null ? Visibility.Collapsed : Visibility.Visible;
        if (_pendingRelease is null) return;
        UpdateBtn.Content = $"UPDATE TO V{_pendingRelease.Version}";
        // Automatic updates can be turned off in Settings; the Update button still appears.
        if (autoInstall && _settings.AutoUpdate && CanUpdateNow() && !IsDevelopmentBuild) await InstallUpdateAsync();
    }

    /// <summary>
    /// Builds run from source (dotnet run / Visual Studio, Debug) never update themselves: they report version 1.0.0, so every
    /// published release looks newer and would replace the code being tested. The update button still works if clicked.
    /// </summary>
#if DEBUG
    private const bool IsDevelopmentBuild = true;
#else
    private const bool IsDevelopmentBuild = false;
#endif

    /// <summary>Never during a session or while staff are setting up a driver.</summary>
    private bool CanUpdateNow() =>
        _activeSessionStore.Load() is null && StepLaunch.Visibility != Visibility.Visible &&
        (StepInit.Visibility == Visibility.Visible || (!IsIndividual && _driverEmail is null && EmailInput.Text.Length == 0));

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

    private SessionPreparation BuildPreparation(RigInfo rig, EventServerConfig evt, string srtUsername, string displayName)
    {
        var rigConfiguration = new RigSessionConfiguration(
            rig.Id,
            evt.Id,
            ContentManagerIntegrationStatus.Verified,
            evt.ToJoinTarget());
        var request = new SessionRequest(new SessionDriver(srtUsername, displayName));
        return new SessionPreparationService(_rigProfileService).Prepare(rigConfiguration, request, RequirePaths().RaceIniPath);
    }

    private AssettoCorsaPaths RequirePaths() =>
        _acPaths ?? AssettoCorsaPaths.DetectOnThisPc(_acFolders.Load()).Paths
        ?? throw new InvalidOperationException("Assetto Corsa installation not found. Return to the initialisation screen.");

    private void ShowDriverStatus(string message)
    {
        DriverStatus.Text = message;
        DriverStatus.Visibility = Visibility.Visible;
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
