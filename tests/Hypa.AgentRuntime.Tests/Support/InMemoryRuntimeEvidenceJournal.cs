using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// In-memory <see cref="IRuntimeEvidenceJournal"/> for unit tests.
/// One cursor row per (session_id, consumer); <c>export_id</c> is updated on upsert.
/// </summary>
public sealed class InMemoryRuntimeEvidenceJournal : IRuntimeEvidenceJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SessionId, string Consumer), ExportAckRecord> _bySessionConsumer =
        new();
    private readonly Dictionary<string, (string SessionId, string Consumer)> _exportIndex =
        new(StringComparer.Ordinal);

    public Task<RuntimeResult<ExportAckRecord>> AckAsync(
        string sessionId,
        string exportId,
        long lastSeq,
        string consumer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        var c = string.IsNullOrWhiteSpace(consumer) ? "atomic" : consumer.Trim();
        var now = DateTimeOffset.UtcNow;
        var key = (sessionId, c);

        lock (_gate)
        {
            if (_exportIndex.TryGetValue(exportId, out var existingKey) &&
                (existingKey.SessionId != sessionId || existingKey.Consumer != c))
            {
                return Task.FromResult(RuntimeResult<ExportAckRecord>.Fail(
                    RuntimePersistenceError.Db(
                        "export_id already bound to a different session/consumer")));
            }

            long storedSeq = lastSeq;
            if (_bySessionConsumer.TryGetValue(key, out var existing))
            {
                storedSeq = Math.Max(existing.LastAckedSeq, lastSeq);

                if (!string.Equals(existing.ExportId, exportId, StringComparison.Ordinal))
                    _exportIndex.Remove(existing.ExportId);
            }

            var record = new ExportAckRecord
            {
                ExportId = exportId,
                SessionId = sessionId,
                Consumer = c,
                LastAckedSeq = storedSeq,
                UpdatedAt = now,
            };
            _bySessionConsumer[key] = record;
            _exportIndex[exportId] = key;
            return Task.FromResult(RuntimeResult<ExportAckRecord>.Ok(record));
        }
    }

    public Task<RuntimeResult<ExportAckRecord?>> GetAsync(
        string exportId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        lock (_gate)
        {
            if (!_exportIndex.TryGetValue(exportId, out var key) ||
                !_bySessionConsumer.TryGetValue(key, out var record) ||
                !string.Equals(record.ExportId, exportId, StringComparison.Ordinal))
            {
                return Task.FromResult(RuntimeResult<ExportAckRecord?>.Ok(null));
            }

            return Task.FromResult(RuntimeResult<ExportAckRecord?>.Ok(record));
        }
    }
}
