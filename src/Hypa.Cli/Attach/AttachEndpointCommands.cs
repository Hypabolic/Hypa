using System.Text.Json.Nodes;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// Activation focus-revoke, surface interest, resize, and focus baseline stay on
/// </summary>
internal sealed class AttachEndpointCommands
{
    internal const string CancelledCode = "endpoint_cancelled";

    internal const string InterruptedMessage =
        "This server action was interrupted. Check its state before retrying.";

    private const int MaxRetiredRequestsPerEndpoint = 128;

    private readonly object _gate = new();
    private readonly Dictionary<string, EndpointCommandLane> _lanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingShellRequest> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShellRequestLocation> _locations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<Hypa.ControlPlane.ControlPlaneCallResult>> _waits =
        new(StringComparer.Ordinal);
    private readonly List<ShellEndpointOutcome> _outcomes = [];
    private int _pumpedReplies;

    internal string? SnapshotBootId { get; set; }

    internal IReadOnlyList<ShellEndpointOutcome> Outcomes => _outcomes;

    internal int PumpedReplyCount => Volatile.Read(ref _pumpedReplies);

    internal void NotePumpedReply() => Interlocked.Increment(ref _pumpedReplies);

    internal void BeginWait(
        string endpointId,
        ulong generation,
        string bootId,
        string requestId,
        string methodName,
        ControlPlaneClient client,
        TaskCompletionSource<Hypa.ControlPlane.ControlPlaneCallResult> wait)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            var key = Key(endpointId, requestId);
            _pending[key] = new PendingShellRequest(bootId, methodName);
            _locations[key] = new ShellRequestLocation(endpointId, generation, bootId, requestId, client);
            _waits[key] = wait;
        }
    }

    /// <summary>
    /// JSON-RPC ids restart on each socket. The lane key is the endpoint plus
    /// that id so a dest reply cannot complete a source wait.
    /// </summary>
    private static string Key(string endpointId, string requestId) =>
        endpointId + "\u001f" + requestId;

    internal void Enqueue(QueuedShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
            Lane(command.EndpointId).Queued.Enqueue(command);
    }

    /// <summary>
    /// <c>send_to</c> is accepted. Hypa's reader is another thread, so the record is
    /// published before that write is visible. A write that fails clears the record
    /// and cancels the id, the same as a send that was not <c>Sent</c>.
    /// </summary>
    internal List<string> SendNext(
        string endpointId,
        Func<QueuedShellCommand, bool> accepts,
        Func<QueuedShellCommand, bool> encode,
        Func<QueuedShellCommand, bool> trySend)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(trySend);
        lock (_gate)
        {
            var cancelled = new List<string>();
            var lane = Lane(endpointId);
            if (lane.InFlight is not null)
                return cancelled;

            while (lane.Queued.TryDequeue(out var queued))
            {
                if (!accepts(queued))
                {
                    cancelled.Add(queued.RequestId);
                    continue;
                }

                if (!encode(queued))
                {
                    cancelled.Add(queued.RequestId);
                    continue;
                }

                // Publish before the write.
                lane.InFlight = new InFlightCommand(queued.Generation, queued.BootId, queued.RequestId);
                if (!trySend(queued))
                {
                    lane.InFlight = null;
                    cancelled.Add(queued.RequestId);
                    continue;
                }

                break;
            }

            return cancelled;
        }
    }

    internal List<string> RetireLane(string endpointId)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(endpointId, out var lane))
                return [];

            var requestIds = new List<string>();
            if (lane.InFlight is { } command)
            {
                lane.Retire((command.Generation, command.BootId, command.RequestId));
                requestIds.Add(command.RequestId);
                lane.InFlight = null;
            }

            while (lane.Queued.TryDequeue(out var queued))
                requestIds.Add(queued.RequestId);
            return requestIds;
        }
    }

    /// Removes one endpoint lane. Does not tombstone and does not touch another lane.
    internal List<string> Disconnect(string endpointId)
    {
        lock (_gate)
        {
            if (!_lanes.Remove(endpointId, out var lane))
                return [];

            var requestIds = new List<string>();
            while (lane.Queued.TryDequeue(out var queued))
                requestIds.Add(queued.RequestId);
            if (lane.InFlight is { } command)
                requestIds.Add(command.RequestId);
            return requestIds;
        }
    }

    internal bool CancelEndpointRequest(string endpointId, string requestId)
    {
        lock (_gate)
        {
            var key = Key(endpointId, requestId);
            if (!_pending.ContainsKey(key))
                return false;

            var pending = _pending[key];
            var (repaint, actions) = HandleEndpointResultCore(
                endpointId,
                pending.BootId,
                requestId,
                CancelledCode,
                InterruptedMessage);
            if (actions != 0)
                throw new InvalidOperationException("cancellation must not start another action");
            if (!IsRetiredCore(endpointId, requestId))
                _locations.Remove(key);
            CompleteWaitCore(endpointId, requestId, new Hypa.ControlPlane.ControlPlaneCallResult(
                default,
                requestId,
                new Hypa.ControlPlane.ControlPlaneException(0, InterruptedMessage, CancelledCode)));
            return repaint;
        }
    }

    /// <summary>
    /// connection. Match that connection's request id, not another socket's.
    /// </summary>
    internal bool TryLocate(
        ControlPlaneClient client,
        string requestId,
        out ShellRequestLocation location)
    {
        lock (_gate)
        {
            foreach (var candidate in _locations.Values)
            {
                if (ReferenceEquals(candidate.Client, client)
                    && string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal))
                {
                    location = candidate;
                    return true;
                }
            }
        }

        location = default;
        return false;
    }

    internal void CompleteWait(
        string endpointId,
        string requestId,
        Hypa.ControlPlane.ControlPlaneCallResult result)
    {
        lock (_gate)
            CompleteWaitCore(endpointId, requestId, result);
    }

    internal EndpointCommandAdmission ReceiveChunk(
        string endpointId,
        ulong generation,
        string bootId,
        string requestId,
        bool finalChunk,
        ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(endpointId, out var lane))
                return new EndpointCommandAdmission.Ignored();

            var retired = (generation, bootId, requestId);
            if (lane.ConsumeRetired(retired, finalChunk))
            {
                if (finalChunk)
                    _locations.Remove(Key(endpointId, requestId));
                return new EndpointCommandAdmission.Retired(finalChunk);
            }

            if (lane.InFlight is not { } inFlight)
                return new EndpointCommandAdmission.Ignored();
            if (inFlight.Generation != generation
                || !string.Equals(inFlight.BootId, bootId, StringComparison.Ordinal)
                || !string.Equals(inFlight.RequestId, requestId, StringComparison.Ordinal))
            {
                return new EndpointCommandAdmission.Ignored();
            }

            // returns no result until final_chunk.
            inFlight.Append(data);
            if (!finalChunk)
                return new EndpointCommandAdmission.Ignored();

            // the accumulated body. The caller parses that body, not this chunk.
            var response = inFlight.CopyResponse();
            lane.InFlight = null;
            _locations.Remove(Key(endpointId, requestId));
            return new EndpointCommandAdmission.Completed(
                endpointId,
                generation,
                bootId,
                requestId,
                response);
        }
    }

    internal (bool Repaint, int Actions) HandleEndpointResult(
        string endpointId,
        string bootId,
        string requestId,
        string? errorCode,
        string? errorMessage)
    {
        lock (_gate)
            return HandleEndpointResultCore(endpointId, bootId, requestId, errorCode, errorMessage);
    }

    private (bool Repaint, int Actions) HandleEndpointResultCore(
        string endpointId,
        string bootId,
        string requestId,
        string? errorCode,
        string? errorMessage)
    {
        if (!_pending.Remove(Key(endpointId, requestId), out var pending))
            return (false, 0);
        if (!string.Equals(pending.BootId, bootId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(SnapshotBootId)
            || !string.Equals(SnapshotBootId, bootId, StringComparison.Ordinal))
        {
            return (false, 0);
        }

        _outcomes.Add(new ShellEndpointOutcome(
            requestId,
            pending.MethodName,
            errorCode,
            errorMessage ?? string.Empty,
            FollowUpActions: 0));
        return (errorCode is not null, 0);
    }

    private void CompleteWaitCore(
        string endpointId,
        string requestId,
        Hypa.ControlPlane.ControlPlaneCallResult result)
    {
        if (_waits.Remove(Key(endpointId, requestId), out var wait))
            wait.TrySetResult(result);
    }

    internal bool HasTombstone(string endpointId, string requestId)
    {
        lock (_gate)
            return IsRetiredCore(endpointId, requestId);
    }

    private bool IsRetiredCore(string endpointId, string requestId) =>
        _lanes.TryGetValue(endpointId, out var lane) && lane.IsRetired(requestId);

    private EndpointCommandLane Lane(string endpointId)
    {
        if (!_lanes.TryGetValue(endpointId, out var lane))
        {
            lane = new EndpointCommandLane();
            _lanes[endpointId] = lane;
        }

        return lane;
    }

    private sealed class EndpointCommandLane
    {
        internal Queue<QueuedShellCommand> Queued { get; } = new();

        internal InFlightCommand? InFlight { get; set; }

        private readonly List<(ulong Generation, string BootId, string RequestId)> _retired = [];

        internal void Retire((ulong Generation, string BootId, string RequestId) request)
        {
            if (_retired.Contains(request))
                return;
            if (_retired.Count == MaxRetiredRequestsPerEndpoint)
                _retired.RemoveAt(0);
            _retired.Add(request);
        }

        internal bool ConsumeRetired(
            (ulong Generation, string BootId, string RequestId) request,
            bool finalChunk)
        {
            var index = _retired.IndexOf(request);
            if (index < 0)
                return false;
            if (finalChunk)
                _retired.RemoveAt(index);
            return true;
        }

        internal bool IsRetired(string requestId)
        {
            foreach (var retired in _retired)
            {
                if (string.Equals(retired.RequestId, requestId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    private sealed class InFlightCommand(
        ulong generation,
        string bootId,
        string requestId)
    {
        private readonly List<byte> _response = [];

        internal ulong Generation { get; } = generation;

        internal string BootId { get; } = bootId;

        internal string RequestId { get; } = requestId;

        internal byte[] CopyResponse() => _response.ToArray();

        internal void Append(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
                _response.Add(value);
        }
    }

    private readonly record struct PendingShellRequest(string BootId, string MethodName);
}

internal sealed record QueuedShellCommand(
    string EndpointId,
    ulong Generation,
    string BootId,
    string RequestId,
    string MethodName,
    JsonObject? Parameters,
    ControlPlaneClient Client);

internal readonly record struct ShellRequestLocation(
    string EndpointId,
    ulong Generation,
    string BootId,
    string RequestId,
    ControlPlaneClient Client);

internal sealed record ShellEndpointOutcome(
    string RequestId,
    string MethodName,
    string? Code,
    string Message,
    int FollowUpActions);

internal abstract record EndpointCommandAdmission
{
    internal sealed record Ignored : EndpointCommandAdmission;

    /// <paramref name="TombstoneRemoved"/> is set only for the final chunk.
    internal sealed record Retired(bool TombstoneRemoved) : EndpointCommandAdmission;

    internal sealed record Completed(
        string EndpointId,
        ulong Generation,
        string BootId,
        string RequestId,
        byte[] Response) : EndpointCommandAdmission;
}
