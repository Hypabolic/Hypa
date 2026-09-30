using System.Threading.Channels;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// In-process framed pair. Same send and receive contract as the outbound TCP session.
/// </summary>
public sealed class ChannelFramedSession : IFramedSession
{
    private readonly Channel<StreamFrame> _incoming;
    private readonly Channel<StreamFrame> _outgoing;
    private readonly ChannelSequenceTracker _received = new();
    private long _nextSequence;
    private int _disposed;

    private ChannelFramedSession(
        JoinBinding binding,
        Channel<StreamFrame> incoming,
        Channel<StreamFrame> outgoing)
    {
        Binding = binding;
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public JoinBinding Binding { get; }

    public bool IsBinaryPaused => false;

    public bool ApplicationEncryptionEnabled => false;

    public IReadOnlyList<ChannelCursor> LastReceived => _received.Snapshot();

    public static (ChannelFramedSession Client, ChannelFramedSession Mux) Pair(string placementId)
    {
        if (!PlacementId.TryParse(placementId, out var parsed))
            throw new ArgumentException("placement id is invalid", nameof(placementId));
        return Pair(parsed);
    }

    public static (ChannelFramedSession Client, ChannelFramedSession Mux) Pair(PlacementId placementId)
    {
        if (string.IsNullOrEmpty(placementId.Value))
            throw new ArgumentException("placement id is invalid", nameof(placementId));
        var toMux = Channel.CreateUnbounded<StreamFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var toClient = Channel.CreateUnbounded<StreamFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var client = new ChannelFramedSession(
            new JoinBinding
            {
                PlacementId = placementId,
                StreamClass = StreamClass.Control,
                Role = JoinRole.Client,
            },
            toClient,
            toMux);
        var mux = new ChannelFramedSession(
            new JoinBinding
            {
                PlacementId = placementId,
                StreamClass = StreamClass.Control,
                Role = JoinRole.Mux,
            },
            toMux,
            toClient);
        return (client, mux);
    }

    public ValueTask<ConnectivityOutcome> SendAsync(
        StreamFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "session closed"));
        }

        var outbound = frame with
        {
            Direction = StreamFrameRules.Outgoing(Binding.Role),
            Sequence = NextSequence(),
        };
        var valid = StreamFrameRules.ValidateFrame(outbound, StreamBudget.Default);
        if (!valid.Ok)
            return ValueTask.FromResult(valid);

        if (!_outgoing.Writer.TryWrite(outbound))
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "session closed"));
        }

        return ValueTask.FromResult(ConnectivityOutcome.Success());
    }

    public async ValueTask<ConnectivityOutcome<StreamFrame>> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _received.Note(frame);
            return ConnectivityOutcome<StreamFrame>.Success(frame);
        }
        catch (ChannelClosedException)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "session closed");
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "session closed");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private ulong NextSequence() => (ulong)Interlocked.Increment(ref _nextSequence);
}
