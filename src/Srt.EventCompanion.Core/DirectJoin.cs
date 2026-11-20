using System.Net;
using System.Text.Json;

namespace Srt.EventCompanion.Core;

/// <summary>What the race server says about itself (its /INFO page).</summary>
public sealed record ServerInfo(int TcpPort, string Track, IReadOnlyList<string> Cars);

/// <summary>One car slot on the race server, as seen by a given Steam account (its /JSON|&lt;steamId&gt; page).</summary>
public sealed record ServerSlot(string Model, string? Skin, bool IsConnected, bool IsReservedForYou, bool IsEntryList);

/// <summary>
/// Everything Content Manager needs to put the driver straight on the server, with no join dialog:
/// <c>acmanager://race/online?ip=…&amp;port=…&amp;httpPort=…&amp;car=…&amp;track=…&amp;name=…&amp;plainPassword=…</c>.
/// </summary>
public sealed record DirectJoinPlan(IPAddress ServerAddress, int TcpPort, int HttpPort, string Track, string Car, string? Skin,
    string? Password, string DriverName)
{
    public Uri ToUri()
    {
        var query = new List<string>
        {
            $"ip={ServerAddress}",
            $"port={TcpPort}",
            $"httpPort={HttpPort}",
            $"car={Uri.EscapeDataString(Car)}",
            $"track={Uri.EscapeDataString(Track)}",
            $"name={Uri.EscapeDataString(DriverName)}"
        };
        if (!string.IsNullOrWhiteSpace(Skin)) query.Add($"skin={Uri.EscapeDataString(Skin)}");
        if (!string.IsNullOrEmpty(Password)) query.Add($"plainPassword={Uri.EscapeDataString(Password)}");
        return new UriBuilder("acmanager", "race", -1, "/online") { Query = string.Join("&", query) }.Uri;
    }
}

public sealed class NoSlotException(string message) : InvalidOperationException(message);

public sealed class ContentMissingException(string message) : InvalidOperationException(message);

/// <summary>
/// Asks the race server which car this Steam account may drive, so the game can be started directly.
/// Uses the same public pages Content Manager reads: /INFO and /JSON|&lt;steamId&gt;.
/// </summary>
public static class ServerJoinResolver
{
    /// <param name="carInstalled">Is this car folder (content/cars/…) on the PC?</param>
    /// <param name="trackInstalled">Is this track folder (content/tracks/…) on the PC?</param>
    public static async Task<DirectJoinPlan> ResolveAsync(HttpClient http, OnlineJoinTarget target, string steamId, string driverName,
        string? password, Func<string, bool> carInstalled, Func<string, bool> trackInstalled, CancellationToken cancellationToken = default)
    {
        var baseUri = $"http://{target.ServerAddress}:{target.HttpPort}";
        var info = ParseInfo(await http.GetStringAsync($"{baseUri}/INFO", cancellationToken));
        var slots = ParseSlots(await http.GetStringAsync($"{baseUri}/JSON|{Uri.EscapeDataString(steamId)}", cancellationToken));
        var slot = ChooseSlot(slots, carInstalled);
        // Content Manager stops with "Track is missing" when it can't find the track, so check first and let the caller
        // fall back to the join window (which can download missing content) instead.
        if (!carInstalled(slot.Model)) throw new ContentMissingException($"The car {slot.Model} is not installed on this PC.");
        // A dash usually separates the layout ("ks_silverstone-gp"), but some track folders have one ("trento-bondone").
        if (!trackInstalled(TrackFolder(info.Track)) && !trackInstalled(info.Track)) throw new ContentMissingException($"The track {TrackFolder(info.Track)} is not installed on this PC.");
        return new DirectJoinPlan(target.ServerAddress, info.TcpPort, target.HttpPort, info.Track, slot.Model, slot.Skin, password, driverName);
    }

    /// <summary>
    /// The server's track as Content Manager knows it. Servers that require a Custom Shaders Patch version report it
    /// encoded, e.g. "csp/3465/../D/../mugello"; the track ID is the part after the last "/../". Layouts stay attached
    /// ("ks_silverstone-gp"): Content Manager splits those itself.
    /// </summary>
    public static string TrackId(string serverTrack)
    {
        var marker = serverTrack.LastIndexOf("/../", StringComparison.Ordinal);
        return serverTrack.StartsWith("csp/", StringComparison.OrdinalIgnoreCase) && marker >= 0 ? serverTrack[(marker + 4)..] : serverTrack;
    }

    /// <summary>The track's folder under content/tracks: the track ID without a layout ("ks_silverstone-gp" → "ks_silverstone").</summary>
    public static string TrackFolder(string serverTrack)
    {
        var id = TrackId(serverTrack);
        var dash = id.LastIndexOf('-');
        return dash > 0 ? id[..dash] : id;
    }

    /// <summary>
    /// The slot reserved for this Steam account wins (locked entry lists). Otherwise the first open slot
    /// with a car that is installed on this PC. Throws <see cref="NoSlotException"/> when there is none,
    /// which is what the server would answer with "no slots available".
    /// </summary>
    public static ServerSlot ChooseSlot(IReadOnlyList<ServerSlot> slots, Func<string, bool> carInstalled)
    {
        var reserved = slots.FirstOrDefault(s => s.IsReservedForYou);
        if (reserved is not null) return reserved;

        var open = slots.FirstOrDefault(s => !s.IsConnected && !s.IsEntryList && carInstalled(s.Model));
        if (open is not null) return open;

        if (slots.Any(s => s.IsEntryList && !s.IsReservedForYou))
            throw new NoSlotException("This PC's Steam account has no car on the server's entry list. Ask an SRT admin to add its Steam ID to the event.");
        throw new NoSlotException("The server has no free car right now. Try again in a moment.");
    }

    public static ServerInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // tport is the TCP port the game connects to; old servers only report "port" (the same number by default).
        var tcp = Int(root, "tport") is int t and > 0 ? t : Int(root, "port") ?? throw new InvalidDataException("The server did not report its port.");
        var track = TrackId(Str(root, "track") ?? throw new InvalidDataException("The server did not report its track."));
        var cars = root.TryGetProperty("cars", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
        return new ServerInfo(tcp, track, cars);
    }

    public static IReadOnlyList<ServerSlot> ParseSlots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Cars", out var cars) || cars.ValueKind != JsonValueKind.Array) return Array.Empty<ServerSlot>();
        return cars.EnumerateArray()
            .Where(c => Str(c, "Model") is { Length: > 0 })
            .Select(c => new ServerSlot(Str(c, "Model")!, Str(c, "Skin"), Bool(c, "IsConnected"), Bool(c, "IsRequestedGUID"), Bool(c, "IsEntryList")))
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
