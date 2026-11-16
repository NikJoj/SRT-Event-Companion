using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Srt.EventCompanion.Core;

/// <summary>A mod (car, track or app) the live event requires. Targets are folders relative to the AC folder.</summary>
public sealed record ContentPackage(string Id, string Name, string Kind, string Sha256, long SizeBytes, IReadOnlyList<string> Targets);

/// <summary>A one-hour signed link for one package, from /api/kerala-race-wars-content.</summary>
public sealed record PackageDownload(string Url, string Sha256, long SizeBytes, string Name, int ExpiresInSeconds);

public enum ContentStatus
{
    /// <summary>Installed by this app, same file as the event requires.</summary>
    Installed,
    /// <summary>Staff said the rig already has it.</summary>
    Skipped,
    /// <summary>Its folders exist (installed some other way), but SRT has not installed this exact package.</summary>
    FoundOnRig,
    /// <summary>Not on this rig.</summary>
    Missing
}

public sealed record ContentCheck(ContentPackage Package, ContentStatus Status)
{
    public bool IsReady => Status is ContentStatus.Installed or ContentStatus.Skipped;

    public string Label => Status switch
    {
        ContentStatus.Installed => "✓ Installed",
        ContentStatus.Skipped => "✓ Skipped (already on this rig)",
        ContentStatus.FoundOnRig => "Found on this rig, not installed by SRT",
        _ => "Not installed"
    };
}

// ── Where zip entries go: the same rules as the admin page (js/krw-package.js). Keep the two in step. ──────
public sealed record PlannedFile(string ZipEntry, string RelativePath, long Size);
public sealed record InstallPlan(IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> Targets, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Files.Count > 0;
}

public static class PackagePlanner
{
    public static readonly IReadOnlyDictionary<string, string> KindBase = new Dictionary<string, string>
    {
        ["car"] = "content/cars", ["track"] = "content/tracks", ["weather"] = "content/weather",
        ["python-app"] = "apps/python", ["lua-app"] = "apps/lua", ["ac-root"] = ""
    };

    private static readonly Regex Junk = new(@"(^|/)(__MACOSX/|\.DS_Store$|Thumbs\.db$|desktop\.ini$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Mods from SRT admins are trusted (any file type, any folder); a target only has to stay inside the AC folder.</summary>
    public static bool IsValidTarget(string target) =>
        target.Length is > 0 and <= 200 && !target.StartsWith('/') && !target.StartsWith('\\') && !Regex.IsMatch(target, "^[A-Za-z]:|[\\x00-\\x1f]") &&
        !target.Split('/').Any(p => p is "" or "." or "..");

    public static InstallPlan Plan(IEnumerable<(string Name, long Size)> entries, string kind)
    {
        var errors = new List<string>();
        if (!KindBase.ContainsKey(kind)) return new InstallPlan([], [], [$"Unknown package kind '{kind}'."]);

        var files = new List<(string Entry, string Name, long Size)>();
        foreach (var (raw, size) in entries)
        {
            var name = raw.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.EndsWith('/') || Junk.IsMatch(name)) continue;
            if (name.StartsWith('/') || Regex.IsMatch(name, "^[A-Za-z]:") || name.Split('/').Any(p => p is "" or "." or ".."))
            {
                errors.Add($"Unsafe path in zip: {name}"); continue;
            }
            files.Add((raw, name, size));
        }
        if (files.Count == 0 && errors.Count == 0) errors.Add("The zip has no files to install.");

        // A zip that wraps the AC layout in one folder ("MyMod/content/cars/...") is unwrapped.
        if (files.Count > 0)
        {
            var first = files[0].Name.Split('/')[0];
            var wrapped = files.All(f =>
            {
                var parts = f.Name.Split('/');
                return parts.Length > 1 && parts[0] == first && (parts[1].Equals("content", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("apps", StringComparison.OrdinalIgnoreCase));
            });
            if (wrapped) files = files.Select(f => (f.Entry, f.Name[(first.Length + 1)..], f.Size)).ToList();
        }

        var planned = new List<PlannedFile>();
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (entry, name, size) in files)
        {
            // Zips already laid out like the AC folder (content/…, apps/…, extension/…) go as they are; otherwise the kind decides.
            var rel = kind == "ac-root" || Regex.IsMatch(name, "^(content|apps)/", RegexOptions.IgnoreCase)
                ? Regex.Replace(Regex.Replace(name, "^content/", "content/", RegexOptions.IgnoreCase), "^apps/", "apps/", RegexOptions.IgnoreCase)
                : KindBase[kind] + "/" + name;
            var parts = rel.Split('/');
            // What the package installs: the mod folder (e.g. content/cars/<id>), or a loose file where it lands.
            var target = parts.Length >= 4 ? string.Join('/', parts.Take(3)) : rel;
            if (!IsValidTarget(target)) { errors.Add($"Unsafe path in zip: {name}"); continue; }
            targets.Add(target);
            planned.Add(new PlannedFile(entry, rel, size));
        }
        if (targets.Count > 50) errors.Add($"Too many top-level folders ({targets.Count}); 50 is the most per package.");
        return new InstallPlan(planned, targets.ToList(), errors);
    }
}

// ── What this rig has installed or skipped ──────────────────────────────────
public sealed record ContentRecord(string Sha256, string State, DateTimeOffset At, IReadOnlyList<string> Targets);

public sealed class ContentStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public ContentStateStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "event-content.json");

    public Dictionary<string, ContentRecord> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, ContentRecord>>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (Exception) { return new(); }
    }

    public void Save(string packageId, ContentRecord record)
    {
        var all = Load();
        all[packageId] = record;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        File.Move(temp, _path, overwrite: true);
    }
}

public static class ContentChecker
{
    public static ContentCheck Check(ContentPackage package, string acInstallDirectory, IReadOnlyDictionary<string, ContentRecord> records)
    {
        var present = package.Targets.Count > 0 && package.Targets.All(t => TargetExists(acInstallDirectory, t));
        if (records.TryGetValue(package.Id, out var record) && string.Equals(record.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (record.State == "skipped") return new ContentCheck(package, ContentStatus.Skipped);
            if (record.State == "installed" && present) return new ContentCheck(package, ContentStatus.Installed);
        }
        return new ContentCheck(package, present ? ContentStatus.FoundOnRig : ContentStatus.Missing);
    }

    public static IReadOnlyList<ContentCheck> CheckAll(IEnumerable<ContentPackage> packages, string acInstallDirectory, ContentStateStore store)
    {
        var records = store.Load();
        return packages.Select(p => Check(p, acInstallDirectory, records)).ToList();
    }

    public static bool TargetExists(string acInstallDirectory, string target)
    {
        var full = Path.Combine(acInstallDirectory, target.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(full) || File.Exists(full);
    }
}

public sealed record ContentProgress(string Stage, double Fraction);

/// <summary>Downloads, verifies and installs one package into the Assetto Corsa folder. AC and CM must be closed first.</summary>
public sealed class ContentInstaller
{
    private readonly HttpClient _http;
    private readonly string _cacheFolder;
    private readonly ContentStateStore _store;

    public ContentInstaller(HttpClient? http = null, string? cacheFolder = null, ContentStateStore? store = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _cacheFolder = cacheFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "content-cache");
        _store = store ?? new ContentStateStore();
    }

    /// <summary>
    /// getLink asks SRT for a fresh signed link (they last an hour). Returns what was installed and anything left untouched.
    /// </summary>
    public async Task<InstallResult> InstallAsync(ContentPackage package, Func<CancellationToken, Task<PackageDownload>> getLink,
        string acInstallDirectory, string? acDocumentsDirectory, IProgress<ContentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(acInstallDirectory)) throw new InvalidOperationException("The Assetto Corsa folder was not found. Check the rig setup.");
        var zip = await DownloadAsync(package, getLink, acInstallDirectory, progress, cancellationToken);

        progress?.Report(new ContentProgress("Checking files", 0));
        InstallPlan plan;
        using (var archive = ZipFile.OpenRead(zip))
            plan = PackagePlanner.Plan(archive.Entries.Select(e => (e.FullName, e.Length)), package.Kind);
        if (!plan.IsValid)
            throw new InvalidOperationException($"{package.Name} can't be installed safely: {plan.Errors.FirstOrDefault()} Contact SRT Labs.");

        var root = Path.GetFullPath(acInstallDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(root, $"_srt_staging_{package.Id}_{stamp}");
        var backup = Path.Combine(root, "_srt_backup", $"{stamp}_{package.Id}");
        var skippedTargets = new List<string>();
        try
        {
            // 1. Unpack into a staging folder on the same drive as AC (so the final move is instant).
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var byName = archive.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
                var total = Math.Max(1, plan.Files.Sum(f => f.Size));
                long done = 0;
                foreach (var file in plan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = SafePath(staging, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    byName[file.ZipEntry].ExtractToFile(destination, overwrite: true);
                    done += file.Size;
                    progress?.Report(new ContentProgress("Unpacking", (double)done / total));
                }
            }

            // 2. Move each target into AC. Anything already there is backed up first (restore it from _srt_backup).
            progress?.Report(new ContentProgress("Installing", 1));
            foreach (var target in plan.Targets)
            {
                var source = SafePath(staging, target);
                var destination = SafePath(root, target);
                var exists = Directory.Exists(destination) || File.Exists(destination);
                if (exists)
                {
                    var saved = SafePath(backup, target);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    if (Directory.Exists(destination)) Directory.Move(destination, saved); else File.Move(destination, saved);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
            }

            // 3. Python apps only run when switched on in Documents\Assetto Corsa\cfg\python.ini.
            var enabledApps = new List<string>();
            if (acDocumentsDirectory is not null)
            {
                foreach (var app in plan.Targets.Where(t => t.StartsWith("apps/python/", StringComparison.Ordinal)).Select(t => t["apps/python/".Length..]))
                {
                    EnablePythonApp(Path.Combine(acDocumentsDirectory, "cfg", "python.ini"), app);
                    enabledApps.Add(app);
                }
            }

            _store.Save(package.Id, new ContentRecord(package.Sha256, "installed", DateTimeOffset.Now, plan.Targets));
            TryDelete(zip);
            return new InstallResult(plan.Targets.Except(skippedTargets).ToList(), skippedTargets, Directory.Exists(backup) ? backup : null, enabledApps);
        }
        finally
        {
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
        }
    }

    public void MarkSkipped(ContentPackage package) =>
        _store.Save(package.Id, new ContentRecord(package.Sha256, "skipped", DateTimeOffset.Now, package.Targets));

    private async Task<string> DownloadAsync(ContentPackage package, Func<CancellationToken, Task<PackageDownload>> getLink,
        string acInstallDirectory, IProgress<ContentProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheFolder);
        var zip = Path.Combine(_cacheFolder, $"{package.Id}.zip");
        var partial = zip + ".download";
        if (File.Exists(zip) && await AppUpdater.MatchesAsync(zip, package.Sha256, cancellationToken)) return zip;

        EnsureSpace(_cacheFolder, package.SizeBytes - (File.Exists(partial) ? new FileInfo(partial).Length : 0), "downloading");
        EnsureSpace(acInstallDirectory, package.SizeBytes * 2, "unpacking");

        for (var attempt = 0; ; attempt++)
        {
            var link = await getLink(cancellationToken);
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("SRT sent an invalid download link. Contact SRT Labs.");
            if (!string.Equals(link.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{package.Name} changed on SRT since this screen opened. Close this window and check again.");

            var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null); // resume an interrupted download
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0) continue; // link expired mid-resume: get a new one
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); continue; }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Downloading {package.Name} failed ({(int)response.StatusCode}). Check the internet connection and try again.");

            var resuming = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 20];
                long written = resuming ? have : 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    progress?.Report(new ContentProgress("Downloading", package.SizeBytes > 0 ? Math.Min(1, (double)written / package.SizeBytes) : 0));
                }
            }
            break;
        }

        progress?.Report(new ContentProgress("Verifying download", 1));
        if (!await AppUpdater.MatchesAsync(partial, package.Sha256, cancellationToken))
        {
            File.Delete(partial);
            throw new InvalidOperationException($"{package.Name} failed its security check (SHA-256 mismatch) and was deleted. Try again; if it repeats, contact SRT Labs.");
        }
        File.Move(partial, zip, overwrite: true);
        return zip;
    }

    /// <summary>Resolves a relative path under root and refuses anything that would land outside it.</summary>
    public static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Refused to write outside the Assetto Corsa folder: {relative}");
        return full;
    }

    public static void EnablePythonApp(string pythonIniPath, string appFolder)
    {
        var section = appFolder.ToUpperInvariant();
        var lines = File.Exists(pythonIniPath) ? File.ReadAllLines(pythonIniPath).ToList() : new List<string>();
        var start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add("ACTIVE=1");
        }
        else
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var active = lines.FindIndex(start + 1, end - start - 1, l => l.Trim().StartsWith("ACTIVE", StringComparison.OrdinalIgnoreCase));
            if (active >= 0) lines[active] = "ACTIVE=1"; else lines.Insert(start + 1, "ACTIVE=1");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(pythonIniPath)!);
        File.WriteAllLines(pythonIniPath, lines);
    }

    private static void EnsureSpace(string folder, long needed, string what)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            if (needed > 0 && drive.AvailableFreeSpace < needed + 200L * 1024 * 1024)
                throw new InvalidOperationException($"Not enough free space on drive {drive.Name} for {what}: need about {needed / 1_000_000_000.0:0.0} GB. Free up space and try again.");
        }
        catch (ArgumentException) { /* unusual path (network share): let the copy itself report problems */ }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch (Exception) { } }
}

public sealed record InstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> LeftUntouched, string? BackupFolder, IReadOnlyList<string> EnabledPythonApps);
