using Srt.EventCompanion.Core;
using System.Net;
using System.Text.Json.Nodes;

var tests = new (string Name, Action Test)[]
{
    ("reads existing AC driver names", ReadsExistingDriverNames),
    ("updates only profile name fields and makes a backup", UpdatesNamesAndMakesBackup),
    ("rejects unsafe driver names", RejectsUnsafeDriverNames),
    ("detects only complete Assetto Corsa installations", DetectsCompleteInstallation),
    ("finds AC in other Steam libraries and says what is missing", FindsAcInSteamLibraries),
    ("accepts manually chosen AC folders, forgiving near misses", AcceptsManualAcFolders),
    ("blocks sessions until Content Manager is verified", BlocksUnverifiedContentManager),
    ("prepares a session", PreparesSession),
    ("enforces the session state sequence", EnforcesSessionStateSequence),
    ("creates the verified Content Manager join URI", CreatesContentManagerJoinUri),
    ("updates the Content Manager player name with a backup", UpdatesContentManagerPlayerName),
    ("persists and restores an active rig session", PersistsAndRestoresActiveRigSession),
    ("restores the rig name from SRT when a backup is missing", RestoresRigNameWhenBackupMissing),
    ("loads active sessions saved by the previous version", LoadsPreviousActiveSessionFormat),
    ("accepts Discord-style SRT usernames", AcceptsDiscordStyleUsernames),
    ("outbox sends in order and keeps messages while offline", OutboxKeepsOrderWhileOffline),
    ("outbox drops messages SRT refuses", OutboxDropsRefusedMessages),
    ("retry spacing doubles and is capped", RetryBackoffDoublesAndCaps),
    ("rig API sends the rig key in X-Rig-Token and reads the live event", RigApiReadsLiveEvent),
    ("rig API separates refusals from network failures", RigApiClassifiesErrors),
    ("pairing errors say what is wrong and what to do", PairingErrorsAreActionable),
    ("requests to SRT carry a Content-Length (Azure rejects chunked bodies)", RequestsHaveContentLength),
    ("detects completed laps from AC telemetry", DetectsCompletedLaps),
    ("waits for AC to publish the lap time", WaitsForLapTime),
    ("flags laps through the pits or off track", FlagsPitAndOffTrackLaps),
    ("starts again when the server restarts the session", ResetsOnNewSession),
    ("keeps a lap finished just before End Session", FlushesPendingLap),
    ("lap messages carry review flags for SRT", LapMessageCarriesFlags),
    ("reads the signed-in Steam account as a SteamID64", ConvertsSteamAccountId),
    ("Steam check says what to do when the account is wrong", SteamCheckExplainsProblem),
    ("pairing needs Steam signed in, before using the code", PairingNeedsSteam),
    ("filters this centre's drivers by any part of a name", FiltersCentreDrivers),
    ("keeps at most five favourite drivers", KeepsFiveFavourites),
    ("reads the centre's recent drivers from SRT", ReadsRecentDrivers),
    ("asks SRT whether the driver is racing on another rig", ReadsDriverBusy),
    ("places zip files exactly like the admin page does", PlansPackagesLikeTheWebsite),
    ("refuses to write outside the Assetto Corsa folder", RefusesUnsafePaths),
    ("switches Python apps on in python.ini", EnablesPythonApps),
    ("tells installed, skipped, found and missing content apart", ChecksContentStatus),
    ("downloads, verifies and installs a package, backing up what it replaces", InstallsPackages),
    ("installs any file type, backing up a folder it replaces", InstallsAnyFileTypeWithBackup),
    ("deletes a download that fails its checksum", RejectsTamperedDownload),
    ("reads required content from the rig config and sends readiness", ReadsRequiredContent),
    ("only installs releases newer than the running version", ComparesVersions),
    ("installs only downloads that match the published SHA-256", VerifiesUpdateDownloads),
    ("heartbeat reports version, backlog, state and Steam", BuildsHeartbeat),
    ("reads the published release from the rig config", ReadsPublishedRelease),
    ("checks driver emails the way SRT does", ChecksDriverEmails),
    ("signs a driver in with an emailed code", SignsDriverInWithEmailCode),
    ("start-session carries the driver's email sign-in", StartSessionCarriesDriverToken),
    ("an expired sign-in shows SRT's message, not a re-pair prompt", ExpiredSignInKeepsMessage),
    ("reads every live event and finds the chosen one", ReadsEveryLiveEvent),
    ("picks the last event used, else the newest", PicksDefaultEvent),
    ("keeps setup choices; rigs from 1.0.x skip setup", KeepsAppSettings),
    ("reads an individual's PC and its owner from the rig config", ReadsIndividualRig),
    ("signs an individual up with an emailed code", SignsIndividualUp),
    ("starts Discord sign-in and reads the result once it's done", RunsDiscordSignIn),
    ("watches the game and ends a session only after it stays closed", WatchesGameAndCountsDown),
    ("waits for a slow game start while Content Manager is open", WaitsForSlowGameStart),
    ("holds a session open until the game runs again", HoldsSessionOpen),
    ("builds the direct join link with car, name and password", BuildsDirectJoinLink),
    ("reads the server's info and slots and picks this PC's car", PicksReservedSlot),
    ("says so when the Steam account has no car on the server", NoSlotForSteamAccount),
    ("keeps a rejoin's laps apart from the first game's", RejoinLapsHaveTheirOwnIds),
    ("reads track IDs the way the SRT servers report them", ReadsServerTrackIds),
    ("shows each event's series, round and race day in the picker", ShowsSeriesAndRoundInPicker)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void ReadsExistingDriverNames()
{
    using var fixture = new RaceIniFixture("[CAR_0]\nDRIVER_NAME=Old Name\n\n[REMOTE]\nNAME=Old Name\n");
    var profile = new RigProfileService().Read(fixture.Path);
    AssertEqual("Old Name", profile.OfflineDriverName);
    AssertEqual("Old Name", profile.MultiplayerDriverName);
}

static void UpdatesNamesAndMakesBackup()
{
    const string original = "[CAR_0]\nDRIVER_NAME=Old Name\nMODEL=car\n\n[REMOTE]\nNAME=Old Name\nSERVER_IP=127.0.0.1\n";
    using var fixture = new RaceIniFixture(original);
    var service = new RigProfileService();

    var preview = service.PreviewDriverNameChange(fixture.Path, "Nikhil Joji");
    AssertEqual("Old Name", preview.Current.OfflineDriverName);
    AssertEqual("Nikhil Joji", preview.Proposed.MultiplayerDriverName);

    var backup = service.ApplyDriverNameChange(fixture.Path, "Nikhil Joji", new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
    AssertEqual(original.Replace("\n", Environment.NewLine), File.ReadAllText(backup));

    var updated = service.Read(fixture.Path);
    AssertEqual("Nikhil Joji", updated.OfflineDriverName);
    AssertEqual("Nikhil Joji", updated.MultiplayerDriverName);
    AssertTrue(File.ReadAllText(fixture.Path).Contains("SERVER_IP=127.0.0.1", StringComparison.Ordinal));
}

static void RejectsUnsafeDriverNames()
{
    using var fixture = new RaceIniFixture("[CAR_0]\n");
    AssertThrows(() => new RigProfileService().PreviewDriverNameChange(fixture.Path, "First\nLast"));
}

static void FindsAcInSteamLibraries()
{
    var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"244210\"\t\t\"12345\"\n\t\t}\n\t}\n}";
    var libraries = AssettoCorsaPaths.ParseSteamLibraries(vdf);
    AssertEqual("2", libraries.Count.ToString());
    AssertEqual(@"D:\SteamLibrary", libraries[1]);

    using var fixture = new AssettoCorsaFixture();
    var docs = Path.Combine(fixture.UserProfileDirectory, "Documents", "Assetto Corsa");
    var missingGame = AssettoCorsaPaths.Diagnose(new[] { docs }, new[] { Path.Combine(fixture.UserProfileDirectory, "nope") });
    AssertTrue(missingGame.Paths is null && missingGame.Problem!.Contains("game folder"));
    var missingSettings = AssettoCorsaPaths.Diagnose(new[] { Path.Combine(fixture.UserProfileDirectory, "OneDrive", "Assetto Corsa") }, new[] { fixture.InstallDirectory });
    AssertTrue(missingSettings.Paths is null && missingSettings.Problem!.Contains("race.ini"));
    var found = AssettoCorsaPaths.Diagnose(new[] { Path.Combine(fixture.UserProfileDirectory, "OneDrive", "Assetto Corsa"), docs }, new[] { "", fixture.InstallDirectory });
    AssertEqual(docs, found.Paths!.UserDocumentsDirectory);
}

static void AcceptsManualAcFolders()
{
    using var fixture = new AssettoCorsaFixture();
    var docs = Path.Combine(fixture.UserProfileDirectory, "Documents", "Assetto Corsa");
    // Game folder: the exact folder, a folder inside it, or its parent all resolve; somewhere else doesn't.
    AssertEqual(fixture.InstallDirectory, AssettoCorsaPaths.ResolveGameFolder(fixture.InstallDirectory));
    Directory.CreateDirectory(Path.Combine(fixture.InstallDirectory, "content"));
    AssertEqual(fixture.InstallDirectory, AssettoCorsaPaths.ResolveGameFolder(Path.Combine(fixture.InstallDirectory, "content")));
    AssertTrue(AssettoCorsaPaths.ResolveGameFolder(fixture.UserProfileDirectory) is null);
    // Settings folder: "Assetto Corsa", Documents itself, or the cfg folder all resolve.
    AssertEqual(docs, AssettoCorsaPaths.ResolveSettingsFolder(docs));
    AssertEqual(docs, AssettoCorsaPaths.ResolveSettingsFolder(Path.Combine(fixture.UserProfileDirectory, "Documents")));
    AssertEqual(docs, AssettoCorsaPaths.ResolveSettingsFolder(Path.Combine(docs, "cfg")));
    AssertTrue(AssettoCorsaPaths.ResolveSettingsFolder(fixture.InstallDirectory) is null);

    // Chosen folders are remembered.
    var store = new AcFolderOverrideStore(Path.Combine(fixture.UserProfileDirectory, "ac-folders.json"));
    AssertTrue(store.Load().InstallDirectory is null);
    store.Save(new AcFolderOverrides(fixture.InstallDirectory, docs));
    AssertEqual(fixture.InstallDirectory, store.Load().InstallDirectory);
    store.Clear();
    AssertTrue(store.Load().DocumentsDirectory is null);
}

static void DetectsCompleteInstallation()
{
    using var fixture = new AssettoCorsaFixture();
    var paths = AssettoCorsaPaths.Detect(fixture.UserProfileDirectory, new[] { fixture.InstallDirectory });
    AssertTrue(paths is not null);
    AssertEqual(Path.Combine(fixture.UserProfileDirectory, "Documents", "Assetto Corsa"), paths!.UserDocumentsDirectory);
}

static void BlocksUnverifiedContentManager()
{
    using var fixture = new RaceIniFixture("[CAR_0]\nDRIVER_NAME=Old Name\n\n[REMOTE]\nNAME=Old Name\n");
    var configuration = new RigSessionConfiguration("KOC-R01", "krw-2026", ContentManagerIntegrationStatus.Unverified, null);
    var request = new SessionRequest(new SessionDriver("@nikhiljoji", "Nikhil Joji"));
    AssertThrowsInvalidOperation(() => new SessionPreparationService(new RigProfileService()).Prepare(configuration, request, fixture.Path));
}

static void PreparesSession()
{
    using var fixture = new RaceIniFixture("[CAR_0]\nDRIVER_NAME=Old Name\n\n[REMOTE]\nNAME=Old Name\n");
    var configuration = new RigSessionConfiguration("KOC-R01", "krw-2026", ContentManagerIntegrationStatus.Verified, new OnlineJoinTarget(IPAddress.Parse("127.0.0.1"), 8027));
    var service = new SessionPreparationService(new RigProfileService());

    var preparation = service.Prepare(configuration, new SessionRequest(new SessionDriver("@nikhiljoji", "Nikhil Joji")), fixture.Path);
    AssertEqual("Nikhil Joji", preparation.ProfileChange.Proposed.OfflineDriverName);
    AssertEqual(CompanionSessionState.Prepared.ToString(), preparation.State.ToString());
}

static void CreatesContentManagerJoinUri()
{
    var uri = new OnlineJoinTarget(IPAddress.Parse("15.235.182.31"), 8027).ToUri();
    AssertEqual("acmanager://race/online/join?httpPort=8027&ip=15.235.182.31", uri.AbsoluteUri);
    AssertThrowsArgumentOutOfRange(() => new OnlineJoinTarget(IPAddress.Loopback, 0).ToUri());
}

static void UpdatesContentManagerPlayerName()
{
    // Compressed the way Content Manager writes it, including a back-reference inside the name ("Test dr" + "ive" + "r").
    using var fixture = new ValuesDataFixture(CmValues.CompressedWithBackReference());
    var service = new ContentManagerSettingsService();
    AssertEqual("Test driver", service.ReadPlayerName(fixture.Path));
    var backup = service.ApplyPlayerNameChange(fixture.Path, "Nikhil Joji", new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
    AssertEqual("Test driver", service.ReadPlayerName(backup));
    AssertEqual("Nikhil Joji", service.ReadPlayerName(fixture.Path));
    AssertEqual("Nikhil Joji", service.ReadValue(fixture.Path, ContentManagerSettingsService.PlayerNameOnlineKey));
    AssertEqual("1", service.ReadValue(fixture.Path, "Settings.Other"));            // other settings kept
    AssertTrue(File.ReadAllBytes(fixture.Path)[0] == 11); // still CM LZF format
    AssertEqual("Nikhil Joji", CmValues.DecompressLikeContentManager(fixture.Path)); // a reference LZF reader agrees

    // A name with characters CM escapes, and a plain-text file, both round-trip.
    using var plain = new ValuesDataFixture(CmValues.Plain("Old"));
    service.ApplyPlayerNameChange(plain.Path, "A\\B Racer", DateTimeOffset.UtcNow);
    AssertEqual("A\\B Racer", service.ReadPlayerName(plain.Path));
}

static void PersistsAndRestoresActiveRigSession()
{
    using var fixture = new ActiveSessionFixture();
    var raceIniPath = System.IO.Path.Combine(fixture.Directory, "race.ini");
    var valuesPath = System.IO.Path.Combine(fixture.Directory, "Values.data");
    var raceBackupPath = $"{raceIniPath}.backup";
    var valuesBackupPath = $"{valuesPath}.backup";
    File.WriteAllText(raceIniPath, "new race profile");
    File.WriteAllText(valuesPath, "new content manager profile");
    File.WriteAllText(raceBackupPath, "original race profile");
    File.WriteAllText(valuesBackupPath, "original content manager profile");
    var session = new ActiveRigSession(Guid.NewGuid(), "@driver", "Driver", raceIniPath, raceBackupPath, valuesPath, valuesBackupPath, DateTimeOffset.UtcNow);
    var store = new ActiveSessionStore(System.IO.Path.Combine(fixture.Directory, "active-session.json"));

    store.Save(session);
    AssertEqual(session.SessionId.ToString(), store.Load()!.SessionId.ToString());
    new SessionRestorationService().Restore(session);
    AssertEqual("original race profile", File.ReadAllText(raceIniPath));
    AssertEqual("original content manager profile", File.ReadAllText(valuesPath));
    store.Clear();
    AssertTrue(store.Load() is null);
}

static void EnforcesSessionStateSequence()
{
    var lifecycle = new SessionLifecycle();
    AssertThrowsInvalidOperation(lifecycle.RequestLaunch);
    lifecycle.MarkPrepared();
    lifecycle.RequestLaunch();
    lifecycle.MarkRunning();
    lifecycle.Complete();
    AssertEqual(CompanionSessionState.Completed.ToString(), lifecycle.State.ToString());
}

static void RestoresRigNameWhenBackupMissing()
{
    using var fixture = new ActiveSessionFixture();
    var raceIniPath = System.IO.Path.Combine(fixture.Directory, "race.ini");
    var valuesPath = System.IO.Path.Combine(fixture.Directory, "Values.data");
    File.WriteAllText(raceIniPath, "[CAR_0]\nDRIVER_NAME=Driver\n\n[REMOTE]\nNAME=Driver\n");
    File.WriteAllBytes(valuesPath, CmValues.Plain("Driver"));
    var valuesBackup = $"{valuesPath}.backup";
    File.WriteAllBytes(valuesBackup, CmValues.Plain("KOC Rig 01"));
    var session = new ActiveRigSession(Guid.NewGuid(), "@driver", "Driver",
        raceIniPath, $"{raceIniPath}.missing", valuesPath, valuesBackup, DateTimeOffset.UtcNow, "round-1", "KOC Rig 01");

    var outcome = new SessionRestorationService().Restore(session, null, new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

    AssertTrue(outcome.ContentManagerFromBackup);
    AssertTrue(!outcome.RaceIniFromBackup);
    AssertEqual("KOC Rig 01", outcome.FallbackNameUsed);
    AssertEqual("KOC Rig 01", new RigProfileService().Read(raceIniPath).MultiplayerDriverName);
    AssertEqual("KOC Rig 01", new ContentManagerSettingsService().ReadPlayerName(valuesPath));

    var noName = session with { RigDefaultName = null };
    AssertThrowsAny(() => new SessionRestorationService().Restore(noName, null, DateTimeOffset.UtcNow));
}

static void LoadsPreviousActiveSessionFormat()
{
    using var fixture = new ActiveSessionFixture();
    var path = System.IO.Path.Combine(fixture.Directory, "active-session.json");
    File.WriteAllText(path, "{\"SessionId\":\"0b9f2a62-4f6e-4d43-9a57-5d0c1d7c9e11\",\"Stage\":1,\"SrtUsername\":\"@a_b\",\"DisplayName\":\"A\"," +
        "\"RaceIniPath\":\"r\",\"RaceIniBackupPath\":\"rb\",\"ContentManagerValuesPath\":\"v\",\"ContentManagerBackupPath\":\"vb\",\"StartedAt\":\"2026-11-13T10:00:00+05:30\"}");
    var session = new ActiveSessionStore(path).Load()!;
    AssertEqual("@a_b", session.SrtUsername);
    AssertTrue(session.EventId is null && session.RigDefaultName is null);
}

static void AcceptsDiscordStyleUsernames()
{
    using var fixture = new RaceIniFixture("[CAR_0]\nDRIVER_NAME=Old Name\n\n[REMOTE]\nNAME=Old Name\n");
    var configuration = new RigSessionConfiguration("KOC-R01", "round-1", ContentManagerIntegrationStatus.Verified, new OnlineJoinTarget(IPAddress.Parse("127.0.0.1"), 8027));
    var service = new SessionPreparationService(new RigProfileService());
    service.Prepare(configuration, new SessionRequest(new SessionDriver("@nikhil.joji", "Nikhil")), fixture.Path);
    AssertThrows(() => service.Prepare(configuration, new SessionRequest(new SessionDriver("@a", "A")), fixture.Path));
    AssertThrows(() => service.Prepare(configuration, new SessionRequest(new SessionDriver("@bad..dots", "B")), fixture.Path));
    AssertEqual("nikhil.joji", SrtUsername.Normalize(" @Nikhil.Joji "));
}

static void OutboxKeepsOrderWhileOffline()
{
    using var fixture = new ActiveSessionFixture();
    var store = new OutboxStore(fixture.Directory);
    store.Enqueue(new JsonObject { ["action"] = "start-session" });
    store.Enqueue(new JsonObject { ["action"] = "record-attempt" });
    store.Enqueue(new JsonObject { ["action"] = "end-session" });

    var sent = new List<string>();
    var online = false;
    var sender = new OutboxSender(store, (body, _) =>
    {
        if (!online && body["action"]!.GetValue<string>() == "record-attempt") throw new RigApiException("offline", null);
        sent.Add(body["action"]!.GetValue<string>());
        return Task.CompletedTask;
    });

    var first = sender.FlushAsync().GetAwaiter().GetResult();
    AssertEqual("1", first.Sent.ToString());
    AssertEqual("2", first.Remaining.ToString());
    AssertEqual("record-attempt", new OutboxStore(fixture.Directory).Pending()[0].Action);

    online = true;
    var second = sender.FlushAsync().GetAwaiter().GetResult();
    AssertEqual("0", second.Remaining.ToString());
    AssertEqual("start-session,record-attempt,end-session", string.Join(",", sent));
}

static void OutboxDropsRefusedMessages()
{
    using var fixture = new ActiveSessionFixture();
    var store = new OutboxStore(fixture.Directory);
    store.Enqueue(new JsonObject { ["action"] = "record-attempt" });
    store.Enqueue(new JsonObject { ["action"] = "end-session" });
    var sender = new OutboxSender(store, (body, _) =>
        body["action"]!.GetValue<string>() == "record-attempt"
            ? throw new RigApiException("Lap was captured after the session ended.", HttpStatusCode.Conflict)
            : Task.CompletedTask);

    var result = sender.FlushAsync().GetAwaiter().GetResult();
    AssertEqual("1", result.Sent.ToString());
    AssertEqual("1", result.Rejected.ToString());
    AssertEqual("0", result.Remaining.ToString());
    AssertTrue(Directory.GetFiles(System.IO.Path.Combine(fixture.Directory, "rejected"), "*.json").Length == 1);
}

static void RetryBackoffDoublesAndCaps()
{
    var backoff = new RetryBackoff();
    var now = DateTimeOffset.UtcNow;
    AssertEqual("00:00:15", backoff.Failed(now).ToString());
    AssertEqual("00:00:30", backoff.Failed(now).ToString());
    for (var i = 0; i < 10; i++) backoff.Failed(now);
    AssertEqual("00:05:00", backoff.Failed(now).ToString());
    backoff.Succeeded();
    AssertTrue(backoff.NextAttemptAt == DateTimeOffset.MinValue);
}

static void RigApiReadsLiveEvent()
{
    HttpRequestMessage? seen = null;
    var http = new HttpClient(new StubHandler(request =>
    {
        seen = request;
        return Json(HttpStatusCode.OK, "{\"rig\":{\"id\":\"KOC-R01\",\"displayName\":\"Rig 01\",\"centreId\":\"kochi\",\"centreName\":\"Kochi\"}," +
            "\"event\":{\"id\":\"round-1\",\"name\":\"Round 1\",\"serverHost\":\"15.235.182.31\",\"httpPort\":8027,\"serverPassword\":\"pit lane\",\"track\":null,\"car\":null}}");
    }));
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "token-123", "76561198000000000"), http);

    var config = api.GetConfigAsync().GetAwaiter().GetResult();

    AssertEqual("token-123", seen!.Headers.GetValues("X-Rig-Token").Single());
    AssertTrue(seen.Headers.Authorization is null);
    AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-config?rigId=KOC-R01", seen.RequestUri!.AbsoluteUri);
    AssertEqual("Rig 01", config.Rig.DisplayName);
    AssertEqual("pit lane", config.Event!.ServerPassword);
    AssertEqual("acmanager://race/online/join?httpPort=8027&ip=15.235.182.31", config.Event.ToJoinTarget().ToUri().AbsoluteUri);
}

static void RigApiClassifiesErrors()
{
    var credential = new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "token", "76561198000000000");
    var refused = new RigApiClient(credential, new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Conflict, "{\"error\":\"@nikhil is already taken by another SRT driver.\"}"))));
    try { refused.CreateDriverAsync("nikhil", "Nikhil").GetAwaiter().GetResult(); throw new InvalidOperationException("Expected refusal."); }
    catch (RigApiException exception)
    {
        AssertTrue(exception.IsPermanent);
        AssertEqual("@nikhil is already taken by another SRT driver.", exception.Message);
    }

    var offline = new RigApiClient(credential, new HttpClient(new StubHandler(_ => throw new HttpRequestException("no route"))));
    try { offline.PostIngestAsync(new JsonObject()).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure."); }
    catch (RigApiException exception) { AssertTrue(!exception.IsPermanent && exception.StatusCode is null); }

    var serverError = new RigApiClient(credential, new HttpClient(new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"))));
    try { serverError.PostIngestAsync(new JsonObject()).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure."); }
    catch (RigApiException exception) { AssertTrue(!exception.IsPermanent); }
}

static void PairingErrorsAreActionable()
{
    PairingException Fail(Func<HttpRequestMessage, HttpResponseMessage> respond, string code = "abcd-efgh-jkmn")
    {
        try { new RigPairingService(new HttpClient(new StubHandler(respond))).PairAsync(code, "76561198000000000").GetAwaiter().GetResult(); }
        catch (PairingException exception) { return exception; }
        throw new InvalidOperationException("Expected PairingException.");
    }

    var expired = Fail(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"That code has expired (codes last 10 minutes). Ask an SRT admin for a new pairing code.\"}"));
    AssertEqual("That code has expired (codes last 10 minutes). Ask an SRT admin for a new pairing code.", expired.Message);
    AssertTrue(!expired.ContactSupport);

    var offline = Fail(_ => throw new HttpRequestException("no route"));
    AssertEqual("This PC can't reach SRT.", offline.Message);
    AssertTrue(!offline.ContactSupport);

    AssertTrue(Fail(_ => Json(HttpStatusCode.InternalServerError, "<html>oops</html>")).ContactSupport);
    AssertTrue(Fail(_ => Json(HttpStatusCode.NotFound, "")).ContactSupport);
    AssertEqual("No pairing code entered.", Fail(_ => Json(HttpStatusCode.OK, "{}"), "  - ").Message);

    string? sent = null;
    var credential = new RigPairingService(new HttpClient(new StubHandler(request =>
    {
        sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, "{\"rigId\":\"KOC-R01\",\"rigToken\":\"t\",\"steamId\":\"76561198000000000\"}");
    }))).PairAsync(" abcd efgh-jkmn ", "76561198000000000").GetAwaiter().GetResult();
    AssertEqual("{\"pairingCode\":\"abcdefghjkmn\",\"steamId\":\"76561198000000000\"}", sent);
    AssertEqual("KOC-R01", credential.RigId);
}

static void RequestsHaveContentLength()
{
    var lengths = new List<long?>();
    var handler = new StubHandler(request =>
    {
        // Mirrors what goes on the wire: no length means HttpClient would stream the body chunked.
        lengths.Add(request.Content!.Headers.ContentLength);
        return Json(HttpStatusCode.OK, "{\"rigId\":\"KOC-R01\",\"rigToken\":\"t\",\"steamId\":\"76561198000000000\",\"driver\":{\"profileId\":1,\"displayName\":\"A\"}}");
    });
    new RigPairingService(new HttpClient(handler)).PairAsync("ABCD-EFGH-JKMN", "76561198000000000").GetAwaiter().GetResult();
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "t", "76561198000000000"), new HttpClient(handler));
    api.PostIngestAsync(new JsonObject { ["action"] = "end-session" }).GetAwaiter().GetResult();
    api.CreateDriverAsync("new.driver", "New Driver").GetAwaiter().GetResult();
    AssertTrue(lengths.Count == 3 && lengths.All(length => length is > 0));
}

static AcTelemetrySample Live(int laps, int lastMs, bool inPit = false, int tyresOut = 0, AcStatus status = AcStatus.Live) =>
    new(status, 2, laps, lastMs, 0, inPit, tyresOut, "ks_vallelunga", "ks_mazda_mx5_cup");

static void DetectsCompletedLaps()
{
    var detector = new LapDetector();
    var t = DateTimeOffset.UtcNow;
    AssertTrue(detector.Observe(Live(0, 0), t) is null);              // joined the server
    AssertTrue(detector.Observe(Live(0, 0), t.AddSeconds(30)) is null);
    var lap1 = detector.Observe(Live(1, 94321), t.AddSeconds(95));
    AssertEqual("1", lap1!.LapNumber.ToString());
    AssertEqual("94321", lap1.LapTimeMs.ToString());
    AssertEqual("1:34.321", LapTimeFormat.Format(lap1.LapTimeMs));
    AssertEqual("lap-1-94321", lap1.SourceAttemptId);
    AssertEqual("race", lap1.SessionTypeName);
    AssertTrue(detector.Observe(Live(1, 94321), t.AddSeconds(96)) is null);   // no double counting
    AssertEqual("2", detector.Observe(Live(2, 93007), t.AddSeconds(190))!.LapNumber.ToString());
    AssertTrue(detector.Observe(Live(2, 93007, status: AcStatus.Pause), t.AddSeconds(200)) is null);
}

static void WaitsForLapTime()
{
    var detector = new LapDetector();
    var t = DateTimeOffset.UtcNow;
    detector.Observe(Live(3, 95000), t);
    // Counter moves first, time still shows the previous lap: wait.
    AssertTrue(detector.Observe(Live(4, 95000), t.AddSeconds(90)) is null);
    var lap = detector.Observe(Live(4, 91234), t.AddSeconds(90.05));
    AssertEqual("91234", lap!.LapTimeMs.ToString());
    AssertEqual("4", lap.LapNumber.ToString());
}

static void FlagsPitAndOffTrackLaps()
{
    var detector = new LapDetector();
    var t = DateTimeOffset.UtcNow;
    detector.Observe(Live(0, 0, inPit: true), t);                 // out-lap starts in the pits
    var outLap = detector.Observe(Live(1, 120000), t.AddSeconds(120));
    AssertTrue(outLap!.TouchedPits);
    detector.Observe(Live(1, 120000, tyresOut: 4), t.AddSeconds(150)); // all four wheels off
    var wide = detector.Observe(Live(2, 93000), t.AddSeconds(213));
    AssertTrue(!wide!.TouchedPits && wide.MaxTyresOut == 4);
    var clean = detector.Observe(Live(3, 92500), t.AddSeconds(306));
    AssertTrue(!clean!.TouchedPits && clean.MaxTyresOut == 0);
}

static void ResetsOnNewSession()
{
    var detector = new LapDetector();
    var t = DateTimeOffset.UtcNow;
    detector.Observe(Live(5, 93000), t);
    AssertTrue(detector.Observe(Live(0, 0), t.AddSeconds(10)) is null);  // server moved practice -> race
    AssertEqual("1", detector.Observe(Live(1, 95500), t.AddSeconds(105))!.LapNumber.ToString());
    detector.Observe(Live(1, 95500, status: AcStatus.Off), t.AddSeconds(110)); // AC closed
    AssertTrue(detector.Observe(Live(7, 90000), t.AddSeconds(200)) is null);    // rejoined: new baseline, no phantom lap
}

static void FlushesPendingLap()
{
    var detector = new LapDetector();
    var t = DateTimeOffset.UtcNow;
    detector.Observe(Live(2, 93000), t);
    AssertTrue(detector.Observe(Live(3, 93000), t.AddSeconds(92)) is null);   // crossed the line, time not yet published
    var lap = detector.FlushPending(Live(3, 92100), t.AddSeconds(92.2));
    AssertEqual("92100", lap!.LapTimeMs.ToString());
    AssertTrue(detector.FlushPending(Live(3, 92100), t.AddSeconds(93)) is null);
}

static void LapMessageCarriesFlags()
{
    var lap = new CompletedLap(4, 91234, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), true, 3, 0, "ks_vallelunga", "ks_mazda_mx5_cup");
    var body = IngestMessages.Lap("KOC-R01", Guid.Parse("0b9f2a62-4f6e-4d43-9a57-5d0c1d7c9e11"), lap);
    AssertEqual("record-attempt", body["action"]!.GetValue<string>());
    AssertEqual("lap-4-91234", body["sourceAttemptId"]!.GetValue<string>());
    AssertTrue(body["valid"] is null);
    AssertEqual("practice", body["payload"]!["acSession"]!.GetValue<string>());
    AssertTrue(body["payload"]!["touchedPits"]!.GetValue<bool>());
    AssertEqual("2026-11-13T09:00:00.0000000+00:00", body["capturedAt"]!.GetValue<string>());
}

static void ConvertsSteamAccountId()
{
    AssertEqual("76561198354778832", SteamAccount.FromAccountId(394513104));
    AssertTrue(SteamAccount.FromAccountId(0) is null);   // Steam running but nobody signed in
    AssertTrue(SteamAccount.FromAccountId(-1) is null);
}

static void SteamCheckExplainsProblem()
{
    var ok = SteamCheck.Evaluate("76561198354778832", "76561198354778832");
    AssertTrue(ok.IsOk);
    AssertEqual("✓ Matches (…8832)", ok.Label);

    var wrong = SteamCheck.Evaluate("76561198354778832", "76561198000000001");
    AssertEqual("Mismatch", wrong.Status.ToString());
    AssertTrue(wrong.Message.Contains("76561198000000001") && wrong.Message.Contains("76561198354778832") && wrong.Message.Contains("SRT Labs"));

    var none = SteamCheck.Evaluate("76561198354778832", null);
    AssertEqual("NotSignedIn", none.Status.ToString());
    AssertTrue(none.Message.StartsWith("Steam is not running or not signed in", StringComparison.Ordinal));
}

static void PairingNeedsSteam()
{
    var called = false;
    var service = new RigPairingService(new HttpClient(new StubHandler(_ => { called = true; return Json(HttpStatusCode.OK, "{}"); })));
    try { service.PairAsync("ABCD-EFGH-JKMN", null).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected refusal."); }
    catch (PairingException exception)
    {
        AssertEqual("Steam is not signed in on this PC.", exception.Message);
        AssertTrue(!exception.ContactSupport && !called);   // code not sent, so it is not used up
    }
}

static void FiltersCentreDrivers()
{
    var drivers = new[]
    {
        new DriverMatch(1, "Nikhil Joji", "nikhil.joji", null, "nikhil.joji", "NJ Racing"),
        new DriverMatch(2, "Arun Kumar", "arunk", null, "arunk", "Arun K"),
        new DriverMatch(3, "Srt New Driver", "srtnewdriver", null, "srtnewdriver")
    };
    AssertEqual("3", DriverFilter.Filter(drivers, "").Count.ToString());
    AssertEqual("Nikhil Joji", DriverFilter.Filter(drivers, "nik").Single().DisplayName);
    AssertEqual("Nikhil Joji", DriverFilter.Filter(drivers, "racing").Single().DisplayName);  // last in-game name
    AssertEqual("Arun Kumar", DriverFilter.Filter(drivers, "@ARUNK").Single().DisplayName);
    AssertEqual("Arun Kumar", DriverFilter.Filter(drivers, "arun kum").Single().DisplayName); // every word must match
    AssertEqual("0", DriverFilter.Filter(drivers, "zzz").Count.ToString());
    AssertEqual("NJ Racing", drivers[0].PreferredInGameName);
    AssertEqual("Srt New Driver", drivers[2].PreferredInGameName);
}

static void KeepsFiveFavourites()
{
    using var fixture = new ActiveSessionFixture();
    var path = System.IO.Path.Combine(fixture.Directory, "favourites.json");
    var store = new FavouriteDriverStore(path);
    for (var i = 1; i <= 5; i++)
        AssertEqual("Added", store.Add(new FavouriteDriver($"driver{i}", $"Driver {i}", $"D{i}", i)).ToString());
    AssertEqual("Full", store.Add(new FavouriteDriver("driver6", "Driver 6", "D6", 6)).ToString());
    AssertEqual("AlreadyFavourite", store.Add(new FavouriteDriver("@Driver1", "Driver 1", "D1", 1)).ToString());
    store.Remove("@driver3");
    AssertTrue(!store.Contains("driver3"));
    AssertEqual("Added", store.Add(new FavouriteDriver("driver6", "Driver 6", "D6", 6)).ToString());
    store.UpdateInGameName("driver6", "Six");
    var reloaded = new FavouriteDriverStore(path).Load();
    AssertEqual("5", reloaded.Count.ToString());
    AssertEqual("Six", reloaded.Single(f => f.SrtUsername == "driver6").InGameName);
    File.WriteAllText(path, "not json");
    AssertEqual("0", new FavouriteDriverStore(path).Load().Count.ToString());   // damaged file: start clean
}

static void ReadsRecentDrivers()
{
    HttpRequestMessage? seen = null;
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "TTL-01", "t", "76561198000000000"),
        new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, "{\"drivers\":[{\"profileId\":99,\"displayName\":\"Srt New Driver\",\"srtUsername\":\"srtnewdriver\",\"discordUsername\":null," +
                "\"suggestedSrtUsername\":\"srtnewdriver\",\"lastDisplayName\":\"SRT New Driver\",\"lastPlayedAt\":\"2026-11-13T11:00:00Z\",\"sessionCount\":5}]}");
        })));
    var drivers = api.RecentDriversAsync().GetAwaiter().GetResult();
    AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-drivers?rigId=TTL-01&recent=1", seen!.RequestUri!.AbsoluteUri);
    AssertEqual("SRT New Driver", drivers.Single().PreferredInGameName);
    AssertEqual("5", drivers.Single().SessionCount.ToString());
}

static void ReadsDriverBusy()
{
    HttpRequestMessage? seen = null;
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "TTL-01", "t", "76561198000000000"),
        new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, "{\"rig\":{\"id\":\"TTL-01\",\"displayName\":\"Rig 01\",\"centreId\":\"ttl\",\"centreName\":\"TTL\"},\"event\":null,\"app\":null," +
                "\"driverBusy\":{\"rigId\":\"KOC-R02\",\"rigName\":\"Rig 02\",\"centreName\":\"Kochi\",\"startedAt\":\"2026-11-15T15:00:00Z\",\"message\":\"Nikhil is already in a session on Rig 02 at Kochi\"}}");
        })));
    var config = api.GetConfigAsync("@Nikhil").GetAwaiter().GetResult();
    AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-config?rigId=TTL-01&driver=nikhil", seen!.RequestUri!.AbsoluteUri);
    AssertEqual("Rig 02", config.DriverBusy!.RigName);
    AssertTrue(config.DriverBusy.Message.Contains("already in a session"));
}

static void ComparesVersions()
{
    AssertTrue(AppVersion.IsNewer("1.2.0", "1.1.9"));
    AssertTrue(AppVersion.IsNewer("1.10.0", "1.9.0"));
    AssertTrue(!AppVersion.IsNewer("1.1.0", "1.1.0"));
    AssertTrue(!AppVersion.IsNewer("1.0.9", "1.1.0"));
    AssertTrue(!AppVersion.IsNewer("not-a-version", "1.0.0"));
}

static void VerifiesUpdateDownloads()
{
    using var fixture = new ActiveSessionFixture();
    var payload = System.Text.Encoding.UTF8.GetBytes("pretend installer");
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
    var calls = 0;
    var http = new HttpClient(new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }; }));
    var updater = new AppUpdater(http, fixture.Directory);

    var path = updater.DownloadAsync(new AppRelease("1.2.0", "https://example.com/setup.exe", sha.ToUpperInvariant())).GetAwaiter().GetResult();
    AssertTrue(File.Exists(path) && path.EndsWith("SRT-Event-Companion-Setup-1.2.0.exe", StringComparison.Ordinal));
    updater.DownloadAsync(new AppRelease("1.2.0", "https://example.com/setup.exe", sha)).GetAwaiter().GetResult();
    AssertEqual("1", calls.ToString());   // verified file reused, not downloaded again

    try { updater.DownloadAsync(new AppRelease("1.3.0", "https://example.com/setup.exe", new string('0', 64))).GetAwaiter().GetResult(); throw new Exception("Expected failure."); }
    catch (InvalidOperationException exception) { AssertTrue(exception.Message.Contains("SHA-256 mismatch")); }
    AssertTrue(Directory.GetFiles(fixture.Directory, "*1.3.0*").Length == 0);   // tampered download deleted

    try { updater.DownloadAsync(new AppRelease("1.4.0", "http://example.com/setup.exe", sha)).GetAwaiter().GetResult(); throw new Exception("Expected failure."); }
    catch (InvalidOperationException exception) { AssertTrue(exception.Message.Contains("https")); }
}

static void BuildsHeartbeat()
{
    var body = IngestMessages.Heartbeat("TTL-01", "1.2.0", 3, inSession: true, steamOk: false);
    AssertEqual("{\"action\":\"heartbeat\",\"rigId\":\"TTL-01\",\"appVersion\":\"1.2.0\",\"pendingSync\":3,\"state\":\"session\",\"steamOk\":false}", body.ToJsonString());
}

static void ReadsPublishedRelease()
{
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "TTL-01", "t", "76561198000000000"),
        new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"rig\":{\"id\":\"TTL-01\",\"displayName\":\"TTL 01\",\"centreId\":\"ttl\",\"centreName\":\"Test Lab\"},\"event\":null," +
            "\"app\":{\"version\":\"1.2.0\",\"downloadUrl\":\"https://example.com/s.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"notes\":\"Fixes\",\"publishedAt\":\"2026-11-13T12:00:00.000Z\"}}"))));
    var config = api.GetConfigAsync().GetAwaiter().GetResult();
    AssertEqual("1.2.0", config.App!.Version);
    AssertEqual("Fixes", config.App.Notes);
}

static void ChecksDriverEmails()
{
    AssertEqual("nikhil@example.com", EmailAddress.Normalize("  Nikhil@Example.COM "));
    AssertTrue(EmailAddress.IsValid("nikhil@example.com"));
    AssertTrue(EmailAddress.IsValid("a.b+krw@mail.co.in"));
    foreach (var bad in new[] { "", "nikhil", "nikhil@", "@example.com", "nikhil@example", "a b@example.com", "a@b@example.com", "nikhil@example.c" })
        AssertTrue(!EmailAddress.IsValid(bad));
}

static void SignsDriverInWithEmailCode()
{
    var bodies = new List<JsonNode>();
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "t", "76561198000000000"), new HttpClient(new StubHandler(request =>
    {
        AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-email", request.RequestUri!.AbsoluteUri);
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
        bodies.Add(body);
        return (string?)body["action"] switch
        {
            "send-code" => Json(HttpStatusCode.OK, "{\"sent\":true,\"email\":\"ni••••@example.com\",\"expiresInSeconds\":600,\"resendInSeconds\":60,\"notice\":\"Only used to verify you.\"}"),
            "verify-code" => Json(HttpStatusCode.OK, "{\"verified\":true,\"needsProfile\":true,\"driver\":null,\"driverToken\":\"tok\"}"),
            _ => Json(HttpStatusCode.Created, "{\"driver\":{\"profileId\":100,\"displayName\":\"Nikhil Joji\",\"srtUsername\":\"nikhiljoji.100\"}}")
        };
    })));

    var sent = api.SendEmailCodeAsync(" Nikhil@Example.com ").GetAwaiter().GetResult();
    AssertEqual("ni••••@example.com", sent.Email);
    AssertTrue(sent.ResendInSeconds == 60);
    AssertEqual("nikhil@example.com", (string?)bodies[0]["email"]);
    AssertEqual("KOC-R01", (string?)bodies[0]["rigId"]);

    var verified = api.VerifyEmailCodeAsync("nikhil@example.com", "123 456").GetAwaiter().GetResult();
    AssertTrue(verified.NeedsProfile && verified.Driver is null);
    AssertEqual("tok", verified.DriverToken);
    AssertEqual("123456", (string?)bodies[1]["code"]);

    var driver = api.CreateEmailProfileAsync("tok", "  Nikhil Joji ").GetAwaiter().GetResult();
    AssertEqual("nikhiljoji.100", driver.SrtUsername);
    AssertEqual("Nikhil Joji", (string?)bodies[2]["displayName"]);

    try { api.SendEmailCodeAsync("not-an-email").GetAwaiter().GetResult(); throw new InvalidOperationException("Expected rejection."); }
    catch (ArgumentException) { }
    try { api.VerifyEmailCodeAsync("nikhil@example.com", "12345").GetAwaiter().GetResult(); throw new InvalidOperationException("Expected rejection."); }
    catch (ArgumentException) { }
    AssertTrue(bodies.Count == 3);
}

static void StartSessionCarriesDriverToken()
{
    var session = new ActiveRigSession(Guid.NewGuid(), "@nikhiljoji.100", "Nikhil Joji", "r", "rb", "v", "vb", DateTimeOffset.UtcNow, "round-1", "Rig 01", "tok");
    AssertEqual("tok", (string?)IngestMessages.StartSession("KOC-R01", session)["driverToken"]);
    AssertTrue(IngestMessages.StartSession("KOC-R01", session with { DriverToken = null })["driverToken"] is null);
}

static void ExpiredSignInKeepsMessage()
{
    var credential = new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "t", "76561198000000000");
    var expired = new RigApiClient(credential, new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"Driver sign-in expired. Verify the email again.\"}"))));
    try { expired.CreateEmailProfileAsync("tok", "Nikhil").GetAwaiter().GetResult(); throw new InvalidOperationException("Expected refusal."); }
    catch (RigApiException exception) { AssertEqual("Driver sign-in expired. Verify the email again.", exception.Message); }

    var unpaired = new RigApiClient(credential, new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"Invalid rig credentials.\"}"))));
    try { unpaired.GetConfigAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("Expected refusal."); }
    catch (RigApiException exception) { AssertEqual("This rig's SRT pairing is no longer valid. Pair the rig again.", exception.Message); }
}

static HttpResponseMessage Json(HttpStatusCode status, string body) =>
    new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

static void AssertThrowsAny(Action action)
{
    try { action(); }
    catch (Exception) { return; }
    throw new InvalidOperationException("Expected an exception.");
}

static void AssertEqual(string expected, string? actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected '{expected}', got '{actual ?? "<null>"}'.");
}

static void AssertTrue(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected condition to be true.");
}

static void AssertThrows(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException("Expected ArgumentException.");
}

static void AssertThrowsInvalidOperation(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidOperationException.");
}

static void AssertThrowsArgumentOutOfRange(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentOutOfRangeException)
    {
        return;
    }

    throw new InvalidOperationException("Expected ArgumentOutOfRangeException.");
}

static byte[] MakeZip(params (string Name, string Text)[] files)
{
    using var memory = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, text) in files)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
    return memory.ToArray();
}

static string Sha(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

static (ContentInstaller Installer, ContentStateStore Store, string Ac, string Docs, string Root) ContentRig(byte[] zip, Action<HttpRequestMessage>? seen = null)
{
    var root = Path.Combine(Path.GetTempPath(), "srt-content-" + Guid.NewGuid().ToString("N"));
    var ac = Path.Combine(root, "assettocorsa"); var docs = Path.Combine(root, "Documents", "Assetto Corsa");
    Directory.CreateDirectory(Path.Combine(ac, "content", "cars")); Directory.CreateDirectory(Path.Combine(docs, "cfg"));
    var http = new HttpClient(new StubHandler(request =>
    {
        seen?.Invoke(request);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
    }));
    var store = new ContentStateStore(Path.Combine(root, "state.json"));
    return (new ContentInstaller(http, Path.Combine(root, "cache"), store), store, ac, docs, root);
}

static Func<CancellationToken, Task<PackageDownload>> LinkFor(ContentPackage p) =>
    _ => Task.FromResult(new PackageDownload("https://acct.r2.cloudflarestorage.com/srt-content/packages/7/x.zip?sig=1", p.Sha256, p.SizeBytes, p.Name, 3600));

static void PlansPackagesLikeTheWebsite()
{
    // Same cases as the website's krw-package.js test.
    var car = PackagePlanner.Plan(new[] { ("ks_bmw_m4/data.acd", 1000L), ("ks_bmw_m4/skins/red/livery.dds", 50L), ("__MACOSX/._junk", 1L), ("ks_bmw_m4/", 0L) }, "car");
    AssertEqual("content/cars/ks_bmw_m4", string.Join(",", car.Targets));
    AssertEqual("2", car.Files.Count.ToString());
    var wrapped = PackagePlanner.Plan(new[] { ("Helicorsa v2/apps/python/helicorsa/helicorsa.py", 8L), ("Helicorsa v2/content/fonts/heli.ttf", 1L) }, "python-app");
    AssertEqual("apps/python/helicorsa,content/fonts/heli.ttf", string.Join(",", wrapped.Targets));
    AssertEqual("apps/python/helicorsa/helicorsa.py", wrapped.Files[0].RelativePath);
    AssertEqual("Helicorsa v2/apps/python/helicorsa/helicorsa.py", wrapped.Files[0].ZipEntry);
    // Mods are trusted: helper scripts and loose files are fine; only paths that leave the AC folder are refused.
    var rss = PackagePlanner.Plan(new[] { ("rss_gtm_furiano_96_v6/data.acd", 1L), ("rss_gtm_furiano_96_v6/install_csp-physics.bat", 1L), ("readme.txt", 1L) }, "car");
    AssertTrue(rss.IsValid);
    AssertEqual("content/cars/readme.txt,content/cars/rss_gtm_furiano_96_v6", string.Join(",", rss.Targets));
    AssertEqual("extension/config/cars", string.Join(",", PackagePlanner.Plan(new[] { ("extension/config/cars/kunos/rss.ini", 1L) }, "ac-root").Targets));
    var bad = PackagePlanner.Plan(new[] { ("../evil.txt", 1L), ("mycar/data.acd", 1L) }, "car");
    AssertTrue(!bad.IsValid && bad.Errors.Count == 1);
    AssertTrue(!PackagePlanner.Plan(new[] { ("C:/Windows/x.txt", 1L) }, "car").IsValid);
}

static void RefusesUnsafePaths()
{
    var root = Path.Combine(Path.GetTempPath(), "srt-safe");
    AssertTrue(ContentInstaller.SafePath(root, "content/cars/x/data.acd").StartsWith(Path.GetFullPath(root)));
    AssertThrowsAny(() => ContentInstaller.SafePath(root, "../outside.txt"));
    AssertThrowsAny(() => ContentInstaller.SafePath(root, "content/../../outside.txt"));
}

static void EnablesPythonApps()
{
    var ini = Path.Combine(Path.GetTempPath(), "srt-python-" + Guid.NewGuid().ToString("N") + ".ini");
    File.WriteAllText(ini, "[HELICORSA]\nACTIVE=0\n\n[OTHERAPP]\nACTIVE=1\n");
    ContentInstaller.EnablePythonApp(ini, "helicorsa");
    ContentInstaller.EnablePythonApp(ini, "realpenalty");
    var text = File.ReadAllText(ini).Replace("\r\n", "\n");
    AssertTrue(text.Contains("[HELICORSA]\nACTIVE=1"));
    AssertTrue(text.Contains("[OTHERAPP]\nACTIVE=1"));
    AssertTrue(text.Contains("[REALPENALTY]\nACTIVE=1"));
}

static void ChecksContentStatus()
{
    var (installer, store, ac, _, _) = ContentRig(Array.Empty<byte>());
    var p = new ContentPackage("7", "Kerala Hills", "track", new string('a', 64), 10, new[] { "content/tracks/kerala_hills" });
    AssertEqual("Missing", ContentChecker.CheckAll(new[] { p }, ac, store)[0].Status.ToString());
    Directory.CreateDirectory(Path.Combine(ac, "content", "tracks", "kerala_hills"));
    AssertEqual("FoundOnRig", ContentChecker.CheckAll(new[] { p }, ac, store)[0].Status.ToString());
    installer.MarkSkipped(p);
    var skipped = ContentChecker.CheckAll(new[] { p }, ac, store)[0];
    AssertTrue(skipped.IsReady && skipped.Status == ContentStatus.Skipped);
    // A new upload of the same mod is a different file: skipping the old one doesn't count.
    AssertEqual("FoundOnRig", ContentChecker.CheckAll(new[] { p with { Sha256 = new string('b', 64) } }, ac, store)[0].Status.ToString());
}

static void InstallsPackages()
{
    var zip = MakeZip(("srt_test/srt_test.py", "print(1)"), ("srt_test/lib/util.py", "x=1"));
    HttpRequestMessage? request = null;
    var (installer, store, ac, docs, _) = ContentRig(zip, r => request = r);
    var p = new ContentPackage("9", "SRT test app", "python-app", Sha(zip), zip.Length, new[] { "apps/python/srt_test" });
    // An older copy is already there: it is backed up, not deleted.
    Directory.CreateDirectory(Path.Combine(ac, "apps", "python", "srt_test"));
    File.WriteAllText(Path.Combine(ac, "apps", "python", "srt_test", "old.py"), "old");
    var stages = new List<string>();
    var result = installer.InstallAsync(p, LinkFor(p), ac, docs, new SyncProgress<ContentProgress>(x => stages.Add(x.Stage))).GetAwaiter().GetResult();

    AssertEqual("print(1)", File.ReadAllText(Path.Combine(ac, "apps", "python", "srt_test", "srt_test.py")));
    AssertTrue(File.Exists(Path.Combine(ac, "apps", "python", "srt_test", "lib", "util.py")));
    AssertTrue(!File.Exists(Path.Combine(ac, "apps", "python", "srt_test", "old.py")));
    AssertTrue(result.BackupFolder is not null && File.Exists(Path.Combine(result.BackupFolder, "apps", "python", "srt_test", "old.py")));
    AssertEqual("srt_test", string.Join(",", result.EnabledPythonApps));
    AssertTrue(File.ReadAllText(Path.Combine(docs, "cfg", "python.ini")).Contains("[SRT_TEST]"));
    AssertTrue(stages.Contains("Downloading") && stages.Contains("Unpacking"));
    AssertTrue(request!.RequestUri!.Host.EndsWith("r2.cloudflarestorage.com"));
    AssertTrue(!Directory.EnumerateDirectories(ac, "_srt_staging_*").Any()); // staging cleaned up
    AssertEqual("Installed", ContentChecker.CheckAll(new[] { p }, ac, store)[0].Status.ToString());
}

static void InstallsAnyFileTypeWithBackup()
{
    var zip = MakeZip(("rss_gtm/data.acd", "new"), ("rss_gtm/install_csp-physics.bat", "@echo off"));
    var (installer, _, ac, docs, _) = ContentRig(zip);
    var existing = Path.Combine(ac, "content", "cars", "rss_gtm");
    Directory.CreateDirectory(existing);
    File.WriteAllText(Path.Combine(existing, "data.acd"), "old");
    var p = new ContentPackage("10", "RSS GTM", "car", Sha(zip), zip.Length, new[] { "content/cars/rss_gtm" });
    var result = installer.InstallAsync(p, LinkFor(p), ac, docs).GetAwaiter().GetResult();
    AssertEqual("new", File.ReadAllText(Path.Combine(existing, "data.acd")));
    AssertTrue(File.Exists(Path.Combine(existing, "install_csp-physics.bat")));
    AssertEqual("old", File.ReadAllText(Path.Combine(result.BackupFolder!, "content", "cars", "rss_gtm", "data.acd")));
}

static void RejectsTamperedDownload()
{
    var zip = MakeZip(("srt_test/srt_test.py", "print(1)"));
    var (installer, _, ac, docs, root) = ContentRig(zip);
    var p = new ContentPackage("11", "SRT test app", "python-app", new string('0', 64), zip.Length, new[] { "apps/python/srt_test" });
    var link = LinkFor(p);
    try { installer.InstallAsync(p, link, ac, docs).GetAwaiter().GetResult(); throw new InvalidOperationException("expected a failure"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("security check")) { }
    AssertTrue(!Directory.EnumerateFiles(Path.Combine(root, "cache")).Any());
    AssertTrue(!Directory.Exists(Path.Combine(ac, "apps", "python", "srt_test")));
}

static void ReadsRequiredContent()
{
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "TTL-01", "t", "76561198000000000"),
        new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("kerala-race-wars-content")
            ? Json(HttpStatusCode.OK, "{\"url\":\"https://acct.r2.cloudflarestorage.com/x.zip?sig\",\"sha256\":\"" + new string('a', 64) + "\",\"sizeBytes\":10,\"name\":\"Kerala Hills\",\"expiresInSeconds\":3600}")
            : Json(HttpStatusCode.OK, "{\"rig\":{\"id\":\"TTL-01\",\"displayName\":\"Rig 01\",\"centreId\":\"ttl\",\"centreName\":\"TTL\"},\"event\":{\"id\":\"krw\",\"name\":\"KRW\",\"serverHost\":\"1.2.3.4\",\"httpPort\":8081," +
              "\"packages\":[{\"id\":\"7\",\"name\":\"Kerala Hills\",\"kind\":\"track\",\"sha256\":\"" + new string('a', 64) + "\",\"sizeBytes\":10,\"targets\":[\"content/tracks/kerala_hills\"]}]}}"))));
    var config = api.GetConfigAsync().GetAwaiter().GetResult();
    AssertEqual("content/tracks/kerala_hills", config.Event!.RequiredContent[0].Targets[0]);
    AssertEqual("Kerala Hills", api.GetPackageDownloadAsync("7").GetAwaiter().GetResult().Name);
    // An older SRT without content still works.
    AssertEqual("0", new EventServerConfig("e", "E", "1.2.3.4", 1, null, null, null).RequiredContent.Count.ToString());
    AssertEqual("2", IngestMessages.Heartbeat("TTL-01", "1.1.0", 0, false, true, contentMissing: 2)["contentMissing"]!.ToString());
    AssertTrue(IngestMessages.Heartbeat("TTL-01", "1.1.0", 0, false, true)["contentMissing"] is null);
}


static EventServerConfig Ev(string id, string name = "E") => new(id, name, "1.2.3.4", 8081, null, null, null);

static void ReadsEveryLiveEvent()
{
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "TTL-01", "t", "76561198000000000"),
        new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"rig\":{\"id\":\"TTL-01\",\"displayName\":\"Rig 01\",\"centreId\":\"ttl\",\"centreName\":\"TTL\"}," +
            "\"events\":[{\"id\":\"krw-r2\",\"name\":\"KRW Round 2\",\"serverHost\":\"15.235.182.31\",\"httpPort\":8027,\"packages\":[]}," +
            "{\"id\":\"gt3-cup\",\"name\":\"GT3 Cup\",\"serverHost\":\"15.235.182.32\",\"httpPort\":8091,\"serverPassword\":\"gt3\",\"audience\":\"all\"}]," +
            "\"event\":{\"id\":\"krw-r2\",\"name\":\"KRW Round 2\",\"serverHost\":\"15.235.182.31\",\"httpPort\":8027}}"))));
    var config = api.GetConfigAsync().GetAwaiter().GetResult();
    AssertEqual("2", config.LiveEvents.Count.ToString());
    AssertEqual("gt3", config.FindEvent("GT3-CUP")!.ServerPassword);
    AssertEqual("all", config.FindEvent("gt3-cup")!.Audience!);
    AssertEqual("acmanager://race/online/join?httpPort=8091&ip=15.235.182.32", config.FindEvent("gt3-cup")!.ToJoinTarget().ToUri().AbsoluteUri);
    AssertTrue(config.FindEvent("old-event") is null);
    AssertTrue(!config.Rig.IsIndividual);
    // Older SRT: only "event" is sent, and it is the one live event.
    var older = new RigConfig(new RigInfo("R", "Rig", "c", "C"), Ev("krw"));
    AssertEqual("krw", older.LiveEvents.Single().Id);
    AssertEqual("0", new RigConfig(new RigInfo("R", "Rig", "c", "C"), null).LiveEvents.Count.ToString());
}

static void PicksDefaultEvent()
{
    var events = new[] { Ev("new"), Ev("krw", "Kerala Race Wars") };
    AssertEqual("krw", EventChoice.Default(events, "KRW")!.Id);
    AssertEqual("new", EventChoice.Default(events, "finished-event")!.Id);
    AssertEqual("new", EventChoice.Default(events, null)!.Id);
    AssertTrue(EventChoice.Default(Array.Empty<EventServerConfig>(), "krw") is null);
    AssertEqual("Kerala Race Wars", EventChoice.Describe(events[1]));
    AssertEqual("GT3 Cup  —  Spa · Porsche", EventChoice.Describe(new EventServerConfig("g", "GT3 Cup", "1.2.3.4", 1, null, "Spa", "Porsche")));
}

static void KeepsAppSettings()
{
    var folder = Path.Combine(Path.GetTempPath(), "srt-settings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsStore(Path.Combine(folder, "settings.json"));
        AssertTrue(store.Load() is null);
        AssertTrue(!AppSettings.NotSetUp.SetupDone);
        AssertTrue(AppSettings.Defaults(AppMode.Centre).StartWithWindows);
        AssertTrue(!AppSettings.Defaults(AppMode.Individual).StartWithWindows);
        var rig = AppSettings.ForExistingRig(startsWithWindows: true);
        AssertTrue(rig.SetupDone && rig.AutoUpdate && rig.Mode == AppMode.Centre);
        store.Save(new AppSettings(true, AppMode.Individual, false, false, "gt3-cup"));
        var loaded = store.Load()!;
        AssertEqual("Individual", loaded.Mode.ToString());
        AssertTrue(!loaded.AutoUpdate && !loaded.StartWithWindows);
        AssertEqual("gt3-cup", loaded.LastEventId!);
        AssertTrue(File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("\"Individual\""));
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{ not json");
        AssertTrue(store.Load() is null);
    }
    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}

static void ReadsIndividualRig()
{
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "IND-7", "t", "76561198000000000"),
        new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"rig\":{\"id\":\"IND-7\",\"displayName\":\"Asha's PC\",\"centreId\":\"independent\",\"centreName\":\"Independent drivers\"," +
            "\"mode\":\"individual\",\"owner\":{\"profileId\":7,\"displayName\":\"Asha K\",\"srtUsername\":\"asha.7\"}},\"events\":[],\"event\":null}"))));
    var config = api.GetConfigAsync().GetAwaiter().GetResult();
    AssertTrue(config.Rig.IsIndividual);
    AssertEqual("Asha K", config.Rig.Owner!.DisplayName);
    AssertEqual("asha.7", config.Rig.Owner.SrtUsername!);
    AssertEqual("0", config.LiveEvents.Count.ToString());
    // Sessions on an individual's PC carry no email sign-in: SRT uses the owner.
    var session = new ActiveRigSession(Guid.NewGuid(), "@asha.7", "Asha K", "r", "rb", "c", "cb", DateTimeOffset.Now, "gt3-cup", "Asha's PC", null);
    AssertTrue(IngestMessages.StartSession("IND-7", session)["driverToken"] is null);
    AssertEqual("gt3-cup", IngestMessages.StartSession("IND-7", session)["eventId"]!.ToString());
}

static void SignsIndividualUp()
{
    var bodies = new List<string>();
    var http = new HttpClient(new StubHandler(request =>
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        bodies.Add(body);
        AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-individual", request.RequestUri!.AbsoluteUri);
        AssertTrue(!request.Headers.Contains("X-Rig-Token"));
        if (body.Contains("send-code")) return Json(HttpStatusCode.OK, "{\"sent\":true,\"email\":\"as••@example.com\",\"expiresInSeconds\":600,\"resendInSeconds\":60,\"notice\":\"n\"}");
        if (!body.Contains("displayName")) return Json(HttpStatusCode.OK, "{\"verified\":false,\"needsProfile\":true}");
        return Json(HttpStatusCode.OK, "{\"verified\":true,\"rigId\":\"IND-200\",\"rigToken\":\"tok\",\"steamId\":\"76561198000000001\",\"movedFromAnotherPc\":false," +
            "\"driver\":{\"profileId\":200,\"displayName\":\"Asha K\",\"srtUsername\":\"ashak.200\"}}");
    }));
    var service = new IndividualSignUpService(http);
    AssertEqual("as••@example.com", service.SendCodeAsync(" Asha@Example.com ").GetAwaiter().GetResult().Email);
    AssertTrue(bodies[0].Contains("\"email\":\"asha@example.com\""));
    var first = service.VerifyAsync("asha@example.com", "123 456", "76561198000000001").GetAwaiter().GetResult();
    AssertTrue(first.NeedsName && first.Credential is null);
    var done = service.VerifyAsync("asha@example.com", "123456", "76561198000000001", "Asha K").GetAwaiter().GetResult();
    AssertEqual("IND-200", done.Credential!.RigId);
    AssertEqual("tok", done.Credential.RigToken);
    AssertEqual("76561198000000001", done.Credential.SteamId);
    AssertEqual("https://www.simracingtharavadu.in", done.Credential.ApiBaseUrl);
    AssertEqual("Asha K", done.Driver!.DisplayName);
    AssertTrue(bodies[1].Contains("\"code\":\"123456\"") && bodies[1].Contains("\"steamId\":\"76561198000000001\""));
    // Steam must be signed in before the code is sent to SRT.
    AssertThrowsAny(() => service.VerifyAsync("asha@example.com", "123456", null).GetAwaiter().GetResult());
    AssertEqual("3", bodies.Count.ToString());
    // SRT's refusal reaches the user as written.
    var refusing = new IndividualSignUpService(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Conflict, "{\"error\":\"This Steam account is used by sim centre rig KOC-R01.\"}"))));
    try { refusing.VerifyAsync("a@example.com", "123456", "76561198000000001").GetAwaiter().GetResult(); throw new InvalidOperationException("Expected refusal."); }
    catch (RigApiException ex) { AssertEqual("This Steam account is used by sim centre rig KOC-R01.", ex.Message); }
}

static void RunsDiscordSignIn()
{
    var bodies = new List<string>();
    var polls = 0;
    var api = new RigApiClient(new RigCredential("https://www.simracingtharavadu.in", "KOC-R01", "rig-key", "76561198000000000"),
        new HttpClient(new StubHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            bodies.Add(body);
            AssertEqual("https://www.simracingtharavadu.in/api/kerala-race-wars-discord", request.RequestUri!.AbsoluteUri);
            AssertEqual("rig-key", request.Headers.GetValues("X-Rig-Token").Single());
            if (body.Contains("\"start\"")) return Json(HttpStatusCode.OK, "{\"code\":\"ABCD-EFGH-JKMN\",\"url\":\"https://www.simracingtharavadu.in/api/discord-auth?action=login&intent=rig-link&code=ABCDEFGHJKMN\",\"pollToken\":\"poll-1\",\"expiresInSeconds\":600,\"inviteUrl\":\"https://discord.gg/NrsGgCkw32\"}");
            return ++polls == 1 ? Json(HttpStatusCode.OK, "{\"status\":\"pending\"}")
                : Json(HttpStatusCode.OK, "{\"status\":\"done\",\"outcome\":\"signed-in\",\"discordUsername\":\"nikhil.joji\",\"inGuild\":false," +
                    "\"driverToken\":\"drv-1\",\"driver\":{\"profileId\":7,\"displayName\":\"Nikhil Joji\",\"srtUsername\":\"nikhil\",\"discordUsername\":\"nikhil.joji\"}}");
        })));
    var start = api.StartDiscordAsync("sign-in").GetAwaiter().GetResult();
    AssertEqual("ABCD-EFGH-JKMN", start.Code);
    AssertTrue(bodies[0].Contains("\"purpose\":\"sign-in\"") && bodies[0].Contains("\"rigId\":\"KOC-R01\"") && !bodies[0].Contains("driverToken"));
    var first = api.PollDiscordAsync(start.PollToken).GetAwaiter().GetResult();
    AssertTrue(!first.IsFinished);
    var done = api.PollDiscordAsync(start.PollToken).GetAwaiter().GetResult();
    AssertTrue(done.IsDone && done.InGuild == false);
    AssertEqual("drv-1", done.DriverToken!);
    AssertEqual("nikhil.joji", done.Driver!.DiscordUsername!);
    api.StartDiscordAsync("link", "drv-9").GetAwaiter().GetResult();
    AssertTrue(bodies[3].Contains("\"purpose\":\"link\"") && bodies[3].Contains("\"driverToken\":\"drv-9\""));

    // Own PC set up with Discord: the finished result becomes this PC's SRT sign-in.
    var service = new IndividualSignUpService(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))));
    var credential = service.CredentialFrom(new DiscordLinkPoll("done", RigId: "IND-7", RigToken: "tok", SteamId: "76561198000000001"));
    AssertEqual("IND-7", credential.RigId);
    AssertThrowsAny(() => service.CredentialFrom(new DiscordLinkPoll("done", RigId: "IND-7", RigToken: "tok", SteamId: null)));
    AssertThrowsAny(() => service.StartDiscordAsync(null).GetAwaiter().GetResult());
    // The owner of an individual's PC carries their linked Discord.
    AssertEqual("asha.k", System.Text.Json.JsonSerializer.Deserialize<RigOwner>("{\"profileId\":7,\"displayName\":\"Asha\",\"srtUsername\":\"asha\",\"discordUsername\":\"asha.k\"}",
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.DiscordUsername!);
}

static void WatchesGameAndCountsDown()
{
    var t0 = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    var w = new SessionWatcher(t0);
    AssertTrue(w.Observe(false, false, t0.AddSeconds(5)).Phase == GamePhase.Starting);
    AssertTrue(w.Observe(true, true, t0.AddSeconds(30)).Phase == GamePhase.InGame);
    AssertTrue(!w.Observe(true, false, t0.AddMinutes(20)).ShouldAutoEnd(t0.AddMinutes(20)));
    // Game closed: countdown, not an immediate end.
    var closed = w.Observe(false, true, t0.AddMinutes(30));
    AssertTrue(closed.Phase == GamePhase.Away && closed.GameWasSeen);
    AssertTrue(!closed.ShouldAutoEnd(t0.AddMinutes(31)));
    // Driver rejoined on their own within the grace: countdown cancelled.
    AssertTrue(w.Observe(true, true, t0.AddMinutes(31)).AutoEndAt is null);
    // Closed for good: ends once the grace has passed.
    w.Observe(false, false, t0.AddMinutes(40));
    AssertTrue(!w.Observe(false, false, t0.AddMinutes(41)).ShouldAutoEnd(t0.AddMinutes(41)));
    AssertTrue(w.Observe(false, false, t0.AddMinutes(42)).ShouldAutoEnd(t0.AddMinutes(42)));
    // Rejoin restarts the wait for the game.
    w.Relaunched(t0.AddMinutes(43));
    AssertTrue(w.Observe(false, false, t0.AddMinutes(43).AddSeconds(10)).Phase == GamePhase.Starting);
}

static void WaitsForSlowGameStart()
{
    var t0 = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    var w = new SessionWatcher(t0);
    // CM is open (downloading, or the driver is in its join window): keep waiting.
    AssertTrue(w.Observe(false, true, t0.AddMinutes(5)).Phase == GamePhase.Starting);
    // CM closed without the game ever starting: count down.
    var away = w.Observe(false, false, t0.AddMinutes(6));
    AssertTrue(away.Phase == GamePhase.Away && !away.GameWasSeen && away.AutoEndAt == t0.AddMinutes(8));
    // A session picked up after an app restart counts down at once if the game isn't running.
    var recovered = new SessionWatcher(t0, gameSeen: true);
    AssertTrue(recovered.Observe(false, false, t0.AddSeconds(2)).Phase == GamePhase.Away);
}

static void HoldsSessionOpen()
{
    var t0 = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    var w = new SessionWatcher(t0, gameSeen: true);
    w.Observe(false, false, t0);
    w.Hold();
    var held = w.Observe(false, false, t0.AddHours(1));
    AssertTrue(held.Held && held.AutoEndAt is null && !held.ShouldAutoEnd(t0.AddHours(1)));
    // Once the game has run again, the next close counts down as usual.
    w.Observe(true, false, t0.AddHours(2));
    AssertTrue(w.Observe(false, false, t0.AddHours(3)).AutoEndAt == t0.AddHours(3).AddMinutes(2));
}

static void BuildsDirectJoinLink()
{
    var plan = new DirectJoinPlan(IPAddress.Parse("15.235.182.31"), 9313, 8313, "spa", "rss_gtm_furiano_96_v6", "05_carbons", "Krw@1 1&x", "Nikhil Joji");
    AssertEqual("acmanager://race/online?ip=15.235.182.31&port=9313&httpPort=8313&car=rss_gtm_furiano_96_v6&track=spa&name=Nikhil%20Joji&skin=05_carbons&plainPassword=Krw%401%201%26x",
        plan.ToUri().AbsoluteUri);
    // Fallback: CM's join window, with the password filled in.
    AssertEqual("acmanager://race/online/join?httpPort=8313&ip=15.235.182.31&plainPassword=a%26b",
        new OnlineJoinTarget(IPAddress.Parse("15.235.182.31"), 8313).ToUri("a&b").AbsoluteUri);
}

static void PicksReservedSlot()
{
    var info = ServerJoinResolver.ParseInfo("""{"ip":"","port":9313,"tport":9313,"cport":8313,"track":"ks_nordschleife-endurance","cars":["rss_gtm_furiano_96_v6"],"pass":true}""");
    AssertTrue(info.TcpPort == 9313);
    AssertEqual("ks_nordschleife-endurance", info.Track);
    var slots = ServerJoinResolver.ParseSlots("""
      {"Cars":[
      {"Model":"rss_gtm_furiano_96_v6","Skin":"00_red","DriverName":"","IsConnected":true,"IsRequestedGUID":false,"IsEntryList":true},
      {"Model":"rss_gtm_furiano_96_v6","Skin":"05_carbons","DriverName":"","IsConnected":false,"IsRequestedGUID":true,"IsEntryList":true},
      {"Model":"ks_mazda_mx5_cup","Skin":"","IsConnected":false,"IsRequestedGUID":false,"IsEntryList":false}]}
      """);
    AssertEqual("05_carbons", ServerJoinResolver.ChooseSlot(slots, _ => true).Skin);
    // Open server: first free open slot whose car is installed here.
    var open = slots.Where(s => !s.IsReservedForYou).ToList();
    AssertEqual("ks_mazda_mx5_cup", ServerJoinResolver.ChooseSlot(open, _ => true).Model);
    AssertTrue(ServerJoinResolver.ParseInfo("""{"port":9600,"track":"monza"}""").TcpPort == 9600);
}

static void NoSlotForSteamAccount()
{
    var slots = ServerJoinResolver.ParseSlots("""{"Cars":[{"Model":"a","IsConnected":false,"IsRequestedGUID":false,"IsEntryList":true}]}""");
    AssertThrowsInvalidOperation(() => ServerJoinResolver.ChooseSlot(slots, _ => true));
    AssertThrowsInvalidOperation(() => ServerJoinResolver.ChooseSlot(Array.Empty<ServerSlot>(), _ => true));
}

static void RejoinLapsHaveTheirOwnIds()
{
    var at = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    AssertEqual("lap-1-90000", new CompletedLap(1, 90000, at, false, 0, 0, "spa", "x").SourceAttemptId);
    AssertEqual("lap-r600-1-90000", new CompletedLap(1, 90000, at, false, 0, 0, "spa", "x", 600).SourceAttemptId);
}

static void ReadsServerTrackIds()
{
    // Exactly what the KRW and Track Nights servers' /INFO reported on 20 Nov 2026.
    var krw = ServerJoinResolver.ParseInfo("""{"ip":"","port":9313,"cport":8313,"name":"Kerala Race Wars","track":"ks_silverstone-gp","cars":["rss_gtm_furiano_96_v6"],"pickup":true,"tport":9313}""");
    AssertEqual("ks_silverstone-gp", krw.Track);
    AssertEqual("ks_silverstone", ServerJoinResolver.TrackFolder(krw.Track));
    var nights = ServerJoinResolver.ParseInfo("""{"ip":"","port":9027,"tport":9027,"cport":8027,"track":"csp/3465/../D/../mugello","cars":["ks_abarth500_assetto_corse"]}""");
    AssertEqual("mugello", nights.Track);
    AssertEqual("mugello", ServerJoinResolver.TrackFolder("csp/3465/../D/../mugello"));
    AssertEqual("spa", ServerJoinResolver.TrackFolder("spa"));
}

static void ShowsSeriesAndRoundInPicker()
{
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"rig":{"id":"KOC-R01","displayName":"Rig 1","centreId":"kochi","centreName":"Kochi"},
             "events":[{"id":"krw-live","name":"KRW Practice","serverHost":"15.235.182.31","httpPort":8313,"serverPassword":null,"track":null,"car":null,
               "audience":"centre","packages":[],
               "series":{"id":"krw","name":"Kerala Race Wars","sim":"ac"},
               "round":{"id":"krw-season-1-r3","number":3,"name":"Monza","track":"monza","raceDate":"2026-12-02","raceStart":"21:00","status":"practice"}},
              {"id":"old","name":"Old server","serverHost":"1.2.3.4","httpPort":8000,"serverPassword":null,"track":"spa","car":null,"packages":[],"series":null,"round":null}]}
            """)
    });
    var config = new RigApiClient(new RigCredential("https://srt.test", "KOC-R01", "token", "7656"), new HttpClient(handler)).GetConfigAsync().GetAwaiter().GetResult();
    var krw = config.FindEvent("krw-live")!;
    AssertEqual("Kerala Race Wars  ·  R3 Monza", EventChoice.Title(krw));
    AssertEqual("Old server", EventChoice.Title(config.FindEvent("old")!));
    AssertEqual("Race Wed 2 Dec, 21:00 IST", EventChoice.RaceLine(krw, new DateOnly(2026, 11, 20)));
    AssertEqual("Race day today from 21:00 IST", EventChoice.RaceLine(krw, new DateOnly(2026, 12, 2)));
    AssertTrue(EventChoice.RaceLine(krw, new DateOnly(2026, 12, 3)) is null);
    AssertEqual("Race day today", EventChoice.RaceLine(krw with { Round = krw.Round! with { Status = "race_day" } }, new DateOnly(2026, 11, 20)));
    AssertEqual("2026-11-21", EventChoice.TodayInIndia(new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero)).ToString("yyyy-MM-dd"));
}

sealed class RaceIniFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"srt-companion-tests-{Guid.NewGuid():N}");

    public RaceIniFixture(string content)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "race.ini");
        File.WriteAllText(Path, content.Replace("\n", Environment.NewLine));
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

sealed class AssettoCorsaFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"srt-companion-install-tests-{Guid.NewGuid():N}");

    public AssettoCorsaFixture()
    {
        UserProfileDirectory = System.IO.Path.Combine(_directory, "user");
        InstallDirectory = System.IO.Path.Combine(_directory, "install");
        Directory.CreateDirectory(System.IO.Path.Combine(UserProfileDirectory, "Documents", "Assetto Corsa", "cfg"));
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllText(System.IO.Path.Combine(UserProfileDirectory, "Documents", "Assetto Corsa", "cfg", "race.ini"), "[CAR_0]");
        File.WriteAllText(System.IO.Path.Combine(InstallDirectory, "AssettoCorsa.exe"), string.Empty);
    }

    public string UserProfileDirectory { get; }

    public string InstallDirectory { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

sealed class ValuesDataFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"srt-companion-values-tests-{Guid.NewGuid():N}");

    public ValuesDataFixture(byte[] content)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "Values.data");
        File.WriteAllBytes(Path, content);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

sealed class ActiveSessionFixture : IDisposable
{
    public ActiveSessionFixture()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"srt-companion-session-tests-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}

sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_respond(request));
}

/// <summary>Builds Content Manager Values.data files for tests.</summary>
static class CmValues
{
    const string Key = "Settings.DriveSettings.PlayerName";

    public static byte[] Plain(string name) => System.Text.Encoding.UTF8.GetBytes($"version: 2\n{Key}\t{name}\nSettings.Other\t1");

    public static byte[] CompressedWithBackReference()
    {
        var head = System.Text.Encoding.UTF8.GetBytes($"version: 2\n{Key}\tTest dr");
        var tail = System.Text.Encoding.UTF8.GetBytes("r\nSettings.Other\t1");
        var output = new List<byte> { 11 };
        AddLiterals(output, head);
        var target = System.Text.Encoding.UTF8.GetString(head).IndexOf("ive", StringComparison.Ordinal);
        var distance = head.Length - target - 1;
        output.Add((byte)((1 << 5) | (distance >> 8)));   // back-reference, length 3
        output.Add((byte)(distance & 0xff));
        AddLiterals(output, tail);
        return output.ToArray();
    }

    static void AddLiterals(List<byte> output, byte[] data)
    {
        for (var i = 0; i < data.Length; i += 32)
        {
            var n = Math.Min(32, data.Length - i);
            output.Add((byte)(n - 1));
            output.AddRange(data.Skip(i).Take(n));
        }
    }

    /// <summary>Independent LZF reader (same algorithm as CM's Lzf.Decompress) to check what CM will see.</summary>
    public static string? DecompressLikeContentManager(string path)
    {
        var input = File.ReadAllBytes(path);
        var result = new List<byte>();
        var i = 1;
        while (i < input.Length)
        {
            uint ctrl = input[i++];
            if (ctrl < 32) { ctrl++; do { result.Add(input[i++]); } while (--ctrl != 0); }
            else
            {
                var len = ctrl >> 5;
                var reference = (int)(result.Count - ((ctrl & 0x1f) << 8) - 1);
                if (len == 7) len += input[i++];
                reference -= input[i++];
                result.Add(result[reference++]); result.Add(result[reference++]);
                do { result.Add(result[reference++]); } while (--len != 0);
            }
        }
        var line = System.Text.Encoding.UTF8.GetString(result.ToArray()).Split('\n').FirstOrDefault(l => l.StartsWith(Key + "\t", StringComparison.Ordinal));
        return line?[(Key.Length + 1)..];
    }
}

sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public SyncProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
