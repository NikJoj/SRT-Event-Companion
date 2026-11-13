namespace Srt.EventCompanion.Core;

public sealed record DriverProfile(string? OfflineDriverName, string? MultiplayerDriverName);

public sealed record ProfileChangePreview(DriverProfile Current, DriverProfile Proposed);

public sealed class RigProfileService
{
    public string? ReadSteamId(string raceIniPath)
    {
        var ini = IniDocument.Parse(File.ReadAllText(raceIniPath));
        return ini.GetValue("REMOTE", "GUID");
    }
    public DriverProfile Read(string raceIniPath)
    {
        var ini = IniDocument.Parse(File.ReadAllText(raceIniPath));
        return new DriverProfile(ini.GetValue("CAR_0", "DRIVER_NAME"), ini.GetValue("REMOTE", "NAME"));
    }

    public ProfileChangePreview PreviewDriverNameChange(string raceIniPath, string driverName)
    {
        ValidateDriverName(driverName);
        return new ProfileChangePreview(Read(raceIniPath), new DriverProfile(driverName, driverName));
    }

    public string ApplyDriverNameChange(string raceIniPath, string driverName, DateTimeOffset now)
    {
        ValidateDriverName(driverName);
        var originalContent = File.ReadAllText(raceIniPath);
        var ini = IniDocument.Parse(originalContent);
        ini.SetValue("CAR_0", "DRIVER_NAME", driverName);
        ini.SetValue("REMOTE", "NAME", driverName);

        var backupPath = $"{raceIniPath}.{now:yyyyMMddHHmmss}.bak";
        var temporaryPath = $"{raceIniPath}.{Guid.NewGuid():N}.tmp";
        File.Copy(raceIniPath, backupPath, overwrite: false);
        try
        {
            File.WriteAllText(temporaryPath, ini.ToString());
            File.Replace(temporaryPath, raceIniPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return backupPath;
    }

    private static void ValidateDriverName(string driverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        if (driverName.Length > 64) throw new ArgumentException("Driver name must be 64 characters or fewer.", nameof(driverName));
        if (driverName.Contains('\r') || driverName.Contains('\n')) throw new ArgumentException("Driver name cannot include a line break.", nameof(driverName));
    }
}
