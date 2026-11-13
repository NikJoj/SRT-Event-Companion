using Srt.EventCompanion.Core;
using System.Net;
using System.Text.Json.Nodes;

var tests = new (string Name, Action Test)[]
{
    ("reads existing AC driver names", ReadsExistingDriverNames),
    ("updates only profile name fields and makes a backup", UpdatesNamesAndMakesBackup),
    ("rejects unsafe driver names", RejectsUnsafeDriverNames),
    ("detects only complete Assetto Corsa installations", DetectsCompleteInstallation),
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
    ("only installs releases newer than the running version", ComparesVersions),
    ("installs only downloads that match the published SHA-256", VerifiesUpdateDownloads),
    ("heartbeat reports version, backlog, state and Steam", BuildsHeartbeat),
    ("reads the published release from the rig config", ReadsPublishedRelease)
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
    AssertEqual("✓ Matches rig (…8832)", ok.Label);

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
