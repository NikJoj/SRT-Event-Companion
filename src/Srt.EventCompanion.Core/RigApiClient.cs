using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Srt.EventCompanion.Core;

/// <summary>The driver who owns an individual's PC; they drive every session on it.</summary>
public sealed record RigOwner(int ProfileId, string DisplayName, string? SrtUsername, string? DiscordUsername = null);

/// <param name="Mode">"individual" for a driver's own PC; null or "centre" for a sim centre rig.</param>
public sealed record RigInfo(string Id, string DisplayName, string CentreId, string CentreName, string? Mode = null, RigOwner? Owner = null)
{
    public bool IsIndividual => string.Equals(Mode, "individual", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Audience">Who the event is open to: "centre", "individual" or "all". SRT only sends events open to this PC.</param>
/// <summary>The SRT Events series a server belongs to (Kerala Race Wars, SRT Track Nights…).</summary>
public sealed record EventSeries(string Id, string Name, string? Sim = null);

/// <summary>The round a server is serving now. RaceDate is yyyy-MM-dd and RaceStart HH:mm, both India time.</summary>
public sealed record EventRound(string Id, int Number, string Name, string? Track = null, string? RaceDate = null, string? RaceStart = null,
    string? Status = null);

/// <param name="Series">Null when SRT is older than this app or the server isn't part of a series yet.</param>
public sealed record EventServerConfig(string Id, string Name, string ServerHost, int HttpPort, string? ServerPassword, string? Track, string? Car,
    IReadOnlyList<ContentPackage>? Packages = null, string? Audience = null, EventSeries? Series = null, EventRound? Round = null)
{
    /// <summary>Mods this event requires (empty when none, or when SRT is older than this app).</summary>
    public IReadOnlyList<ContentPackage> RequiredContent => Packages ?? Array.Empty<ContentPackage>();


    public OnlineJoinTarget ToJoinTarget()
    {
        if (!IPAddress.TryParse(ServerHost, out var address)) throw new InvalidOperationException($"{Name}'s server IP '{ServerHost}' is not valid. Ask an SRT admin to fix it.");
        return new OnlineJoinTarget(address, HttpPort);
    }
}

/// <summary>What a rig needs before every launch: who it is, and the live event's server.</summary>
/// <summary>Set when the driver asked about is already in a live session on another rig.</summary>
public sealed record DriverBusy(string RigId, string RigName, string CentreName, DateTimeOffset StartedAt, string Message);

/// <param name="Event">The newest live event (what 1.0.x apps used).</param>
/// <param name="Events">Every live event, newest first. Missing when SRT is older than this app.</param>
public sealed record RigConfig(RigInfo Rig, EventServerConfig? Event, AppRelease? App = null, DriverBusy? DriverBusy = null,
    IReadOnlyList<EventServerConfig>? Events = null)
{
    /// <summary>Every event a driver can pick right now.</summary>
    public IReadOnlyList<EventServerConfig> LiveEvents => Events ?? (Event is null ? Array.Empty<EventServerConfig>() : new[] { Event });

    /// <summary>The live event with this ID, or null if it is no longer live.</summary>
    public EventServerConfig? FindEvent(string? eventId) =>
        eventId is null ? null : LiveEvents.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase));
}

public sealed record DriverMatch(int ProfileId, string DisplayName, string? SrtUsername, string? DiscordUsername, string? SuggestedSrtUsername,
    string? LastDisplayName = null, DateTimeOffset? LastPlayedAt = null, int? SessionCount = null)
{
    /// <summary>The in-game name to pre-fill: what they used last time at this centre, else their profile name.</summary>
    public string PreferredInGameName => string.IsNullOrWhiteSpace(LastDisplayName) ? DisplayName : LastDisplayName;
}

public sealed record RecentDrivers(IReadOnlyList<DriverMatch> Drivers);

/// <summary>A Discord sign-in waiting on the driver: show Url as a QR code (and Code as text).</summary>
public sealed record DiscordLinkStart(string Code, string Url, string PollToken, int ExpiresInSeconds, string? InviteUrl);

/// <summary>
/// Where a Discord sign-in stands. Status: pending, done, error or expired. Outcome (when done): linked, pending-review
/// (an admin must confirm an older SRT profile) or signed-in (NewProfile: SRT made a driver profile from their Discord). Sign-in gives DriverToken (rig) or RigId/RigToken (own PC).
/// InGuild: whether they are in the SRT Discord server (null when SRT couldn't tell).
/// </summary>
public sealed record DiscordLinkPoll(string Status, string? Error = null, string? Outcome = null, string? DiscordUsername = null,
    bool? InGuild = null, string? InviteUrl = null, bool? Merged = null, bool? PendingReview = null, DriverMatch? Driver = null,
    string? DriverToken = null, string? RigId = null, string? RigToken = null, string? SteamId = null, bool? MovedFromAnotherPc = null,
    bool? NewProfile = null)
{
    public bool IsDone => Status == "done";
    public bool IsFinished => Status is "done" or "error" or "expired";
}

/// <summary>SRT emailed a 6-digit code. Email is shown masked (ni••••@gmail.com).</summary>
public sealed record EmailCodeSent(string Email, int ExpiresInSeconds, int ResendInSeconds, string Notice);

/// <summary>
/// The driver proved they own the email. DriverToken lets this rig start one session for them (SRT refuses a session
/// without it). NeedsProfile: the email is new, so ask for the name to show on the leaderboard and call CreateEmailProfileAsync.
/// </summary>
public sealed record EmailVerification(bool NeedsProfile, DriverMatch? Driver, string DriverToken);

public sealed record DriverLookup(string Query, IReadOnlyList<DriverMatch> Matches, bool UsernameAvailable);

public sealed class RigApiException : Exception
{
    public RigApiException(string message, HttpStatusCode? statusCode, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;

    /// <summary>Null when SRT could not be reached at all.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The backend understood and refused the request; retrying the same request will not help.</summary>
    public bool IsPermanent => StatusCode is { } code && (int)code is >= 400 and < 500 && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}

/// <summary>Authenticated calls from a paired rig (or an individual's PC) to the SRT event API.</summary>
public sealed class RigApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RigCredential _credential;
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public RigApiClient(RigCredential credential, HttpClient? client = null)
    {
        _credential = credential;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _baseUri = new Uri(credential.ApiBaseUrl.TrimEnd('/') + "/");
    }

    public string RigId => _credential.RigId;

    /// <param name="driverUsername">Optional SRT username: SRT then says (DriverBusy) if that driver is racing on another rig.</param>
    public async Task<RigConfig> GetConfigAsync(string? driverUsername = null, CancellationToken cancellationToken = default)
    {
        var driver = driverUsername is null ? string.Empty : $"&driver={Uri.EscapeDataString(SrtUsername.Normalize(driverUsername))}";
        var json = await SendAsync(HttpMethod.Get, $"api/kerala-race-wars-config?rigId={Uri.EscapeDataString(_credential.RigId)}{driver}", null, cancellationToken);
        return json.Deserialize<RigConfig>(Json) ?? throw new RigApiException("SRT returned an empty rig configuration.", HttpStatusCode.OK);
    }

    /// <summary>A fresh one-hour download link for one package of a live event's required content.</summary>
    public async Task<PackageDownload> GetPackageDownloadAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"api/kerala-race-wars-content?rigId={Uri.EscapeDataString(_credential.RigId)}&packageId={Uri.EscapeDataString(packageId)}", null, cancellationToken);
        return json.Deserialize<PackageDownload>(Json) ?? throw new RigApiException("SRT returned an empty download link.", HttpStatusCode.OK);
    }

    public async Task<DriverLookup> FindDriverAsync(string usernameQuery, CancellationToken cancellationToken = default)
    {
        var query = SrtUsername.Normalize(usernameQuery);
        if (query.Length == 0) throw new ArgumentException("Enter the driver's Discord or SRT username.", nameof(usernameQuery));
        var json = await SendAsync(HttpMethod.Get,
            $"api/kerala-race-wars-drivers?rigId={Uri.EscapeDataString(_credential.RigId)}&q={Uri.EscapeDataString(query)}", null, cancellationToken);
        return json.Deserialize<DriverLookup>(Json) ?? new DriverLookup(query, Array.Empty<DriverMatch>(), false);
    }

    /// <summary>Drivers who have raced at this rig's centre, most recent first, so staff can pick by name.</summary>
    public async Task<IReadOnlyList<DriverMatch>> RecentDriversAsync(CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"api/kerala-race-wars-drivers?rigId={Uri.EscapeDataString(_credential.RigId)}&recent=1", null, cancellationToken);
        return json.Deserialize<RecentDrivers>(Json)?.Drivers ?? Array.Empty<DriverMatch>();
    }

    public Task<DriverMatch> ClaimDriverAsync(int profileId, string discordUsername, string srtUsername, CancellationToken cancellationToken = default) =>
        PostDriverAsync(new JsonObject
        {
            ["action"] = "claim", ["profileId"] = profileId,
            ["discordUsername"] = discordUsername, ["srtUsername"] = SrtUsername.Normalize(srtUsername)
        }, cancellationToken);

    public Task<DriverMatch> CreateDriverAsync(string srtUsername, string displayName, CancellationToken cancellationToken = default) =>
        PostDriverAsync(new JsonObject
        {
            ["action"] = "create", ["srtUsername"] = SrtUsername.Normalize(srtUsername), ["displayName"] = displayName
        }, cancellationToken);

    /// <summary>Step 1 of driver sign-in: SRT emails the driver a 6-digit code.</summary>
    public async Task<EmailCodeSent> SendEmailCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = EmailAddress.Normalize(email);
        if (!EmailAddress.IsValid(normalized)) throw new ArgumentException("Enter a valid email address.", nameof(email));
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-email",
            new JsonObject { ["action"] = "send-code", ["rigId"] = _credential.RigId, ["email"] = normalized }, cancellationToken);
        return json.Deserialize<EmailCodeSent>(Json) ?? throw new RigApiException("SRT did not confirm the code was sent.", HttpStatusCode.OK);
    }

    /// <summary>Step 2: check the code. Only a verified driver can be started on this rig.</summary>
    public async Task<EmailVerification> VerifyEmailCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var digits = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6) throw new ArgumentException("Enter the 6-digit code from the email.", nameof(code));
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-email",
            new JsonObject { ["action"] = "verify-code", ["rigId"] = _credential.RigId, ["email"] = EmailAddress.Normalize(email), ["code"] = digits }, cancellationToken);
        var verification = json.Deserialize<EmailVerification>(Json);
        if (verification is null || string.IsNullOrEmpty(verification.DriverToken)) throw new RigApiException("SRT did not confirm the code.", HttpStatusCode.OK);
        return verification;
    }

    /// <summary>Step 3 (new emails only): create the driver with the name shown on the leaderboard.</summary>
    public async Task<DriverMatch> CreateEmailProfileAsync(string driverToken, string displayName, CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-email",
            new JsonObject { ["action"] = "create-profile", ["rigId"] = _credential.RigId, ["driverToken"] = driverToken, ["displayName"] = displayName.Trim() }, cancellationToken);
        return json["driver"].Deserialize<DriverMatch>(Json) ?? throw new RigApiException("SRT did not return the driver.", HttpStatusCode.OK);
    }

    /// <summary>
    /// Starts a Discord sign-in shown as a QR code. "link": link Discord to the signed-in driver (pass their sign-in token;
    /// on an individual's PC the owner is used). "sign-in": sign a driver in at a sim centre rig with a linked Discord.
    /// </summary>
    public async Task<DiscordLinkStart> StartDiscordAsync(string purpose, string? driverToken = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["action"] = "start", ["purpose"] = purpose, ["rigId"] = _credential.RigId };
        if (driverToken is not null) body["driverToken"] = driverToken;
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-discord", body, cancellationToken);
        return json.Deserialize<DiscordLinkStart>(Json) ?? throw new RigApiException("SRT did not start the Discord sign-in.", HttpStatusCode.OK);
    }

    public async Task<DiscordLinkPoll> PollDiscordAsync(string pollToken, CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-discord", new JsonObject { ["action"] = "poll", ["pollToken"] = pollToken }, cancellationToken);
        return json.Deserialize<DiscordLinkPoll>(Json) ?? new DiscordLinkPoll("pending");
    }

    /// <summary>Sends one start-session / record-attempt / end-session message. The backend treats repeats as success.</summary>
    public Task PostIngestAsync(JsonObject body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/kerala-race-wars-ingest", body, cancellationToken);

    private async Task<DriverMatch> PostDriverAsync(JsonObject body, CancellationToken cancellationToken)
    {
        body["rigId"] = _credential.RigId;
        var json = await SendAsync(HttpMethod.Post, "api/kerala-race-wars-drivers", body, cancellationToken);
        return json["driver"].Deserialize<DriverMatch>(Json) ?? throw new RigApiException("SRT did not return the driver.", HttpStatusCode.OK);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string relativeUrl, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativeUrl));
        // Not "Authorization: Bearer": Azure Static Web Apps overwrites that header before it reaches the API.
        request.Headers.Add("X-Rig-Token", _credential.RigToken);
        if (body is not null) request.Content = JsonBody.Create(body);

        HttpResponseMessage response;
        try { response = await _client.SendAsync(request, cancellationToken); }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new RigApiException("SRT did not respond in time. Check the internet connection.", null, exception); }
        catch (HttpRequestException exception)
        { throw new RigApiException("SRT could not be reached. Check the internet connection.", null, exception); }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (JsonException) { }
            if (response.IsSuccessStatusCode) return json ?? new JsonObject();

            var message = json?["error"]?.GetValue<string>() ?? response.StatusCode switch
            {
                HttpStatusCode.NotFound => "This SRT feature is not deployed yet. Ask an admin to deploy the latest backend.",
                _ => $"SRT returned an error ({(int)response.StatusCode})."
            };
            // Only a rejected rig key means re-pairing; other refusals (expired code, used sign-in) keep SRT's own message.
            if (response.StatusCode == HttpStatusCode.Unauthorized && (json?["error"] is null || message == "Invalid rig credentials."))
                message = "This rig's SRT pairing is no longer valid. Pair the rig again.";
            throw new RigApiException(message, response.StatusCode);
        }
    }
}

/// <summary>SRT usernames: stored lowercase without "@", same character set as Discord usernames.</summary>
public static class SrtUsername
{
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    public static bool IsValid(string normalized) =>
        normalized.Length is >= 2 and <= 32
        && !normalized.Contains("..", StringComparison.Ordinal)
        && normalized.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '.');

    public static string Display(string normalized) => "@" + normalized;
}

/// <summary>Driver sign-in emails: trimmed and lowercased, the same rule SRT uses.</summary>
public static class EmailAddress
{
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string normalized)
    {
        if (normalized.Length is < 6 or > 254 || normalized.Any(char.IsWhiteSpace)) return false;
        var at = normalized.IndexOf('@');
        if (at < 1 || at != normalized.LastIndexOf('@')) return false;
        var domain = normalized[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return dot > 0 && domain.Length - dot - 1 >= 2 && domain[(dot + 1)..].All(char.IsAsciiLetterLower);
    }
}

/// <summary>Builds the ingest messages exactly as the backend contract expects them.</summary>
public static class IngestMessages
{
    public static JsonObject StartSession(string rigId, ActiveRigSession session) => new()
    {
        ["action"] = "start-session", ["rigId"] = rigId, ["sessionId"] = session.SessionId.ToString(),
        ["srtUsername"] = session.SrtUsername, ["displayName"] = session.DisplayName,
        ["eventId"] = session.EventId, ["startedAt"] = session.StartedAt.ToUniversalTime().ToString("O"),
        // Proof the driver verified their email on this rig. SRT takes the driver from it, not from the name above.
        ["driverToken"] = session.DriverToken
    };

    /// <summary>Once a minute: lets admins see the rig is online, its app version and any backlog waiting to sync.</summary>
    /// <param name="contentMissing">How many of the live event's required packages are not installed or skipped (null = unknown).</param>
    public static JsonObject Heartbeat(string rigId, string appVersion, int pendingSync, bool inSession, bool steamOk, int? contentMissing = null)
    {
        var message = new JsonObject
        {
            ["action"] = "heartbeat", ["rigId"] = rigId, ["appVersion"] = appVersion, ["pendingSync"] = pendingSync,
            ["state"] = inSession ? "session" : "idle", ["steamOk"] = steamOk
        };
        if (contentMissing is { } missing) message["contentMissing"] = missing;
        return message;
    }

    public static JsonObject EndSession(string rigId, Guid sessionId, DateTimeOffset endedAt, bool completed = true) => new()
    {
        ["action"] = "end-session", ["rigId"] = rigId, ["sessionId"] = sessionId.ToString(),
        ["status"] = completed ? "completed" : "aborted", ["endedAt"] = endedAt.ToUniversalTime().ToString("O")
    };

    public static JsonObject RecordAttempt(string rigId, Guid sessionId, string sourceAttemptId, int? lapNumber, int? lapTimeMs, bool? valid, DateTimeOffset capturedAt, JsonObject? payload = null) => new()
    {
        ["action"] = "record-attempt", ["rigId"] = rigId, ["sessionId"] = sessionId.ToString(),
        ["sourceAttemptId"] = sourceAttemptId, ["lapNumber"] = lapNumber, ["lapTimeMs"] = lapTimeMs, ["valid"] = valid,
        ["capturedAt"] = capturedAt.ToUniversalTime().ToString("O"), ["payload"] = payload
    };

    /// <summary>A lap from AC. Validity is left for admin review; the flags help them decide.</summary>
    public static JsonObject Lap(string rigId, Guid sessionId, CompletedLap lap) =>
        RecordAttempt(rigId, sessionId, lap.SourceAttemptId, lap.LapNumber, lap.LapTimeMs, valid: null, lap.CapturedAt, new JsonObject
        {
            ["source"] = "ac-shared-memory", ["acSession"] = lap.SessionTypeName, ["touchedPits"] = lap.TouchedPits,
            ["maxTyresOut"] = lap.MaxTyresOut, ["track"] = lap.Track, ["car"] = lap.Car
        });
}
