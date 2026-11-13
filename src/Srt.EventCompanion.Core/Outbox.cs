using System.Text.Json;
using System.Text.Json.Nodes;

namespace Srt.EventCompanion.Core;

public sealed record OutboxMessage(Guid Id, JsonObject Body, DateTimeOffset CreatedAt)
{
    public string Action => Body["action"]?.GetValue<string>() ?? "unknown";
}

public sealed record OutboxFlushResult(int Sent, int Rejected, int Remaining, string? LastError)
{
    public bool Offline => Remaining > 0;
}

/// <summary>
/// Durable, ordered queue of messages for SRT. Every session and lap message is written here first and
/// only removed once SRT accepts it, so nothing is lost when the sim centre's internet drops or the rig restarts.
/// One small JSON file per message; files are written atomically and sent in the order they were queued.
/// </summary>
public sealed class OutboxStore
{
    private static readonly object TicksLock = new();
    private static long _lastTicks;
    private readonly string _directory;
    private readonly string _failedDirectory;

    public OutboxStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrtEventCompanion", "outbox");
        _failedDirectory = Path.Combine(_directory, "rejected");
    }

    public int Count => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json").Length : 0;

    public OutboxMessage Enqueue(JsonObject body)
    {
        Directory.CreateDirectory(_directory);
        var message = new OutboxMessage(Guid.NewGuid(), body, DateTimeOffset.UtcNow);
        var path = Path.Combine(_directory, $"{NextOrderKey():D20}-{message.Id:N}.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StoredMessage(message.Id, message.CreatedAt, body.ToJsonString())));
        File.Move(temporaryPath, path, overwrite: false);
        return message;
    }

    public IReadOnlyList<OutboxMessage> Pending()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<OutboxMessage>();
        var messages = new List<OutboxMessage>();
        foreach (var file in Directory.GetFiles(_directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredMessage>(File.ReadAllText(file));
                if (stored is not null && JsonNode.Parse(stored.Body) is JsonObject body) messages.Add(new OutboxMessage(stored.Id, body, stored.CreatedAt));
                else MoveToRejected(file, "Unreadable message.");
            }
            catch (JsonException) { MoveToRejected(file, "Unreadable message."); }
        }
        return messages;
    }

    public void Remove(Guid id)
    {
        foreach (var file in FilesFor(id)) File.Delete(file);
    }

    /// <summary>Keeps a copy of a message SRT refused, for support, and takes it out of the queue.</summary>
    public void Reject(Guid id, string reason)
    {
        foreach (var file in FilesFor(id)) MoveToRejected(file, reason);
    }

    private IEnumerable<string> FilesFor(Guid id) =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, $"*-{id:N}.json") : Enumerable.Empty<string>();

    private void MoveToRejected(string file, string reason)
    {
        Directory.CreateDirectory(_failedDirectory);
        var destination = Path.Combine(_failedDirectory, Path.GetFileName(file));
        File.Move(file, destination, overwrite: true);
        File.WriteAllText(destination + ".reason.txt", reason);
    }

    private static long NextOrderKey()
    {
        lock (TicksLock)
        {
            _lastTicks = Math.Max(DateTime.UtcNow.Ticks, _lastTicks + 1);
            return _lastTicks;
        }
    }

    private sealed record StoredMessage(Guid Id, DateTimeOffset CreatedAt, string Body);
}

/// <summary>Sends queued messages in order. Stops at the first network failure so ordering is preserved.</summary>
public sealed class OutboxSender
{
    private readonly OutboxStore _store;
    private readonly Func<JsonObject, CancellationToken, Task> _send;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxSender(OutboxStore store, Func<JsonObject, CancellationToken, Task> send)
    {
        _store = store;
        _send = send;
    }

    public async Task<OutboxFlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            int sent = 0, rejected = 0;
            string? lastError = null;
            foreach (var message in _store.Pending())
            {
                try
                {
                    await _send(message.Body, cancellationToken);
                    _store.Remove(message.Id);
                    sent++;
                }
                catch (RigApiException exception) when (exception.IsPermanent)
                {
                    // SRT refused it (for example a lap captured after the session ended). Retrying cannot succeed.
                    _store.Reject(message.Id, $"{(int?)exception.StatusCode}: {exception.Message}");
                    rejected++;
                    lastError = exception.Message;
                }
                catch (RigApiException exception)
                {
                    lastError = exception.Message;
                    break;
                }
            }
            return new OutboxFlushResult(sent, rejected, _store.Count, lastError);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Retry spacing while offline: 15 s, 30 s, 60 s … capped at 5 minutes.</summary>
public sealed class RetryBackoff
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Maximum = TimeSpan.FromMinutes(5);
    private TimeSpan _current = TimeSpan.Zero;

    public DateTimeOffset NextAttemptAt { get; private set; } = DateTimeOffset.MinValue;

    public TimeSpan Failed(DateTimeOffset now)
    {
        _current = _current == TimeSpan.Zero ? Initial : TimeSpan.FromTicks(Math.Min(_current.Ticks * 2, Maximum.Ticks));
        NextAttemptAt = now + _current;
        return _current;
    }

    public void Succeeded()
    {
        _current = TimeSpan.Zero;
        NextAttemptAt = DateTimeOffset.MinValue;
    }
}
