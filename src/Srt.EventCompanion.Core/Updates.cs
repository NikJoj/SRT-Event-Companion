using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace Srt.EventCompanion.Core;

/// <summary>A companion app version published by an SRT admin; every rig installs it when idle.</summary>
public sealed record AppRelease(string Version, string DownloadUrl, string Sha256, string? Notes = null, DateTimeOffset? PublishedAt = null);

public static class AppVersion
{
    /// <summary>This build's version, e.g. "1.2.0" (any "+commit" suffix removed).</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    /// <summary>True when the published release is newer than what is running.</summary>
    public static bool IsNewer(string published, string running) =>
        System.Version.TryParse(published, out var a) && System.Version.TryParse(running, out var b) && a > b;
}

/// <summary>Downloads a published release, checks it against the admin's SHA-256 and hands over to the installer.</summary>
public sealed class AppUpdater
{
    private readonly HttpClient _client;
    private readonly string _folder;

    public AppUpdater(HttpClient? client = null, string? folder = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _folder = folder ?? Path.Combine(Path.GetTempPath(), "SrtEventCompanion", "updates");
    }

    /// <summary>Returns the path of a verified installer. Throws if the download does not match the published SHA-256.</summary>
    public async Task<string> DownloadAsync(AppRelease release, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The published update link is not a valid https:// address. Contact the SRT Labs team.");

        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, $"SRT-Event-Companion-Setup-{release.Version}.exe");
        var partial = target + ".download";

        // Reuse an earlier, already-verified download.
        if (File.Exists(target) && await MatchesAsync(target, release.Sha256, cancellationToken)) return target;

        using (var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, cancellationToken);
        }

        if (!await MatchesAsync(partial, release.Sha256, cancellationToken))
        {
            File.Delete(partial);
            throw new InvalidOperationException($"The downloaded update v{release.Version} failed its security check (SHA-256 mismatch) and was deleted. Contact the SRT Labs team.");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Starts the installer silently; it waits for this app to exit, installs, then reopens the app.</summary>
    public static void StartInstaller(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });

    public static async Task<bool> MatchesAsync(string path, string expectedSha256, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
