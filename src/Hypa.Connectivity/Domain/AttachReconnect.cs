namespace Hypa.Connectivity.Domain;

/// <summary>Why the current attach socket ended.</summary>
public enum ClientLossKind
{
    Stall = 0,
    Detach = 1,
    Disconnect = 2,
}

/// <summary>Reconnect state for one attach client.</summary>
public enum AttachReconnectState
{
    Detached = 0,
    Connected = 1,
    Stalled = 2,
    Reconnecting = 3,
    Replaying = 4,
}

/// <summary>Last received sequence on one multiplex channel.</summary>
public sealed record ChannelCursor
{
    public required uint ChannelId { get; init; }
    public required ulong LastReceivedSequence { get; init; }
    public bool Unavailable { get; init; }
    public string AttemptId { get; init; } = "";
    public string PlacementId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public JoinRole? Role { get; init; }
}

/// <summary>Reconnect after a stall. Capability must be fresh.</summary>
public sealed record AttachReconnectRequest
{
    public required AttachAttemptId PreviousAttemptId { get; init; }
    public required AttachAttemptId AttemptId { get; init; }
    public required JoinCapability Capability { get; init; }
    public required IReadOnlyList<ChannelCursor> LastReceived { get; init; }
}

/// <summary>Replay plan. Input is never included.</summary>
public sealed record AttachReconnectOffer
{
    public required AttachAttemptId AttemptId { get; init; }
    public required IReadOnlyList<StreamFrame> ReplayFrames { get; init; }
    public required bool SnapshotPaint { get; init; }
    public required bool InputLeaseRequired { get; init; }
}

/// <summary>Product defaults for stall reconnect.</summary>
public static class AttachReconnectLimits
{
    public static TimeSpan StallWindow { get; } = TimeSpan.FromSeconds(2);

    public const int MaxReconnectAttempts = 3;
}

/// <summary>Stall, sequence, snapshot, and lease rules for attach reconnect.</summary>
public static class AttachReconnectRules
{
    public static bool ShouldReconnect(ClientLossKind kind) =>
        kind == ClientLossKind.Stall;

    public static bool DumpsToShell(ClientLossKind kind) =>
        kind is ClientLossKind.Detach or ClientLossKind.Disconnect;

    public static ClientLossKind Classify(bool userDetach, string? reason)
    {
        if (userDetach)
            return ClientLossKind.Detach;

        if (string.Equals(reason, "tty hangup", StringComparison.Ordinal)
            || string.Equals(reason, "stdin closed", StringComparison.Ordinal))
        {
            return ClientLossKind.Detach;
        }

        if (string.Equals(reason, "control ping timed out", StringComparison.Ordinal)
            || string.Equals(reason, "control ping failed", StringComparison.Ordinal)
            || string.Equals(reason, "connection closed", StringComparison.Ordinal)
            || string.Equals(reason, "control plane closed", StringComparison.Ordinal)
            || string.Equals(reason, "peer closed", StringComparison.Ordinal)
            || string.Equals(reason, "render closed", StringComparison.Ordinal)
            || string.Equals(reason, "lease ended", StringComparison.Ordinal)
            || string.Equals(reason, "input sender fault", StringComparison.Ordinal))
        {
            return ClientLossKind.Stall;
        }

        return ClientLossKind.Disconnect;
    }

    public static bool MayReplay(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Direction == StreamDirection.ClientToMux)
            return false;
        if (frame.Kind is StreamFrameKind.Heartbeat or StreamFrameKind.FlowControl)
            return false;
        return true;
    }

    public static bool RequiresSnapshot(IReadOnlyList<ChannelCursor>? lastReceived)
    {
        if (lastReceived is null || lastReceived.Count == 0)
            return true;

        foreach (var cursor in lastReceived)
        {
            if (cursor.Unavailable)
                return true;
        }

        return false;
    }

    public static ConnectivityOutcome ValidateRequest(
        AttachReconnectRequest request,
        AttachAttemptId previousAttemptId,
        JoinCapability? previousCapability,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AttemptId.Value.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "attach attempt id is required");
        }

        if (string.Equals(request.AttemptId.Value, previousAttemptId.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "reconnect requires a new attach attempt id");
        }

        if (!string.Equals(
                request.PreviousAttemptId.Value,
                previousAttemptId.Value,
                StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "previous attach attempt id does not match");
        }

        if (previousCapability is not null
            && string.Equals(
                request.Capability.Nonce.Value,
                previousCapability.Nonce.Value,
                StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "reconnect requires a fresh capability");
        }

        if (previousCapability is not null)
        {
            if (request.Capability.PlacementId != previousCapability.PlacementId)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "capability placement id does not match");
            }

            if (!string.Equals(
                    request.Capability.DeviceId.Value,
                    previousCapability.DeviceId.Value,
                    StringComparison.Ordinal))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "capability device id does not match");
            }

            if (request.Capability.Role != previousCapability.Role)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "capability role does not match");
            }
        }

        if (request.Capability.ExpiresAt <= utcNow)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        return ValidateCursors(request.LastReceived, previousAttemptId, previousCapability);
    }

    public static ConnectivityOutcome ValidateFramedBinding(
        JoinBinding binding,
        AttachReconnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);

        if (binding.PlacementId != request.Capability.PlacementId)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability placement id does not match framed peer");
        }

        if (binding.Role != JoinRole.Mux)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "framed peer role must be mux");
        }

        if (request.Capability.Role != JoinRole.Client)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability role does not match framed peer");
        }

        return ValidateCursors(
            request.LastReceived,
            request.PreviousAttemptId,
            request.Capability);
    }

    public static ConnectivityOutcome ValidateCursors(
        IReadOnlyList<ChannelCursor>? lastReceived,
        AttachAttemptId attempt,
        JoinCapability? capability)
    {
        if (lastReceived is null || lastReceived.Count == 0)
            return ConnectivityOutcome.Success();

        if (capability is null)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "cursor does not match attach destination");
        }

        foreach (var cursor in lastReceived)
        {
            var bound = CursorBelongsTo(cursor, attempt, capability);
            if (!bound.Ok)
                return bound;
        }

        return ConnectivityOutcome.Success();
    }

    public static ConnectivityOutcome CursorBelongsTo(
        ChannelCursor cursor,
        AttachAttemptId attempt,
        JoinCapability capability)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(capability);
        if (string.IsNullOrWhiteSpace(cursor.AttemptId)
            || string.IsNullOrWhiteSpace(cursor.PlacementId)
            || string.IsNullOrWhiteSpace(cursor.DeviceId)
            || cursor.Role is null)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "cursor does not match attach destination");
        }

        if (!string.Equals(cursor.AttemptId, attempt.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "cursor does not match attach destination");
        }

        if (!string.Equals(cursor.PlacementId, capability.PlacementId.Value, StringComparison.Ordinal)
            || !string.Equals(cursor.DeviceId, capability.DeviceId.Value, StringComparison.Ordinal)
            || cursor.Role != capability.Role)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "cursor does not match attach destination");
        }

        return ConnectivityOutcome.Success();
    }

    public static IReadOnlyList<ChannelCursor> BindCursors(
        IReadOnlyList<ChannelCursor> cursors,
        AttachAttemptId attempt,
        JoinCapability capability)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(capability);
        if (cursors.Count == 0)
            return cursors;

        var bound = new ChannelCursor[cursors.Count];
        for (var i = 0; i < cursors.Count; i++)
        {
            var cursor = cursors[i];
            bound[i] = cursor with
            {
                AttemptId = attempt.Value,
                PlacementId = capability.PlacementId.Value,
                DeviceId = capability.DeviceId.Value,
                Role = capability.Role,
            };
        }

        return bound;
    }

    public static bool ObservedBelongsTo(
        string? placementId,
        string? deviceId,
        JoinRole role,
        JoinCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return string.Equals(placementId, capability.PlacementId.Value, StringComparison.Ordinal)
            && string.Equals(deviceId, capability.DeviceId.Value, StringComparison.Ordinal)
            && role == capability.Role;
    }

    public static IReadOnlyList<StreamFrame> SelectReplay(
        IReadOnlyList<StreamFrame> observed,
        IReadOnlyList<ChannelCursor> lastReceived)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(lastReceived);
        if (RequiresSnapshot(lastReceived))
            return [];

        var cursors = new Dictionary<uint, ulong>();
        foreach (var cursor in lastReceived)
            cursors[cursor.ChannelId] = cursor.LastReceivedSequence;

        var selected = new List<StreamFrame>();
        foreach (var frame in observed)
        {
            if (!MayReplay(frame))
                continue;
            if (!cursors.TryGetValue(frame.ChannelId, out var last))
                continue;
            if (frame.Sequence <= last)
                continue;
            selected.Add(frame);
        }

        return selected;
    }
}
