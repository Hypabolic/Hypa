using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>One admitted client leg from register through splice.</summary>
public sealed class AcceptClientSession : IDisposable
{
    private readonly CancellationTokenSource _revoke = new();
    private int _disposed;

    internal AcceptClientSession(JoinBootstrap bootstrap, Stream stream)
    {
        Bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public JoinBootstrap Bootstrap { get; }

    public Stream Stream { get; }

    internal JoinMatch? Match { get; set; }

    internal bool IsActive { get; set; }

    internal bool BridgeReserved { get; set; }

    public CancellationToken Token => _revoke.Token;

    public bool IsRevoked => _revoke.IsCancellationRequested;

    internal void Revoke()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _revoke.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _revoke.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _revoke.Dispose();
    }
}
