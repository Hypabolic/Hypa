using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Attach reconnect after a tunnel stall. Mux stays up. Input is never replayed.
/// </summary>
public interface IAttachReconnect
{
    AttachReconnectState State { get; }

    AttachAttemptId AttemptId { get; }

    JoinCapability? Capability { get; }

    bool MuxRunning { get; }

    bool DumpedToShell { get; }

    bool ServerStopRequested { get; }

    string? LeaseId { get; }

    int PaintCount { get; }

    int InputAppliedCount { get; }

    IReadOnlyList<ChannelCursor> LastReceived { get; }

    ConnectivityOutcome<AttachAttemptId> Attach(JoinCapability capability);

    AttachAttemptId MintAttemptId();

    ConnectivityOutcome NoteObserved(StreamFrame frame);

    ConnectivityOutcome ApplyInput(StreamFrame frame);

    ConnectivityOutcome Stall(TimeSpan gap);

    ConnectivityOutcome Detach();

    ConnectivityOutcome Disconnect();

    ConnectivityOutcome<AttachReconnectOffer> Reconnect(AttachReconnectRequest request);

    ConnectivityOutcome ReclaimLease(string leaseId);

    /// <summary>
    /// ClientShell reconnect observes. It does not reclaim an exclusive
    // / input lease.
    /// </summary>
    ConnectivityOutcome AdmitWithoutInputLease();

    ConnectivityOutcome CompleteReplay(bool painted);
}
