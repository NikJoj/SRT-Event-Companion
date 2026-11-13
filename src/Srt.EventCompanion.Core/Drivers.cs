using System.Text.Json;

namespace Srt.EventCompanion.Core;

/// <summary>Filters the centre's driver list as staff type: any part of the name, SRT username or last in-game name.</summary>
public static class DriverFilter
{
    public static IReadOnlyList<DriverMatch> Filter(IEnumerable<DriverMatch> drivers, string? text)
    {
        var terms = (text ?? string.Empty).Trim().TrimStart('@').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return drivers.ToList();
        return drivers.Where(driver =>
        {
            var haystack = $"{driver.DisplayName} {driver.SrtUsername} {driver.LastDisplayName}";
            return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    public static string Describe(DriverMatch driver)
    {
        var name = driver.DisplayName;
        var user = driver.SrtUsername is null ? string.Empty : $"  ·  @{driver.SrtUsername}";
        var when = driver.LastPlayedAt is { } at ? $"  ·  last raced {at.ToLocalTime():d MMM}" : string.Empty;
        return name + user + when;
    }
}

public sealed record FavouriteDriver(string SrtUsername, string DisplayName, string InGameName, int ProfileId);

public enum FavouriteResult { Added, AlreadyFavourite, Full }

/// <summary>Up to five drivers this rig's staff starred for one-click selection. Stored on this PC only.</summary>
public sealed class FavouriteDriverStore
{
    public const int MaxFavourites = 5;
    private readonly string _path;

    public FavouriteDriverStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "favourite-drivers.json");
    }

    public IReadOnlyList<FavouriteDriver> Load()
    {
        try
        {
            return File.Exists(_path)
                ? (JsonSerializer.Deserialize<List<FavouriteDriver>>(File.ReadAllText(_path)) ?? new()).Take(MaxFavourites).ToList()
                : new List<FavouriteDriver>();
        }
        catch (JsonException)
        {
            return new List<FavouriteDriver>(); // A damaged file just means no favourites; it is rewritten on the next change.
        }
    }

    public bool Contains(string srtUsername) => Load().Any(favourite => Same(favourite.SrtUsername, srtUsername));

    public FavouriteResult Add(FavouriteDriver driver)
    {
        var favourites = Load().ToList();
        if (favourites.Any(favourite => Same(favourite.SrtUsername, driver.SrtUsername))) return FavouriteResult.AlreadyFavourite;
        if (favourites.Count >= MaxFavourites) return FavouriteResult.Full;
        favourites.Add(driver);
        Save(favourites);
        return FavouriteResult.Added;
    }

    public void Remove(string srtUsername) =>
        Save(Load().Where(favourite => !Same(favourite.SrtUsername, srtUsername)).ToList());

    /// <summary>Keeps the stored in-game name current after a driver changes it.</summary>
    public void UpdateInGameName(string srtUsername, string inGameName)
    {
        var favourites = Load().ToList();
        var index = favourites.FindIndex(favourite => Same(favourite.SrtUsername, srtUsername));
        if (index < 0 || favourites[index].InGameName == inGameName) return;
        favourites[index] = favourites[index] with { InGameName = inGameName };
        Save(favourites);
    }

    private void Save(List<FavouriteDriver> favourites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(favourites, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static bool Same(string a, string b) => string.Equals(SrtUsername.Normalize(a), SrtUsername.Normalize(b), StringComparison.Ordinal);
}
