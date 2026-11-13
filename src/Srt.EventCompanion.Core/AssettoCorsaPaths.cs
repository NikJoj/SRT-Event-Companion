namespace Srt.EventCompanion.Core;

public sealed record AssettoCorsaPaths(string InstallDirectory, string UserDocumentsDirectory)
{
    public string RaceIniPath => Path.Combine(UserDocumentsDirectory, "cfg", "race.ini");

    public static AssettoCorsaPaths? Detect(string userProfileDirectory, IEnumerable<string>? installCandidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var documentsDirectory = Path.Combine(userProfileDirectory, "Documents", "Assetto Corsa");
        if (!Directory.Exists(documentsDirectory) || !File.Exists(Path.Combine(documentsDirectory, "cfg", "race.ini")))
        {
            return null;
        }

        var candidates = installCandidates ?? DefaultInstallCandidates();
        var installDirectory = candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "AssettoCorsa.exe")));
        return installDirectory is null ? null : new AssettoCorsaPaths(installDirectory, documentsDirectory);
    }

    private static IEnumerable<string> DefaultInstallCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "assettocorsa");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "assettocorsa");
    }
}
