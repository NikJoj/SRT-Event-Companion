using System.Text.Json;

namespace Srt.EventCompanion.Core;

public sealed record ActiveRigSession(
    Guid SessionId,
    string SrtUsername,
    string DisplayName,
    string RaceIniPath,
    string RaceIniBackupPath,
    string ContentManagerValuesPath,
    string ContentManagerBackupPath,
    DateTimeOffset StartedAt,
    string? EventId = null,
    string? RigDefaultName = null);

public sealed class ActiveSessionStore
{
    private readonly string _path;

    public ActiveSessionStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "active-session.json");
    }

    public ActiveRigSession? Load()
    {
        if (!File.Exists(_path)) return null;
        return JsonSerializer.Deserialize<ActiveRigSession>(File.ReadAllText(_path));
    }

    public void Save(ActiveRigSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Active session path has no directory."));
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(session));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed record RestoreOutcome(bool ContentManagerFromBackup, bool RaceIniFromBackup, string? FallbackNameUsed);

/// <summary>
/// Puts the rig's own name back after a session. The name saved in the launch backup wins;
/// if a backup is missing, the rig name from SRT is written instead.
/// </summary>
public sealed class SessionRestorationService
{
    private readonly RigProfileService _rigProfileService;
    private readonly ContentManagerSettingsService _contentManagerSettings;

    public SessionRestorationService(RigProfileService? rigProfileService = null, ContentManagerSettingsService? contentManagerSettings = null)
    {
        _rigProfileService = rigProfileService ?? new RigProfileService();
        _contentManagerSettings = contentManagerSettings ?? new ContentManagerSettingsService();
    }

    public void Restore(ActiveRigSession session) => Restore(session, fallbackName: null, DateTimeOffset.Now);

    public RestoreOutcome Restore(ActiveRigSession session, string? fallbackName, DateTimeOffset now)
    {
        var cmFromBackup = File.Exists(session.ContentManagerBackupPath);
        var raceFromBackup = File.Exists(session.RaceIniBackupPath);
        var fallback = string.IsNullOrWhiteSpace(fallbackName) ? session.RigDefaultName : fallbackName;
        if ((!cmFromBackup || !raceFromBackup) && string.IsNullOrWhiteSpace(fallback))
        {
            throw new FileNotFoundException("A session backup is missing and the rig's name could not be fetched from SRT. Check the internet connection and try again.",
                cmFromBackup ? session.RaceIniBackupPath : session.ContentManagerBackupPath);
        }

        if (cmFromBackup) RestoreFile(session.ContentManagerBackupPath, session.ContentManagerValuesPath);
        else _contentManagerSettings.ApplyPlayerNameChange(session.ContentManagerValuesPath, fallback!, now);

        if (raceFromBackup) RestoreFile(session.RaceIniBackupPath, session.RaceIniPath);
        else _rigProfileService.ApplyDriverNameChange(session.RaceIniPath, fallback!, now);

        return new RestoreOutcome(cmFromBackup, raceFromBackup, cmFromBackup && raceFromBackup ? null : fallback);
    }

    /// <summary>Copies a backup over the live file atomically.</summary>
    public static void RestoreFile(string backupPath, string destinationPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("The session backup file was not found.", backupPath);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(backupPath, temporaryPath, overwrite: true);
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
