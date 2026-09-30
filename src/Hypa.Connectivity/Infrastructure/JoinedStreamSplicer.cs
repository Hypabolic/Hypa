using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Relay splice after join. Bounds each direction. Pauses terminal at high water.
/// </summary>
public sealed class JoinedStreamSplicer
{
    private readonly Stream _mux;
    private readonly Stream _client;
    private readonly StreamBudget _budget;
    private readonly TimeProvider _clock;
    private readonly IRelayForwardSink _forwardSink;
    private readonly IRelayDurableStore _durableStore;
    private readonly SemaphoreSlim _muxWrite = new(1, 1);
    private readonly SemaphoreSlim _clientWrite = new(1, 1);
    private readonly TaskCompletionSource<ConnectivityOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _run = new();
    private ulong _muxRelaySeq = 1;
    private ulong _clientRelaySeq = 1;

    public JoinedStreamSplicer(
        Stream mux,
        Stream client,
        StreamBudget? budget = null,
        TimeProvider? clock = null,
        IRelayForwardSink? forwardSink = null,
        IRelayDurableStore? durableStore = null)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _budget = budget ?? StreamBudget.Default;
        _clock = clock ?? TimeProvider.System;
        _forwardSink = forwardSink ?? NullRelayForwardSink.Instance;
        _durableStore = durableStore ?? NullRelayDurableStore.Instance;
        MuxToClient = new DirectionFlowControl(StreamDirection.MuxToClient, _budget);
        ClientToMux = new DirectionFlowControl(StreamDirection.ClientToMux, _budget);
    }

    public DirectionFlowControl MuxToClient { get; }

    public DirectionFlowControl ClientToMux { get; }

    public Task<ConnectivityOutcome> WhenCompleted => _done.Task;

    public async Task<ConnectivityOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _run.Token);
        var ct = linked.Token;
        ConnectivityOutcome outcome;
        try
        {
            var muxRead = ReadLoopAsync(
                _mux,
                JoinRole.Mux,
                MuxToClient,
                _mux,
                _muxWrite,
                ct);
            var clientRead = ReadLoopAsync(
                _client,
                JoinRole.Client,
                ClientToMux,
                _client,
                _clientWrite,
                ct);
            var toClient = WriteLoopAsync(_client, MuxToClient, _clientWrite, JoinRole.Mux, ct);
            var toMux = WriteLoopAsync(_mux, ClientToMux, _muxWrite, JoinRole.Client, ct);
            var timeout = WatchHighWaterAsync(ct);
            var finished = await Task.WhenAny(muxRead, clientRead, toClient, toMux, timeout)
                .ConfigureAwait(false);
            outcome = await finished.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "joined stream cancelled");
        }
        catch (IOException ex)
        {
            outcome = ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            outcome = ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "peer closed");
        }

        await _run.CancelAsync().ConfigureAwait(false);
        _done.TrySetResult(outcome);
        return outcome;
    }

    public void Cancel() => _run.Cancel();

    private async Task<ConnectivityOutcome> WatchHighWaterAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _clock.GetUtcNow();
            if (MuxToClient.IsHighWaterTimeout(now) || ClientToMux.IsHighWaterTimeout(now))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "high water timeout");
            }

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return ConnectivityOutcome.Failure(
            ConnectivityReasons.PeerUnavailable,
            "joined stream cancelled");
    }

    private async Task<ConnectivityOutcome> ReadLoopAsync(
        Stream source,
        JoinRole sender,
        DirectionFlowControl outbound,
        Stream pauseTarget,
        SemaphoreSlim pauseWrite,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var header = await StreamFrameCodec.ReadHeaderAsync(source, _budget, cancellationToken)
                .ConfigureAwait(false);
            if (!header.Ok)
                return header.WithoutValue();

            var parsed = header.Value;
            if (parsed.Kind != StreamFrameKind.FlowControl
                && !StreamFrameRules.DirectionMatchesSender(parsed.Direction, sender))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "frame direction does not match sender");
            }

            if (parsed.Kind is StreamFrameKind.Binary or StreamFrameKind.Resize)
            {
                var waited = await WaitForBinaryRoomAsync(
                        outbound,
                        pauseTarget,
                        pauseWrite,
                        parsed.Direction,
                        parsed.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!waited.Ok)
                    return waited;
            }

            var body = await StreamFrameCodec.ReadExactAsync(source, parsed.Length, cancellationToken)
                .ConfigureAwait(false);
            if (!body.Ok || body.Value is null)
                return body.WithoutValue();

            if (parsed.Kind == StreamFrameKind.FlowControl)
                continue;

            _forwardSink.OnForwardedPayload(parsed.Kind, body.Value);
            _durableStore.OnStreamed("forwarded", body.Value.Length);
            if (RelayDurability.StoresWorkPack)
                _ = _durableStore.TryPut("forwarded", body.Value);
            var frame = StreamFrameCodec.ToFrame(parsed, body.Value);
            var now = _clock.GetUtcNow();
            if (!outbound.TryEnqueue(frame, now))
            {
                if (!frame.CountsAsBinary)
                {
                    return ConnectivityOutcome.Failure(
                        ConnectivityReasons.StreamReset,
                        "control reserved capacity is exhausted");
                }

                var waited = await WaitForBinaryRoomAsync(
                        outbound,
                        pauseTarget,
                        pauseWrite,
                        frame.Direction,
                        frame.PayloadLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!waited.Ok)
                    return waited;
                if (!outbound.TryEnqueue(frame, _clock.GetUtcNow()))
                {
                    return ConnectivityOutcome.Failure(
                        ConnectivityReasons.StreamReset,
                        "high water timeout");
                }
            }

            if (outbound.PauseShouldBeSent)
            {
                await SendFlowAsync(
                        pauseTarget,
                        pauseWrite,
                        parsed.Direction,
                        StreamFlowCommand.Pause,
                        cancellationToken)
                    .ConfigureAwait(false);
                outbound.MarkPauseSent();
            }
        }

        return ConnectivityOutcome.Failure(
            ConnectivityReasons.PeerUnavailable,
            "joined stream cancelled");
    }

    private async Task<ConnectivityOutcome> WaitForBinaryRoomAsync(
        DirectionFlowControl outbound,
        Stream pauseTarget,
        SemaphoreSlim pauseWrite,
        StreamDirection direction,
        int incomingLength,
        CancellationToken cancellationToken)
    {
        while (!outbound.HasRoomForBinary(incomingLength))
        {
            await SendFlowAsync(
                    pauseTarget,
                    pauseWrite,
                    direction,
                    StreamFlowCommand.Pause,
                    cancellationToken)
                .ConfigureAwait(false);
            outbound.MarkPauseSent();
            if (outbound.IsHighWaterTimeout(_clock.GetUtcNow()))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "high water timeout");
            }

            var wait = outbound.WaitForBinaryRoomAsync(incomingLength, cancellationToken);
            var finished = await Task.WhenAny(wait, Task.Delay(50, cancellationToken))
                .ConfigureAwait(false);
            if (finished != wait && outbound.IsHighWaterTimeout(_clock.GetUtcNow()))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "high water timeout");
            }
        }

        return ConnectivityOutcome.Success();
    }

    private async Task<ConnectivityOutcome> WriteLoopAsync(
        Stream dest,
        DirectionFlowControl outbound,
        SemaphoreSlim writeGate,
        JoinRole sourceRole,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!outbound.TryDequeue(out var frame))
            {
                await outbound.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var write = await WriteDequeuedAsync(dest, outbound, writeGate, frame, cancellationToken)
                .ConfigureAwait(false);
            if (write.Preempted)
                continue;
            if (!write.Outcome.Ok)
                return write.Outcome;

            outbound.CompleteWrite(frame);

            if (outbound.ResumeShouldBeSent)
            {
                var source = sourceRole == JoinRole.Mux ? _mux : _client;
                var sourceGate = sourceRole == JoinRole.Mux ? _muxWrite : _clientWrite;
                await SendFlowAsync(
                        source,
                        sourceGate,
                        StreamFrameRules.Outgoing(sourceRole),
                        StreamFlowCommand.Resume,
                        cancellationToken)
                    .ConfigureAwait(false);
                outbound.MarkResumeSent();
            }
        }

        return ConnectivityOutcome.Failure(
            ConnectivityReasons.PeerUnavailable,
            "joined stream cancelled");
    }

    private static async Task<DequeuedWrite> WriteDequeuedAsync(
        Stream dest,
        DirectionFlowControl outbound,
        SemaphoreSlim writeGate,
        StreamFrame frame,
        CancellationToken cancellationToken)
    {
        if (!frame.CountsAsBinary)
            return await WriteExclusiveAsync(dest, writeGate, frame, cancellationToken).ConfigureAwait(false);

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = CancelWhenHighPriorityAsync(outbound, writeCts, cancellationToken);
        var acquired = false;
        try
        {
            await writeGate.WaitAsync(writeCts.Token).ConfigureAwait(false);
            acquired = true;
            await StreamFrameCodec.WriteAsync(dest, frame, writeCts.Token).ConfigureAwait(false);
            return DequeuedWrite.Written();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outbound.ReturnBinary(frame);
            return DequeuedWrite.Preempt();
        }
        catch (OperationCanceledException)
        {
            outbound.CompleteWrite(frame);
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    "joined stream cancelled"));
        }
        catch (IOException ex)
        {
            outbound.CompleteWrite(frame);
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, ex.Message));
        }
        catch (ObjectDisposedException)
        {
            outbound.CompleteWrite(frame);
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, "peer closed"));
        }
        finally
        {
            if (acquired)
                writeGate.Release();
            await writeCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<DequeuedWrite> WriteExclusiveAsync(
        Stream dest,
        SemaphoreSlim writeGate,
        StreamFrame frame,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StreamFrameCodec.WriteAsync(dest, frame, cancellationToken).ConfigureAwait(false);
            return DequeuedWrite.Written();
        }
        catch (OperationCanceledException)
        {
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    "joined stream cancelled"));
        }
        catch (IOException ex)
        {
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, ex.Message));
        }
        catch (ObjectDisposedException)
        {
            return DequeuedWrite.Fail(
                ConnectivityOutcome.Failure(ConnectivityReasons.PeerUnavailable, "peer closed"));
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task CancelWhenHighPriorityAsync(
        DirectionFlowControl outbound,
        CancellationTokenSource writeCts,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                writeCts.Token);
            while (!linked.IsCancellationRequested)
            {
                if (outbound.HasHighPriority)
                {
                    await writeCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await outbound.WaitForHighPriorityAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct DequeuedWrite(bool Preempted, ConnectivityOutcome Outcome)
    {
        public static DequeuedWrite Written() => new(false, ConnectivityOutcome.Success());

        public static DequeuedWrite Preempt() => new(true, ConnectivityOutcome.Success());

        public static DequeuedWrite Fail(ConnectivityOutcome outcome) => new(false, outcome);
    }

    private async Task SendFlowAsync(
        Stream dest,
        SemaphoreSlim writeGate,
        StreamDirection pausedDirection,
        StreamFlowCommand command,
        CancellationToken cancellationToken)
    {
        var seq = pausedDirection == StreamDirection.MuxToClient
            ? _muxRelaySeq++
            : _clientRelaySeq++;
        var frame = StreamFrame.FlowControl(pausedDirection, command, seq);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StreamFrameCodec.WriteAsync(dest, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
