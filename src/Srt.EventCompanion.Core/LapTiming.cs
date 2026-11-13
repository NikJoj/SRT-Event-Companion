using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace Srt.EventCompanion.Core;

/// <summary>AC_STATUS from Assetto Corsa's shared memory.</summary>
public enum AcStatus { Off = 0, Replay = 1, Live = 2, Pause = 3 }

/// <summary>One reading of the values lap timing needs from Assetto Corsa's shared memory.</summary>
public sealed record AcTelemetrySample(
    AcStatus Status,
    int SessionType,
    int CompletedLaps,
    int LastLapMs,
    int BestLapMs,
    bool IsInPit,
    int TyresOut,
    string? Track,
    string? Car);

/// <summary>A completed lap, ready to send to SRT.</summary>
public sealed record CompletedLap(int LapNumber, int LapTimeMs, DateTimeOffset CapturedAt, bool TouchedPits, int MaxTyresOut, int SessionType, string? Track, string? Car)
{
    /// <summary>Stable per lap so a resend is recognised as the same lap by SRT.</summary>
    public string SourceAttemptId => $"lap-{LapNumber}-{LapTimeMs}";

    /// <summary>AC's own session type on the server (practice, qualify, race…), kept for reference.</summary>
    public string SessionTypeName => SessionType switch
    {
        0 => "practice", 1 => "qualify", 2 => "race", 3 => "hotlap", 4 => "time_attack", 5 => "drift", 6 => "drag", _ => "unknown"
    };
}

/// <summary>
/// Turns a stream of shared-memory samples into completed laps. Pure logic, no game needed, fully unit-tested.
/// A lap is reported when AC's completed-lap counter goes up and its "last lap" time has been updated.
/// Validity is not decided here: laps that touched the pit lane or went wide are flagged for admin review.
/// </summary>
public sealed class LapDetector
{
    private static readonly TimeSpan LastLapTimeGrace = TimeSpan.FromSeconds(1.5);
    private int? _completedLaps;
    private int _lastLapMsAtCount;
    private bool _touchedPits;
    private int _maxTyresOut;
    private PendingLap? _pending;

    public CompletedLap? Observe(AcTelemetrySample sample, DateTimeOffset now)
    {
        if (sample.Status != AcStatus.Live)
        {
            // Off / replay / paused: nothing to record. If AC is closed, start fresh next time it runs.
            if (sample.Status == AcStatus.Off) Reset();
            return null;
        }

        if (_completedLaps is null || sample.CompletedLaps < _completedLaps)
        {
            // First reading, or the server moved to a new session (counter went back to 0).
            Reset();
            _completedLaps = sample.CompletedLaps;
            _lastLapMsAtCount = sample.LastLapMs;
            Track(sample);
            return null;
        }

        CompletedLap? finished = null;
        if (sample.CompletedLaps > _completedLaps)
        {
            finished = FinishPending(sample, now, force: true);
            _pending = new PendingLap(sample.CompletedLaps, _lastLapMsAtCount, now, _touchedPits, _maxTyresOut);
            _completedLaps = sample.CompletedLaps;
            _touchedPits = false;
            _maxTyresOut = 0;
        }

        finished ??= FinishPending(sample, now, force: false);
        Track(sample);
        return finished;
    }

    /// <summary>Called just before the game is closed, so a lap finished in the last moment is not lost.</summary>
    public CompletedLap? FlushPending(AcTelemetrySample? latest, DateTimeOffset now) =>
        latest is null ? null : FinishPending(latest, now, force: true);

    private CompletedLap? FinishPending(AcTelemetrySample sample, DateTimeOffset now, bool force)
    {
        if (_pending is not { } pending) return null;

        // AC can update the lap counter a frame before the lap time; wait briefly for the new time.
        var timeUpdated = sample.LastLapMs > 0 && sample.LastLapMs != pending.PreviousLastLapMs;
        if (!timeUpdated && !force && now - pending.CountedAt < LastLapTimeGrace) return null;

        _pending = null;
        _lastLapMsAtCount = sample.LastLapMs;
        if (sample.LastLapMs <= 0) return null;
        return new CompletedLap(pending.LapNumber, sample.LastLapMs, pending.CountedAt, pending.TouchedPits, pending.MaxTyresOut,
            sample.SessionType, sample.Track, sample.Car);
    }

    private void Track(AcTelemetrySample sample)
    {
        if (sample.IsInPit) _touchedPits = true;
        _maxTyresOut = Math.Max(_maxTyresOut, sample.TyresOut);
    }

    private void Reset()
    {
        _completedLaps = null;
        _pending = null;
        _touchedPits = false;
        _maxTyresOut = 0;
    }

    private sealed record PendingLap(int LapNumber, int PreviousLastLapMs, DateTimeOffset CountedAt, bool TouchedPits, int MaxTyresOut);
}

/// <summary>
/// Reads Assetto Corsa's shared memory pages (Local\acpmf_graphics, acpmf_physics, acpmf_static).
/// Offsets follow AC's published SPageFile structs (4-byte packing, UTF-16 strings).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcSharedMemoryReader : IDisposable
{
    // SPageFileGraphic
    private const int GraphicsStatus = 4, GraphicsSession = 8, GraphicsCompletedLaps = 132, GraphicsLastTime = 144, GraphicsBestTime = 148, GraphicsIsInPit = 160;
    // SPageFilePhysics
    private const int PhysicsTyresOut = 244;
    // SPageFileStatic
    private const int StaticCarModel = 68, StaticTrack = 134, StaticStringChars = 33;

    private MemoryMappedViewAccessor? _graphics, _physics, _static;
    private readonly List<MemoryMappedFile> _files = new();

    /// <summary>Returns null while Assetto Corsa is not running (the pages do not exist yet).</summary>
    public AcTelemetrySample? Read()
    {
        if (!EnsureOpen()) return null;
        try
        {
            var status = (AcStatus)_graphics!.ReadInt32(GraphicsStatus);
            return new AcTelemetrySample(
                status,
                _graphics.ReadInt32(GraphicsSession),
                _graphics.ReadInt32(GraphicsCompletedLaps),
                _graphics.ReadInt32(GraphicsLastTime),
                _graphics.ReadInt32(GraphicsBestTime),
                _graphics.ReadInt32(GraphicsIsInPit) != 0,
                _physics!.ReadInt32(PhysicsTyresOut),
                ReadString(_static!, StaticTrack, StaticStringChars),
                ReadString(_static!, StaticCarModel, StaticStringChars));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Close();
            return null;
        }
    }

    private bool EnsureOpen()
    {
        if (_graphics is not null) return true;
        try
        {
            _graphics = Open("Local\\acpmf_graphics");
            _physics = Open("Local\\acpmf_physics");
            _static = Open("Local\\acpmf_static");
            return true;
        }
        catch (FileNotFoundException)
        {
            Close();
            return false;
        }
    }

    private MemoryMappedViewAccessor Open(string name)
    {
        var file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        _files.Add(file);
        return file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    private static string? ReadString(MemoryMappedViewAccessor view, int offset, int maxChars)
    {
        var bytes = new byte[maxChars * 2];
        view.ReadArray(offset, bytes, 0, bytes.Length);
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }

    private void Close()
    {
        _graphics?.Dispose(); _physics?.Dispose(); _static?.Dispose();
        _graphics = _physics = _static = null;
        foreach (var file in _files) file.Dispose();
        _files.Clear();
    }

    public void Dispose() => Close();
}

public static class LapTimeFormat
{
    public static string Format(int milliseconds) =>
        milliseconds <= 0 ? "—" : $"{milliseconds / 60000}:{milliseconds % 60000 / 1000:00}.{milliseconds % 1000:000}";
}
