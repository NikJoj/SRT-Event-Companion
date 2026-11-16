using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Srt.EventCompanion.Core;

/// <summary>Result of checking the emailed code. NeedsName: the email is new; ask for a leaderboard name and verify again.</summary>
public sealed record IndividualSignUpResult(bool NeedsName, RigCredential? Credential, RigOwner? Driver, bool MovedFromAnotherPc);

/// <summary>
/// Individual drivers sign up from their own PC with their email (no pairing code, no sim centre).
/// SRT makes the PC a rig the driver owns and returns its key; the driver is then the driver of every session.
/// </summary>
public sealed class IndividualSignUpService
{
    private static readonly Uri SrtApiBaseUri = new("https://www.simracingtharavadu.in");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public IndividualSignUpService(HttpClient? client = null, Uri? baseUri = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _baseUri = baseUri ?? SrtApiBaseUri;
    }

    public async Task<EmailCodeSent> SendCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = EmailAddress.Normalize(email);
        if (!EmailAddress.IsValid(normalized)) throw new ArgumentException("Enter your email address, for example name@gmail.com.", nameof(email));
        var json = await PostAsync(new JsonObject { ["action"] = "send-code", ["email"] = normalized }, cancellationToken);
        return json.Deserialize<EmailCodeSent>(Json) ?? throw new RigApiException("SRT did not confirm the code was sent.", HttpStatusCode.OK);
    }

    /// <param name="steamId">The Steam account signed in on this PC (laps are matched to it).</param>
    /// <param name="displayName">Only for a new email: the name to show on leaderboards.</param>
    public async Task<IndividualSignUpResult> VerifyAsync(string email, string code, string? steamId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        var digits = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6) throw new ArgumentException("Enter the 6-digit code from the email.", nameof(code));
        if (steamId is null) throw new InvalidOperationException("Steam is not signed in on this PC. Open Steam and sign in to your account, then try again. The code still works.");
        var body = new JsonObject { ["action"] = "verify-code", ["email"] = EmailAddress.Normalize(email), ["code"] = digits, ["steamId"] = steamId };
        if (!string.IsNullOrWhiteSpace(displayName)) body["displayName"] = displayName.Trim();
        var json = await PostAsync(body, cancellationToken);
        if (json["needsProfile"]?.GetValue<bool>() == true) return new IndividualSignUpResult(true, null, null, false);

        var rigId = json["rigId"]?.GetValue<string>();
        var rigToken = json["rigToken"]?.GetValue<string>();
        var savedSteam = json["steamId"]?.GetValue<string>();
        var driver = json["driver"]?.Deserialize<RigOwner>(Json);
        if (rigId is null || rigToken is null || driver is null || !Regex.IsMatch(savedSteam ?? string.Empty, "^\\d{17}$"))
            throw new RigApiException("SRT returned an unexpected reply. Try again, or contact the SRT Labs team.", HttpStatusCode.OK);
        return new IndividualSignUpResult(false, new RigCredential(_baseUri.GetLeftPart(UriPartial.Authority), rigId, rigToken, savedSteam!), driver,
            json["movedFromAnotherPc"]?.GetValue<bool>() == true);
    }

    /// <summary>Set up this PC with a Discord account already linked to an SRT driver (instead of email).</summary>
    public async Task<DiscordLinkStart> StartDiscordAsync(string? steamId, CancellationToken cancellationToken = default)
    {
        if (steamId is null) throw new InvalidOperationException("Steam is not signed in on this PC. Open Steam and sign in to your account, then try again.");
        var json = await PostAsync(new JsonObject { ["action"] = "start", ["purpose"] = "individual", ["steamId"] = steamId }, cancellationToken, "/api/kerala-race-wars-discord");
        return json.Deserialize<DiscordLinkStart>(Json) ?? throw new RigApiException("SRT did not start the Discord sign-in.", HttpStatusCode.OK);
    }

    public async Task<DiscordLinkPoll> PollDiscordAsync(string pollToken, CancellationToken cancellationToken = default)
    {
        var json = await PostAsync(new JsonObject { ["action"] = "poll", ["pollToken"] = pollToken }, cancellationToken, "/api/kerala-race-wars-discord");
        return json.Deserialize<DiscordLinkPoll>(Json) ?? new DiscordLinkPoll("pending");
    }

    /// <summary>The PC's SRT sign-in from a finished Discord sign-in.</summary>
    public RigCredential CredentialFrom(DiscordLinkPoll result)
    {
        if (result.RigId is null || result.RigToken is null || !Regex.IsMatch(result.SteamId ?? string.Empty, "^\\d{17}$"))
            throw new RigApiException("SRT returned an unexpected reply. Try again, or contact the SRT Labs team.", HttpStatusCode.OK);
        return new RigCredential(_baseUri.GetLeftPart(UriPartial.Authority), result.RigId, result.RigToken, result.SteamId!);
    }

    private async Task<JsonNode> PostAsync(JsonObject body, CancellationToken cancellationToken, string path = "/api/kerala-race-wars-individual")
    {
        HttpResponseMessage response;
        try { response = await _client.PostAsync(new Uri(_baseUri, path), JsonBody.Create(body), cancellationToken); }
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
                HttpStatusCode.NotFound => "Sign-up for individual drivers is not available yet. Contact the SRT Labs team.",
                _ => $"SRT returned an error ({(int)response.StatusCode})."
            };
            throw new RigApiException(message, response.StatusCode);
        }
    }
}
