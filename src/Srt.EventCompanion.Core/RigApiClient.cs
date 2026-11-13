using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Srt.EventCompanion.Core;

public sealed record RigInfo(string Id, string DisplayName, string CentreId, string CentreName);

public sealed record EventServerConfig(string Id, string Name, string ServerHost, int HttpPort, string? ServerPassword, string? Track, string? Car)
{

    public OnlineJoinTarget ToJoinTarget()
    {
        if (!IPAddress.TryParse(ServerHost, out var address)) throw new InvalidOperationException($"The live event's server IP '{ServerHost}' is not valid. Ask an SRT admin to fix it.");
        return new OnlineJoinTarget(address, HttpPort);
    }
}

/// <summary>What a rig needs before every launch: who it is, and the live event's server.</summary>
public sealed record RigConfig(RigInfo Rig, EventServerConfig? Event, AppRelease? App = null);

public sealed record DriverMatch(int ProfileId, string DisplayName, string? SrtUsername, string? DiscordUsername, string? SuggestedSrtUsername,
    string? LastDisplayName = null, DateTimeOffset? LastPlayedAt = null, int? SessionCount = null)
{
    /// <summary>The in-game name to pre-fill: what they used last time at this centre, else their profile name.</summary>
    public string PreferredInGameName => string.IsNullOrWhiteSpace(LastDisplayName) ? DisplayName : LastDisplayName;
}

public sealed record RecentDrivers(IReadOnlyList<DriverMatch> Drivers);

public sealed record DriverLookup(string Query, IReadOnlyList<DriverMatch> Matches, bool UsernameAvailable);

public sealed class RigApiException : Exception
{
    public RigApiException(string message, HttpStatusCode? statusCode, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;

    /// <summary>Null when SRT could not be reached at all.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The backend understood and refused the request; retrying the same request will not help.</summary>
    public bool IsPermanent => StatusCode is { } code && (int)code is >= 400 and < 500 && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}

/// <summary>Authenticated calls from a paired rig to the SRT Kerala Race Wars API.</summary>
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

    public async Task<RigConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"api/kerala-race-wars-config?rigId={Uri.EscapeDataString(_credential.RigId)}", null, cancellationToken);
        return json.Deserialize<RigConfig>(Json) ?? throw new RigApiException("SRT returned an empty rig configuration.", HttpStatusCode.OK);
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
            if (response.StatusCode == HttpStatusCode.Unauthorized) message = "This rig's SRT pairing is no longer valid. Pair the rig again.";
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

/// <summary>Builds the ingest messages exactly as the backend contract expects them.</summary>
public static class IngestMessages
{
    public static JsonObject StartSession(string rigId, ActiveRigSession session) => new()
    {
        ["action"] = "start-session", ["rigId"] = rigId, ["sessionId"] = session.SessionId.ToString(),
        ["srtUsername"] = session.SrtUsername, ["displayName"] = session.DisplayName,
        ["eventId"] = session.EventId, ["startedAt"] = session.StartedAt.ToUniversalTime().ToString("O")
    };

    /// <summary>Once a minute: lets admins see the rig is online, its app version and any backlog waiting to sync.</summary>
    public static JsonObject Heartbeat(string rigId, string appVersion, int pendingSync, bool inSession, bool steamOk) => new()
    {
        ["action"] = "heartbeat", ["rigId"] = rigId, ["appVersion"] = appVersion, ["pendingSync"] = pendingSync,
        ["state"] = inSession ? "session" : "idle", ["steamOk"] = steamOk
    };

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
