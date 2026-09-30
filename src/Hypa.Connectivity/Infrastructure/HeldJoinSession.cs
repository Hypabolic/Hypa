using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Live outbound join after the join result. Dispose ends this leg.
/// It does not stop the destination mux.
/// </summary>
public sealed class HeldJoinSession : IAsyncDisposable
{
    private readonly IAsyncDisposable _owner;
    private int _disposed;

    public HeldJoinSession(IAsyncDisposable owner, Stream stream, JoinBinding binding)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public Stream Stream { get; }

    public JoinBinding Binding { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _owner.DisposeAsync().ConfigureAwait(false);
    }
}
