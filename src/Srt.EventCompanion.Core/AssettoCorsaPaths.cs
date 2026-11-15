using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Srt.EventCompanion.Core;

public enum AcMissing { None, GameFolder, SettingsFolder }

/// <summary>Result of looking for Assetto Corsa, with a staff-readable reason when something is missing.</summary>
public sealed record AssettoCorsaDetection(AssettoCorsaPaths? Paths, string? Problem, AcMissing Missing = AcMissing.None,
    string? GameFolderFound = null, bool UsedManualFolders = false);

/// <summary>Folders chosen by staff when automatic detection can't find Assetto Corsa. Saved per Windows user.</summary>
public sealed record AcFolderOverrides(string? InstallDirectory, string? DocumentsDirectory);

public sealed class AcFolderOverrideStore
{
    private readonly string _path;

    public AcFolderOverrideStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "ac-folders.json");

    public AcFolderOverrides Load()
    {
        try
        {
            return File.Exists(_path)
                ? System.Text.Json.JsonSerializer.Deserialize<AcFolderOverrides>(File.ReadAllText(_path)) ?? new(null, null)
                : new(null, null);
        }
        catch (Exception) { return new(null, null); }
    }

    public void Save(AcFolderOverrides overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(overrides));
    }

    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }
}

public sealed record AssettoCorsaPaths(string InstallDirectory, string UserDocumentsDirectory)
{
    public const string SteamAppId = "244210";

    public string RaceIniPath => Path.Combine(UserDocumentsDirectory, "cfg", "race.ini");

    /// <summary>Looks in a given user profile only (used by tests and tools).</summary>
    public static AssettoCorsaPaths? Detect(string userProfileDirectory, IEnumerable<string>? installCandidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);
        return Diagnose(
            new[] { Path.Combine(userProfileDirectory, "Documents", "Assetto Corsa") },
            installCandidates ?? DefaultInstallCandidates()).Paths;
    }

    /// <summary>
    /// Finds Assetto Corsa on this PC:
    /// - settings: the Windows Documents folder (follows OneDrive / moved Documents), then %USERPROFILE%\Documents;
    /// - game: Steam's own record of where app 244210 is installed, every Steam library folder, then the default paths.
    /// </summary>
    public static AssettoCorsaDetection DetectOnThisPc(AcFolderOverrides? manual = null)
    {
        var documents = new List<string>();
        var installs = new List<string>();
        if (manual?.DocumentsDirectory is { } d) documents.Add(d);
        if (manual?.InstallDirectory is { } i) installs.Add(i);
        documents.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assetto Corsa"));
        documents.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Assetto Corsa"));
        installs.AddRange(ThisPcInstallCandidates());
        var result = Diagnose(documents, installs);
        var usedManual = result.Paths is not null &&
            (string.Equals(result.Paths.InstallDirectory, manual?.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(result.Paths.UserDocumentsDirectory, manual?.DocumentsDirectory, StringComparison.OrdinalIgnoreCase));
        return result with { UsedManualFolders = usedManual };
    }

    /// <summary>
    /// Turns a folder staff picked into the Assetto Corsa game folder, forgiving near misses
    /// (steamapps\common, the Steam library root, or a folder inside the game folder). Null if AC isn't there.
    /// </summary>
    public static string? ResolveGameFolder(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return null;
        var candidates = new List<string>
        {
            picked,
            Path.Combine(picked, "assettocorsa"),
            Path.Combine(picked, "common", "assettocorsa"),
            Path.Combine(picked, "steamapps", "common", "assettocorsa")
        };
        var parent = Directory.GetParent(picked)?.FullName;
        if (parent is not null) candidates.Add(parent);
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "AssettoCorsa.exe")));
    }

    /// <summary>
    /// Turns a folder staff picked into the AC settings folder (the one holding cfg\race.ini), forgiving
    /// Documents itself or the cfg folder. Null if race.ini isn't there.
    /// </summary>
    public static string? ResolveSettingsFolder(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return null;
        var candidates = new List<string> { picked, Path.Combine(picked, "Assetto Corsa") };
        var parent = Directory.GetParent(picked)?.FullName;
        if (parent is not null) candidates.Add(parent);
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "cfg", "race.ini")));
    }

    public static AssettoCorsaDetection Diagnose(IEnumerable<string> documentsCandidates, IEnumerable<string> installCandidates)
    {
        var installDirectory = installCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(c => File.Exists(Path.Combine(c, "AssettoCorsa.exe")));
        if (installDirectory is null)
            return new(null, "Assetto Corsa's game folder was not found automatically. If AC is installed, select " +
                             "CHOOSE GAME FOLDER below and pick it. If it isn't, install it from Steam, then select CHECK AGAIN.",
                AcMissing.GameFolder);

        var documentsDirectory = documentsCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .FirstOrDefault(c => File.Exists(Path.Combine(c, "cfg", "race.ini")));
        if (documentsDirectory is null)
            return new(null, "Assetto Corsa is installed, but its settings folder (Documents\\Assetto Corsa, with cfg\\race.ini inside) " +
                             "was not found. Start AC once and close it, then select CHECK AGAIN, or select CHOOSE SETTINGS FOLDER below.",
                AcMissing.SettingsFolder, installDirectory);

        return new(new AssettoCorsaPaths(installDirectory, documentsDirectory), null);
    }

    /// <summary>Library folder paths listed in Steam's steamapps\libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> ParseSteamLibraries(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value.Replace("\\\\", "\\"))
            .ToList();

    private static IEnumerable<string> ThisPcInstallCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var c in WindowsInstallCandidates()) yield return c;
        }
        foreach (var c in DefaultInstallCandidates()) yield return c;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> WindowsInstallCandidates()
    {
        var found = new List<string>();

        // 1. Steam registers each installed game for Add/Remove Programs, with its folder.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}");
                if (key?.GetValue("InstallLocation") is string location) found.Add(location);
            }
            catch (Exception) { /* not readable: fall through to the other checks */ }
        }

        // 2. Every Steam library folder (games on D:\SteamLibrary etc.).
        foreach (var steam in SteamInstallDirectories())
        {
            var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libraries = new List<string> { steam };
            try
            {
                if (File.Exists(vdfPath)) libraries.AddRange(ParseSteamLibraries(File.ReadAllText(vdfPath)));
            }
            catch (Exception) { /* unreadable file: use the main Steam folder only */ }
            found.AddRange(libraries.Select(l => Path.Combine(l, "steamapps", "common", "assettocorsa")));
        }

        return found;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> SteamInstallDirectories()
    {
        var dirs = new List<string>();
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string p) dirs.Add(p.Replace('/', '\\'));
        }
        catch (Exception) { /* ignore */ }
        foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            try
            {
                using var machine = Registry.LocalMachine.OpenSubKey(sub);
                if (machine?.GetValue("InstallPath") is string p) dirs.Add(p);
            }
            catch (Exception) { /* ignore */ }
        }
        return dirs;
    }

    private static IEnumerable<string> DefaultInstallCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "assettocorsa");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "assettocorsa");
    }
}
