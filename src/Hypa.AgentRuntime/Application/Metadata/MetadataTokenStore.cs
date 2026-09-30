namespace Hypa.AgentRuntime.Application.Metadata;

public sealed record MetadataTokenExpiry(string Kind, string Id, IReadOnlyDictionary<string, string> Values);

public sealed record MetadataTokenPatchResult(
    bool Accepted,
    bool Changed,
    bool StaleSequence,
    string? Error,
    IReadOnlyDictionary<string, string> Values);

/// <summary>In-memory live metadata token store. Tokens are never persisted.</summary>
public sealed class MetadataTokenStore : IDisposable
{
    private sealed class Resource
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Expiry { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Sequences { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> KeySources { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private bool _disposed;

    public MetadataTokenStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public event Action<MetadataTokenExpiry>? Expired;

    /// <summary>
    /// Raised from the TTL timer. A host that emits from <see cref="ExpireDue"/>
    /// must subscribe here so the timer does not expire keys before that sweep.
    /// The timer callback waits for each handler so emission can complete before
    /// the callback returns.
    /// </summary>
    public event Func<Task>? SweepRequested;

    public MetadataTokenPatchResult ApplyPatch(
        string kind,
        string id,
        string? source,
        IReadOnlyDictionary<string, string?>? tokens,
        long? sequence = null,
        int? ttlMs = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id))
            return Reject("resource identity is required");
        if (!MetadataTokenNormalizer.TryNormalizeSource(source, out var normalizedSource))
            return Reject("source is invalid");
        if (tokens is null || tokens.Count == 0 || tokens.Count > MetadataTokenLimits.MaxKeysPerReport)
            return Reject("tokens must contain 1 to 16 keys");
        if (!MetadataTokenNormalizer.TryNormalizeTtl(ttlMs, out var ttl))
            return Reject("ttl_ms is out of range");

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in tokens)
        {
            if (!MetadataTokenNormalizer.TryNormalizeKey(pair.Key, out var key))
                return Reject("token key is invalid");
            normalized[key] = MetadataTokenNormalizer.NormalizeValue(pair.Value);
        }

        lock (_gate)
        {
            var resourceKey = Key(kind, id);
            _resources.TryGetValue(resourceKey, out var existing);
            existing ??= new Resource();
            if (sequence is { } seq)
            {
                if (!existing.Sequences.ContainsKey(normalizedSource)
                    && existing.Sequences.Count >= MetadataTokenLimits.MaxSequencedSources)
                {
                    return Reject("too many sequenced sources");
                }
                if (existing.Sequences.TryGetValue(normalizedSource, out var previous) && seq <= previous)
                    return new MetadataTokenPatchResult(true, false, true, null, SnapshotValues(existing));
            }

            var retained = new HashSet<string>(existing.Values.Keys, StringComparer.Ordinal);
            foreach (var pair in normalized)
            {
                if (pair.Value.Length == 0)
                    retained.Remove(pair.Key);
                else
                    retained.Add(pair.Key);
            }
            if (retained.Count > MetadataTokenLimits.MaxRetainedKeys)
                return Reject("too many retained token keys");

            var changed = false;
            var instant = now ?? _time.GetUtcNow();
            foreach (var pair in normalized)
            {
                if (pair.Value.Length == 0)
                {
                    changed |= existing.Values.Remove(pair.Key);
                    changed |= existing.Expiry.Remove(pair.Key);
                    changed |= existing.KeySources.Remove(pair.Key);
                    continue;
                }

                changed |= !existing.Values.TryGetValue(pair.Key, out var old) || old != pair.Value;
                existing.Values[pair.Key] = pair.Value;
                existing.KeySources[pair.Key] = normalizedSource;
                if (ttl is { } duration)
                    existing.Expiry[pair.Key] = instant.Add(duration);
                else
                    changed |= existing.Expiry.Remove(pair.Key);
            }

            if (sequence is { } acceptedSequence)
                existing.Sequences[normalizedSource] = acceptedSequence;
            if (existing.Values.Count == 0 && existing.Sequences.Count == 0)
                _resources.Remove(resourceKey);
            else
                _resources[resourceKey] = existing;
            ArmTimerLocked();
            return new MetadataTokenPatchResult(true, changed, false, null, SnapshotValues(existing));
        }
    }

    public int ClearSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 0;
        var removed = 0;
        lock (_gate)
        {
            foreach (var pair in _resources.ToArray())
            {
                var resource = pair.Value;
                var keys = resource.KeySources
                    .Where(item => string.Equals(item.Value, source, StringComparison.Ordinal))
                    .Select(item => item.Key)
                    .ToArray();
                foreach (var key in keys)
                {
                    resource.Values.Remove(key);
                    resource.Expiry.Remove(key);
                    resource.KeySources.Remove(key);
                    removed++;
                }

                resource.Sequences.Remove(source);
                if (resource.Values.Count == 0 && resource.Sequences.Count == 0)
                    _resources.Remove(pair.Key);
            }
        }

        return removed;
    }

    public IReadOnlyDictionary<string, string> Get(string kind, string id)
    {
        lock (_gate)
            return _resources.TryGetValue(Key(kind, id), out var resource)
                ? SnapshotValues(resource)
                : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Snapshot()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var pair in _resources)
                result[pair.Key] = SnapshotValues(pair.Value);
            return result;
        }
    }

    public IReadOnlyList<MetadataTokenExpiry> ExpireDue(DateTimeOffset now)
    {
        List<MetadataTokenExpiry> expired = [];
        lock (_gate)
        {
            foreach (var pair in _resources.ToArray())
            {
                var resource = pair.Value;
                var removed = resource.Expiry
                    .Where(item => item.Value <= now)
                    .Select(item => item.Key)
                    .ToArray();
                if (removed.Length == 0)
                    continue;
                foreach (var key in removed)
                {
                    resource.Expiry.Remove(key);
                    resource.Values.Remove(key);
                }
                if (resource.Values.Count == 0 && resource.Sequences.Count == 0)
                    _resources.Remove(pair.Key);
                expired.Add(ToExpiry(pair.Key, resource));
            }
            ArmTimerLocked();
        }

        // Hosts that subscribe SweepRequested emit from the returned list only.
        if (SweepRequested is null)
        {
            foreach (var item in expired)
                Expired?.Invoke(item);
        }

        return expired;
    }

    public void Drop(string kind, string id)
    {
        lock (_gate)
        {
            _resources.Remove(Key(kind, id));
            ArmTimerLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _resources.Clear();
        }
    }

    private static MetadataTokenPatchResult Reject(string error) =>
        new(false, false, false, error, new Dictionary<string, string>(StringComparer.Ordinal));

    private static string Key(string kind, string id) => kind + "\u001f" + id;

    private static Dictionary<string, string> SnapshotValues(Resource resource) =>
        new(resource.Values, StringComparer.Ordinal);

    private static MetadataTokenExpiry ToExpiry(string key, Resource resource)
    {
        var separator = key.IndexOf('\u001f');
        var kind = separator < 0 ? key : key[..separator];
        var id = separator < 0 ? string.Empty : key[(separator + 1)..];
        return new MetadataTokenExpiry(kind, id, SnapshotValues(resource));
    }

    private void ArmTimerLocked()
    {
        if (_disposed)
            return;
        DateTimeOffset? next = null;
        foreach (var resource in _resources.Values)
        {
            foreach (var deadline in resource.Expiry.Values)
            {
                if (next is null || deadline < next.Value)
                    next = deadline;
            }
        }
        _timer?.Dispose();
        if (next is null)
        {
            _timer = null;
            return;
        }
        try
        {
            var due = next.Value - _time.GetUtcNow();
            if (due < TimeSpan.Zero)
                due = TimeSpan.Zero;
            _timer = _time.CreateTimer(static state => ((MetadataTokenStore)state!).TimerTick(), this,
                due, Timeout.InfiniteTimeSpan);
        }
        catch (NotSupportedException)
        {
            // A deterministic test clock may expose only GetUtcNow; explicit ExpireDue remains available.
            _timer = null;
        }
    }

    private void TimerTick()
    {
        try
        {
            var sweep = SweepRequested;
            if (sweep is not null)
            {
                foreach (var handler in sweep.GetInvocationList())
                {
                    var task = ((Func<Task>)handler)();
                    if (!task.IsCompletedSuccessfully)
                        task.GetAwaiter().GetResult();
                }
                return;
            }

            ExpireDue(_time.GetUtcNow());
        }
        catch (ObjectDisposedException) { }
    }
}
