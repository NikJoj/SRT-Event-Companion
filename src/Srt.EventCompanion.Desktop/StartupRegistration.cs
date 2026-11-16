using Microsoft.Win32;

namespace Srt.EventCompanion.Desktop;

/// <summary>
/// "Start with Windows": a value under HKCU\...\Run, the same one older installers created, so a rig set up with the
/// installer's checkbox is recognised. The app owns this setting now; the installer no longer writes it.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SRT Event Companion";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Turns starting with Windows on (pointing at this exe) or off. Returns false if Windows refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }
}
