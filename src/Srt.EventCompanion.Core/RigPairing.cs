using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Srt.EventCompanion.Core;

public sealed record RigCredential(string ApiBaseUrl, string RigId, string RigToken, string SteamId);

[SupportedOSPlatform("windows")]
public sealed class RigCredentialStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "rig-credential.dat");

    public void Save(RigCredential credential)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credential)), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
    }

    public RigCredential? Load()
    {
        if (!File.Exists(_path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<RigCredential>(Encoding.UTF8.GetString(bytes));
    }
}

/// <summary>
/// JSON request body with a Content-Length. Azure Static Web Apps rejects streamed (chunked) bodies with
/// "Backend call failure" (HTTP 500), which is what HttpClient sends for JsonContent.
/// </summary>
public static class JsonBody
{
    public static StringContent Create<T>(T value) =>
        new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
}

/// <summary>A problem the person at the rig can understand: what went wrong, what to do, and whether SRT Labs must help.</summary>
public sealed class PairingException : Exception
{
    public PairingException(string problem, string nextStep, bool contactSupport, Exception? inner = null) : base(problem, inner)
    {
        NextStep = nextStep;
        ContactSupport = contactSupport;
    }

    public string NextStep { get; }

    /// <summary>True when there is nothing the sim centre can fix themselves.</summary>
    public bool ContactSupport { get; }
}

public sealed class RigPairingService
{
    public const string SupportMessage = "Still not working? Contact the SRT Labs team and tell them the message above.";
    private static readonly Uri SrtApiBaseUri = new("https://www.simracingtharavadu.in");
    private readonly HttpClient _client;

    public RigPairingService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Codes are shown as ABCD-EFGH-JKMN; case, spaces and dashes do not matter.</summary>
    public static string NormalizeCode(string? code) => new((code ?? string.Empty).Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());

    /// <param name="signedInSteamId">The Steam account signed in on this PC; SRT refuses to pair if it is not the rig's registered account.</param>
    public async Task<RigCredential> PairAsync(string pairingCode, string? signedInSteamId, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(pairingCode);
        if (code.Length == 0)
            throw new PairingException("No pairing code entered.", "Type the code shown in SRT admin (Kerala Race Wars → Rigs → Pair).", contactSupport: false);
        if (signedInSteamId is null)
            throw new PairingException("Steam is not signed in on this PC.", "Open Steam and sign in to the account registered for this rig, then select Try again. The code still works.", contactSupport: false);

        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync(new Uri(SrtApiBaseUri, "/api/kerala-race-wars-pair"), JsonBody.Create(new { pairingCode = code, steamId = signedInSteamId }), cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PairingException("SRT did not respond in time.", "Check this PC's internet connection, then select Try again.", contactSupport: false, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PairingException("This PC can't reach SRT.", "Check the internet connection (and any firewall or antivirus blocking the app), then select Try again.", contactSupport: false, exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            PairingResponse? payload = null;
            string? serverMessage = null;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("error", out var error)) serverMessage = error.GetString();
                payload = json.Deserialize<PairingResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException) { /* Not JSON: handled by the status code below. */ }

            switch ((int)response.StatusCode)
            {
                case 400 or 401 or 409 when serverMessage is not null:
                    // The backend's message already says what is wrong and what to do (expired, used, mistyped, rig switched off).
                    throw new PairingException(serverMessage, string.Empty, contactSupport: false);
                case 404:
                    throw new PairingException("SRT's pairing service is not available.", "This needs fixing on SRT's side.", contactSupport: true);
                case >= 500:
                    throw new PairingException("SRT had a problem on its side.", "Wait a minute and select Try again.", contactSupport: true);
            }

            if (!response.IsSuccessStatusCode || payload?.RigId is null || payload.RigToken is null || !Regex.IsMatch(payload.SteamId ?? string.Empty, "^\\d{17}$"))
                throw new PairingException($"SRT returned an unexpected reply ({(int)response.StatusCode}).", "Get a fresh code from SRT admin and select Try again.", contactSupport: true);

            return new RigCredential(SrtApiBaseUri.GetLeftPart(UriPartial.Authority), payload.RigId, payload.RigToken, payload.SteamId!);
        }
    }

    private sealed record PairingResponse(string? RigId, string? RigToken, string? SteamId);
}
