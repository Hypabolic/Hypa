namespace Hypa.Connectivity.Application;

/// <summary>
/// Captured durable write API. The live relay must not call it for WorkPack bytes.
/// </summary>
public interface IRelayDurableStore
{
    void OnStreamed(string objectName, int byteCount);

    bool TryPut(string objectName, ReadOnlySpan<byte> bytes);

    IReadOnlyList<RelayDurableObject> Snapshot();

    int StreamedCount { get; }
}

public sealed record RelayDurableObject
{
    public required string Name { get; init; }

    public required byte[] Bytes { get; init; }
}

public sealed class NullRelayDurableStore : IRelayDurableStore
{
    public static NullRelayDurableStore Instance { get; } = new();

    public int StreamedCount => 0;

    public void OnStreamed(string objectName, int byteCount)
    {
    }

    public bool TryPut(string objectName, ReadOnlySpan<byte> bytes) => false;

    public IReadOnlyList<RelayDurableObject> Snapshot() => [];
}

public sealed class MemoryRelayDurableStore : IRelayDurableStore
{
    private readonly object _gate = new();
    private readonly List<RelayDurableObject> _objects = [];
    private int _streamed;

    public int StreamedCount
    {
        get
        {
            lock (_gate)
                return _streamed;
        }
    }

    public void OnStreamed(string objectName, int byteCount)
    {
        lock (_gate)
            _streamed++;
    }

    public bool TryPut(string objectName, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _objects.Add(new RelayDurableObject
            {
                Name = objectName,
                Bytes = bytes.ToArray(),
            });
        }

        return true;
    }

    public IReadOnlyList<RelayDurableObject> Snapshot()
    {
        lock (_gate)
            return [.. _objects];
    }
}
