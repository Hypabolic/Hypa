using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// In-memory <see cref="ICheckpointStore"/> for unit tests. Optionally materializes
/// under a temp root when <paramref name="stateDirectory"/> is provided.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CheckpointRecord> _records = new(StringComparer.Ordinal);
    private readonly string _stateDirectory;

    public InMemoryCheckpointStore(string? stateDirectory = null)
    {
        _stateDirectory = stateDirectory ?? Path.Combine(Path.GetTempPath(), "hypa-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);
    }

    public string StateDirectory => _stateDirectory;

    public Task<RuntimeResult<CheckpointRecord>> SaveAsync(
        CheckpointRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.TryGetValue(record.CheckpointId, out var existing)
                && !CheckpointStates.CanTransition(existing.State, record.State))
            {
                return Task.FromResult(RuntimeResult<CheckpointRecord>.Fail(
                    RuntimePersistenceError.Conflict(
                        $"illegal checkpoint transition {existing.State} -> {record.State}")));
            }

            Directory.CreateDirectory(GetCheckpointDirectory(record.CheckpointId));
            _records[record.CheckpointId] = record;
            return Task.FromResult(RuntimeResult<CheckpointRecord>.Ok(record));
        }
    }

    public Task<RuntimeResult<CheckpointRecord?>> GetAsync(
        string checkpointId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        lock (_gate)
        {
            _records.TryGetValue(checkpointId, out var record);
            return Task.FromResult(RuntimeResult<CheckpointRecord?>.Ok(record));
        }
    }

    public Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            IReadOnlyList<CheckpointRecord> list = _records.Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(RuntimeResult<IReadOnlyList<CheckpointRecord>>.Ok(list));
        }
    }

    public async Task<RuntimeResult<CheckpointManifestWriteResult>> WriteManifestAsync(
        string checkpointId,
        string manifestJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        try
        {
            var dir = GetCheckpointDirectory(checkpointId);
            Directory.CreateDirectory(dir);
            var abs = Path.Combine(dir, "manifest.json");
            var bytes = Encoding.UTF8.GetBytes(manifestJson);
            await File.WriteAllBytesAsync(abs, bytes, ct).ConfigureAwait(false);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var rel = GetCheckpointRelativeDirectory(checkpointId) + "/manifest.json";
            return RuntimeResult<CheckpointManifestWriteResult>.Ok(new CheckpointManifestWriteResult
            {
                ManifestRelativePath = rel,
                ManifestSha256 = sha,
                ByteCount = bytes.LongLength,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<CheckpointManifestWriteResult>.Fail(
                RuntimePersistenceError.Io(ex.Message));
        }
    }

    public string GetCheckpointDirectory(string checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        return Path.Combine(_stateDirectory, "checkpoints", SanitizeId(checkpointId));
    }

    public string GetCheckpointRelativeDirectory(string checkpointId) =>
        "checkpoints/" + SanitizeId(checkpointId);

    private static string SanitizeId(string checkpointId)
    {
        if (checkpointId.Contains("..", StringComparison.Ordinal) ||
            checkpointId.Contains('/') ||
            checkpointId.Contains('\\'))
        {
            throw new ArgumentException("checkpoint_id must not contain path separators", nameof(checkpointId));
        }

        return checkpointId;
    }
}
