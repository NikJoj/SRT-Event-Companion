using System.Text.Json;

namespace Srt.EventCompanion.Core;

/// <summary>How this copy of the app is used, chosen in first-run setup and changeable in Settings.</summary>
public enum AppMode { Centre, Individual }

/// <param name="SetupDone">False until first-run setup has been completed.</param>
/// <param name="StartWithWindows">The app starts when this Windows user signs in.</param>
/// <param name="AutoUpdate">Install updates published by SRT on their own (between sessions); otherwise only show the update button.</param>
/// <param name="LastEventId">The event picked last time, chosen again by default.</param>
public sealed record AppSettings(bool SetupDone, AppMode Mode, bool StartWithWindows, bool AutoUpdate, string? LastEventId = null)
{
    /// <summary>A brand-new install: setup runs first.</summary>
    public static AppSettings NotSetUp { get; } = new(false, AppMode.Centre, StartWithWindows: false, AutoUpdate: true);

    /// <summary>
    /// A rig updated from 1.0.x has no settings file but is already paired: keep it working exactly as before
    /// (sim centre, automatic updates, and starting with Windows if the installer set that up).
    /// </summary>
    public static AppSettings ForExistingRig(bool startsWithWindows) => new(true, AppMode.Centre, startsWithWindows, AutoUpdate: true);

    /// <summary>Suggested choices when setup asks how the app will be used.</summary>
    public static AppSettings Defaults(AppMode mode) => new(false, mode, StartWithWindows: mode == AppMode.Centre, AutoUpdate: true);
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _path;

    public AppSettingsStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "settings.json");

    public bool Exists => File.Exists(_path);

    /// <summary>The saved settings, or null when there are none (new install, or a rig from before settings existed).</summary>
    public AppSettings? Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) : null; }
        catch (JsonException) { return null; }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Which live event to select when the event list loads.</summary>
public static class EventChoice
{
    /// <summary>The last event used on this PC if it is still live; otherwise the only one, or the newest.</summary>
    public static EventServerConfig? Default(IReadOnlyList<EventServerConfig> liveEvents, string? lastEventId) =>
        liveEvents.FirstOrDefault(e => string.Equals(e.Id, lastEventId, StringComparison.OrdinalIgnoreCase)) ?? liveEvents.FirstOrDefault();

    /// <summary>One line for the event list: name, then track and car when set.</summary>
    public static string Describe(EventServerConfig evt)
    {
        var detail = string.Join(" · ", new[] { evt.Track, evt.Car }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return detail.Length == 0 ? evt.Name : $"{evt.Name}  —  {detail}";
    }
}
